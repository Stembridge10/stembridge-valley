@echo off
title Stembridge Valley server
echo stop> "%LOCALAPPDATA%\StembridgeValley-Server\stop.flag"
echo Stopping the server (it saves at the start of each in-game day)...
timeout /t 8 /nobreak >nul
tasklist /fi "imagename eq SVHost.exe" | find /i "SVHost.exe" >nul && (
  echo Still closing; give it a few more seconds.
) || echo Server stopped.
pause
