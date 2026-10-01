# WorldTravel.asi for GTA V Enhanced

World Travel is the level switcher of Liberty City Preservation Project (Los Santos ↔ Liberty City, F11).
Its source is [Splatcrafter/worldTravelASI](https://github.com/Splatcrafter/worldTravelASI) (GPL-3.0).

The WorldTravel.asi that LCPP ships (December 2024) hooks four functions of GTA V Legacy by signature and crashes
GTA V Enhanced at start. The current source already skips those hooks when the game isn't `GTA5.exe`, so a build
from it runs on Enhanced (without Liberty City's own minimap tiles, far LODs and fog volumes).

`enhanced.patch` adds two things:

- **Calm ocean in Liberty City.** Enhanced's water switch knows only Los Santos and Cayo Perico, so Liberty City lies
  in the open ocean with its full swell. While the player is in Liberty City the patch scales the deep-ocean waves
  down every frame (`SET_DEEP_OCEAN_SCALER`); `[Enhanced] OceanWaveScale` in WorldTravel.ini sets the scale
  (0 = flat, 1 = the game's own, default 0.2).
- **Story mode or online map.** World Travel keeps separate lists of map files for the story mode map and the online
  (MP) map (`Levels/*/IPLsSP.txt`, `IPLsMP.txt`) and tells which one runs by an interior id GTA V Legacy gives
  the Mission Row police station. `[Enhanced] Map` in WorldTravel.ini says it instead: `SP` — the story mode map;
  `MP` — the online map, which the plugin then loads itself (`ON_ENTER_MP`) once the player is in Los Santos, so no
  MP map loader is needed. Without the key it is detected as before. (On Legacy, LCPP's own WorldTravelPatches.asi
  does that through `DefaultGroupMap` in its .ini.)

ModDrop V puts this build in place of the package's WorldTravel.asi when it installs LCPP into GTA V Enhanced.
`build.cmd` rebuilds it (upstream commit 2b328bf + the patch) into `data/plugins/worldtravel-enhanced`.
