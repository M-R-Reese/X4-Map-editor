@echo off
rem Runs the checks of the 3D Galaxy Editor's data layer against the installed game. Double-click to start.
cd /d "%~dp0"
dotnet run --project tests
echo.
pause
