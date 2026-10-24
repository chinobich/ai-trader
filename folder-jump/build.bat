@echo off
setlocal
cd /d "%~dp0"

rem ---------------------------------------------------------------
rem  FolderJump build script
rem  Uses the C# compiler that ships with Windows (.NET Framework 4.x).
rem  No installation, no admin rights, no internet needed.
rem ---------------------------------------------------------------

set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FW%\csc.exe" set "FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"
if not exist "%FW%\csc.exe" (
  echo [ERROR] csc.exe was not found. .NET Framework 4.x is required.
  pause
  exit /b 1
)

echo Building FolderJump.exe ...
"%FW%\csc.exe" /nologo /noconfig /target:winexe /platform:anycpu /optimize+ /codepage:65001 ^
  /out:FolderJump.exe /win32icon:src\FolderJump.ico /win32manifest:src\app.manifest ^
  /resource:src\FolderJump.ico,FolderJump.ico ^
  /lib:"%FW%" /lib:"%FW%\WPF" ^
  /r:System.dll /r:System.Core.dll /r:System.Xaml.dll /r:WindowsBase.dll ^
  /r:PresentationCore.dll /r:PresentationFramework.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  src\*.cs
if errorlevel 1 (
  echo.
  echo [ERROR] Build failed. See the messages above.
  pause
  exit /b 1
)

echo.
echo Done: "%CD%\FolderJump.exe"
echo Double-click FolderJump.exe to start. Press Ctrl+Alt+Space to open it.
pause
