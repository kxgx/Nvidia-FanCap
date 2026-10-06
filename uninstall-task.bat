@echo off
rem Removes the Nvidia-FanCap autostart and stops the daemon.
powershell -NoProfile -Command "exit ([int](-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)))"
if errorlevel 1 (
  echo Requesting administrator rights...
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs -ArgumentList '-nopause'"
  exit /b
)
if exist "%~dp0dist\Nvidia-FanCap-x64.exe" ( "%~dp0dist\Nvidia-FanCap-x64.exe" --uninstall ) else ( schtasks /end /tn NvidiaFanCap 2>nul & schtasks /delete /tn NvidiaFanCap /f )
taskkill /im Nvidia-FanCap-x64.exe /f 2>nul
if /i not "%1"=="-nopause" pause
