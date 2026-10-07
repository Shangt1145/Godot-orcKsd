@echo off
REM Opens the project in the .NET (mono) Godot editor.
REM
REM Do NOT open this project with the plain build
REM   H:\Godot_v4.7-stable_win64.exe
REM It carries no C# script loader, so every .cs fails with
REM   "No loader found for resource: .../Main.cs (expected type: Script)"
REM and res://proto/scenes/Main.tscn then fails to load. The project is fine;
REM the binary is the wrong flavour. Only 4.7 .NET (mono) can run it:
REM   H:\g\tools\godot47\Godot_v4.7-stable_mono_win64\Godot_v4.7-stable_mono_win64.exe
REM
REM This runs the same sync + build + import the project's own run.ps1 -Editor
REM does, then leaves the editor open on the build copy.
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run.ps1" -Editor
