@echo off
title Stembridge Valley server
setlocal EnableDelayedExpansion
set "DIR=%LOCALAPPDATA%\StembridgeValley-Server"
echo.
echo ===== Stembridge Valley server =====
tasklist /fi "imagename eq SVHost.exe" | find /i "SVHost.exe" >nul && (echo Running: yes) || (echo Running: NO - double-click "Start Server")
if exist "%DIR%\state\server-status.txt" type "%DIR%\state\server-status.txt"
echo.
for /f "usebackq delims=" %%P in (`powershell -NoProfile -Command "(Get-Content '%DIR%\host.json' | ConvertFrom-Json).Password"`) do set "PW=%%P"
for /f "usebackq delims=" %%I in (`powershell -NoProfile -Command "try{(Invoke-RestMethod -TimeoutSec 8 https://api.ipify.org)}catch{'YOUR-PUBLIC-IP'}"`) do set "IP=%%I"
echo Invite code for friends (send it privately):
echo.
echo     sv:!IP!:24642/!PW!
echo.
if exist "%DIR%\state\port-forward.txt" (
  findstr /b "ok" "%DIR%\state\port-forward.txt" >nul && (echo Router: port opened automatically.) || (echo Router: needs the one-time port forward - see the guide.)
)
echo.
pause
