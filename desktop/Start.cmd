@echo off
rem Starts the Prompt Advisor companion with no console window.
rem It lives in the tray. Right-click the tray icon to pause or exit.
start "" powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0PromptAdvisor.ps1"
