@echo off
setlocal
REM Build, package and verify WinGet artifacts.

powershell -ExecutionPolicy Bypass -File ".\package-release.ps1" %*
if errorlevel 1 exit /b %errorlevel%

echo Done.
