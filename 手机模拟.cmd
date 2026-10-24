@echo off
rem Desktop mobile simulation: 20:9 window + touch input path.
rem Mouse held 0.45s = long press (cancels targeting / gesture).
rem Change -MobileSize to resize (e.g. 1040x480 or 1440x648).
rem Keep this file ASCII-only: cmd.exe reads .cmd in the OEM codepage and
rem non-ASCII comments can split into stray commands.
setlocal
set "ROOT=%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%tools\run.ps1" -MobileSim -MobileSize 1200x540
if errorlevel 1 (echo. & echo [FAILED] & pause)
endlocal
