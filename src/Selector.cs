// Подбор конфига zapret: запускаем general*.bat по очереди, проверяем сайты, оставляем запущенным лучший.
// Раньше это делал PowerShell-скрипт "auto select.ps1", но антивирусы принимали его за вредонос (Trojan:Script/Wacatac!ml)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace ZapretTray
{
    static class Selector
    {
        // Полный GET (а не HEAD), чтобы ловить и "заморозку" на 16-20 КБ
        static readonly string[] Targets =
        {
            "https://discord.com", "https://gateway.discord.gg", "https://cdn.discordapp.com", "https://updates.discord.com",
            "https://www.youtube.com", "https://i.ytimg.com", "https://redirector.googlevideo.com", "https://www.google.com",
            "https://arcraiders.com", "https://id.embark.games",
        };

        const int CurlTimeout = 8;
        const int BestRounds = 3;   // в режиме best меряем по 3 раза: пинг скачет, один замер - лотерея

        class Score
        {
            public string File;
            public int Ok;
            public double Time;
        }

        static string Curl { get { return Path.Combine(Environment.SystemDirectory, "curl.exe"); } }

        // best=false: прошлый удачный конфиг, иначе первый, где всё работает.
        // best=true: все конфиги по 3 раза, берём тот, где всё работает с минимальным пингом.
        // cancelled() - выключили посреди подбора. progress - текст для подсказки у значка
        public static bool Run(AppPaths paths, bool best, Func<bool> cancelled, Action<string> progress, out string error)
        {
            error = null;
            if (!File.Exists(Curl)) { error = "не найден curl.exe (нужна Windows 10 1803 или новее)"; return false; }

            var bats = Directory.GetFiles(paths.Core, "general*.bat")
                .OrderBy(f => Regex.Replace(Path.GetFileName(f), @"\d+", m => m.Value.PadLeft(8, '0')))
                .ToList();
            if (bats.Count == 0) { error = "не найдено ни одного general*.bat"; return false; }

            // До 3 запомненных конфигов, первый - лучший
            var saved = Util.ReadEntries(paths.BestConfig);

            if (!best && saved.Count > 0)
            {
                foreach (var name in saved)
                {
                    var file = bats.FirstOrDefault(b => string.Equals(Path.GetFileName(b), name, StringComparison.OrdinalIgnoreCase));
                    if (file == null) continue;
                    if (cancelled()) return false;
                    progress("Проверяю " + Path.GetFileNameWithoutExtension(file) + "...");
                    StopZapret();
                    StartConfig(paths, file);
                    if (WaitWinwsReady() && Check().Count(r => r.Item1) == Targets.Length)
                    {
                        // Рабочий - наверх списка, остальные запомненные за ним
                        Util.WriteLinesAtomic(paths.BestConfig, new[] { name }.Concat(saved.Where(n => n != name)));
                        return true;
                    }
                }
                StopZapret();
            }

            int rounds = best ? BestRounds : 1;
            var scores = new List<Score>();
            for (int i = 0; i < bats.Count; i++)
            {
                if (cancelled()) { StopZapret(); return false; }
                var file = bats[i];
                progress((best ? "Ищу конфиг с мин. пингом: " : "Подбираю конфиг: ") + (i + 1) + "/" + bats.Count);
                StopZapret();
                StartConfig(paths, file);
                if (!WaitWinwsReady()) continue;

                var m = Measure(rounds);
                m.File = file;
                scores.Add(m);
                if (!best && m.Ok == Targets.Length) break;   // быстрый режим: первый, где всё работает
            }
            StopZapret();
            if (cancelled()) return false;

            if (scores.Count == 0) { error = "ни один конфиг не запустился"; return false; }

            // Больше рабочих сайтов - лучше; при равенстве - меньше пинг
            var top = scores.OrderByDescending(s => s.Ok).ThenBy(s => s.Time).ToList();
            var winner = top[0];
            if (winner.Ok == 0) { error = "ни один сайт не открылся ни на одном конфиге - похоже, нет интернета"; return false; }

            // Запоминаем до 3 полностью рабочих конфигов (или хотя бы лучший) - запасные для быстрого режима
            var keep = top.Where(s => s.Ok == Targets.Length).Take(3).ToList();
            if (keep.Count == 0) keep.Add(winner);
            Util.WriteLinesAtomic(paths.BestConfig, keep.Select(s => Path.GetFileName(s.File)));

            // Ждём, пока winws поднимется: трей сразу после подбора проверяет, что он работает
            StartConfig(paths, winner.File);
            if (!WaitWinwsReady()) { error = "конфиг " + Path.GetFileNameWithoutExtension(winner.File) + " не запустился"; return false; }
            return true;
        }

        public static void StartConfig(AppPaths paths, string bat)
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c \"\"" + bat + "\"\"")
            {
                WorkingDirectory = paths.Core, UseShellExecute = false, CreateNoWindow = true
            };
            psi.EnvironmentVariables["NO_UPDATE_CHECK"] = "1";
            // Конфиги зовут "call service.bat" из текущей папки. С этой переменной cmd текущую папку не ищет,
            // service.bat не находится, и winws падает с "bad value for --wf-tcp"
            psi.EnvironmentVariables.Remove("NoDefaultCurrentDirectoryInExePath");
            try { using (Process.Start(psi)) { } } catch { }
        }

        static void StopZapret()
        {
            Util.KillWinws();
            Thread.Sleep(300);
        }

        static bool WaitWinwsReady()
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 5000)
            {
                if (Util.WinwsRunning()) { Thread.Sleep(700); return true; }
                Thread.Sleep(200);
            }
            return false;
        }

        // Все сайты параллельно. Время = TLS-рукопожатие в мс: тут DPI и тормозит, поэтому это и считаем "пингом"
        static List<Tuple<bool, double>> Check()
        {
            var procs = new List<Process>();
            foreach (var url in Targets)
            {
                var psi = new ProcessStartInfo(Curl,
                    "-s -o NUL -m " + CurlTimeout + " --connect-timeout 3 -w \"%{http_code} %{time_connect} %{time_appconnect}\" \"" + url + "\"")
                {
                    UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true
                };
                try { procs.Add(Process.Start(psi)); } catch { procs.Add(null); }
            }

            var results = new List<Tuple<bool, double>>();
            foreach (var p in procs)
            {
                if (p == null) { results.Add(Tuple.Create(false, 0.0)); continue; }
                using (p)
                {
                    var parts = p.StandardOutput.ReadToEnd().Trim().Split(' ');
                    p.WaitForExit();
                    double connect = 0, app = 0;
                    if (parts.Length > 2)
                    {
                        double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out connect);
                        double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out app);
                    }
                    bool ok = p.ExitCode == 0 && parts.Length > 0 && parts[0] != "000";
                    results.Add(Tuple.Create(ok, Math.Max(0, (app - connect) * 1000)));
                }
            }
            return results;
        }

        // Сайт считается рабочим, только если открылся во всех замерах; пинг - медиана средних по замерам
        static Score Measure(int rounds)
        {
            var okCounts = new List<int>();
            var avgs = new List<double>();
            for (int r = 0; r < rounds; r++)
            {
                var res = Check().Where(x => x.Item1).ToList();
                okCounts.Add(res.Count);
                avgs.Add(res.Count > 0 ? res.Average(x => x.Item2) : 0);
            }
            avgs.Sort();
            return new Score { Ok = okCounts.Min(), Time = avgs[avgs.Count / 2] };
        }
    }
}
