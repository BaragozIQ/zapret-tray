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
        public const string Version = "0.2";
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

        // Разбор того, что ввёл пользователь: домен, IP или подсеть
        public static bool ParseEntry(string text, out string value, out bool isIp)
        {
            value = null;
            isIp = false;
            if (text == null) return false;
            var s = text.Trim().ToLowerInvariant();
            s = Regex.Replace(s, @"^[a-z]+://", "");
            int slash = s.IndexOf('/');
            // "1.2.3.0/24" - подсеть, "site.com/path" - отрезаем путь
            if (slash >= 0)
            {
                var head = s.Substring(0, slash);
                var tail = s.Substring(slash + 1);
                int bits;
                IPAddress ipNet;
                if (IPAddress.TryParse(head, out ipNet) && ipNet.AddressFamily == AddressFamily.InterNetwork
                    && int.TryParse(tail, out bits) && bits >= 8 && bits <= 32)
                {
                    value = head + "/" + bits;
                    isIp = true;
                    return true;
                }
                s = head;
            }
            s = s.TrimStart('*', '.');
            int colon = s.LastIndexOf(':');
            if (colon > 0 && s.IndexOf(':') == colon) s = s.Substring(0, colon);

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

        static readonly string[] VpnProcesses = {
            "verge-mihomo", "mihomo", "clash", "clash-verge", "clash-meta", "sing-box", "xray", "v2ray",
            "nekoray", "nekobox", "hiddify", "happ", "outline", "amneziavpn", "openvpn", "wireguard"
        };

        // Имя запущенного VPN-клиента или null: с ним подбор и сбор адресов игр врут
        public static string RunningVpn()
        {
            foreach (var name in VpnProcesses)
            {
                if (Process.GetProcessesByName(name).Length > 0) return name;
            }
            return null;
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

        public void Set(string key, string value)
        {
            values[key] = value;
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

            if (added.Count == prevAdded.Count && added.All(prevAdded.Contains)) return true;

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
