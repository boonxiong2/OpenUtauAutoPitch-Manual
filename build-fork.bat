@echo off
REM Rebuild the patched OpenUtau (Debug). Packages are already restored.
cd /d %~dp0
"dotnet" build "%~dp0OpenUtau\OpenUtau.csproj" -c Debug --no-restore
echo.
echo Output: %~dp0OpenUtau\bin\Debug\net10.0-windows\OpenUtau.exe
pause
