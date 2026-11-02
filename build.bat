@echo off
:: Builds dist\ZapretTray.exe and dist\Setup.exe, copies the zapret engine next to them
:: and packs release\ZapretTray-<version>.zip (the file uploaded to GitHub Releases)
setlocal
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "OPTS=/nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 /win32icon:src\app.ico /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.ServiceProcess.dll /r:System.Net.Http.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll"

:: Version comes from AppInfo.Version in src\Common.cs
for /f "tokens=6" %%v in ('findstr /c:"string Version = " src\Common.cs') do set "VER=%%~v"
if not defined VER goto :fail
set "VER=%VER:;=%"
set "VER=%VER:"=%"

if not exist dist mkdir dist

:: Ссылка для "Отправить баг" (вебхук Discord). Лежит только у владельца, как ключ подписи. Нет файла - кнопки в программе не будет
set "HOOKRES="
if exist "%USERPROFILE%\.zapret-tray\webhook.txt" set "HOOKRES=/res:%USERPROFILE%\.zapret-tray\webhook.txt,webhook.txt"
powershell -NoProfile -ExecutionPolicy Bypass -File src\make-icon.ps1 -Out src\app.ico || goto :fail

"%CSC%" %OPTS% /win32manifest:src\tray.manifest /res:src\icons\arc.png,arc.png /res:src\icons\wardogs.png,wardogs.png %HOOKRES% /out:dist\ZapretTray.exe src\Common.cs src\DnsFix.cs src\SiteCheck.cs src\Games.cs src\Updater.cs src\Theme.cs src\Selector.cs src\SettingsFile.cs src\HintsWindow.cs src\ControlPanel.cs src\Tray.cs || goto :fail
"%CSC%" %OPTS% /win32manifest:src\setup.manifest /out:dist\Setup.exe src\Common.cs src\DnsFix.cs src\Theme.cs src\Setup.cs || goto :fail

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
powershell -NoProfile -Command "Compress-Archive -Path '%REL%\*' -DestinationPath 'release\ZapretTray-%VER%.zip' -Force" || goto :fail

:: Подпись архива. Без неё программа обновление не поставит. Ключ лежит только у владельца и в репозиторий не попадает
set "KEY=%USERPROFILE%\.zapret-tray\signing-key.xml"
if not exist "%KEY%" (
    echo No signing key: %KEY%
    goto :fail
)
"%CSC%" /nologo /codepage:65001 /out:release\zsign.exe src\SignTool.cs || goto :fail
release\zsign.exe sign "%KEY%" "release\ZapretTray-%VER%.zip" || goto :fail

echo Build OK: %~dp0dist
echo Release: %~dp0release\ZapretTray-%VER%.zip
exit /b 0

:fail
echo Build FAILED
exit /b 1
