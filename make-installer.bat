@echo off
rem Build the MSI installer into dist\.
rem First run fetches the WiX toolset into .tools (MIT licensed WiX 5).
setlocal
set DIR=%~dp0
if not exist "%DIR%.tools\wix.exe" (
  echo installing WiX toolset ^(one time^)...
  dotnet tool install --tool-path "%DIR%.tools" wix --version 5.0.2
)
echo building MSI...
pushd "%DIR%"
".tools\wix.exe" build -arch x64 Product.wxs -o "dist\Nvidia-FanCap-1.0.3-x64.msi" -nologo
set RC=%errorlevel%
popd
if not "%RC%"=="0" ( echo MSI BUILD FAILED & exit /b 1 )
echo.
echo Installer: dist\Nvidia-FanCap-1.0.3-x64.msi
