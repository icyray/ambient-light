@echo off
setlocal
cd /d "%~dp0"

rem Locate MSVC via vswhere (version-independent)
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
set "VSPATH="
if exist "%VSWHERE%" (
    for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSPATH=%%i"
)
if "%VSPATH%"=="" (
    echo [build] MSVC not found via vswhere
    exit /b 1
)
echo [build] VS at %VSPATH%
call "%VSPATH%\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1

if not exist obj mkdir obj
if not exist bin mkdir bin

cl /nologo /EHsc /O2 /W3 /std:c++17 /utf-8 /DNOMINMAX /D_UNICODE /DUNICODE /DAMBIENTCORE_BUILD /Zi /LD ^
   AmbientCore.cpp capture.cpp colorchain.cpp serial.cpp log.cpp ^
   /Fe:bin\AmbientCore.dll /Fd:obj\AmbientCore.pdb /Fo:obj\
if errorlevel 1 (
    echo [build] FAILED
    exit /b 1
)
del bin\AmbientCore.lib bin\AmbientCore.exp 2>nul
echo [build] OK - bin\AmbientCore.dll
