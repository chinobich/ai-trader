@echo off
rem ============================================================
rem  PhoneBridge build script
rem  Builds PhoneBridge.exe with the C# compiler that ships with
rem  Windows (.NET Framework 4.x). Nothing needs to be installed.
rem  Usage: double-click this file (or run "build.bat /nopause").
rem ============================================================
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo [ERROR] csc.exe was not found. .NET Framework 4.x is required.
  goto :fail
)

if exist PhoneBridge.exe del /q PhoneBridge.exe
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /out:PhoneBridge.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll src\*.cs
if errorlevel 1 goto :fail

echo.
echo [OK] PhoneBridge.exe was created in %CD%
if /i not "%~1"=="/nopause" pause
exit /b 0

:fail
echo.
echo [ERROR] Build failed.
if /i not "%~1"=="/nopause" pause
exit /b 1
