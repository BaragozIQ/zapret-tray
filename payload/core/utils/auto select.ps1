﻿# Автоподбор конфига: запускает general*.bat, проверяет сайты, оставляет запущенным лучший.
# -Mode fast: прошлый удачный конфиг, иначе первый, где всё работает (пинг не важен).
# -Mode best: проверяет все конфиги по несколько раз и берёт тот, где всё работает с минимальным пингом.
# -NoWait: без пауз и ожидания клавиши (для запуска из трея в скрытом окне).
# Код выхода: 0 - конфиг запущен, 1 - ошибка.

param(
    [ValidateSet("fast", "best")][string]$Mode = "fast",
    [switch]$NoWait
)

$rootDir = Split-Path $PSScriptRoot
$utilsDir = Join-Path $rootDir "utils"
$bestFile = Join-Path $utilsDir "best_config.txt"

$curlTimeout = 8
# Сколько раз мерить каждый конфиг в режиме best: пинг скачет, один замер - лотерея
$bestRounds = 3

# Полный GET (а не HEAD), чтобы ловить и "заморозку" на 16-20 КБ
$targets = [ordered]@{
    "Discord"         = "https://discord.com"
    "DiscordGateway"  = "https://gateway.discord.gg"
    "DiscordCDN"      = "https://cdn.discordapp.com"
    "DiscordUpdates"  = "https://updates.discord.com"
    "YouTube"         = "https://www.youtube.com"
    "YouTubeImage"    = "https://i.ytimg.com"
    "YouTubeVideo"    = "https://redirector.googlevideo.com"
    "Google"          = "https://www.google.com"
    "ArcRaiders"      = "https://arcraiders.com"
    "Embark"          = "https://id.embark.games"
}

function Wait-AnyKey {
    param([string]$message = "Нажми любую клавишу, чтобы закрыть...")
    if ($NoWait) { return }
    Write-Host $message -ForegroundColor Yellow
    try {
        while ([System.Console]::KeyAvailable) { [void][System.Console]::ReadKey($true) }
        [void][System.Console]::ReadKey($true)
    } catch {
        Start-Sleep -Seconds 5
    }
}

function Stop-Zapret {
    Get-Process -Name "winws" -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 300
}

function Wait-WinwsReady {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($timer.ElapsedMilliseconds -lt 5000) {
        if (Get-Process -Name "winws" -ErrorAction SilentlyContinue) {
            Start-Sleep -Milliseconds 700
            return $true
        }
        Start-Sleep -Milliseconds 200
    }
    return $false
}

function Start-Config {
    param($file)
    return Start-Process -FilePath "cmd.exe" -ArgumentList "/c `"$($file.FullName)`"" -WorkingDirectory $rootDir -PassThru -WindowStyle Hidden
}

# Все запросы стартуют параллельно, потом собираем результаты.
# Time = время TLS-рукопожатия в мс: тут DPI и тормозит, поэтому это и считаем "пингом"
function Invoke-Checks {
    $procs = @()
    foreach ($name in $targets.Keys) {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = "curl.exe"
        $psi.Arguments = "-s -o NUL -m $curlTimeout --connect-timeout 3 -w `"%{http_code} %{time_connect} %{time_appconnect}`" `"$($targets[$name])`""
        $psi.UseShellExecute = $false
        $psi.RedirectStandardOutput = $true
        $psi.CreateNoWindow = $true
        $procs += [pscustomobject]@{ Name = $name; Proc = [System.Diagnostics.Process]::Start($psi) }
    }

    $results = @()
    foreach ($p in $procs) {
        $out = $p.Proc.StandardOutput.ReadToEnd().Trim()
        $p.Proc.WaitForExit()
        $parts = $out -split ' '
        $code = $parts[0]
        $connect = 0.0
        $appconnect = 0.0
        if ($parts.Count -gt 2) {
            $inv = [Globalization.CultureInfo]::InvariantCulture
            [void][double]::TryParse($parts[1], [Globalization.NumberStyles]::Float, $inv, [ref]$connect)
            [void][double]::TryParse($parts[2], [Globalization.NumberStyles]::Float, $inv, [ref]$appconnect)
        }
        $time = [Math]::Max(0, ($appconnect - $connect) * 1000)
        $ok = ($p.Proc.ExitCode -eq 0) -and ($code -ne "000")
        $results += [pscustomobject]@{ Name = $p.Name; Ok = $ok; Time = $time }
    }
    return $results
}

# Несколько замеров подряд: сайт считается рабочим, только если открылся во всех,
# пинг - медиана средних по замерам
function Measure-Config {
    param([int]$rounds)
    $okCounts = @()
    $avgs = @()
    $failed = @()
    for ($r = 0; $r -lt $rounds; $r++) {
        $res = Invoke-Checks
        $okRes = @($res | Where-Object Ok)
        $okCounts += $okRes.Count
        $sum = ($okRes | Measure-Object -Property Time -Sum).Sum
        $avgs += if ($okRes.Count -gt 0) { $sum / $okRes.Count } else { 0 }
        $failed += $res | Where-Object { -not $_.Ok } | ForEach-Object Name
    }
    $sorted = @($avgs | Sort-Object)
    return [pscustomobject]@{
        Ok     = ($okCounts | Measure-Object -Minimum).Minimum
        Time   = $sorted[[int][Math]::Floor($sorted.Count / 2)]
        Failed = (@($failed | Select-Object -Unique)) -join ", "
    }
}

# Проверки окружения
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "[ОШИБКА] Нужны права администратора" -ForegroundColor Red
    Wait-AnyKey; exit 1
}
if (-not (Get-Command "curl.exe" -ErrorAction SilentlyContinue)) {
    Write-Host "[ОШИБКА] curl.exe не найден" -ForegroundColor Red
    Wait-AnyKey; exit 1
}
if (Get-Service -Name "zapret" -ErrorAction SilentlyContinue) {
    Write-Host "[ОШИБКА] Установлена служба 'zapret' - она мешает тесту" -ForegroundColor Red
    Write-Host "         Удали её: запусти Setup.exe и отметь 'Удалить службу zapret'" -ForegroundColor Yellow
    Wait-AnyKey; exit 1
}

$batFiles = Get-ChildItem -Path $rootDir -Filter "general*.bat" |
    Sort-Object { [Regex]::Replace($_.Name, "(\d+)", { $args[0].Value.PadLeft(8, "0") }) }
if (-not $batFiles) {
    Write-Host "[ОШИБКА] Не найдено ни одного general*.bat" -ForegroundColor Red
    Wait-AnyKey; exit 1
}

$env:NO_UPDATE_CHECK = "1"

# best_config.txt: до 3 запомненных конфигов, первый - лучший
$savedNames = @()
if (Test-Path $bestFile) {
    $savedNames = @(Get-Content $bestFile | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

if ($Mode -eq "fast" -and $savedNames) {
    foreach ($name in $savedNames) {
        $savedFile = $batFiles | Where-Object Name -eq $name
        if (-not $savedFile) { continue }
        Write-Host ""
        Write-Host "Проверяю запомненный конфиг: $($savedFile.BaseName)..." -ForegroundColor Cyan
        Stop-Zapret
        [void](Start-Config $savedFile)
        if (Wait-WinwsReady) {
            $res = Invoke-Checks
            if (@($res | Where-Object Ok).Count -eq $targets.Count) {
                # Рабочий - наверх списка, остальные запомненные за ним
                $order = @($name) + @($savedNames | Where-Object { $_ -ne $name })
                Set-Content -Path $bestFile -Value $order -Encoding UTF8
                Write-Host "Всё работает, zapret запущен." -ForegroundColor Green
                if (-not $NoWait) { Start-Sleep -Seconds 4 }
                exit 0
            }
        }
        Write-Host "Не работает." -ForegroundColor Yellow
    }
    Write-Host "Запомненные конфиги не работают - подбираю новый." -ForegroundColor Yellow
    Stop-Zapret
}

$rounds = if ($Mode -eq "best") { $bestRounds } else { 1 }

Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
if ($Mode -eq "best") {
    Write-Host "   ПОИСК КОНФИГА С МИНИМАЛЬНЫМ ПИНГОМ   (конфигов: $($batFiles.Count))" -ForegroundColor Cyan
} else {
    Write-Host "   БЫСТРЫЙ ПОДБОР КОНФИГА   (конфигов: $($batFiles.Count))" -ForegroundColor Cyan
}
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "Подожди, окно не закрывай." -ForegroundColor Yellow
Write-Host ""

$scores = @()
$i = 0
foreach ($file in $batFiles) {
    $i++
    $label = "[{0,2}/{1}] {2}" -f $i, $batFiles.Count, $file.BaseName
    Write-Host ($label.PadRight(42)) -NoNewline

    Stop-Zapret
    $proc = Start-Config $file
    if (-not (Wait-WinwsReady)) {
        Write-Host "не запустился" -ForegroundColor Red
        if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
        continue
    }

    $m = Measure-Config -rounds $rounds
    $allOk = ($m.Ok -eq $targets.Count)

    $color = if ($allOk) { "Green" } elseif ($m.Ok -ge $targets.Count / 2) { "Yellow" } else { "Red" }
    Write-Host ("{0}/{1}  (пинг {2:N0} мс)" -f $m.Ok, $targets.Count, $m.Time) -ForegroundColor $color -NoNewline
    if ($m.Failed) { Write-Host "  не работает: $($m.Failed)" -ForegroundColor DarkGray } else { Write-Host "" }

    $scores += [pscustomobject]@{ File = $file; Ok = $m.Ok; Time = $m.Time }
    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }

    if ($Mode -eq "fast" -and $allOk) {
        Write-Host ""
        Write-Host "Нашёлся конфиг, где всё работает - остальные не проверяю." -ForegroundColor Green
        break
    }
}

Stop-Zapret

if (-not $scores) {
    Write-Host ""
    Write-Host "[ОШИБКА] Ни один конфиг не запустился" -ForegroundColor Red
    Wait-AnyKey; exit 1
}

# Больше рабочих сайтов - лучше; при равенстве - меньше пинг
$top = @($scores | Sort-Object @{ Expression = "Ok"; Descending = $true }, @{ Expression = "Time"; Descending = $false })
$best = $top[0]

if ($best.Ok -eq 0) {
    Write-Host ""
    Write-Host "[ОШИБКА] Ни один сайт не открылся ни на одном конфиге - похоже, нет интернета" -ForegroundColor Red
    Wait-AnyKey; exit 1
}

if ($Mode -eq "best") {
    Write-Host ""
    Write-Host "Топ-3:" -ForegroundColor Cyan
    $top | Select-Object -First 3 | ForEach-Object {
        Write-Host ("  {0,-36} {1}/{2}  (пинг {3:N0} мс)" -f $_.File.BaseName, $_.Ok, $targets.Count, $_.Time)
    }
}

# Запоминаем до 3 полностью рабочих конфигов (или хотя бы лучший) - запасные для быстрого режима
$keep = @($top | Where-Object { $_.Ok -eq $targets.Count } | Select-Object -First 3)
if (-not $keep) { $keep = @($best) }
Set-Content -Path $bestFile -Value ($keep | ForEach-Object { $_.File.Name }) -Encoding UTF8

Write-Host ""
Write-Host "Запускаю: $($best.File.BaseName)" -ForegroundColor Green
[void](Start-Config $best.File)
Write-Host ""
Write-Host "Готово, zapret запущен. Окно закроется само." -ForegroundColor Green
if (-not $NoWait) { Start-Sleep -Seconds $(if ($Mode -eq "best") { 10 } else { 5 }) }
exit 0
