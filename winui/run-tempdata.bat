@echo off
rem ============================================================
rem  Launch PortProxy Manager with a temporary data directory.
rem
rem  settings.json / meta.json / logs / backups are written to
rem  %TEMP%\PortProxyManager.WinUI instead of the exe folder, so
rem  the portable data directory stays untouched.
rem
rem  Extra arguments are forwarded to the app, e.g.:
rem    run-tempdata.bat --tray
rem    run-tempdata.bat --no-elevate
rem
rem  Note: if an instance is already running in the tray, the
rem  single-instance logic activates it and this --data-dir has no
rem  effect. Exit the existing instance first.
rem ============================================================
setlocal

set "DATA_DIR=%TEMP%\PortProxyManager.WinUI"
if not exist "%DATA_DIR%" mkdir "%DATA_DIR%"

set "PLATFORM=x64"
if /i "%PROCESSOR_ARCHITECTURE%"=="ARM64" set "PLATFORM=ARM64"
if /i "%PROCESSOR_ARCHITECTURE%"=="x86" set "PLATFORM=x86"

set "EXE="
rem Prefer the current Debug build (latest local changes) ...
for /f "delims=" %%F in ('dir /s /b "%~dp0PortProxyManager.WinUI\bin\%PLATFORM%\Debug\PortProxyManager.WinUI.exe" 2^>nul') do (
    if not defined EXE set "EXE=%%F"
)
rem ... then fall back to the published dist build.
if not defined EXE if exist "%~dp0dist\PortProxyManager.WinUI.exe" set "EXE=%~dp0dist\PortProxyManager.WinUI.exe"

if not defined EXE (
    echo [run-tempdata] PortProxyManager.WinUI.exe not found.
    echo [run-tempdata] Build it first:  winapp run PortProxyManager.WinUI\PortProxyManager.WinUI.csproj
    echo [run-tempdata] or publish:       dotnet publish PortProxyManager.WinUI\PortProxyManager.WinUI.csproj -c Release -r win-x64 --self-contained true -o dist
    pause
    exit /b 1
)

echo [run-tempdata] data-dir = %DATA_DIR%
echo [run-tempdata] exe      = %EXE%
start "" "%EXE%" --data-dir "%DATA_DIR%" %*
endlocal
