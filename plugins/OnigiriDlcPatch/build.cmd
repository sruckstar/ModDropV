@echo off
rem Builds OnigiriDlcPatch.asi into data\plugins\onigiri (needs Visual Studio with C++ tools).
setlocal
set VCVARS=
for %%v in (18 2022) do for %%e in (Community Professional Enterprise BuildTools) do (
    if not defined VCVARS if exist "%ProgramFiles%\Microsoft Visual Studio\%%v\%%e\VC\Auxiliary\Build\vcvars64.bat" set "VCVARS=%ProgramFiles%\Microsoft Visual Studio\%%v\%%e\VC\Auxiliary\Build\vcvars64.bat"
)
if not defined VCVARS (echo Visual Studio C++ tools not found & exit /b 1)
call "%VCVARS%" >nul || exit /b 1
set "OUT=%~dp0..\..\data\plugins\onigiri"
set "OBJ=%TEMP%\odp-obj"
if not exist "%OBJ%" mkdir "%OBJ%"
if not exist "%OUT%" mkdir "%OUT%"
cl /nologo /O2 /MT /EHsc /std:c++17 /DUNICODE /D_UNICODE /LD "%~dp0dllmain.cpp" /Fo"%OBJ%\\" /Fe"%OBJ%\OnigiriDlcPatch.asi" || exit /b 1
copy /y "%OBJ%\OnigiriDlcPatch.asi" "%OUT%" >nul
copy /y "%~dp0LICENSE.txt" "%OUT%\OnigiriDlcPatch.LICENSE.txt" >nul
echo Built %OUT%\OnigiriDlcPatch.asi
