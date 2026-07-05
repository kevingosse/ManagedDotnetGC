@echo off
REM Usage: soak-aspnet.cmd [seconds] [workers]
set SECONDS=%1
if "%SECONDS%"=="" set SECONDS=3600
set WORKERS=%2
if "%WORKERS%"=="" set WORKERS=32

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0soak-aspnet.ps1" -Seconds %SECONDS% -Workers %WORKERS%
exit /b %ERRORLEVEL%
