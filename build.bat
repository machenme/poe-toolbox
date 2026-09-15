@echo off
setlocal

cd /d "%~dp0"

where dotnet >nul 2>&1
if errorlevel 1 (
    echo [ERROR] .NET SDK was not found in PATH.
    goto :failed
)

rem Version single source: version.json (same as CI)
set "APP_VERSION="
for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "(Get-Content version.json -Raw -Encoding UTF8 | ConvertFrom-Json).version"`) do set "APP_VERSION=%%v"
if "%APP_VERSION%"=="" (
    echo [ERROR] Failed to read version from version.json.
    goto :failed
)
echo Version: %APP_VERSION%

echo Publishing PoEToolbox...
dotnet publish "src\PoEToolbox.App\PoEToolbox.App.csproj" -c Release -r win-x64 --self-contained false -o "publish" -p:Version=%APP_VERSION% -p:DebugSymbols=false -p:DebugType=None -p:GenerateDocumentationFile=false
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
