@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish-drageon-win-x64.ps1" %*
exit /b %ERRORLEVEL%
