@echo off
setlocal
cd /d "%~dp0"
powershell -NoProfile -File "%~dp0scripts\publish-win.ps1" %*
set "PUBLISH_EXIT_CODE=%ERRORLEVEL%"
echo.
if not "%PUBLISH_EXIT_CODE%"=="0" echo Publish failed. See the output above.
pause
exit /b %PUBLISH_EXIT_CODE%
