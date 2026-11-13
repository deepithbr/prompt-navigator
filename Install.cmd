@echo off
rem Sets up Prompt Advisor on this PC, or updates an existing setup.
rem Safe to run again. See desktop\Install.ps1 for what it does.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0desktop\Install.ps1" %*
echo.
pause
