@echo off
setlocal
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
set "result=%errorlevel%"
echo.
if not "%result%"=="0" echo CANDesk build failed. Read the error and log path above.
pause
exit /b %result%
