@echo off
:: Builds dist\ZapretTray.exe and dist\Setup.exe, copies the zapret engine next to them
:: and packs release\ZapretTray-<version>.zip (the file uploaded to GitHub Releases)
setlocal
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "OPTS=/nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 /win32icon:src\app.ico /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Net.Http.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll"

:: Version comes from AppInfo.Version in src\Common.cs
for /f "tokens=6" %%v in ('findstr /c:"string Version = " src\Common.cs') do set "VER=%%~v"
if not defined VER goto :fail
set "VER=%VER:;=%"
set "VER=%VER:"=%"

if not exist dist mkdir dist
powershell -NoProfile -ExecutionPolicy Bypass -File src\make-icon.ps1 -Out src\app.ico || goto :fail

"%CSC%" %OPTS% /win32manifest:src\tray.manifest /out:dist\ZapretTray.exe src\Common.cs src\Games.cs src\Updater.cs src\Theme.cs src\Selector.cs src\Tray.cs || goto :fail
"%CSC%" %OPTS% /win32manifest:src\setup.manifest /out:dist\Setup.exe src\Common.cs src\Setup.cs || goto :fail

robocopy payload\core dist\core /MIR /NFL /NDL /NJH /NJS /NP >nul
if %errorlevel% geq 8 goto :fail

:: Release zip for friends: only Setup.exe and README.txt on top, the rest in files\
set "REL=release\ZapretTray-%VER%"
if exist release rmdir /s /q release
mkdir "%REL%\files" || goto :fail
copy /y dist\Setup.exe "%REL%\" >nul || goto :fail
copy /y src\readme.txt "%REL%\README.txt" >nul || goto :fail
copy /y dist\ZapretTray.exe "%REL%\files\" >nul || goto :fail
robocopy dist\core "%REL%\files\core" /E /NFL /NDL /NJH /NJS /NP >nul
if %errorlevel% geq 8 goto :fail
powershell -NoProfile -Command "Compress-Archive -Path '%REL%' -DestinationPath 'release\ZapretTray-%VER%.zip' -Force" || goto :fail

echo Build OK: %~dp0dist
echo Release: %~dp0release\ZapretTray-%VER%.zip
exit /b 0

:fail
echo Build FAILED
exit /b 1
