@echo off
rem Re-export the Android APK into dist\android (3-5 min, about 1 GB).
rem Keep this file ASCII-only (cmd.exe reads .cmd in the OEM codepage).
setlocal
set "ROOT=%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%tools\export-android.ps1"
if errorlevel 1 (echo. & echo [FAILED] & pause)
endlocal
