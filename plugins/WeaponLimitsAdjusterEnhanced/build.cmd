@echo off
rem Builds WeaponLimitsAdjusterEnhanced.asi into data\plugins\limits (needs Visual Studio with C++ tools).
setlocal
set VCVARS=
for %%v in (18 2022) do for %%e in (Community Professional Enterprise BuildTools) do (
    if not defined VCVARS if exist "%ProgramFiles%\Microsoft Visual Studio\%%v\%%e\VC\Auxiliary\Build\vcvars64.bat" set "VCVARS=%ProgramFiles%\Microsoft Visual Studio\%%v\%%e\VC\Auxiliary\Build\vcvars64.bat"
)
if not defined VCVARS (echo Visual Studio C++ tools not found & exit /b 1)
call "%VCVARS%" >nul || exit /b 1
set "OUT=%~dp0..\..\data\plugins\limits"
set "OBJ=%TEMP%\wlae-obj"
if not exist "%OBJ%" mkdir "%OBJ%"
cl /nologo /O2 /MT /EHsc /std:c++17 /DUNICODE /D_UNICODE /LD "%~dp0dllmain.cpp" /Fo"%OBJ%\\" /Fe"%OBJ%\WeaponLimitsAdjusterEnhanced.asi" || exit /b 1
copy /y "%OBJ%\WeaponLimitsAdjusterEnhanced.asi" "%OUT%" >nul
copy /y "%~dp0WeaponLimitsAdjusterEnhanced.ini" "%OUT%" >nul
copy /y "%~dp0LICENSE.txt" "%OUT%\WeaponLimitsAdjusterEnhanced.LICENSE.txt" >nul
echo Built %OUT%\WeaponLimitsAdjusterEnhanced.asi
