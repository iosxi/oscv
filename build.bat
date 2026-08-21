@echo off
rem Entry point for double-clicking. The real script is build.ps1.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
if errorlevel 1 (
    echo.
    echo BUILD FAILED
    pause
    exit /b 1
)
