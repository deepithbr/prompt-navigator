@echo off
rem Takes Prompt Advisor off this PC. Add -Purge to delete its task history too.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0desktop\Uninstall.ps1" %*
echo.
pause
