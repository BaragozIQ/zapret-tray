// Общий код для ZapretTray.exe и Setup.exe (C# 5, .NET Framework 4.x)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace ZapretTray
{
    static class AppInfo
    {
        public const string Name = "Zapret Tray";
        public const string Version = "0.7";
        public const string RunTask = "ZapretTray";
        public const string AutoTask = "ZapretTray Autostart";
        public const string TrayExe = "ZapretTray.exe";
        public const string SetupExe = "Setup.exe";
        public const string ShortcutName = "Zapret.lnk";
        public const string Repo = "BaragozIQ/zapret-tray";   // отсюда берутся обновления (GitHub Releases)
    }

    // Пути внутри установленной программы
    class AppPaths
    {
        public readonly string Root;
        public AppPaths(string root) { Root = root.TrimEnd('\\'); }

        public string Core { get { return Path.Combine(Root, "core"); } }
        public string Bin { get { return Path.Combine(Core, "bin"); } }
        public string Lists { get { return Path.Combine(Core, "lists"); } }
        public string Utils { get { return Path.Combine(Core, "utils"); } }
        public string GamesDir { get { return Path.Combine(Lists, "games"); } }

        public string BestConfig { get { return Path.Combine(Utils, "best_config.txt"); } }
        public string Settings { get { return Path.Combine(Utils, "tray.ini"); } }
        public string ServiceBat { get { return Path.Combine(Core, "service.bat"); } }

        public string IpsetAll { get { return Path.Combine(Lists, "ipset-all.txt"); } }
        public string IpsetUser { get { return Path.Combine(Lists, "tray-ipset-user.txt"); } }
        public string IpsetAdded { get { return Path.Combine(Lists, "tray-ipset-added.txt"); } }
        public string GamesList { get { return Path.Combine(Lists, "tray-games.txt"); } }
        public string ListGeneralUser { get { return Path.Combine(Lists, "list-general-user.txt"); } }
        public string ListExcludeUser { get { return Path.Combine(Lists, "list-exclude-user.txt"); } }
        public string IpsetExcludeUser { get { return Path.Combine(Lists, "ipset-exclude-user.txt"); } }

        public string TrayExePath { get { return Path.Combine(Root, AppInfo.TrayExe); } }
        public string SetupExePath { get { return Path.Combine(Root, AppInfo.SetupExe); } }

        // Файлы пользователя: при переустановке не перезаписываются
        public static readonly string[] UserFiles = {
            @"lists\list-general-user.txt", @"lists\list-exclude-user.txt", @"lists\ipset-exclude-user.txt",
            @"lists\tray-ipset-user.txt", @"lists\tray-ipset-added.txt", @"lists\tray-games.txt",
            @"utils\best_config.txt", @"utils\tray.ini"
        };
    }

    static class Util
    {
        public static bool IsAdmin()
        {
            var p = new WindowsPrincipal(WindowsIdentity.GetCurrent());
            return p.IsInRole(WindowsBuiltInRole.Administrator);
        }

        // Запуск скрытой команды, возвращает код выхода (вывод - в output)
        public static int Run(string exe, string args, out string output, int timeoutMs = 30000)
        {
            output = "";
            try
            {
                var psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
                using (var p = Process.Start(psi))
                {
                    var err = p.StandardError.ReadToEndAsync();
                    output = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return -1; }
                    output += err.Result;
                    return p.ExitCode;
                }
            }
            catch (Exception ex) { output = ex.Message; return -1; }
        }

        public static int Run(string exe, string args)
        {
            string o;
            return Run(exe, args, out o);
        }

        public static bool WinwsRunning()
        {
            return Process.GetProcessesByName("winws").Length > 0;
        }

        public static void KillWinws()
        {
            foreach (var p in Process.GetProcessesByName("winws"))
            {
                try { p.Kill(); p.WaitForExit(3000); } catch { }
            }
        }

        public static bool ServiceExists(string name)
        {
            string o;
            return Run("sc.exe", "query \"" + name + "\"", out o) == 0;
        }

        // Так помечаем выключенный пользователем домен встроенной игры (для zapret это комментарий)
        public const string OffPrefix = "#off ";

        // Чтение списков: без пустых строк и комментариев, без пробелов по краям
        public static List<string> ReadEntries(string path)
        {
            var result = new List<string>();
            if (!File.Exists(path)) return result;
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim().TrimStart('﻿');
                if (line.Length == 0 || line.StartsWith("#")) continue;
                result.Add(line);
            }
            return result;
        }

        // Атомарная запись: winws может читать файл в этот момент
        public static void WriteLinesAtomic(string path, IEnumerable<string> lines)
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        // Добавить строку в список, если её там ещё нет. true - добавлено
        public static bool AppendUnique(string path, string entry)
        {
            var existing = ReadEntries(path);
            if (existing.Any(e => string.Equals(e, entry, StringComparison.OrdinalIgnoreCase))) return false;
            var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
            while (lines.Count > 0 && lines[lines.Count - 1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
            lines.Add(entry);
            WriteLinesAtomic(path, lines);
            return true;
        }

        public static bool RemoveEntry(string path, string entry)
        {
            if (!File.Exists(path)) return false;
            var lines = File.ReadAllLines(path).ToList();
            int removed = lines.RemoveAll(l => string.Equals(l.Trim(), entry, StringComparison.OrdinalIgnoreCase));
            if (removed > 0) WriteLinesAtomic(path, lines);
            return removed > 0;
        }

        // Разбор того, что ввёл пользователь: домен, IP или подсеть. Можно вставить ссылку как есть, останется только сайт.
        // Поддомены, кроме www, не срезаем: иначе обход или исключение заденет весь сайт, а не то, что просили
        public static bool ParseEntry(string text, out string value, out bool isIp)
        {
            value = null;
            isIp = false;
            if (text == null) return false;
            var s = text.Trim().ToLowerInvariant();
            if (s.StartsWith("#")) return false;   // комментарий из файла, даже если в нём упомянут сайт
            // Вставили с текстом вокруг: берём первое слово с точкой
            var words = s.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 1) s = words.FirstOrDefault(w => w.Contains('.')) ?? s;
            s = s.Trim('"', '\'', '`', '<', '>', '(', ')', '[', ']', '{', '}', ',', ';', '!', '|', '«', '»').Replace('\\', '/');
            s = Regex.Replace(s, @"^([a-z]+:/*|//)", "");
            int tailAt = s.IndexOfAny(new[] { '?', '#' });
            if (tailAt >= 0) s = s.Substring(0, tailAt);
            int slash = s.IndexOf('/');
            // "1.2.3.0/24" - подсеть, "site.com/path" - отрезаем путь
            if (slash >= 0)
            {
                var head = s.Substring(0, slash);
                var tail = s.Substring(slash + 1).TrimEnd('/');
                int bits;
                IPAddress ipNet;
                if (IPAddress.TryParse(head, out ipNet) && ipNet.AddressFamily == AddressFamily.InterNetwork
                    && int.TryParse(tail, out bits))
                {
                    if (bits < 8 || bits > 32) return false;   // такая подсеть слишком широкая, молча брать один адрес нельзя
                    value = head + "/" + bits;
                    isIp = true;
                    return true;
                }
                s = head;
            }
            s = s.Substring(s.LastIndexOf('@') + 1);   // логин в ссылке: user@site.com
            s = s.TrimStart('*', '.');
            int colon = s.LastIndexOf(':');
            if (colon > 0 && s.IndexOf(':') == colon) s = s.Substring(0, colon);
            s = s.TrimEnd('.');
            if (s.StartsWith("www.") && s.IndexOf('.', 4) > 0) s = s.Substring(4);

            IPAddress ip;
            if (IPAddress.TryParse(s, out ip) && ip.AddressFamily == AddressFamily.InterNetwork && s.Count(c => c == '.') == 3)
            {
                value = s;
                isIp = true;
                return true;
            }

            try { s = new IdnMapping().GetAscii(s); } catch { return false; }
            if (!Regex.IsMatch(s, @"^([a-z0-9-]+\.)+[a-z0-9-]{2,}$")) return false;
            value = s;
            return true;
        }

        public static string Subnet24(byte[] b)
        {
            return b[0] + "." + b[1] + "." + b[2] + ".0/24";
        }

        // Локальные, служебные и мультикаст-адреса нам не нужны
        public static bool IsPublic(byte[] b)
        {
            if (b[0] == 0 || b[0] == 10 || b[0] == 127 || b[0] >= 224) return false;
            if (b[0] == 169 && b[1] == 254) return false;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;
            if (b[0] == 192 && b[1] == 168) return false;
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return false;
            // 198.18.0.0/15 - фейковые адреса VPN-клиентов (Clash/mihomo, sing-box в режиме fake-ip)
            if (b[0] == 198 && (b[1] == 18 || b[1] == 19)) return false;
            return true;
        }

        static readonly int[] WebPorts = { 53, 80, 443, 853 };

        // Сети, где точно нет серверов игр: публичные DNS, Cloudflare, Fastly. Адрес и маска в битах
        static readonly Tuple<byte[], int>[] NotGameNetworks =
        {
            Net(8, 8, 8, 0, 24), Net(8, 8, 4, 0, 24), Net(1, 1, 1, 0, 24), Net(1, 0, 0, 0, 24), Net(9, 9, 9, 0, 24),
            Net(104, 16, 0, 0, 12), Net(172, 64, 0, 0, 13), Net(146, 75, 0, 0, 16), Net(151, 101, 0, 0, 16),
        };

        static Tuple<byte[], int> Net(byte a, byte b, byte c, byte d, int bits)
        {
            return Tuple.Create(new[] { a, b, c, d }, bits);
        }

        public static bool InNotGameNetwork(byte[] ip)
        {
            foreach (var n in NotGameNetworks)
            {
                uint mask = uint.MaxValue << (32 - n.Item2);
                if ((ToUInt(ip) & mask) == (ToUInt(n.Item1) & mask)) return true;
            }
            return false;
        }

        static uint ToUInt(byte[] b)
        {
            return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        }

        // Похоже ли соединение игры на её сервер: сайты, API и DNS не берём
        public static bool IsGameServer(byte[] ip, int port)
        {
            return IsPublic(ip) && !WebPorts.Contains(port) && !InNotGameNetwork(ip);
        }

        // "1.2.3.0/24" в байты адреса, null - не такой формат
        public static byte[] ParseSubnet(string subnet)
        {
            var m = Regex.Match(subnet, @"^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})/\d+$");
            if (!m.Success) return null;
            var b = new byte[4];
            for (int i = 0; i < 4; i++)
                if (!byte.TryParse(m.Groups[i + 1].Value, out b[i])) return null;
            return b;
        }

        static readonly string[] VpnProcesses = {
            "verge-mihomo", "mihomo", "clash", "clash-verge", "clash-meta", "sing-box", "xray", "v2ray",
            "nekoray", "nekobox", "hiddify", "happ", "outline", "amneziavpn", "openvpn", "wireguard"
        };

        // Что мешает проверкам: имя VPN-клиента или адаптера, через который сейчас идёт трафик, или null.
        // Просто запущенный клиент не считается: Clash часто висит включённым, а трафик идёт напрямую.
        // Незнакомый клиент тоже заметим: смотрим, куда Windows отправляет трафик
        public static string RunningVpn()
        {
            foreach (var name in VpnProcesses)
            {
                if (Process.GetProcessesByName(name).Length > 0 && VpnCarriesTraffic()) return name;
            }
            var route = RouteAdapter();
            return route != null && LooksLikeVpn(route) ? route.Name : null;
        }

        public static List<string> VpnClientsRunning()
        {
            return VpnProcesses.Where(n => Process.GetProcessesByName(n).Length > 0).ToList();
        }

        [System.Runtime.InteropServices.DllImport("iphlpapi.dll")]
        static extern int GetBestInterface(uint destAddr, out int bestIfIndex);

        // Адаптер, через который Windows отправит трафик в интернет. Сам ничего не отправляет
        public static System.Net.NetworkInformation.NetworkInterface RouteAdapter()
        {
            try
            {
                int index;
                if (GetBestInterface(BitConverter.ToUInt32(IPAddress.Parse("8.8.8.8").GetAddressBytes(), 0), out index) != 0) return null;
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    try
                    {
                        if (ni.Supports(System.Net.NetworkInformation.NetworkInterfaceComponent.IPv4) && ni.GetIPProperties().GetIPv4Properties().Index == index) return ni;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        public static bool LooksLikeVpn(System.Net.NetworkInformation.NetworkInterface ni)
        {
            return ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Tunnel
                || ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Ppp
                || Regex.IsMatch(ni.Name + " " + ni.Description, @"vpn|tunnel|\btap\b|\btun\b|wintun|wireguard|\bmeta\b|outline|amnezia|tailscale|zerotier|nord|proton|surfshark|mullvad", RegexOptions.IgnoreCase);
        }

        public static bool SystemProxyOn()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
                {
                    var proxy = k == null ? null : k.GetValue("ProxyEnable");
                    return proxy is int && (int)proxy != 0;
                }
            }
            catch { return false; }
        }

        // Поднят адаптер VPN (режим TUN) или включён системный прокси
        static bool VpnCarriesTraffic()
        {
            try
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                    if (LooksLikeVpn(ni)) return true;
                }
                return SystemProxyOn();
            }
            catch { return true; }   // не смогли понять - считаем, что VPN работает: так ничего не сломаем
        }

        // GET страницы целиком; true - сервер ответил (любым HTTP-кодом)
        public static bool HttpWorks(string url, int timeoutMs)
        {
            try
            {
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.AllowAutoRedirect = false;
                req.UserAgent = "Mozilla/5.0";
                using (var resp = req.GetResponse())
                using (var stream = resp.GetResponseStream())
                {
                    var buf = new byte[16384];
                    long total = 0;
                    int n;
                    while ((n = stream.Read(buf, 0, buf.Length)) > 0 && total < 256 * 1024) total += n;
                }
                return true;
            }
            catch (WebException ex)
            {
                return ex.Response != null;
            }
            catch { return false; }
        }

        public static bool InternetUp()
        {
            return HttpWorks("http://www.msftconnecttest.com/connecttest.txt", 6000);
        }
    }

    // Журнал utils\tray.log. Адреса и имена посещённых сайтов сюда не пишем
    static class Logger
    {
        const long MaxBytes = 512 * 1024;
        static readonly object sync = new object();

        public static string File_;

        public static void Write(string text)
        {
            if (File_ == null) return;
            lock (sync)
            {
                try
                {
                    // Большой журнал - в .old, чтобы не рос бесконечно
                    if (File.Exists(File_) && new FileInfo(File_).Length > MaxBytes) File.Copy(File_, File_ + ".old", true);
                    if (File.Exists(File_) && new FileInfo(File_).Length > MaxBytes) File.Delete(File_);
                    File.AppendAllText(File_, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + text + "\r\n");
                }
                catch { }
            }
        }

        public static List<string> Tail(int count)
        {
            lock (sync)
            {
                try
                {
                    if (File_ == null || !File.Exists(File_)) return new List<string>();
                    var lines = File.ReadAllLines(File_);
                    return lines.Skip(Math.Max(0, lines.Length - count)).ToList();
                }
                catch { return new List<string>(); }
            }
        }
    }

    // Задачи планировщика: запуск с правами админа без окна UAC
    static class Tasks
    {
        public static bool Exists(string name)
        {
            string o;
            return Util.Run("schtasks.exe", "/query /tn \"" + name + "\"", out o) == 0;
        }

        public static bool RunNow(string name)
        {
            return Util.Run("schtasks.exe", "/run /tn \"" + name + "\"") == 0;
        }

        public static void End(string name)
        {
            Util.Run("schtasks.exe", "/end /tn \"" + name + "\"");
        }

        public static void Delete(string name)
        {
            Util.Run("schtasks.exe", "/delete /tn \"" + name + "\" /f");
        }

        public static bool IsEnabled(string name)
        {
            string o;
            if (Util.Run("schtasks.exe", "/query /tn \"" + name + "\" /xml", out o) != 0) return false;
            // В XML отключённой задачи в Settings стоит <Enabled>false</Enabled>
            return !Regex.IsMatch(o, @"<Settings>[\s\S]*?<Enabled>false</Enabled>[\s\S]*?</Settings>");
        }

        public static bool SetEnabled(string name, bool enabled)
        {
            return Util.Run("schtasks.exe", "/change /tn \"" + name + "\" " + (enabled ? "/enable" : "/disable")) == 0;
        }

        public static bool Create(string name, string exePath, bool atLogon, out string error)
        {
            var user = WindowsIdentity.GetCurrent().Name;
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-16\"?>");
            sb.AppendLine("<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">");
            sb.AppendLine("  <RegistrationInfo><Description>" + AppInfo.Name + "</Description></RegistrationInfo>");
            if (atLogon)
            {
                sb.AppendLine("  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + SecurityElement.Escape(user) +
                              "</UserId><Delay>PT5S</Delay></LogonTrigger></Triggers>");
            }
            sb.AppendLine("  <Principals><Principal id=\"Author\"><UserId>" + SecurityElement.Escape(user) +
                          "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>");
            sb.AppendLine("  <Settings>");
            sb.AppendLine("    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>");
            sb.AppendLine("    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>");
            sb.AppendLine("    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>");
            sb.AppendLine("    <AllowHardTerminate>true</AllowHardTerminate>");
            sb.AppendLine("    <AllowStartOnDemand>true</AllowStartOnDemand>");
            sb.AppendLine("    <Enabled>true</Enabled>");
            sb.AppendLine("    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>");
            // По умолчанию у задач пониженный приоритет 7 - winws унаследовал бы его
            sb.AppendLine("    <Priority>4</Priority>");
            sb.AppendLine("  </Settings>");
            sb.AppendLine("  <Actions Context=\"Author\"><Exec><Command>" + SecurityElement.Escape(exePath) +
                          "</Command><WorkingDirectory>" + SecurityElement.Escape(Path.GetDirectoryName(exePath)) +
                          "</WorkingDirectory></Exec></Actions>");
            sb.AppendLine("</Task>");

            var xmlPath = Path.Combine(Path.GetTempPath(), "zapret-task-" + Guid.NewGuid().ToString("N") + ".xml");
            File.WriteAllText(xmlPath, sb.ToString(), Encoding.Unicode);
            try
            {
                int code = Util.Run("schtasks.exe", "/create /tn \"" + name + "\" /xml \"" + xmlPath + "\" /f", out error);
                return code == 0;
            }
            finally { try { File.Delete(xmlPath); } catch { } }
        }
    }

    // tray.ini: простые пары ключ=значение
    class Settings
    {
        readonly string path;
        readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public Settings(string path)
        {
            this.path = path;
            if (!File.Exists(path)) return;
            foreach (var line in File.ReadAllLines(path))
            {
                int eq = line.IndexOf('=');
                if (eq > 0) values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
        }

        public bool GetBool(string key, bool def)
        {
            string v;
            return values.TryGetValue(key, out v) ? v == "1" : def;
        }

        public void SetBool(string key, bool value)
        {
            values[key] = value ? "1" : "0";
            Save();
        }

        public string Get(string key)
        {
            string v;
            return values.TryGetValue(key, out v) ? v : null;
        }

        // null - убрать ключ совсем
        public void Set(string key, string value)
        {
            if (value == null) values.Remove(key); else values[key] = value;
            Save();
        }

        void Save()
        {
            File.WriteAllLines(path, values.Select(kv => kv.Key + "=" + kv.Value));
        }
    }

    // Свои IP (вручную и от игр) подмешиваются в ipset-all.txt.
    // Что добавили мы - помним отдельно, чтобы уметь убрать и не затереть обновление списка из service.bat
    static class Ipset
    {
        public static bool Rebuild(AppPaths paths, IEnumerable<string> wanted, out string message)
        {
            message = "";
            var current = Util.ReadEntries(paths.IpsetAll);
            // ipset в режиме none/any - не трогаем, иначе сломаем выбранный пользователем режим
            if (current.Count == 0 || (current.Count == 1 && current[0] == "203.0.113.113/32"))
            {
                message = "IPSet выключен (режим none/any) - свои IP не применяются. Включи его в настройках zapret.";
                return false;
            }

            var prevAdded = new HashSet<string>(Util.ReadEntries(paths.IpsetAdded), StringComparer.OrdinalIgnoreCase);
            var baseList = current.Where(l => !prevAdded.Contains(l)).ToList();
            var baseSet = new HashSet<string>(baseList, StringComparer.OrdinalIgnoreCase);
            var added = wanted.Distinct(StringComparer.OrdinalIgnoreCase).Where(w => !baseSet.Contains(w)).ToList();

            // Смотрим и в сам список: переустановка заменяет его чистым, а заметка prevAdded остаётся
            var currentSet = new HashSet<string>(current, StringComparer.OrdinalIgnoreCase);
            if (added.Count == prevAdded.Count && added.All(a => prevAdded.Contains(a) && currentSet.Contains(a))) return true;

            Util.WriteLinesAtomic(paths.IpsetAll, baseList.Concat(added));
            Util.WriteLinesAtomic(paths.IpsetAdded, added);
            return true;
        }
    }

    static class Shortcut
    {
        public static void Create(string lnkPath, string target, string description)
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(shellType);
            object lnk = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
            var t = lnk.GetType();
            t.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { target });
            t.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { Path.GetDirectoryName(target) });
            t.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { description });
            t.InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { target + ",0" });
            t.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, lnk, null);
        }

        public static string DesktopPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppInfo.ShortcutName); }
        }

        public static string StartMenuPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppInfo.ShortcutName); }
        }
    }
}
