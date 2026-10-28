@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-packages.ps1" %*
exit /b %ERRORLEVEL%
