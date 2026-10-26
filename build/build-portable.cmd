@echo off
rem Thin wrapper so the package build also runs by double-click / from cmd.
rem The real work lives in build-portable.ps1 (PowerShell handles the Chinese
rem file names in the package reliably; cmd.exe mangles them per code page).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-portable.ps1" %*
exit /b %ERRORLEVEL%
