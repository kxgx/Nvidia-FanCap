@echo off
rem Build everything into dist\ :
rem   dist\Nvidia-FanCap-x64.exe       single native binary (daemon + GUI + installer helper)
rem   dist\Nvidia-FanCap-1.0.0-x64.msi MSI installer
rem Requires the .NET 8 SDK; the first run downloads the WiX toolset (MIT) once.
setlocal
set DIR=%~dp0

echo [1/2] building Nvidia-FanCap-x64.exe (NativeAOT x64)...
pushd "%DIR%"
dotnet publish Nvidia-FanCap.csproj -c Release --nologo -v minimal -o dist
if errorlevel 1 ( popd & echo BUILD FAILED & exit /b 1 )
popd

echo [2/2] building the MSI installer...
call "%DIR%make-installer.bat"
if errorlevel 1 ( echo BUILD FAILED & exit /b 1 )

echo.
echo Done:
echo   dist\Nvidia-FanCap-x64.exe        2 MB single file - double click for the GUI
echo   dist\Nvidia-FanCap-1.0.0-x64.msi  installer - registers hidden autostart
