// Проверка сайтов и отчёт о проблемах. Отличаем "сломан конфиг" от "сломан DNS": второй конфиг не чинит
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.ServiceProcess;
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
        public bool Measured;     // curl успел сказать, докуда дошло соединение
        public bool Connected;    // подключились к адресу
        public bool TlsDone;      // шифрованное соединение установилось
        public long Bytes;        // сколько успело загрузиться
        public string HttpCode;

        // Докуда дошло: от этого зависит лечение. Адрес недоступен - конфиг может не помочь, обрыв на шифровании или заморозка - его дело
        public string Stage
        {
            get
            {
                if (!Measured) return "";
                if (!Connected) return "не подключился к адресу";
                if (!TlsDone) return "подключился, шифрование оборвалось";
                return Bytes > 0 ? "начал загружаться и завис на " + Bytes / 1024 + " КБ" : "соединился, сервер не ответил";
            }
        }

        // Строка для отчёта: у открывшегося сайта цифры, у остального этап
        public string Details
        {
            get
            {
                if (!Measured) return Text;
                if (Opened) return Text + " (HTTP " + HttpCode + ", шифрование " + (int)Math.Round(TlsMs) + " мс)";
                return Status == SiteStatus.NoConnect ? Text + ": " + Stage : Text;
            }
        }

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
            var args = "-s -o NUL -m " + timeoutSec + " --connect-timeout 3 -w \"%{http_code} %{time_connect} %{time_appconnect} %{size_download}\" ";
            if (doh != null) args += "--doh-url " + doh + " ";
            var psi = new ProcessStartInfo(Curl, args + "\"" + url + "\"")
            {
                UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true
            };
            try
            {
                using (var p = Process.Start(psi))
                {
                    var output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                    r.CurlExit = p.ExitCode;
                    ReadOutput(output, r);
                    return p.ExitCode == 0 && r.HttpCode != "000";
                }
            }
            catch
            {
                r.CurlExit = -1;
                return false;
            }
        }

        // Ответ curl: "код время_подключения время_шифрования байты"
        public static void ReadOutput(string output, SiteResult r)
        {
            var parts = output.Trim().Split(' ');
            double connect = 0, app = 0;
            long bytes = 0;
            r.HttpCode = parts[0].Length > 0 ? parts[0] : "000";
            r.Measured = parts.Length > 3;
            if (r.Measured)
            {
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out connect);
                double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out app);
                long.TryParse(parts[3], out bytes);
            }
            r.Connected = connect > 0;
            r.TlsDone = app > 0;
            r.Bytes = bytes;
            r.TlsMs = Math.Max(0, (app - connect) * 1000);
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
        // Обычно не заблокированы: не открылись и они - беда общая, а не блокировка
        static readonly string[] ControlSites = { "https://ya.ru", "https://www.microsoft.com" };
        static readonly string[] EngineFiles = { "winws.exe", "cygwin1.dll", "WinDivert.dll", "WinDivert64.sys" };
        // Службы, которые известны тем, что мешают WinDivert: сетевые "ускорители" и фильтры
        static readonly string[] ConflictServices = { "killer", "smartbyte", "check point", "checkpoint", "intel connectivity", "adguard" };

        // Состояние компьютера без единого обращения в сеть: то, что ломает zapret на любой сети
        public class LocalState
        {
            public List<string> MissingFiles = new List<string>();
            public List<string> Conflicts = new List<string>();
            public List<string> Antivirus = new List<string>();
            public string Bfe, Driver, Timestamps;
            public int WinwsCount;
            public string WinwsSince = "";
        }

        public static LocalState ReadLocalState(AppPaths paths)
        {
            var s = new LocalState();
            s.MissingFiles = EngineFiles.Where(n => !File.Exists(Path.Combine(paths.Bin, n))).ToList();
            s.Bfe = ServiceState("BFE");
            s.Driver = ServiceState("WinDivert");

            string output;
            s.Conflicts = FindConflicts(RunningServiceNames());

            // Названия приходят в base64: кодировка вывода команд зависит от настроек Windows, а название может быть русским
            Util.Run("powershell.exe", "-NoProfile -NonInteractive -Command \"$n = Get-CimInstance -Namespace root/SecurityCenter2 -ClassName AntiVirusProduct | ForEach-Object { $_.displayName }; [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($n -join '|')))\"", out output, 20000);
            try
            {
                var encoded = Regex.Match(output, @"^[A-Za-z0-9+/]+=*\s*$", RegexOptions.Multiline).Value.Trim();
                s.Antivirus = Encoding.UTF8.GetString(Convert.FromBase64String(encoded)).Split('|').Where(n => n.Length > 0).Distinct().ToList();
            }
            catch (FormatException) { }

            Util.Run("netsh.exe", "interface tcp show global", out output, 15000);
            var stamp = Regex.Match(output, @"^.*(1323|timestamps).*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            // Берём только значение после двоеточия: название строки переведено, и кодировка у него бывает разная. В китайской Windows двоеточие широкое
            s.Timestamps = stamp.Success ? stamp.Value.Substring(stamp.Value.LastIndexOfAny(new[] { ':', '：' }) + 1).Trim() : "не удалось узнать";

            var running = Process.GetProcessesByName("winws");
            s.WinwsCount = running.Length;
            try { if (running.Length > 0) s.WinwsSince = running[0].StartTime.ToString("HH:mm:ss"); } catch { }
            return s;
        }

        // Службы спрашиваем у системы, а не читаем текст команды sc: ответ не зависит от языка Windows
        static IEnumerable<string> RunningServiceNames()
        {
            try
            {
                var services = ServiceController.GetServices();
                var names = services.Where(x => x.Status == ServiceControllerStatus.Running).SelectMany(x => new[] { x.ServiceName, x.DisplayName }).ToList();
                foreach (var x in services) x.Dispose();
                return names;
            }
            catch { return new string[0]; }
        }

        public static List<string> FindConflicts(IEnumerable<string> names)
        {
            return names.Where(n => ConflictServices.Any(c => n.IndexOf(c, StringComparison.OrdinalIgnoreCase) >= 0))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        // "Running", "Stopped" или "нет"
        static string ServiceState(string name)
        {
            try
            {
                using (var service = new ServiceController(name)) return service.Status.ToString();
            }
            catch (InvalidOperationException) { return "нет"; }
        }

        // Короткий вывод в начале отчёта: что не так и куда смотреть. Работает одинаково на любом компьютере
        static List<string> Conclusions(bool winws, bool internet, string vpn, List<string> others, bool service, List<SiteResult> sites, bool dnsFixed, LocalState local, List<SiteResult> control)
        {
            var o = new List<string>();
            if (!internet) o.Add("Нет интернета: дело не в программе.");
            if (local.MissingFiles.Count > 0) o.Add("Нет файлов zapret: " + string.Join(", ", local.MissingFiles) + ". Обычно их удаляет антивирус: переустановить и добавить папку в его исключения.");
            if (local.Bfe != "Running") o.Add("Служба Base Filtering Engine (BFE): " + local.Bfe + ". Без неё zapret не работает, нужно включить.");
            if (!winws) o.Add("zapret не запущен: обхода сейчас нет." + (local.Antivirus.Count > 0 ? " Антивирус: " + string.Join(", ", local.Antivirus) + ", проверить, не блокирует ли он." : ""));
            if (local.WinwsCount > 1) o.Add("Запущено несколько winws (" + local.WinwsCount + "): мешают друг другу.");
            if (local.Conflicts.Count > 0) o.Add("Запущено то, что может мешать: " + string.Join(", ", local.Conflicts) + ".");
            if (internet && control.Count > 0 && control.All(r => !r.Opened)) o.Add("Не открываются даже обычные незаблокированные сайты: беда общая (антивирус, прокси, время на часах), а не блокировка.");
            if (service) o.Add("Установлена служба zapret: она мешает, нужно убрать через Setup.exe.");
            if (others.Count > 0) o.Add("Запущен другой обходчик (" + string.Join(", ", others) + "): может мешать.");
            if (vpn != null) o.Add("Трафик идёт через VPN (" + vpn + "): проверки ниже показывают его сеть, а не работу zapret.");
            var failed = sites.Where(r => r.Status == SiteStatus.NoConnect).ToList();
            var noAddress = failed.Where(r => r.Measured && !r.Connected).Select(r => r.Url).ToList();
            var noConnect = failed.Where(r => !r.Measured || r.Connected).Select(r => r.Url).ToList();
            if (noAddress.Count > 0) o.Add("Не удаётся подключиться к адресу: " + string.Join(", ", noAddress) + ". Похоже на блокировку по IP или проблему сети, другой конфиг может не помочь.");
            var dns = sites.Where(r => r.DnsProblem).Select(r => r.Url).ToList();
            if (noConnect.Count > 0) o.Add("Соединение обрывается или виснет: " + string.Join(", ", noConnect) + ". Похоже на конфиг, нужен другой.");
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
            var control = SiteCheck.RunAll(ControlSites, 8);
            var local = ReadLocalState(paths);
            var ini = new Settings(paths.Settings);

            o.AppendLine("=== ZAPRET TRAY: ОТЧЁТ ===");
            o.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            o.AppendLine();
            o.AppendLine("=== ВЫВОД ===");
            foreach (var line in Conclusions(winws, internet, vpn, others, service, sites, dnsFixed, local, control)) o.AppendLine("- " + line);

            o.AppendLine();
            o.AppendLine("=== ПРОГРАММА ===");
            o.AppendLine("Программа " + AppInfo.Version + ", zapret " + ZapretUpdater.LocalVersion(paths));
            o.AppendLine("Windows: сборка " + DnsFix.WindowsBuild());
            o.AppendLine("Статус: " + status);
            o.AppendLine("winws: " + (winws ? "работает, запущено " + local.WinwsCount + ", с " + local.WinwsSince : "не работает"));
            o.AppendLine("Конфиги: " + string.Join(", ", Util.ReadEntries(paths.BestConfig)));
            o.AppendLine("Сторож: " + (ini.GetBool("watchdog", true) ? "включён" : "выключен") + ", автообновление: " + (ini.GetBool("autoupdate", true) ? "включено" : "выключено"));
            o.AppendLine("Игровой фильтр: " + (File.Exists(Path.Combine(paths.Utils, "game_filter.enabled")) ? "включён" : "выключен"));
            o.AppendLine("Служба zapret: " + (service ? "есть" : "нет"));
            o.AppendLine("Другие обходчики: " + (others.Count > 0 ? string.Join(", ", others) : "нет"));
            o.AppendLine("Исправление DNS: " + (dnsFixed ? "включено" : "выключено"));
            // Только количество: сами записи - личное
            o.AppendLine("Мои списки: сайтов " + Util.ReadEntries(paths.ListGeneralUser).Count + ", исключений " + Util.ReadEntries(paths.ListExcludeUser).Count + ", IP " + Util.ReadEntries(paths.IpsetUser).Count);

            o.AppendLine();
            o.AppendLine("=== КОМПЬЮТЕР ===");
            o.AppendLine("Файлы zapret: " + (local.MissingFiles.Count == 0 ? "все на месте" : "нет " + string.Join(", ", local.MissingFiles)));
            o.AppendLine("Служба BFE: " + local.Bfe + ", драйвер WinDivert: " + local.Driver);
            o.AppendLine("Антивирус: " + (local.Antivirus.Count > 0 ? string.Join(", ", local.Antivirus) : "не определён"));
            o.AppendLine("Службы, которые могут мешать: " + (local.Conflicts.Count > 0 ? string.Join(", ", local.Conflicts) : "не найдены"));
            o.AppendLine("Метки времени TCP: " + local.Timestamps);
            o.AppendLine("С последней загрузки: " + (int)TimeSpan.FromMilliseconds((uint)Environment.TickCount).TotalHours + " ч");

            o.AppendLine();
            o.AppendLine("=== САЙТЫ ===");
            foreach (var r in sites)
                o.AppendLine(r.Url + ": " + r.Details);
            o.AppendLine("Обычные сайты для сравнения:");
            foreach (var r in control)
                o.AppendLine(r.Url + ": " + r.Details);

            // Только количество: сами сайты - личное
            if (userSites.Count > 0)
            {
                var mine = SiteCheck.RunAll(userSites, 8);
                o.AppendLine("Мои сайты: открылось " + mine.Count(r => r.Opened) + " из " + userSites.Count);
                foreach (var g in mine.Where(r => !r.Opened).GroupBy(r => r.Status == SiteStatus.NoConnect ? r.Stage : "адрес не нашёл никто"))
                    o.AppendLine("- " + (g.Key.Length > 0 ? g.Key : "не открылся") + ": " + g.Count());
            }

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
                    // Есть ли IPv6 важно: через него сайт может идти мимо правил. Сам адрес не пишем
                    bool ipv6 = ni.GetIPProperties().UnicastAddresses.Any(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && !a.Address.IsIPv6LinkLocal);
                    o.AppendLine("- " + ni.Name + " (" + ni.NetworkInterfaceType + ", " + ni.Description + (Util.LooksLikeVpn(ni) ? ", похоже на VPN" : "") + ", IPv6 " + (ipv6 ? "есть" : "нет") + "): DNS " + string.Join(", ", dns));
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
