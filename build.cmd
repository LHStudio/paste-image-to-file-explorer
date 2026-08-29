@echo off
rem Builds PasteImageToExplorer.exe using the .NET Framework compiler bundled
rem with Windows (no SDK or runtime installation required).
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo [ERROR] .NET Framework compiler csc.exe not found.
    exit /b 1
)

if not exist "src\app.ico" (
    echo Generating app.ico ...
    "%CSC%" /nologo /target:exe /optimize+ /codepage:65001 /r:System.Drawing.dll /out:src\MakeIcon.exe src\MakeIcon.cs
    if errorlevel 1 exit /b 1
    src\MakeIcon.exe src\app.ico
    if errorlevel 1 exit /b 1
    del "src\MakeIcon.exe" 2>nul
)

echo Compiling PasteImageToExplorer.exe ...
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /win32icon:src\app.ico /win32manifest:src\app.manifest /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /out:PasteImageToExplorer.exe src\Program.cs
if errorlevel 1 (
    echo [ERROR] Build failed.
    exit /b 1
)

echo Build OK: %CD%\PasteImageToExplorer.exe
