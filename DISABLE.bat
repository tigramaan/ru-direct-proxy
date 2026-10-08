@echo off
setlocal
cd /d "%~dp0"
start "" /wait "%~dp0bin\CountryProxy.exe" --disable
if errorlevel 1 (
  echo Disable failed. See the error message.
  pause
  exit /b 1
)
exit /b 0
