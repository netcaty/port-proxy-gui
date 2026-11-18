@echo off
rem ============================================================
rem  Debug-launch the WinUI build via the winapp CLI (method 2).
rem  Builds the project, then launches it unpackaged.
rem
rem  Pass-through args are forwarded to the app, e.g.:
rem    run-debug.bat --debug-output
rem    run-debug.bat --no-elevate
rem ============================================================

rem --- Adjust these two paths if your setup differs ---
set DOTNET_ROOT=C:\Users\WDAGUtilityAccount\scoop\apps\dotnet-sdk\current
set WINAPP=C:\Users\WDAGUtilityAccount\tools\winappcli\v0.8.0\winapp.exe

set PATH=%PATH%;%DOTNET_ROOT%

cd /d "%~dp0PortProxyManager.WinUI"

echo [run-debug] Building and launching (unpackaged) ...
"%WINAPP%" run PortProxyManager.WinUI.csproj -p WindowsPackageType=None %*
