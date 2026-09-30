@echo off
rem Starts the Prompt Advisor companion with no window at all.
rem conhost --headless keeps Windows Terminal from opening a PowerShell
rem window for it, which would end the companion if you closed it.
rem It lives in the tray. Right-click the tray icon to pause or exit.
start "" "%WINDIR%\System32\conhost.exe" --headless powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0PromptAdvisor.ps1"
