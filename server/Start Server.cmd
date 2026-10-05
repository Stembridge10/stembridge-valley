@echo off
title Junimo Hollow server
cd /d "%~dp0"
if not exist "%LOCALAPPDATA%\StembridgeValley-Server" mkdir "%LOCALAPPDATA%\StembridgeValley-Server"
tasklist /fi "imagename eq SVHost.exe" | find /i "SVHost.exe" >nul && (
  echo The server is already running.
  echo Use "Server Status" to see the invite code.
  pause
  exit /b
)
start "" /b "%~dp0SVHost.exe" --instance "%LOCALAPPDATA%\StembridgeValley-Server"
echo Starting the Junimo Hollow server in the background...
timeout /t 25 /nobreak >nul
call "%~dp0Server Status.cmd"
