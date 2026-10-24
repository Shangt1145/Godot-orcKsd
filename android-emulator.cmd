@echo off
rem Android emulator launcher for OrC-KSD. Double-click to start the emulator and install the latest APK.
rem First run may ask for UAC (UAC is only needed for the hardware acceleration driver).
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\android-emulator.ps1" %*
set RC=%ERRORLEVEL%
if not "%RC%"=="0" pause
endlocal
