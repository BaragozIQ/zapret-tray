// Проверка сайтов и отчёт о проблемах. Отличаем "сломан конфиг" от "сломан DNS": второй конфиг не чинит
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ZapretTray
{
    enum SiteStatus
    {
        Ok,               // открылся обычным путём
        OkViaSecureDns,   // открылся, но адрес нашёл только защищённый DNS: DNS Windows его не знает
        NoConnect,        // адрес есть, а сайт не открывается - это дело конфига
        NoDns             // адрес не нашёл никто - конфиг ни при чём
    }

    class SiteResult
    {
        public string Url;
        public SiteStatus Status;
        public double TlsMs;
        public int CurlExit;

        public bool Opened { get { return Status == SiteStatus.Ok || Status == SiteStatus.OkViaSecureDns; } }
        public bool DnsProblem { get { return Status == SiteStatus.OkViaSecureDns || Status == SiteStatus.NoDns; } }

        public string Text
        {
            get
            {
                switch (Status)
                {
                    case SiteStatus.Ok: return "открылся";
                    case SiteStatus.OkViaSecureDns: return "открылся через защищённый DNS, DNS Windows адрес не знает";
                    case SiteStatus.NoConnect: return "адрес есть, но не открылся (curl " + CurlExit + ")";
                    default: return "адрес не нашёл никто (curl " + CurlExit + ")";
                }
            }
        }
    }

    static class SiteCheck
    {
        // Числовые адреса: чтобы спросить DNS, DNS не нужен. Quad9 здесь нет: у него в проверке такой запрос не прошёл
        static readonly string[] SecureDns = { "https://1.1.1.1/dns-query", "https://8.8.8.8/dns-query" };

        const int CurlCantResolve = 6;

        static string Curl { get { return Path.Combine(Environment.SystemDirectory, "curl.exe"); } }

        public static bool Available { get { return File.Exists(Curl); } }

        public static SiteResult Run(string url, int timeoutSec)
        {
            var r = new SiteResult { Url = url };
            if (!Available)
            {
                r.Status = Util.HttpWorks(url, timeoutSec * 1000) ? SiteStatus.Ok : SiteStatus.NoConnect;
                return r;
            }

            if (Probe(url, null, timeoutSec, r))
            {
                r.Status = SiteStatus.Ok;
                return r;
            }
            if (r.CurlExit != CurlCantResolve)
            {
                r.Status = SiteStatus.NoConnect;
                return r;
            }

            // DNS Windows не знает адрес - спрашиваем защищённые сервера
            foreach (var doh in SecureDns)
            {
                if (Probe(url, doh, timeoutSec, r))
                {
                    r.Status = SiteStatus.OkViaSecureDns;
                    return r;
                }
                if (r.CurlExit != CurlCantResolve)
                {
                    r.Status = SiteStatus.NoConnect;
                    return r;
                }
            }
            r.Status = SiteStatus.NoDns;
            return r;
        }

        // Полный GET (а не HEAD), чтобы ловить и "заморозку" на 16-20 КБ. true - сервер ответил любым HTTP-кодом
        static bool Probe(string url, string doh, int timeoutSec, SiteResult r)
        {
            var args = "-s -o NUL -m " + timeoutSec + " --connect-timeout 3 -w \"%{http_code} %{time_connect} %{time_appconnect}\" ";
            if (doh != null) args += "--doh-url " + doh + " ";
            var psi = new ProcessStartInfo(Curl, args + "\"" + url + "\"")
            {
                UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true
            };
            try
            {
                using (var p = Process.Start(psi))
                {
                    var parts = p.StandardOutput.ReadToEnd().Trim().Split(' ');
                    p.WaitForExit();
                    double connect = 0, app = 0;
                    if (parts.Length > 2)
                    {
                        double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out connect);
                        double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out app);
                    }
                    r.CurlExit = p.ExitCode;
                    r.TlsMs = Math.Max(0, (app - connect) * 1000);
                    return p.ExitCode == 0 && parts.Length > 0 && parts[0] != "000";
                }
            }
            catch
            {
                r.CurlExit = -1;
                return false;
            }
        }

        // Все сайты параллельно, результат в том же порядке
        public static List<SiteResult> RunAll(IEnumerable<string> urls, int timeoutSec)
        {
            var tasks = urls.Select(u => Task.Factory.StartNew(() => Run(u, timeoutSec), TaskCreationOptions.LongRunning)).ToList();
            return tasks.Select(t => t.Result).ToList();
        }
    }

    // Отчёт "Что-то не работает": текст для вставки в чат, копия - файлом на рабочем столе.
    // Внешний IP, историю сайтов и свои списки сюда не пишем, имя пользователя заменяем
    static class ProblemReport
    {
        const int LogLines = 200;
        static readonly string[] DnsHosts = { "www.youtube.com", "i.ytimg.com", "discord.com" };
        static readonly string[] DnsServers = { null, "1.1.1.1", "8.8.8.8", "9.9.9.9" };   // null - DNS Windows
        // Другие обходчики: вместе с zapret они мешают друг другу
        static readonly string[] OtherBypass = { "goodbyedpi", "byedpi", "ciadpi", "spoofdpi", "warp-svc" };

        // Короткий вывод в начале отчёта: что не так и куда смотреть. Работает одинаково на любом компьютере
        static List<string> Conclusions(bool winws, bool internet, string vpn, List<string> others, bool service, List<SiteResult> sites, bool dnsFixed)
        {
            var o = new List<string>();
            if (!internet) o.Add("Нет интернета: дело не в программе.");
            if (!winws) o.Add("zapret не запущен: обхода сейчас нет.");
            if (service) o.Add("Установлена служба zapret: она мешает, нужно убрать через Setup.exe.");
            if (others.Count > 0) o.Add("Запущен другой обходчик (" + string.Join(", ", others) + "): может мешать.");
            if (vpn != null) o.Add("Трафик идёт через VPN (" + vpn + "): проверки ниже показывают его сеть, а не работу zapret.");
            var noConnect = sites.Where(r => r.Status == SiteStatus.NoConnect).Select(r => r.Url).ToList();
            var dns = sites.Where(r => r.DnsProblem).Select(r => r.Url).ToList();
            if (noConnect.Count > 0) o.Add("Адрес есть, но не открывается: " + string.Join(", ", noConnect) + ". Похоже на конфиг, нужен другой.");
            if (dns.Count > 0) o.Add("DNS не находит адрес: " + string.Join(", ", dns) + (dnsFixed ? ". Исправление DNS уже включено." : ". Поможет «Исправить DNS»."));
            if (o.Count == 0) o.Add("Явных проблем не видно: все проверенные сайты открываются.");
            return o;
        }

        // Что отвечает каждый обычный DNS: так видно, кто подменяет ответ
        static string DnsLine(string host, string server)
        {
            string output;
            Util.Run("nslookup.exe", "-timeout=3 " + host + (server != null ? " " + server : ""), out output, 15000);
            // Сверху nslookup пишет адрес самого DNS, ответ идёт после пустой строки
            var blank = output.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            var answer = blank >= 0 ? output.Substring(blank) : "";
            var ip = Regex.Match(answer, @"\b\d{1,3}(\.\d{1,3}){3}\b");
            return host + " через " + (server ?? "DNS Windows") + ": " + (ip.Success ? ip.Value : "не нашёл");
        }

        // Текст отчёта. Файл - запасной вариант, поэтому его ошибка отчёт не отменяет
        public static string Build(AppPaths paths, string status, List<string> userSites)
        {
            var o = new StringBuilder();
            var winws = Util.WinwsRunning();
            var vpn = Util.RunningVpn();
            var others = OtherBypass.Where(n => Process.GetProcessesByName(n).Length > 0).ToList();
            var service = Util.ServiceExists("zapret");
            var dnsFixed = DnsFix.IsApplied(paths);
            var internet = Util.InternetUp();
            var sites = SiteCheck.RunAll(Selector.Targets, 8);
            var ini = new Settings(paths.Settings);

            o.AppendLine("=== ZAPRET TRAY: ОТЧЁТ ===");
            o.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            o.AppendLine();
            o.AppendLine("=== ВЫВОД ===");
            foreach (var line in Conclusions(winws, internet, vpn, others, service, sites, dnsFixed)) o.AppendLine("- " + line);

            o.AppendLine();
            o.AppendLine("=== ПРОГРАММА ===");
            o.AppendLine("Программа " + AppInfo.Version + ", zapret " + ZapretUpdater.LocalVersion(paths));
            o.AppendLine("Windows: сборка " + DnsFix.WindowsBuild());
            o.AppendLine("Статус: " + status);
            o.AppendLine("winws: " + (winws ? "работает" : "не работает"));
            o.AppendLine("Конфиги: " + string.Join(", ", Util.ReadEntries(paths.BestConfig)));
            o.AppendLine("Сторож: " + (ini.GetBool("watchdog", true) ? "включён" : "выключен") + ", автообновление: " + (ini.GetBool("autoupdate", true) ? "включено" : "выключено"));
            o.AppendLine("Игровой фильтр: " + (File.Exists(Path.Combine(paths.Utils, "game_filter.enabled")) ? "включён" : "выключен"));
            o.AppendLine("Служба zapret: " + (service ? "есть" : "нет"));
            o.AppendLine("Другие обходчики: " + (others.Count > 0 ? string.Join(", ", others) : "нет"));
            o.AppendLine("Исправление DNS: " + (dnsFixed ? "включено" : "выключено"));

            o.AppendLine();
            o.AppendLine("=== САЙТЫ ===");
            foreach (var r in sites)
                o.AppendLine(r.Url + ": " + r.Text);

            // Только количество: сами сайты - личное
            if (userSites.Count > 0)
                o.AppendLine("Мои сайты: открылось " + SiteCheck.RunAll(userSites, 8).Count(r => r.Opened) + " из " + userSites.Count);

            o.AppendLine();
            o.AppendLine("=== ОБЫЧНЫЙ DNS ===");
            var dnsLines = DnsHosts.SelectMany(h => DnsServers.Select(s => Task.Factory.StartNew(() => DnsLine(h, s), TaskCreationOptions.LongRunning))).ToList();
            foreach (var t in dnsLines) o.AppendLine(t.Result);

            o.AppendLine();
            o.AppendLine("=== СЕТЬ ===");
            o.AppendLine("Интернет: " + (internet ? "есть" : "нет"));
            var route = Util.RouteAdapter();
            o.AppendLine("Трафик идёт через: " + (route != null ? route.Name + (Util.LooksLikeVpn(route) ? " (похоже на VPN)" : "") : "не удалось определить"));
            var clients = Util.VpnClientsRunning();
            o.AppendLine("Запущенные VPN-клиенты: " + (clients.Count > 0 ? string.Join(", ", clients) : "не найдены"));
            o.AppendLine("Системный прокси: " + (Util.SystemProxyOn() ? "включён" : "выключен"));
            o.AppendLine("Адаптеры:");
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    var dns = ni.GetIPProperties().DnsAddresses.Select(a => a.ToString());
                    o.AppendLine("- " + ni.Name + " (" + ni.NetworkInterfaceType + ", " + ni.Description + (Util.LooksLikeVpn(ni) ? ", похоже на VPN" : "") + "): DNS " + string.Join(", ", dns));
                }
            }
            catch (Exception ex) { o.AppendLine(ex.Message); }

            o.AppendLine();
            o.AppendLine("=== HOSTS: строки про YouTube, Google, Discord ===");
            try
            {
                var hosts = Path.Combine(Environment.SystemDirectory, @"drivers\etc\hosts");
                foreach (var l in File.ReadAllLines(hosts))
                {
                    var t = l.Trim();
                    if (t.StartsWith("#")) continue;
                    if (t.IndexOf("youtu", StringComparison.OrdinalIgnoreCase) >= 0 || t.IndexOf("ytimg", StringComparison.OrdinalIgnoreCase) >= 0
                        || t.IndexOf("google", StringComparison.OrdinalIgnoreCase) >= 0 || t.IndexOf("discord", StringComparison.OrdinalIgnoreCase) >= 0)
                        o.AppendLine(t);
                }
            }
            catch (Exception ex) { o.AppendLine(ex.Message); }

            o.AppendLine();
            o.AppendLine("=== ЖУРНАЛ ===");
            foreach (var l in Logger.Tail(LogLines)) o.AppendLine(l);

            var text = o.ToString()
                .Replace(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "<профиль>")
                .Replace(Environment.UserName, "<пользователь>");
            try
            {
                var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "zapret-report.txt");
                File.WriteAllText(file, text, new UTF8Encoding(true));
            }
            catch (Exception ex) { Logger.Write("отчёт: файл не записан: " + ex.Message); }
            return text;
        }
    }
}
