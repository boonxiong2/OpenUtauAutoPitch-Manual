@echo off
REM One click: close OpenUtau -> build -> relaunch.
REM Save your project with Ctrl+S BEFORE running this, unsaved edits are lost.
cd /d %~dp0

tasklist /FI "IMAGENAME eq OpenUtau.exe" 2>nul | find /I "OpenUtau.exe" >nul
if not errorlevel 1 (
  echo.
  echo OpenUtau is running.
  echo Press any key to close it and rebuild. Make sure you saved with Ctrl+S!
  echo.
  pause >nul
  taskkill /F /IM OpenUtau.exe >nul 2>&1
  timeout /t 2 >nul
)

echo Building...
"dotnet" build "%~dp0OpenUtau\OpenUtau.csproj" -c Debug --no-restore -v q
if errorlevel 1 (
  echo.
  echo Build failed.
  pause
  exit /b 1
)

echo Starting OpenUtau...
start "" "%~dp0OpenUtau\bin\Debug\net10.0-windows\OpenUtau.exe"
