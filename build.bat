@echo off
setlocal

cd /d "%~dp0"

where dotnet >nul 2>&1
if errorlevel 1 (
    echo [ERROR] .NET SDK was not found in PATH.
    goto :failed
)

echo Publishing PoEToolbox...
dotnet publish "src\PoEToolbox.App\PoEToolbox.App.csproj" -c Release -r win-x64 --self-contained false -o "publish"
if errorlevel 1 (
    goto :failed
)

if not exist "publish\PoEToolbox.exe" (
    echo [ERROR] The publish command completed, but publish\PoEToolbox.exe was not found.
    goto :failed
)

echo.
echo Build succeeded: %CD%\publish\PoEToolbox.exe
pause
exit /b 0

:failed
echo.
echo Build failed. Check the output above for details.
pause
exit /b 1
