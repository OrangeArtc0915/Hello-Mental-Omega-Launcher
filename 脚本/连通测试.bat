@echo off
chcp 65001 >nul
title MO direct-connect test listener
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0connect-test.ps1"
set RC=%ERRORLEVEL%
echo.
if not "%RC%"=="0" (echo ***** FAILED *****) else (echo ***** Done *****)
pause
exit /b %RC%
