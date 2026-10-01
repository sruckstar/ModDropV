// PoolHeapAdjusterEnhanced — raises the memory budget GTA V Enhanced gives to its pools (the gameconfig.xml PoolSize limits).
//
// Enhanced doesn't take pool storage from the game heap Heap Adjuster grows. Its memory allocator has a separate one for
// pools (slot 5 of the multi-allocator): big blocks come straight from VirtualAlloc, small ones from the game heap, and
// both count against a fixed budget of 136 MB (GTA5_Enhanced.exe build 1158), about 120 of which the game uses itself.
// Past the budget the allocator returns null and the game crashes at Game Init, so raising pool sizes in gameconfig.xml
// (as mods like Liberty City Preservation Project do) only works with a bigger budget. The budget is set once, in the
// static initialization of the memory system — Heap Adjuster patches the same function, so an .asi is early enough:
//
//   lea rcx, [rip + poolAllocator]; lea rdx, [rip + gameHeap]; mov r8d, 0x10000; mov r9d, 0x8800000; call ctor
//
// The budget is only a cap: nothing is reserved until pools are created, so a bigger one costs no memory by itself.
//
// It also raises the radio station limit (96 in Enhanced, see RaiseRadioStations), a fixed array mods overflow.

#include <Windows.h>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

namespace
{
    wchar_t g_dir[MAX_PATH];
    FILE* g_log;

    void Log(const char* fmt, ...)
    {
        if (!g_log) return;
        va_list args;
        va_start(args, fmt);
        vfprintf(g_log, fmt, args);
        va_end(args);
        fputc('\n', g_log);
        fflush(g_log);
    }

    struct Range { uint8_t* begin; uint8_t* end; };

    std::vector<Range> CodeSections()
    {
        auto base = (uint8_t*)GetModuleHandleW(nullptr);
        auto nt = (IMAGE_NT_HEADERS*)(base + ((IMAGE_DOS_HEADER*)base)->e_lfanew);
        std::vector<Range> ranges;
        auto section = IMAGE_FIRST_SECTION(nt);
        for (int i = 0; i < nt->FileHeader.NumberOfSections; i++, section++)
            if (section->Characteristics & IMAGE_SCN_MEM_EXECUTE)
                ranges.push_back({ base + section->VirtualAddress, base + section->VirtualAddress + section->Misc.VirtualSize });
        return ranges;
    }

    // "48 8D 0D ? ? ? ?" — hex bytes, ? for any byte.
    std::vector<uint8_t*> Find(const char* pattern)
    {
        std::vector<int> bytes;
        for (const char* p = pattern; *p;)
        {
            if (*p == ' ') { p++; continue; }
            if (*p == '?') { bytes.push_back(-1); p++; continue; }
            bytes.push_back((int)strtol(std::string(p, 2).c_str(), nullptr, 16));
            p += 2;
        }

        std::vector<uint8_t*> hits;
        for (auto& r : CodeSections())
            for (uint8_t* p = r.begin; p + bytes.size() <= r.end; p++)
            {
                size_t i = 0;
                while (i < bytes.size() && (bytes[i] < 0 || p[i] == bytes[i])) i++;
                if (i == bytes.size()) hits.push_back(p);
            }
        return hits;
    }

    void Write(void* at, const void* data, size_t size)
    {
        DWORD old;
        VirtualProtect(at, size, PAGE_EXECUTE_READWRITE, &old);
        memcpy(at, data, size);
        VirtualProtect(at, size, old, &old);
        FlushInstructionCache(GetCurrentProcess(), at, size);
    }

    // The constructor call of the pool allocator: its parent is the game heap, the 64 KB threshold splits small blocks
    // (from the parent) from big ones (VirtualAlloc), r9d is the budget in bytes.
    bool AdjustBudget(int megabytes)
    {
        auto hits = Find("48 8D 0D ? ? ? ? 48 8D 15 ? ? ? ? 41 B8 00 00 01 00 41 B9 ? ? ? ? E8");
        if (hits.size() != 1)
        {
            Log("Pool heap: not found (%zu matches) - a game version this plugin doesn't know.", hits.size());
            return false;
        }
        uint8_t* imm = hits[0] + 22;
        uint32_t original = *(uint32_t*)imm;
        uint32_t size = (uint32_t)megabytes << 20;
        if (size <= original)
        {
            Log("Pool heap: the game already has %u MB, left as is.", original >> 20);
            return true;
        }
        Write(imm, &size, sizeof size);
        Log("Pool heap: %u MB -> %u MB", original >> 20, size >> 20);
        return true;
    }

    int32_t Rel32(const uint8_t* at) { int32_t v; memcpy(&v, at, 4); return v; }

    // The radio station list: an array of 96 pointers (Legacy has 255 since a later update). Stations are appended
    // without a capacity check, and Enhanced itself has ~95 — every added station writes past the heap block and
    // corrupts whatever lies behind it (crashes with a zeroed vtable, in the radio or anywhere else). Three places
    // create the array: mov ecx, 0x300 (96 * 8); mov edx, 8; call alloc; mov [rip + list], rax — and set its
    // u16 count (list + 8) and capacity (list + 10) with mov word [rip + ...], 0x60.
    bool RaiseRadioStations(int max)
    {
        auto allocs = Find("B9 00 03 00 00 BA 08 00 00 00 E8 ? ? ? ? 48 89 05");
        uint8_t* list = nullptr;
        for (auto a : allocs)
        {
            uint8_t* target = a + 22 + Rel32(a + 18);
            if (list && target != list) { list = nullptr; break; }
            list = target;
        }
        if (allocs.size() != 3 || !list)
        {
            Log("Radio stations: not found (%zu matches) - a game version this plugin doesn't know.", allocs.size());
            return false;
        }
        std::vector<uint8_t*> words;
        for (auto w : Find("66 C7 05 ? ? ? ? 60 00"))
        {
            uint8_t* target = w + 9 + Rel32(w + 3);
            if (target == list + 8 || target == list + 10) words.push_back(w);
        }
        if (words.size() != 6)
        {
            Log("Radio stations: %zu of 6 count/capacity writes found - left as is.", words.size());
            return false;
        }
        uint16_t count = (uint16_t)max;
        uint32_t bytes = (uint32_t)max * 8;
        for (auto w : words) Write(w + 7, &count, sizeof count);
        for (auto a : allocs) Write(a + 1, &bytes, sizeof bytes);
        Log("Radio stations: 96 -> %d", max);
        return true;
    }

    int ReadInt(const wchar_t* section, const wchar_t* key, int fallback, int max)
    {
        std::wstring ini = std::wstring(g_dir) + L"PoolHeapAdjusterEnhanced.ini";
        int value = GetPrivateProfileIntW(section, key, -1, ini.c_str());
        if (value <= 0) return fallback;
        return value > max ? max : value;
    }

    void Run(HMODULE self)
    {
        GetModuleFileNameW(self, g_dir, MAX_PATH);
        if (auto slash = wcsrchr(g_dir, L'\\')) slash[1] = 0;
        g_log = _wfopen((std::wstring(g_dir) + L"PoolHeapAdjusterEnhanced.log").c_str(), L"w");
        Log("PoolHeapAdjusterEnhanced 1.1");

        wchar_t exe[MAX_PATH];
        GetModuleFileNameW(nullptr, exe, MAX_PATH);
        auto name = wcsrchr(exe, L'\\');
        if (!name || _wcsicmp(name + 1, L"GTA5_Enhanced.exe") != 0)
        {
            Log("Not GTA5_Enhanced.exe - nothing to do (GTA V Legacy keeps its pools in the game heap: use Heap Adjuster).");
            return;
        }

        AdjustBudget(ReadInt(L"PoolHeapAdjuster", L"PoolHeapSize", 512, 4095));   // a 32-bit immediate
        int stations = ReadInt(L"RadioStations", L"MaxRadioStations", 255, 255);  // a station index is a byte elsewhere
        if (stations > 96) RaiseRadioStations(stations);
    }
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(module);
        Run(module);
    }
    return TRUE;
}
