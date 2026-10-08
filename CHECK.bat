@echo off
setlocal
cd /d "%~dp0"
start "" /wait "%~dp0bin\CountryProxy.exe" --status
if errorlevel 1 (
  echo Check failed. See the error message.
  pause
  exit /b 1
)
exit /b 0
