@echo off
setlocal
cd /d "%~dp0"

echo === Building speciRPG ===
dotnet build "%~dp0speciRPG.sln"
if errorlevel 1 (
    echo.
    echo Build failed.
    pause
    exit /b 1
)

echo.
echo === Running speciRPG ===
dotnet run --no-build --project "%~dp0src\speciRPG.App\speciRPG.App.csproj"
set "EXIT_CODE=%ERRORLEVEL%"

echo.
echo Application exited with code %EXIT_CODE%.
pause
exit /b %EXIT_CODE%
