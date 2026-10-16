@echo off
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo [ERROR] csc.exe not found. Windows 10/11 with .NET Framework 4.x is required.
  pause
  exit /b 1
)

echo Compiling SteamFix.exe ...
"%CSC%" /nologo /target:exe /platform:anycpu /optimize+ /codepage:65001 ^
  /out:"%~dp0SteamFix.exe" "%~dp0src\SteamFix.cs"

if errorlevel 1 (
  echo [ERROR] Build failed.
  pause
  exit /b 1
)

echo.
echo Done. Output: %~dp0SteamFix.exe
pause
