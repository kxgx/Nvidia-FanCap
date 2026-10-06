@echo off
rem Registers Nvidia-FanCap: hidden autostart at logon with admin rights.
rem Double click - asks for administrator once.
powershell -NoProfile -Command "exit ([int](-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)))"
if errorlevel 1 (
  echo Requesting administrator rights...
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs -ArgumentList '-nopause'"
  exit /b
)
if exist "%~dp0dist\Nvidia-FanCap-x64.exe" ( "%~dp0dist\Nvidia-FanCap-x64.exe" --install ) else ( echo build first: run build.bat )
if /i not "%1"=="-nopause" pause
