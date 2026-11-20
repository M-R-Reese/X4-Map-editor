@echo off
rem Builds and starts the X4 Galaxy Editor 3D (development start; a release will be one .exe). Double-click to start.
rem Godot location: change GODOT below if Godot is moved.
set GODOT=%USERPROFILE%\Desktop\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64.exe
cd /d "%~dp0editor"
dotnet build GalaxyEditor3D.csproj -v q -nologo
if errorlevel 1 ( echo Build failed. & pause & exit /b 1 )
start "" "%GODOT%" --path .
