@echo off
rem Builds PoolHeapAdjusterEnhanced.asi into data\plugins\limits-enhanced (needs Visual Studio with C++ tools).
setlocal
set VCVARS=
for %%v in (18 2022) do for %%e in (Community Professional Enterprise BuildTools) do (
    if not defined VCVARS if exist "%ProgramFiles%\Microsoft Visual Studio\%%v\%%e\VC\Auxiliary\Build\vcvars64.bat" set "VCVARS=%ProgramFiles%\Microsoft Visual Studio\%%v\%%e\VC\Auxiliary\Build\vcvars64.bat"
)
if not defined VCVARS (echo Visual Studio C++ tools not found & exit /b 1)
call "%VCVARS%" >nul || exit /b 1
set "OUT=%~dp0..\..\data\plugins\limits-enhanced"
set "OBJ=%TEMP%\phae-obj"
if not exist "%OBJ%" mkdir "%OBJ%"
cl /nologo /O2 /MT /EHsc /std:c++17 /DUNICODE /D_UNICODE /LD "%~dp0dllmain.cpp" /Fo"%OBJ%\\" /Fe"%OBJ%\PoolHeapAdjusterEnhanced.asi" || exit /b 1
copy /y "%OBJ%\PoolHeapAdjusterEnhanced.asi" "%OUT%" >nul
copy /y "%~dp0PoolHeapAdjusterEnhanced.ini" "%OUT%" >nul
copy /y "%~dp0LICENSE.txt" "%OUT%\PoolHeapAdjusterEnhanced.LICENSE.txt" >nul
echo Built %OUT%\PoolHeapAdjusterEnhanced.asi
