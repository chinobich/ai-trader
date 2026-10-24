@echo off
setlocal
cd /d "%~dp0.."

set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FW%\csc.exe" set "FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"

"%FW%\csc.exe" /nologo /noconfig /target:exe /codepage:65001 /out:tests\SelfTest.exe ^
  /r:System.dll /r:System.Core.dll ^
  src\Native.cs src\Util.cs src\TextNorm.cs src\FileIndex.cs src\Crawler.cs ^
  src\Query.cs src\Synonyms.cs src\Settings.cs src\UsageStore.cs src\QueryEngine.cs tests\SelfTest.cs
if errorlevel 1 (
  pause
  exit /b 1
)
chcp 65001 >nul
tests\SelfTest.exe
pause
