// OnigiriDlcPatch — lets Onigiri users replace the files of update.rpf\dlc_patch, the title update's patches of DLC packs.
//
// Onigiri (onigiri.asi, the loose-file loader of NaturalVision Enhanced) mounts <game>\onigiri as onigiri:/ and lays its
// common, platform and dlcpacks folders over common:/, platform:/ and dlcpacks:/. update.rpf\dlc_patch has no place there:
// the game mounts each folder of it over its pack's device, from the list in update:/common/data/ExtraTitleUpdateData.meta
// (dlcMPBeach:/ <- update:/dlc_patch/mpBeach/), which it reads straight from update.rpf. A file there wins over the pack's
// own, so a mod's copy in onigiri\dlcpacks never shows.
//
// This plugin adds onigiri\dlc_patch\<folder>\ (folder = the one in the game's list) on top: right after the game mounts a
// pack and its patch, the folder is mounted over the same device — last mounted is looked at first, a file the folder
// doesn't have still comes from the patch or the pack. Like the game, it also mounts it over the pack's CRC device
// (dlcMPBeachCRC:/), which reads the patch directly.
//
// GTA5_Enhanced.exe build 1158, all found by pattern:
//   MountContent: ...; mov rcx, rsi; mov rdx, r14; call MountPatches; jmp     (the one call of MountPatches)
//   MountPatches: mov rsi, [rip + titleUpdateMounts]; ...; mov ecx, 0x118; call alloc; lea rcx, [rip + fiDeviceRelative];
//                 ...; call fiDeviceRelative::Init(dev, path, readOnly, parent); ...; call fiDeviceRelative::MountAs(dev, name)
//   MountCrc:     mov ecx, 0x120; call alloc; ...; lea rax, [rip + crc device vtable]
//
// update:/ (1.1). The game mounts update\update.rpf as update:/ and reads some of it straight from there — the archives
// its content.xml lists (x64\data\cdimages\scaleform_generic.rpf with the HUD, paths.rpf, ...) and data files such as
// common\data\ExtraTitleUpdateData.meta — past Onigiri, which lays onigiri\platform and onigiri\common only over
// platform:/ and common:/. So right after that mount <game>\onigiri\update\ is mounted over update:/ as well: a file
// there wins, any other still comes from update.rpf.
//   MountUpdate:  mov rcx, r15; mov r8b, 1; xor r9d, r9d; call fiPackfile::Init("update/update.rpf"); test al, al; je;
//                 lea rdx, "update:/"; mov rcx, r15; call fiPackfile::MountAs; mov ebx, eax

#include <Windows.h>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

namespace
{
    FILE* g_log;
    std::wstring g_onigiri;   // <game>\onigiri\dlc_patch\ (with the slash)
    std::string g_update;     // <game>/onigiri/update/

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

    // Target of a rel32 at `at` whose instruction ends at `end`.
    uint8_t* Rel(uint8_t* at, uint8_t* end) { int32_t v; memcpy(&v, at, 4); return end + v; }

    void Write(void* at, const void* data, size_t size)
    {
        DWORD old;
        VirtualProtect(at, size, PAGE_EXECUTE_READWRITE, &old);
        memcpy(at, data, size);
        VirtualProtect(at, size, old, &old);
        FlushInstructionCache(GetCurrentProcess(), at, size);
    }

    // A block within 2 GB of `close`, for a jump a rel32 call can reach.
    uint8_t* AllocateNear(uint8_t* close, size_t size)
    {
        SYSTEM_INFO si;
        GetSystemInfo(&si);
        const ULONG_PTR range = 0x7FF00000, step = si.dwAllocationGranularity;
        auto origin = (ULONG_PTR)close - (ULONG_PTR)close % step;
        for (ULONG_PTR d = step; d < range; d += step)
            for (ULONG_PTR at : { origin - d, origin + d })
            {
                MEMORY_BASIC_INFORMATION mbi;
                if (VirtualQuery((void*)at, &mbi, sizeof mbi) == 0 || mbi.State != MEM_FREE) continue;
                if (void* block = VirtualAlloc((void*)at, size, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE))
                    return (uint8_t*)block;
            }
        return nullptr;
    }

    // rage::atString / atArray as the game lays them out.
    struct AtString { const char* data; uint16_t length; uint16_t capacity; uint32_t pad; };
    struct TitleUpdateMount { AtString deviceName; AtString path; };   // SExtraTitleUpdateMount
    struct MountList { TitleUpdateMount* items; uint16_t count; uint16_t capacity; };

    using MountPatchesFn = uint64_t(*)(void* content, const char* deviceName);
    using AllocFn = void* (*)(size_t size);
    using InitFn = void(*)(void* device, const char* path, bool readOnly, void* parent);
    using MountAsFn = bool(*)(void* device, const char* name);
    using MountPackfileFn = bool(*)(void* packfile, const char* name);

    MountPatchesFn g_mountPatches;
    MountList* g_mounts;
    AllocFn g_alloc;
    InitFn g_init;
    MountAsFn g_mountAs;
    void* g_relativeVtable;
    void* g_crcVtable;
    MountPackfileFn g_mountUpdate;

    const char* Text(const AtString& s) { return s.length && s.data ? s.data : ""; }

    // The folder of update:/dlc_patch/ the game patches a device from, or "".
    std::string PatchFolder(const char* deviceName)
    {
        static const char prefix[] = "update:/dlc_patch/";
        for (uint16_t i = 0; i < g_mounts->count; i++)
        {
            auto& m = g_mounts->items[i];
            if (_stricmp(Text(m.deviceName), deviceName) != 0) continue;
            std::string path = Text(m.path);
            if (_strnicmp(path.c_str(), prefix, sizeof prefix - 1) != 0) return {};
            path = path.substr(sizeof prefix - 1);
            while (!path.empty() && path.back() == '/') path.pop_back();
            return path;
        }
        return {};
    }

    void* NewDevice(size_t size, void* vtable)
    {
        auto device = (uint8_t*)g_alloc(size);
        memset(device, 0, size);
        *(void**)device = vtable;
        return device;
    }

    void MountFolder(const char* deviceName)
    {
        if (!g_mounts->items || !deviceName) return;
        auto folder = PatchFolder(deviceName);
        if (folder.empty()) return;
        auto dir = g_onigiri + std::wstring(folder.begin(), folder.end());
        DWORD attributes = GetFileAttributesW(dir.c_str());
        if (attributes == INVALID_FILE_ATTRIBUTES || !(attributes & FILE_ATTRIBUTE_DIRECTORY)) return;

        auto path = "onigiri:/dlc_patch/" + folder + "/";
        auto device = NewDevice(0x118, g_relativeVtable);
        g_init(device, path.c_str(), true, nullptr);
        if (!g_mountAs(device, deviceName))
        {
            Log("%s: onigiri\\dlc_patch\\%s not mounted - is Onigiri installed?", deviceName, folder.c_str());
            return;
        }

        std::string name = deviceName;
        name = name.substr(0, name.find(':')) + "CRC:/";
        auto crc = NewDevice(0x120, g_crcVtable);
        g_init(crc, deviceName, true, device);
        bool crcMounted = g_mountAs(crc, name.c_str());
        Log("%s <- onigiri\\dlc_patch\\%s%s", deviceName, folder.c_str(), crcMounted ? "" : " (CRC device not mounted)");
    }

    uint64_t MountPatches(void* content, const char* deviceName)
    {
        uint64_t result = g_mountPatches(content, deviceName);
        MountFolder(deviceName);
        return result;
    }

    // onigiri\update answers for files only. The game picks a relative device's parent once, at Init, by looking the folder
    // up (GetDevice: the last mounted device whose GetAttributes(path) isn't -1) — platform:/ over update:/x64/ and others.
    // Had our folder answered for x64\ (it does once a mod puts a file there), that device would read through ours
    // alone, and every other file under it would be gone (first 1.1 build: a pack's patched content.xml went missing, the
    // game asserted on an archive only the unpatched one lists).
    using GetAttributesFn = uint32_t(*)(void* device, const char* path);
    GetAttributesFn g_relativeAttributes;
    void* g_filesOnlyTable[1 + 96];                         // the slot before a vtable (type info) comes along
    void** const g_filesOnlyVtable = g_filesOnlyTable + 1;

    uint32_t FilesOnlyAttributes(void* device, const char* path)
    {
        uint32_t attributes = g_relativeAttributes(device, path);
        return attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY) ? INVALID_FILE_ATTRIBUTES : attributes;
    }

    bool MountUpdate(void* packfile, const char* name)
    {
        bool mounted = g_mountUpdate(packfile, name);
        DWORD attributes = GetFileAttributesA(g_update.c_str());
        if (!mounted || attributes == INVALID_FILE_ATTRIBUTES || !(attributes & FILE_ATTRIBUTE_DIRECTORY)) return mounted;
        if (!g_relativeAttributes)
        {
            memcpy(g_filesOnlyTable, (void**)g_relativeVtable - 1, sizeof g_filesOnlyTable);
            g_relativeAttributes = (GetAttributesFn)g_filesOnlyVtable[0xF8 / 8];
            g_filesOnlyVtable[0xF8 / 8] = (void*)&FilesOnlyAttributes;
        }
        auto device = NewDevice(0x118, g_filesOnlyVtable);
        g_init(device, g_update.c_str(), true, nullptr);   // parent: the device of that path, the disk
        Log(g_mountAs(device, name) ? "%s <- onigiri\\update" : "%s: onigiri\\update not mounted", name);
        return mounted;
    }

    // Points the rel32 call at `call` to `target` through a jump placed near the game's code; the old target, or null.
    uint8_t* Redirect(uint8_t* call, void* target)
    {
        auto old = Rel(call + 1, call + 5);   // the game's, or another plugin's hook of it
        auto stub = AllocateNear(call, 64);
        if (!stub) return nullptr;
        const uint8_t jump[6] = { 0xFF, 0x25, 0, 0, 0, 0 };   // jmp [rip + 0]
        memcpy(stub, jump, sizeof jump);
        memcpy(stub + sizeof jump, &target, sizeof target);
        int32_t rel = (int32_t)(stub - (call + 5));
        Write(call + 1, &rel, sizeof rel);
        return old;
    }

    bool Install()
    {
        auto calls = Find("48 89 F1 4C 89 F2 E8 ? ? ? ? E9 ? ? ? ? 48 8B 0E 48 85 C9 74 0A");
        auto lists = Find("48 8B 35 ? ? ? ? 48 85 F6 0F 84 ? ? ? ? B9 18 01 00 00 E8 ? ? ? ? 48 8D 0D ? ? ? ? 48 89 08 "
                          "48 C7 40 08 00 00 00 00 49 89 46 18 66 83 7C 1E 08 00 74 06 48 8B 14 1E EB 07 48 8D 15 ? ? ? ? "
                          "48 89 C1 41 B0 01 45 31 C9 E8 ? ? ? ? 49 8B 4E 18 4C 89 E2 E8");
        auto crcs = Find("B9 20 01 00 00 E8 ? ? ? ? 48 89 C1 48 C7 40 08 00 00 00 00 48 8D 05 ? ? ? ? 48 89 01 "
                         "48 C7 81 18 01 00 00 00 00 00 00");
        if (calls.size() != 1 || lists.size() != 1 || crcs.size() != 1)
        {
            Log("Not found (%zu / %zu / %zu matches) - a game version this plugin doesn't know.", calls.size(), lists.size(), crcs.size());
            return false;
        }

        uint8_t* l = lists[0];
        g_mounts = (MountList*)Rel(l + 3, l + 7);
        g_alloc = (AllocFn)Rel(l + 22, l + 26);
        g_relativeVtable = Rel(l + 29, l + 33);
        g_init = (InitFn)Rel(l + 79, l + 83);
        g_mountAs = (MountAsFn)Rel(l + 91, l + 95);
        g_crcVtable = Rel(crcs[0] + 24, crcs[0] + 28);

        g_mountPatches = (MountPatchesFn)Redirect(calls[0] + 6, (void*)&MountPatches);
        if (!g_mountPatches)
        {
            Log("No memory near the game's code.");
            return false;
        }
        Log("Hooked: DLC patches get onigiri\\dlc_patch\\<folder> on top.");

        auto updates = Find("4C 89 F9 41 B0 01 45 31 C9 E8 ? ? ? ? 84 C0 74 ? 48 8D 15 ? ? ? ? 4C 89 F9 E8 ? ? ? ? 89 C3");
        if (updates.size() != 1)
            Log("update:/ mount not found (%zu matches) - onigiri\\update is not used.", updates.size());
        else if ((g_mountUpdate = (MountPackfileFn)Redirect(updates[0] + 28, (void*)&MountUpdate)))
            Log("Hooked: update:/ gets onigiri\\update on top.");
        return true;
    }

    void Run(HMODULE self)
    {
        wchar_t dir[MAX_PATH];
        GetModuleFileNameW(self, dir, MAX_PATH);
        if (auto slash = wcsrchr(dir, L'\\')) slash[1] = 0;
        g_log = _wfopen((std::wstring(dir) + L"OnigiriDlcPatch.log").c_str(), L"w");
        Log("OnigiriDlcPatch 1.1.1");

        wchar_t exe[MAX_PATH];
        GetModuleFileNameW(nullptr, exe, MAX_PATH);
        auto name = wcsrchr(exe, L'\\');
        if (!name || _wcsicmp(name + 1, L"GTA5_Enhanced.exe") != 0)
        {
            Log("Not GTA5_Enhanced.exe - nothing to do (Onigiri is for GTA V Enhanced).");
            return;
        }
        g_onigiri = std::wstring(exe, name + 1) + L"onigiri\\dlc_patch\\";
        char exeA[MAX_PATH];
        GetModuleFileNameA(nullptr, exeA, MAX_PATH);
        if (auto slash = strrchr(exeA, '\\')) slash[1] = 0;
        g_update = std::string(exeA) + "onigiri/update/";
        for (auto& c : g_update) if (c == '\\') c = '/';
        Install();
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
