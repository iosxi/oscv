@echo off
rem Builds Oscv.exe. No SDK required - uses the csc.exe shipped with .NET Framework 4.
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo csc.exe not found - is .NET Framework 4 installed?
    exit /b 1
)

"%CSC%" /nologo /target:winexe /optimize+ /codepage:65001 ^
    /out:"%~dp0Oscv.exe" ^
    /reference:System.dll ^
    /reference:System.Drawing.dll ^
    /reference:System.Windows.Forms.dll ^
    "%~dp0src\Oscv.cs"

if errorlevel 1 (
    echo BUILD FAILED
    exit /b 1
)
echo Built "%~dp0Oscv.exe"
