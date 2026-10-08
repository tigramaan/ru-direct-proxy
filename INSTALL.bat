@echo off
setlocal
cd /d "%~dp0"
start "" /wait "%~dp0bin\CountryProxy.exe" --install
if errorlevel 1 (
  echo Installation failed. See the error message or manager.log.
  pause
  exit /b 1
)
exit /b 0
