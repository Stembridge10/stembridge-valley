@echo off
title Junimo Hollow - allow through firewall
net session >nul 2>&1 || (
  powershell -NoProfile -Command "Start-Process -Verb RunAs -FilePath '%~f0'"
  exit /b
)
netsh advfirewall firewall delete rule name="Stembridge Valley server" >nul 2>&1
netsh advfirewall firewall delete rule name="Junimo Hollow server" >nul 2>&1
netsh advfirewall firewall add rule name="Junimo Hollow server" dir=in action=allow protocol=UDP localport=24642 profile=any
echo.
echo Done. Friends can now reach the server through Windows Firewall (UDP 24642).
pause
