@echo off
set "LOCAL_VERSION=1.10.4"
:: Zapret Tray: only what general*.bat configs call. Menu, services, hosts editing and downloads are removed.

if "%~1"=="status_zapret" (
    call :tcp_enable
    exit /b
)
if "%~1"=="check_updates" exit /b
if "%~1"=="load_game_filter" (
    call :game_switch_status
    exit /b
)
if "%~1"=="load_user_lists" (
    call :load_user_lists
    exit /b
)
exit /b


:load_user_lists
set "LISTS_PATH=%~dp0lists\"

if not exist "%LISTS_PATH%ipset-exclude-user.txt" (
    echo 203.0.113.113/32>"%LISTS_PATH%ipset-exclude-user.txt"
)
if not exist "%LISTS_PATH%list-general-user.txt" (
    echo # Never leave this file empty>"%LISTS_PATH%list-general-user.txt"
    echo domain.example.abc>>"%LISTS_PATH%list-general-user.txt"
)
if not exist "%LISTS_PATH%list-exclude-user.txt" (
    echo domain.example.abc>"%LISTS_PATH%list-exclude-user.txt"
)

exit /b

:tcp_enable
chcp 437 > nul
netsh interface tcp show global | findstr /i "timestamps" | findstr /i "enabled" > nul || netsh interface tcp set global timestamps=enabled > nul 2>&1
exit /b

:game_switch_status
chcp 437 > nul

set "gameFlagFile=%~dp0utils\game_filter.enabled"
set "GameFilterMode=disabled"
set "GameFilterTCPRange=1024-65535"
set "GameFilterUDPRange=1024-65535"
set "GameFilterStatus=disabled"
set "GameFilter=12"
set "GameFilterTCP=12"
set "GameFilterUDP=12"

if not exist "%gameFlagFile%" exit /b

set "GameFilterTCPCandidate="
set "GameFilterUDPCandidate="
for /f "usebackq tokens=1,* delims==" %%A in ("%gameFlagFile%") do (
    if /i "%%A"=="mode" set "GameFilterMode=%%B"
    if /i "%%A"=="all" set "GameFilterMode=all"
    if /i "%%A"=="udp" (
        if "%%B"=="" (set "GameFilterMode=udp") else set "GameFilterUDPCandidate=%%B"
    )
    if /i "%%A"=="tcp" (
        if "%%B"=="" (set "GameFilterMode=tcp") else set "GameFilterTCPCandidate=%%B"
    )
)

call :validate_game_filter_range "%GameFilterTCPCandidate%"
if defined ValidatedGameFilterRange set "GameFilterTCPRange=%ValidatedGameFilterRange%"
call :validate_game_filter_range "%GameFilterUDPCandidate%"
if defined ValidatedGameFilterRange set "GameFilterUDPRange=%ValidatedGameFilterRange%"

if /i "%GameFilterMode%"=="all" (
    set "GameFilterStatus=enabled (TCP and UDP)"
    set "GameFilter=%GameFilterTCPRange%"
    set "GameFilterTCP=%GameFilterTCPRange%"
    set "GameFilterUDP=%GameFilterUDPRange%"
) else if /i "%GameFilterMode%"=="tcp" (
    set "GameFilterStatus=enabled (TCP)"
    set "GameFilter=%GameFilterTCPRange%"
    set "GameFilterTCP=%GameFilterTCPRange%"
    set "GameFilterUDP=12"
) else if /i "%GameFilterMode%"=="udp" (
    set "GameFilterStatus=enabled (UDP)"
    set "GameFilter=%GameFilterUDPRange%"
    set "GameFilterTCP=12"
    set "GameFilterUDP=%GameFilterUDPRange%"
) else (
    set "GameFilterMode=disabled"
)
exit /b

:validate_game_filter_range
set "ValidatedGameFilterRange="
setlocal EnableDelayedExpansion
set "GameFilterRangeToValidate=%~1"
set "GameFilterRangeToValidate=!GameFilterRangeToValidate: =!"
if not defined GameFilterRangeToValidate exit /b
for %%A in ("!GameFilterRangeToValidate:,=" "!") do (
    call :gf_validate_item "%%~A"
    if not defined GameFilterRangeItemValid exit /b
)
endlocal & set "ValidatedGameFilterRange=%GameFilterRangeToValidate%"
exit /b

:gf_validate_item
set "GameFilterRangeItemValid="
setlocal EnableDelayedExpansion
set "GameFilterRangeItem=%~1"

echo(!GameFilterRangeItem!| findstr /r /x /c:"[1-9][0-9]*" /c:"[1-9][0-9]*-[1-9][0-9]*" > nul || exit /b
for /f "tokens=1,2 delims=-" %%A in ("!GameFilterRangeItem!") do (
    set "GameFilterRangeStart=%%A"
    set "GameFilterRangeEnd=%%B"
)
if not defined GameFilterRangeEnd set "GameFilterRangeEnd=!GameFilterRangeStart!"

if not "!GameFilterRangeStart:~5,1!"=="" exit /b
if not "!GameFilterRangeEnd:~5,1!"=="" exit /b
set /a GameFilterRangeStartNumber=GameFilterRangeStart, GameFilterRangeEndNumber=GameFilterRangeEnd
if !GameFilterRangeStartNumber! gtr 65535 exit /b
if !GameFilterRangeEndNumber! gtr 65535 exit /b
if !GameFilterRangeStartNumber! gtr !GameFilterRangeEndNumber! exit /b

endlocal & set "GameFilterRangeItemValid=1"
exit /b
