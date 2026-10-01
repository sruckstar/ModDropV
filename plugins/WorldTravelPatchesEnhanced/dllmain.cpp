// WorldTravelPatchesEnhanced — raises two of GTA V Enhanced's hardcoded limits that Liberty City Preservation
// Project (LCPP) runs into: the result list of fwBoxStreamerVariable and the number of decal definitions.
//
// A port of the limit patches of WorldTravelPatches.asi (World Travel, GPL-3.0,
// https://github.com/Splatcrafter/worldTravelASI) to GTA5_Enhanced.exe. That plugin knows only GTA V Legacy; in
// the build LCPP ships only these limits are actually applied on Legacy, and LCPP works there with them.
// Enhanced is built by another compiler, so instead of one pattern per use this finds each array once and then
// every `lea reg, [rip + array]` that points at it (clang loads the address again after loops).
//
//  - fwBoxStreamerVariable: GetIntersectingAABB / GetIntersectingLine collect up to 1000 map-data entries into a
//    static list (Legacy plugin: 6000). With LC's thousands of map files the list is too short.
//  - Decal definitions (fxdecal, DECAL_DEF_START): a static array of 260 entries of 80 bytes
//    (Legacy has room for fewer than 620, the plugin's value).

#include <Windows.h>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

namespace wtpe
{
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

    // The game's image: its code and its function table (.pdata), which gives the bounds of a function.
    struct Image
    {
        uint8_t* base = nullptr;
        std::vector<Range> code;
        RUNTIME_FUNCTION* functions = nullptr;
        size_t functionCount = 0;

        explicit Image(uint8_t* at) : base(at)
        {
            auto nt = (IMAGE_NT_HEADERS*)(base + ((IMAGE_DOS_HEADER*)base)->e_lfanew);
            auto section = IMAGE_FIRST_SECTION(nt);
            for (int i = 0; i < nt->FileHeader.NumberOfSections; i++, section++)
                if (section->Characteristics & IMAGE_SCN_MEM_EXECUTE)
                    code.push_back({ base + section->VirtualAddress, base + section->VirtualAddress + section->Misc.VirtualSize });
            auto& dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXCEPTION];
            functions = (RUNTIME_FUNCTION*)(base + dir.VirtualAddress);
            functionCount = dir.Size / sizeof(RUNTIME_FUNCTION);
        }

        uint32_t Rva(const uint8_t* p) const { return (uint32_t)(p - base); }

        // Bounds of the function holding p (the table is sorted by start address).
        bool FunctionOf(const uint8_t* p, Range& out) const
        {
            uint32_t rva = Rva(p);
            size_t lo = 0, hi = functionCount;
            while (lo < hi)
            {
                size_t mid = (lo + hi) / 2;
                if (functions[mid].BeginAddress <= rva) lo = mid + 1; else hi = mid;
            }
            if (lo == 0) return false;
            auto& f = functions[lo - 1];
            if (rva >= f.EndAddress) return false;
            out = { base + f.BeginAddress, base + f.EndAddress };
            return true;
        }

        // "BA CF ? E3" — hex bytes, ? for any byte.
        std::vector<uint8_t*> Find(const char* pattern) const
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
            for (auto& r : code)
                for (uint8_t* p = r.begin; p + bytes.size() <= r.end; p++)
                {
                    size_t i = 0;
                    while (i < bytes.size() && (bytes[i] < 0 || p[i] == bytes[i])) i++;
                    if (i == bytes.size()) hits.push_back(p);
                }
            return hits;
        }

        // Every `lea r64, [rip + target]` (REX.W 8D, mod 00 rm 101).
        std::vector<uint8_t*> LeaUses(const uint8_t* target) const
        {
            std::vector<uint8_t*> uses;
            for (auto& r : code)
                for (uint8_t* p = r.begin; p + 7 <= r.end; p++)
                    if ((p[0] == 0x48 || p[0] == 0x4C) && p[1] == 0x8D && (p[2] & 0xC7) == 0x05 && p + 7 + *(int32_t*)(p + 3) == target)
                        uses.push_back(p);
            return uses;
        }

        // Functions with a rip-relative operand aimed at target: a 32-bit displacement that lands on it from the end
        // of the instruction, with 0, 1 or 4 bytes of immediate after it. Only a guess per byte, so it is used to
        // widen a set of functions that is then checked by what is patched in them.
        std::vector<Range> FunctionsReferring(const uint8_t* target) const
        {
            std::vector<Range> found;
            for (auto& r : code)
                for (uint8_t* p = r.begin; p + 4 <= r.end; p++)
                {
                    int32_t disp = *(int32_t*)p;
                    for (int tail : { 0, 1, 4 })
                        if (p + 4 + tail + disp == target)
                        {
                            Range f;
                            if (FunctionOf(p, f)) found.push_back(f);
                        }
                }
            return found;
        }
    };

    void Write(void* at, const void* data, size_t size)
    {
        DWORD old;
        VirtualProtect(at, size, PAGE_EXECUTE_READWRITE, &old);
        memcpy(at, data, size);
        VirtualProtect(at, size, old, &old);
        FlushInstructionCache(GetCurrentProcess(), at, size);
    }

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

    // A block within 1 GB below the image, so rip-relative code can reach it.
    uint8_t* AllocateNear(const Image& image, size_t size)
    {
        const ULONG_PTR range = 0x40000000;
        auto origin = (ULONG_PTR)image.base;
        SYSTEM_INFO si;
        GetSystemInfo(&si);
        ULONG_PTR minAddr = (ULONG_PTR)si.lpMinimumApplicationAddress;
        if (origin > range && minAddr < origin - range) minAddr = origin - range;

        void* at = (void*)origin;
        while ((ULONG_PTR)at >= minAddr)
        {
            at = FindPrevFreeRegion(at, (void*)minAddr, si.dwAllocationGranularity);
            if (!at) break;
            if (void* block = VirtualAlloc(at, size, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE)) return (uint8_t*)block;
        }
        return nullptr;
    }

    // Points every use at the new block.
    void Retarget(const std::vector<uint8_t*>& uses, uint8_t* moved)
    {
        for (auto p : uses)
        {
            int32_t disp = (int32_t)(moved - (p + 7));
            Write(p + 3, &disp, sizeof disp);
        }
    }

    // fwBoxStreamerVariable::GetIntersectingAABB / GetIntersectingLine and their two non-variable siblings:
    //   mov rcx, [rax + 8]; mov dword [rsp + 20h], 1000; lea r13, [rip + list]; ... call query(bvh, ..., list, max)
    // All four share the list; each passes the size once as that stack argument.
    bool AdjustBoxStreamer(const Image& image, int size)
    {
        auto hits = image.Find("48 8B 48 08 C7 44 24 20 ? ? 00 00 4C 8D 2D");
        if (hits.empty())
        {
            Log("BoxStreamer: not found - a game version this plugin doesn't know.");
            return false;
        }
        int capacity = *(int32_t*)(hits[0] + 8);
        uint8_t* list = hits[0] + 19 + *(int32_t*)(hits[0] + 15);
        for (auto h : hits)
            if (*(int32_t*)(h + 8) != capacity || h + 19 + *(int32_t*)(h + 15) != list)
            {
                Log("BoxStreamer: the %zu matches disagree - already changed by another plugin, or an unknown game version.", hits.size());
                return false;
            }
        if (size <= capacity)
        {
            Log("BoxStreamer: the game already has room for %d, left as is.", capacity);
            return true;
        }

        auto uses = image.LeaUses(list);

        // the size argument in every function that uses the list
        const uint8_t sizeArg[4] = { 0xC7, 0x44, 0x24, 0x20 };
        std::vector<uint8_t*> sizes;
        std::vector<uint8_t*> seen;
        for (auto p : uses)
        {
            Range f;
            if (!image.FunctionOf(p, f)) continue;
            bool known = false;
            for (auto s : seen) known |= s == f.begin;
            if (known) continue;
            seen.push_back(f.begin);
            for (uint8_t* q = f.begin; q + 8 <= f.end; q++)
                if (memcmp(q, sizeArg, 4) == 0 && *(int32_t*)(q + 4) == capacity)
                    sizes.push_back(q + 4);
        }
        if (sizes.size() != seen.size())
        {
            Log("BoxStreamer: %zu functions use the list but %zu size arguments found - unknown game version, left as is.", seen.size(), sizes.size());
            return false;
        }

        auto moved = AllocateNear(image, sizeof(void*) * size);
        if (!moved)
        {
            Log("BoxStreamer: no memory near the game's code.");
            return false;
        }
        Retarget(uses, moved);
        for (auto s : sizes) Write(s, &size, sizeof size);
        Log("BoxStreamer: fwBoxStreamerVariable list %d -> %d, %zu uses moved, %zu sizes (build 1158: 8 and 4).",
            capacity, size, uses.size(), sizes.size());
        return true;
    }

    // Decal definitions. The lookup by name is
    //   test ecx, ecx; je; mov dword [rdx], capacity; mov dword [r8], 0; mov r11d, [rip + count]; test r11d, r11d; je;
    //   xor r9d, r9d; lea r10, [rip + array]
    // (count sits 16 bytes before the array). The capacity and capacity - 1 are compared in the functions that use
    // the array or the count; they are found as the immediate of the instruction forms the game uses for it.
    bool IsLimitImmediate(const uint8_t* imm)
    {
        uint8_t b1 = imm[-1], b2 = imm[-2];
        if (b1 == 0x3D) return true;                                   // cmp eax, imm
        if (b2 == 0x81 && (b1 & 0xF8) == 0xF8) return true;           // cmp r32/r64, imm (with or without REX)
        if (b2 == 0x81 && b1 == 0x3A) return true;                    // cmp dword [rdx], imm
        if (b2 == 0xC7 && b1 == 0x02) return true;                    // mov dword [rdx], imm
        if ((b1 & 0xF8) == 0xB8 && (b2 == 0x41 || b2 == 0x40)) return true;   // mov r8d..r15d, imm
        return false;
    }

    bool AdjustDecals(const Image& image, int size)
    {
        auto hits = image.Find("85 C9 74 ? C7 02 ? ? 00 00 41 C7 00 00 00 00 00 44 8B 1D ? ? ? ? 45 85 DB 74 ? 45 31 C9 4C 8D 15");
        if (hits.size() != 1)
        {
            Log("Decals: lookup not found (%zu matches) - already changed by another plugin, or an unknown game version.", hits.size());
            return false;
        }
        uint8_t* h = hits[0];
        int capacity = *(int32_t*)(h + 6);
        uint8_t* count = h + 24 + *(int32_t*)(h + 20);
        uint8_t* array = h + 39 + *(int32_t*)(h + 35);
        if (array - count != 16)
        {
            Log("Decals: the count isn't next to the array - unknown game version.");
            return false;
        }
        if (size <= capacity)
        {
            Log("Decals: the game already has room for %d, left as is.", capacity);
            return true;
        }

        auto uses = image.LeaUses(array);

        std::vector<Range> functions = image.FunctionsReferring(count);
        for (auto p : uses)
        {
            Range f;
            if (image.FunctionOf(p, f)) functions.push_back(f);
        }
        std::vector<uint8_t*> limits, below;   // the capacity and capacity - 1
        for (auto& f : functions)
            for (uint8_t* q = f.begin + 2; q + 4 <= f.end; q++)
            {
                int32_t v = *(int32_t*)q;
                if ((v != capacity && v != capacity - 1) || !IsLimitImmediate(q)) continue;
                auto& list = v == capacity ? limits : below;
                bool known = false;
                for (auto x : list) known |= x == q;
                if (!known) list.push_back(q);
            }

        const size_t entry = 80;
        auto moved = AllocateNear(image, entry * size);
        if (!moved)
        {
            Log("Decals: no memory near the game's code.");
            return false;
        }
        memcpy(moved, array, entry * capacity);   // loaded later, but keep what's there

        Retarget(uses, moved);
        int last = size - 1;
        for (auto q : limits) Write(q, &size, sizeof size);
        for (auto q : below) Write(q, &last, sizeof last);
        Log("Decals: decal definitions %d -> %d, %zu uses moved, %zu + %zu limits (build 1158: 31 and 8 + 12).",
            capacity, size, uses.size(), limits.size(), below.size());
        for (auto q : limits) Log("  limit %d at +%X", capacity, image.Rva(q));
        for (auto q : below) Log("  limit %d at +%X", capacity - 1, image.Rva(q));
        return true;
    }

    struct Settings { int boxStreamer = 6000; int decals = 620; };

    Settings ReadSettings(const std::wstring& ini)
    {
        Settings s;
        int v = GetPrivateProfileIntW(L"Limits", L"BoxStreamerVariable", -1, ini.c_str());
        if (v > 0) s.boxStreamer = v;
        v = GetPrivateProfileIntW(L"Limits", L"DecalDefinitions", -1, ini.c_str());
        if (v > 0) s.decals = v;
        return s;
    }

    void Patch(const Image& image, const Settings& s)
    {
        AdjustBoxStreamer(image, s.boxStreamer);
        AdjustDecals(image, s.decals);
    }
}

#ifndef WTPE_NO_DLLMAIN
BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    using namespace wtpe;
    if (reason != DLL_PROCESS_ATTACH) return TRUE;
    DisableThreadLibraryCalls(module);

    wchar_t dir[MAX_PATH];
    GetModuleFileNameW(module, dir, MAX_PATH);
    if (auto slash = wcsrchr(dir, L'\\')) slash[1] = 0;
    g_log = _wfopen((std::wstring(dir) + L"WorldTravelPatchesEnhanced.log").c_str(), L"w");
    Log("WorldTravelPatchesEnhanced 1.0");

    wchar_t exe[MAX_PATH];
    GetModuleFileNameW(nullptr, exe, MAX_PATH);
    auto name = wcsrchr(exe, L'\\');
    if (!name || _wcsicmp(name + 1, L"GTA5_Enhanced.exe") != 0)
    {
        Log("Not GTA5_Enhanced.exe - nothing to do (for GTA V Legacy use WorldTravelPatches.asi).");
        return TRUE;
    }

    Patch(Image((uint8_t*)GetModuleHandleW(nullptr)), ReadSettings(std::wstring(dir) + L"WorldTravelPatchesEnhanced.ini"));
    return TRUE;
}
#endif
