@echo off
rem Restart the Nvidia-FanCap daemon (after editing Nvidia-FanCap.ini by hand
rem this is not needed - the daemon hot reloads within ~5 seconds).
powershell -NoProfile -Command "exit ([int](-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)))"
if errorlevel 1 (
  echo Requesting administrator rights...
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs -ArgumentList '-nopause'"
  exit /b
)
taskkill /im Nvidia-FanCap-x64.exe /f 2>nul
schtasks /end /tn NvidiaFanCap 2>nul
timeout /t 1 /nobreak >nul
schtasks /run /tn NvidiaFanCap
echo Daemon restarted.
if /i not "%1"=="-nopause" pause
