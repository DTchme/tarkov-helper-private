@echo off
setlocal
cd /d "%~dp0"

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-release.ps1"
set "ERR=%ERRORLEVEL%"

echo.
if not "%ERR%"=="0" (
  echo [FAILED] Build failed. See build-release.log for details.
) else (
  echo [SUCCESS] Build finished.
  echo Output: %~dp0release\TarkovHelper_v1.5.10_1.1_windows_v6.zip
)
echo.
pause
exit /b %ERR%
