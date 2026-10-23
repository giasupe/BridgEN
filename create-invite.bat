@echo off
setlocal
rem Launch without arguments to choose a user ID interactively.
rem Allow local scripts only in this child process; do not change saved policies.
powershell -NoProfile -ExecutionPolicy RemoteSigned -File "%~dp0scripts\create-invite.ps1" -OpenFolder %*
set "INVITE_EXIT_CODE=%ERRORLEVEL%"
echo.
if not "%INVITE_EXIT_CODE%"=="0" echo Invitation creation failed. See the output above.
pause
exit /b %INVITE_EXIT_CODE%
