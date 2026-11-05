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
        // Сайты по умолчанию, если список проверок пуст
        public static readonly string[] Targets = CheckStore.Defaults.Select(c => c.Url).ToArray();

        // Что реально проверяем сейчас (список из настроек «Обязательные проверки»)
        public static string[] CheckUrls(AppPaths paths)
        {
            var all = new CheckStore(paths).All();
            return (all.Count > 0 ? all : CheckStore.Defaults.ToList()).Select(c => c.Url).ToArray();
        }

        const int CurlTimeout = 8;
        const int ConfirmPauseMs = 4000;
        const int BestRounds = 3;   // в режиме best меряем по 3 раза: пинг скачет, один замер - лотерея

        class Score
        {
            public string File;
            public int ReqOk;   // сколько обязательных открылось
            public int OptOk;   // сколько необязательных открылось
            public double Time;
        }

        // best=false: прошлый удачный конфиг, иначе первый, где всё работает.
        // best=true: все конфиги по 3 раза, берём тот, где всё работает с минимальным пингом.
        // cancelled() - выключили посреди подбора. progress - текст для подсказки у значка
        // underVpn - запуск при включённом VPN: замеры показывают сеть VPN, а не zapret. Конфиг не меняем, ставим запомненный
        public static bool Run(AppPaths paths, bool best, bool underVpn, Func<bool> cancelled, Action<string> progress, out string error)
        {
            error = null;
            if (!SiteCheck.Available) { error = "не найден curl.exe (нужна Windows 10 1803 или новее)"; return false; }

            var bats = Directory.GetFiles(paths.Core, "general*.bat")
                .OrderBy(f => Regex.Replace(Path.GetFileName(f), @"\d+", m => m.Value.PadLeft(8, '0')))
                .ToList();
            if (bats.Count == 0) { error = "не найдено ни одного general*.bat"; return false; }

            // Проверки из настроек: под обязательные подбираем, необязательные - для сведения.
            var checks = new CheckStore(paths).All();
            if (checks.Count == 0) checks = CheckStore.Defaults.ToList();
            var urls = checks.Select(c => c.Url).ToArray();
            var required = checks.Select(c => c.Required).ToArray();
            int reqCount = required.Count(x => x);
            if (reqCount == 0) { for (int i = 0; i < required.Length; i++) required[i] = true; reqCount = urls.Length; }  // ни одной обязательной - считаем все

            // До 3 запомненных конфигов, первый - лучший
            var saved = Util.ReadEntries(paths.BestConfig);

            var kept = underVpn && !best ? saved.Select(n => bats.FirstOrDefault(b => string.Equals(Path.GetFileName(b), n, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(b => b != null) : null;
            if (kept != null)
            {
                StopZapret();
                StartConfig(paths, kept);
                Logger.Write("подбор: VPN, оставляю " + Path.GetFileName(kept) + " без проверки");
                if (WaitWinwsReady()) return true;
            }

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
                    if (WaitWinwsReady() && AllRequiredOpen(urls, required, name) && AllRequiredOpenAfterPause(urls, required, name))
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

                var m = Measure(rounds, urls, required, Path.GetFileName(file));
                m.File = file;
                scores.Add(m);
                // Быстрый режим: первый, где открылись все обязательные. Случайную удачу отсеивает вторая проверка
                if (!best && m.ReqOk == reqCount)
                {
                    if (AllRequiredOpenAfterPause(urls, required, Path.GetFileName(file))) break;
                    m.ReqOk--;
                }
            }
            StopZapret();
            if (cancelled()) return false;

            if (scores.Count == 0) { error = "ни один конфиг не запустился"; return false; }

            // Больше обязательных - лучше; при равенстве больше необязательных, потом меньше пинг
            var top = scores.OrderByDescending(s => s.ReqOk).ThenByDescending(s => s.OptOk).ThenBy(s => s.Time).ToList();
            var winner = top[0];
            Logger.Write("подбор: выбран " + Path.GetFileName(winner.File) + ", обязательных открылось " + winner.ReqOk + " из " + reqCount);
            if (winner.ReqOk == 0 && winner.OptOk == 0) { error = "ни один сайт не открылся ни на одном конфиге - похоже, нет интернета"; return false; }

            // Запоминаем до 3 конфигов, где открылись все обязательные (или хотя бы лучший) - запасные для быстрого режима
            var keep = top.Where(s => s.ReqOk == reqCount).Take(3).ToList();
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

        // Все сайты параллельно (в порядке urls). Время = TLS-рукопожатие в мс: тут DPI и тормозит, поэтому это и считаем "пингом"
        static List<SiteResult> Probe(string[] urls, string config)
        {
            var results = SiteCheck.RunAll(urls, CurlTimeout);
            var failed = results.Where(r => !r.Opened).Select(r => r.Url.Replace("https://", "") + ": " + r.Text).ToList();
            Logger.Write("подбор: " + config + ": открылось " + (results.Count - failed.Count) + " из " + results.Count
                         + (failed.Count > 0 ? " (" + string.Join("; ", failed) + ")" : ""));
            return results;
        }

        // Считаем, сколько обязательных и необязательных открылось, и средний пинг по открывшимся
        static void CountOpen(List<SiteResult> res, bool[] required, out int reqOk, out int optOk, out double avgTime)
        {
            reqOk = optOk = 0;
            double sum = 0;
            int opened = 0;
            for (int i = 0; i < res.Count; i++)
            {
                if (!res[i].Opened) continue;
                if (i < required.Length && required[i]) reqOk++; else optOk++;
                sum += res[i].TlsMs;
                opened++;
            }
            avgTime = opened > 0 ? sum / opened : 0;
        }

        static bool AllRequiredOpen(string[] urls, bool[] required, string config)
        {
            var res = Probe(urls, config);
            for (int i = 0; i < res.Count; i++) if (i < required.Length && required[i] && !res[i].Opened) return false;
            return true;
        }

        static bool AllRequiredOpenAfterPause(string[] urls, bool[] required, string config)
        {
            Thread.Sleep(ConfirmPauseMs);
            return AllRequiredOpen(urls, required, config + " (повторно)");
        }

        // Сайт считается рабочим, только если открылся во всех замерах; пинг - медиана средних по замерам
        static Score Measure(int rounds, string[] urls, bool[] required, string config)
        {
            var reqOks = new List<int>();
            var optOks = new List<int>();
            var avgs = new List<double>();
            for (int r = 0; r < rounds; r++)
            {
                int ro, oo;
                double at;
                CountOpen(Probe(urls, config), required, out ro, out oo, out at);
                reqOks.Add(ro);
                optOks.Add(oo);
                avgs.Add(at);
            }
            avgs.Sort();
            return new Score { ReqOk = reqOks.Min(), OptOk = optOks.Min(), Time = avgs[avgs.Count / 2] };
        }
    }
}
