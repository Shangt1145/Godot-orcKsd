@echo off
REM Launch the OrC-KSD UI build copy in a real window so the deployment slam can be
REM inspected visually (headless renders no pixels and plays no audio).
REM Usage: run_slam_visual.bat[project_path]
setlocal
set "ROOT=%~1"
if "%ROOT%"=="" set "ROOT=H:\g\kards"
set "GODOT=H:\g\tools\godot47\Godot_v4.7-stable_mono_win64\Godot_v4.7-stable_mono_win64_console.exe"
if not exist "%GODOT%" (
  echo [ERROR] Godot not found: %GODOT%
  exit /b 1
)
cd /d "%ROOT%" || exit /b 1
echo [RUN] project=%ROOT%
"%GODOT%" --path "%ROOT%" --resolution 1280x720
echo [EXIT] code=%errorlevel%
endlocal