@echo off
rem Builds World Travel's WorldTravel.asi for GTA V Enhanced into data\plugins\worldtravel-enhanced:
rem upstream sources (GPL-3, https://github.com/Splatcrafter/worldTravelASI, commit 2b328bf) + enhanced.patch.
rem Needs git and Visual Studio with C++ tools.
setlocal
set VCVARS=
for %%v in (18 2022) do for %%e in (Community Professional Enterprise BuildTools) do (
    if not defined VCVARS if exist "%ProgramFiles%\Microsoft Visual Studio\%%v\%%e\VC\Auxiliary\Build\vcvars64.bat" set "VCVARS=%ProgramFiles%\Microsoft Visual Studio\%%v\%%e\VC\Auxiliary\Build\vcvars64.bat"
)
if not defined VCVARS (echo Visual Studio C++ tools not found & exit /b 1)
set "SRC=%TEMP%\worldTravelASI"
set "OBJ=%TEMP%\wt-obj"
set "OUT=%~dp0..\..\data\plugins\worldtravel-enhanced"
if not exist "%SRC%" git clone https://github.com/Splatcrafter/worldTravelASI "%SRC%" || exit /b 1
pushd "%SRC%"
git checkout -q -f 2b328bf || exit /b 1
git apply "%~dp0enhanced.patch" || exit /b 1
popd
call "%VCVARS%" >nul || exit /b 1
if not exist "%OBJ%" mkdir "%OBJ%"
if not exist "%OUT%" mkdir "%OUT%"
cd /d "%OBJ%"
cl /nologo /O2 /MD /EHsc /std:c++17 /LD /DNDEBUG /D_CRT_SECURE_NO_WARNINGS /I"%SRC%\WorldTravel\dependencies\include" /I"%SRC%\WorldTravel\src" "%SRC%\WorldTravel\src\*.cpp" /Fe"%OBJ%\WorldTravel.asi" /link /LIBPATH:"%SRC%\WorldTravel\dependencies\lib" libMinHook-x64-v141-md.lib ScriptHookV.lib user32.lib shlwapi.lib ole32.lib || exit /b 1
copy /y "%OBJ%\WorldTravel.asi" "%OUT%" >nul
copy /y "%~dp0LICENSE.txt" "%OUT%\WorldTravel.LICENSE.txt" >nul
echo Built %OUT%\WorldTravel.asi
