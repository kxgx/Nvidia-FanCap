@echo off
rem Self test - run as administrator. Verifies the core promise: a fan above the
rem ceiling gets detected and forced back down. Stops the service first, restarts
rem it afterwards. Details go to selftest.log.
powershell -NoProfile -Command "exit ([int](-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)))"
if errorlevel 1 (
  echo Requesting administrator rights...
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs -ArgumentList '-nopause'"
  exit /b
)
set EXE=%~dp0dist\Nvidia-FanCap-x64.exe
if not exist "%EXE%" ( echo build first: run build.bat & exit /b 1 )

schtasks /end /tn NvidiaFanCap 2>nul
taskkill /im Nvidia-FanCap-x64.exe /f 2>nul

echo [1/3] T1: pin the fan to 60%% and leave it there...
"%EXE%" --daemon --mode fixed --cap 60 --duration 25 --no-restore --verbose --log "%~dp0selftest.log"

echo [2/3] T2: a 25%% ceiling must detect it and force it back...
"%EXE%" --daemon --cap 25 --preempt-temp 0 --release-temp 20 --duration 15 --verbose --log "%~dp0selftest.log"

echo [3/3] T3: default settings must stay passive while idle...
"%EXE%" --daemon --duration 10 --verbose --log "%~dp0selftest.log"

echo.
findstr /c:"CEILING ENGAGED" "%~dp0selftest.log" >nul && echo PASS: the ceiling engaged and pulled the fan back. || echo FAIL: see selftest.log
schtasks /run /tn NvidiaFanCap
if /i not "%1"=="-nopause" pause
