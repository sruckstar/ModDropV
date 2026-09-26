<p align="center">
  <img src="src/Mdv.App/Assets/moddropv.png" alt="ModDrop V" width="112">
</p>

<h1 align="center">ModDrop V</h1>

<p align="center">
  <b>Drop a GTA V mod in. It gets installed — the right way.</b><br>
  A universal mod installer for players and an Add-On packaging kit for modders.
</p>

<p align="center">
  <img alt="Windows 10/11 x64" src="https://img.shields.io/badge/Windows-10%20%2F%2011%20x64-1B222C?style=for-the-badge&labelColor=0F1319">
  <img alt="GTA V Legacy" src="https://img.shields.io/badge/GTA%20V-Legacy-4DA3FF?style=for-the-badge&labelColor=0F1319">
  <img alt="GTA V Enhanced" src="https://img.shields.io/badge/GTA%20V-Enhanced-4DA3FF?style=for-the-badge&labelColor=0F1319">
  <img alt="Nothing to install" src="https://img.shields.io/badge/.NET-not%20required-1B222C?style=for-the-badge&labelColor=0F1319">
</p>

---

> **Status: early development.** ModDrop V grows out of
> [AddonWeapons Builder](https://github.com/sruckstar/AddonWeaponsBuilder) — its engine, installer and add-on weapon
> support are already here and work. Everything else below is on the way.

## 🎮 For players

Drag the mod into the window exactly as you downloaded it — folder, `.zip`, `.rar`, `.7z` or `.oiv`.
ModDrop V works out what it is and installs it through the `mods` folder, so your original game files
stay untouched and every install can be switched off or removed again.

| Mod type | Status |
|---|---|
| Add-on weapons (replace mods become real add-on weapons) | ✅ ready |
| OIV packages — installed into the `mods` folder, removable | ✅ ready |
| File replacements (textures, models, sounds, metas…) — the right place is found in the game | ✅ ready |
| Scripts and plugins — `.asi`, ScriptHookVDotNet, RAGE Plugin Hook / LSPDFR; missing ScriptHookV, SHVDN or libraries are pointed out, LemonUI is added for you | ✅ ready |
| Add-on vehicles and peds — finished packs, FiveM resources (packed into a `dlc.rpf` for you), peds shared as bare models (a `peds.meta` is written for them) and replacements, with a 3D preview; clashing spawn names and modkit ids are caught before installing, Legacy models are converted for Enhanced | ✅ ready |
| Vehicle liveries — pictures (PNG / JPG / DDS) put into a car's own textures (compressed to the game's format, the car guessed from the texture names), whole texture dictionaries, modkit liveries added to the car's modkit; for the game's cars and installed add-ons, with a 3D preview | ✅ ready |
| Clothes — MP clothes packs (sub-packs included) and FiveM clothing packed as Rockstar's own DLCs lay them out, loose models as a collection of their own for the MP male / female (its .ymt written), replacements for Michael, Franklin, Trevor and the MP peds; models a character has no slot for get new slots in their .ymt | ✅ ready |
| Maps and props — finished map packs, FiveM maps and loose `.ymap` / `.ytyp` files (binary or CodeWalker XML) packed as Rockstar lays its map DLCs out, props spawnable by name; Menyoo maps into `menyooStuff\Spooner`, Map Editor maps into `scripts\AutoloadMaps` (the tool checked); the game files and scripts a map's readme asks for go in with it; models the game lacks are pointed out | ✅ ready |
| Big packs and total conversions — the space they take (archive copies in mods) shown before the install, every step as it goes, cancel any time with everything taken back; a mod can be put on top of others that change the same files | ✅ ready |

LemonUI ships with ModDrop V under its MIT licence (`data/dependencies`); ScriptHookV, ScriptHookVDotNet and
RAGE Plugin Hook are never bundled — ModDrop V links to their official pages.

Coming from AddonWeapons Builder? The weapons it installed show up in ModDrop V, and new weapons keep going
into the same `AddonWeapons` pack.

## 🛠️ For modders

Switch to **For Modders** to build a complete Add-On DLC (`dlc.rpf` or loose folders) from your files —
for GTA V Legacy or Enhanced.

| Add-On type | Status |
|---|---|
| Weapons | ✅ ready |
| Vehicles | planned |
| Peds | planned |
| Props | planned |
| MP clothing | planned |

A command-line tool, `mdvctl`, ships next to the app for scripting and batch builds.

## 🚀 Building from source

You need the .NET 10 SDK. CodeWalker comes in as a git submodule:

```powershell
git clone --recursive <repo-url>
dotnet run --project src/Mdv.App      # the app
.\build.ps1 -Zip                       # self-contained publish\ModDropV (+ zip)
```

## 🙏 Credits

- [CodeWalker](https://github.com/dexyfex/CodeWalker) by dexyfex — resource reading and gen9 conversion.
- [BCnEncoder.NET](https://github.com/Nominom/BCnEncoder.NET) by Nominom — texture compression for liveries.
- **OpenIV.asi** by the OpenIV team and the **ASI Loader** by Alexander Blade — mod support for GTA V Legacy.
- **Simple Mods Loader** (`DSOUND.dll`) by NativeCoder — mod support for GTA V Enhanced.

Grand Theft Auto V is a trademark of Take-Two Interactive / Rockstar Games. This project is not affiliated
with or endorsed by them.
