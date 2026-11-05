@echo off
:: Runs all tests: compiles each test file together with src\*.cs and runs it.
:: Network is not used: only files in a temp folder and servers on 127.0.0.1.
:: Logic tests need the release zip, so run build.bat first.
:: WindowTests shows the bug dialog on screen for a couple of seconds: do not touch the mouse meanwhile.
:: Results and screenshots go to tests\out (not in git). Screenshots: tests\out\Screenshots\screens.
setlocal enabledelayedexpansion
cd /d "%~dp0.."
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "REFS=/r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.ServiceProcess.dll /r:System.Security.dll /r:System.Net.Http.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll"
set "HOOKRES="
if exist "%USERPROFILE%\.zapret-tray\webhook.txt" set "HOOKRES=/res:%USERPROFILE%\.zapret-tray\webhook.txt,webhook.txt"
set "SRC="
for %%f in (src\Common.cs src\DnsFix.cs src\SiteCheck.cs src\Games.cs src\AppScan.cs src\Pickers.cs src\Updater.cs src\Theme.cs src\Selector.cs src\SettingsFile.cs src\HintsWindow.cs src\ControlPanel.cs src\Tray.cs) do set "SRC=!SRC! %%f"
set "ZIP="
for %%z in (release\ZapretTray-*.zip) do set "ZIP=%~dp0..\%%z"
set "WORK=%~dp0out"
if exist "%WORK%" rmdir /s /q "%WORK%"
mkdir "%WORK%"
set FAILED=0

for %%t in (UiTests:T LogicTests:U ReportTests:R AnimationTests:A BugTests:B WindowTests:W Screenshots:Shots) do (
    for /f "tokens=1,2 delims=:" %%a in ("%%t") do (
        mkdir "%WORK%\%%a"
        "%CSC%" /nologo /codepage:65001 /nowarn:649,169,414,162,168,219 /target:exe /main:ZapretTray.%%b %REFS% %HOOKRES% /res:src\icons\arc.png,arc.png /res:src\icons\wardogs.png,wardogs.png /out:"%WORK%\%%a\run.exe" tests\%%a.cs !SRC! >"%WORK%\%%a\build.txt"
        if errorlevel 1 (
            echo %%a: BUILD FAILED
            type "%WORK%\%%a\build.txt"
            set FAILED=1
        ) else (
            pushd "%WORK%\%%a"
            if "%%a"=="LogicTests" "%WORK%\%%a\run.exe" "!ZIP!" >nul 2>&1
            if not "%%a"=="LogicTests" "%WORK%\%%a\run.exe" >nul 2>&1
            popd
            if not exist "%WORK%\%%a\result.txt" (
                echo %%a: NO RESULT
                set FAILED=1
            ) else for /f "usebackq delims=" %%r in (`powershell -NoProfile -Command "(Get-Content '%WORK%\%%a\result.txt' -Encoding UTF8 -Tail 1)"`) do echo %%a: %%r
            findstr /b /c:"FAIL" "%WORK%\%%a\result.txt" >nul && set FAILED=1
        )
    )
)

if "%FAILED%"=="1" (
    echo TESTS FAILED, details in %WORK%
    exit /b 1
)
echo Screenshots: %WORK%\Screenshots\screens
echo ALL TESTS OK
exit /b 0