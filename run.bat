@echo off
setlocal
cd /d "%~dp0"

echo === Building BridgEN ===
dotnet build "%~dp0BridgEN.sln"
if errorlevel 1 (
    echo.
    echo Build failed.
    pause
    exit /b 1
)

echo.
echo === Running BridgEN ===
dotnet run --no-build --project "%~dp0src\BridgEN.App\BridgEN.App.csproj"
set "EXIT_CODE=%ERRORLEVEL%"

echo.
echo Application exited with code %EXIT_CODE%.
pause
exit /b %EXIT_CODE%
