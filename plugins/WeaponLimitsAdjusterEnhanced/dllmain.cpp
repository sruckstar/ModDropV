// WeaponLimitsAdjusterEnhanced — raises GTA V Enhanced's limit on weapon components (weaponcomponents.meta).
//
// The game keeps every CWeaponComponentInfo in a pool and a static array of pointers, both sized 470 in
// GTA5_Enhanced.exe (build 1158) — only a few more than the game's own. Past that it stops with a fatal error
// while loading. This plugin gives the pool the size from the .ini and moves the pointer array to a bigger block.
//
// A port of WeaponLimitsAdjuster by alexguirre (MIT, https://github.com/alexguirre/gtav-WeaponLimitsAdjuster),
// whose component-array relocation comes from FiveM (PatchWeaponLimits.cpp, CitizenFX Collective). Enhanced is
// built by another compiler, so instead of one pattern per use of the array this finds the array once and then
// every `lea reg, [rip + array]` that points at it.

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

    // "BA CF ? E3" — hex bytes, ? for any byte.
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

    // From WeaponLimitsAdjuster (alexguirre, MIT): a block within ±2 GB of the exe, so rip-relative code can reach it.
    void* FindPrevFreeRegion(void* address, void* minAddr, DWORD granularity)
    {
        ULONG_PTR tryAddr = (ULONG_PTR)address;
        tryAddr -= tryAddr % granularity;
        tryAddr -= granularity;
        while (tryAddr >= (ULONG_PTR)minAddr)
        {
            MEMORY_BASIC_INFORMATION mbi;
            if (VirtualQuery((void*)tryAddr, &mbi, sizeof(mbi)) == 0) break;
            if (mbi.State == MEM_FREE) return (void*)tryAddr;
            if ((ULONG_PTR)mbi.AllocationBase < granularity) break;
            tryAddr = (ULONG_PTR)mbi.AllocationBase - granularity;
        }
        return nullptr;
    }

    void* AllocateNearExe(size_t size)
    {
        const ULONG_PTR range = 0x40000000;   // 1 GB below the exe, which itself is < 100 MB
        auto origin = (ULONG_PTR)GetModuleHandleW(nullptr);
        SYSTEM_INFO si;
        GetSystemInfo(&si);
        ULONG_PTR minAddr = (ULONG_PTR)si.lpMinimumApplicationAddress;
        if (origin > range && minAddr < origin - range) minAddr = origin - range;

        void* at = (void*)origin;
        while ((ULONG_PTR)at >= minAddr)
        {
            at = FindPrevFreeRegion(at, (void*)minAddr, si.dwAllocationGranularity);
            if (!at) break;
            if (void* block = VirtualAlloc(at, size, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE)) return block;
        }
        return nullptr;
    }

    // Pool: `mov edx, hash("CWeaponComponentInfo"); mov r8d, 470; call fwConfigManager::GetSizeOfPool`
    // — the call becomes `mov eax, size`, as in the Legacy plugin.
    bool AdjustPool(int size)
    {
        auto hits = Find("BA CF 3C E3 BA 41 B8 ? ? ? ? E8");
        if (hits.size() != 1)
        {
            auto patched = Find("BA CF 3C E3 BA 41 B8 ? ? ? ? B8");
            if (patched.size() == 1)
                Log("Pool: already changed by another plugin (%d) - is another WeaponLimitsAdjuster installed?", *(int*)(patched[0] + 12));
            else
                Log("Pool: not found (%zu matches) - a game version this plugin doesn't know.", hits.size());
            return false;
        }
        int original = *(int*)(hits[0] + 7);
        uint8_t code[5] = { 0xB8 };
        memcpy(code + 1, &size, 4);
        Write(hits[0] + 11, code, sizeof code);
        Log("Pool: CWeaponComponentInfo %d -> %d", original, size);
        return true;
    }

    // Array: the loop that fills it is
    //   movsxd rdx, [rip + count]; lea ecx, [rdx + 1]; mov [rip + count], ecx; mov [r10 + rdx*8], r8
    // and the count sits right after the array, so the array is the `lea r10, [rip + X]` just before the loop.
    bool AdjustArray(int size)
    {
        auto hits = Find("48 63 15 ? ? ? ? 8D 4A 01 89 0D ? ? ? ? 4D 89 04 D2");
        if (hits.size() != 1)
        {
            Log("Array: fill loop not found (%zu matches) - a game version this plugin doesn't know.", hits.size());
            return false;
        }
        uint8_t* count = hits[0] + 7 + *(int32_t*)(hits[0] + 3);

        uint8_t* array = nullptr;
        for (uint8_t* p = hits[0] - 7; p > hits[0] - 0x100 && !array; p--)
            if (p[0] == 0x4C && p[1] == 0x8D && p[2] == 0x15)
            {
                uint8_t* target = p + 7 + *(int32_t*)(p + 3);
                if (target < count && count - target <= 0x10000 && (count - target) % 8 == 0) array = target;
            }
        if (!array)
        {
            Log("Array: not found next to the count - already moved by another plugin, or an unknown game version.");
            return false;
        }

        int capacity = (int)((count - array) / 8);
        if (size <= capacity)
        {
            Log("Array: the game already has room for %d, left as is.", capacity);
            return true;
        }

        // every `lea r64, [rip + array]` (REX.W 8D, mod 00 rm 101) in the exe's code
        std::vector<uint8_t*> uses;
        for (auto& r : CodeSections())
            for (uint8_t* p = r.begin; p + 7 <= r.end; p++)
                if ((p[0] == 0x48 || p[0] == 0x4C) && p[1] == 0x8D && (p[2] & 0xC7) == 0x05 && p + 7 + *(int32_t*)(p + 3) == array)
                    uses.push_back(p);

        auto moved = (uint8_t*)AllocateNearExe(sizeof(void*) * size);
        if (!moved)
        {
            Log("Array: no memory near the game's code.");
            return false;
        }
        memcpy(moved, array, sizeof(void*) * capacity);   // nothing is loaded yet, but keep what's there

        for (auto p : uses)
        {
            int32_t disp = (int32_t)(moved - (p + 7));
            Write(p + 3, &disp, sizeof disp);
        }
        Log("Array: CWeaponComponentInfo %d -> %d, %zu uses moved (build 1158 has 17).", capacity, size, uses.size());
        return !uses.empty();
    }

    int ReadSize()
    {
        std::wstring ini = std::wstring(g_dir) + L"WeaponLimitsAdjusterEnhanced.ini";
        int size = GetPrivateProfileIntW(L"WeaponLimitsAdjuster", L"CWeaponComponentInfo", -1, ini.c_str());
        return size <= 0 ? 1024 : size;
    }

    void Run(HMODULE self)
    {
        GetModuleFileNameW(self, g_dir, MAX_PATH);
        if (auto slash = wcsrchr(g_dir, L'\\')) slash[1] = 0;
        g_log = _wfopen((std::wstring(g_dir) + L"WeaponLimitsAdjusterEnhanced.log").c_str(), L"w");
        Log("WeaponLimitsAdjusterEnhanced 1.0");

        wchar_t exe[MAX_PATH];
        GetModuleFileNameW(nullptr, exe, MAX_PATH);
        auto name = wcsrchr(exe, L'\\');
        if (!name || _wcsicmp(name + 1, L"GTA5_Enhanced.exe") != 0)
        {
            Log("Not GTA5_Enhanced.exe - nothing to do (for GTA V Legacy use WeaponLimitsAdjuster by alexguirre).");
            return;
        }

        int size = ReadSize();
        // The pool only grows together with the array: a bigger pool with the old array would overrun it.
        if (AdjustArray(size)) AdjustPool(size);
        else Log("Nothing changed.");
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
