// Защищённый DNS одной кнопкой и откат второй. Входит и в ZapretTray.exe, и в Setup.exe: откат работает, даже если трей не запускается.
// Windows 11: DNS-over-HTTPS самой системы. Windows 10: настройки браузеров, в системе такой функции может не быть.
// Перед изменением всё прежнее пишем в utils\dns_backup.txt; откат возвращает именно это
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace ZapretTray
{
    static class DnsFix
    {
        const string Primary = "1.1.1.1", PrimaryDoh = "https://cloudflare-dns.com/dns-query";
        const string Secondary = "8.8.8.8", SecondaryDoh = "https://dns.google/dns-query";
        const string InterfacesKey = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\";
        const char Sep = '|';

        // Политики браузеров: ключ, имя значения, что поставить
        static readonly Tuple<string, string, object>[] BrowserPolicies =
        {
            Tuple.Create(@"SOFTWARE\Policies\Google\Chrome", "DnsOverHttpsMode", (object)"automatic"),
            Tuple.Create(@"SOFTWARE\Policies\Google\Chrome", "DnsOverHttpsTemplates", (object)PrimaryDoh),
            Tuple.Create(@"SOFTWARE\Policies\Microsoft\Edge", "DnsOverHttpsMode", (object)"automatic"),
            Tuple.Create(@"SOFTWARE\Policies\Microsoft\Edge", "DnsOverHttpsTemplates", (object)PrimaryDoh),
            Tuple.Create(@"SOFTWARE\Policies\Mozilla\Firefox\DNSOverHTTPS", "Enabled", (object)1),
            Tuple.Create(@"SOFTWARE\Policies\Mozilla\Firefox\DNSOverHTTPS", "ProviderURL", (object)PrimaryDoh),
        };

        static string BackupFile(AppPaths paths) { return Path.Combine(paths.Utils, "dns_backup.txt"); }

        public static bool IsApplied(AppPaths paths) { return File.Exists(BackupFile(paths)); }

        public static int WindowsBuild()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    int build;
                    return k != null && int.TryParse(k.GetValue("CurrentBuildNumber") as string, out build) ? build : 0;
                }
            }
            catch { return 0; }
        }

        static bool IsWindows11 { get { return WindowsBuild() >= 22000; } }

        // true - включено. message - что сказать пользователю
        public static bool Apply(AppPaths paths, out string message)
        {
            if (IsApplied(paths)) { message = "Защищённый DNS уже включён."; return true; }
            var vpn = Util.RunningVpn();
            if (vpn != null)
            {
                message = "Запущен VPN (" + vpn + "). Пока он включён, DNS менять нельзя: выключи его и попробуй снова.";
                return false;
            }
            try
            {
                if (!IsWindows11)
                {
                    ApplyBrowsers(paths);
                    Logger.Write("DNS: включён через настройки браузеров, сборка Windows " + WindowsBuild());
                    message = "Защищённый DNS включён в Chrome, Edge и Firefox. Полностью перезапусти браузер.\nВернуть можно в «Подключение» → «Вернуть DNS как было».";
                    return true;
                }

                ApplySystem(paths);
                Util.Run("ipconfig.exe", "/flushdns");
                if (!Resolves("www.youtube.com"))
                {
                    string undo;
                    Restore(paths, out undo);
                    Logger.Write("DNS: после включения YouTube не находится, откатил. " + undo);
                    message = "Защищённый DNS не помог, всё вернул как было.";
                    return false;
                }
                Logger.Write("DNS: включён защищённый DNS Windows");
                message = "Защищённый DNS включён.\nВернуть можно в «Подключение» → «Вернуть DNS как было».";
                return true;
            }
            catch (Exception ex)
            {
                string undo = "";
                if (IsApplied(paths)) Restore(paths, out undo);
                Logger.Write("DNS: не удалось включить: " + ex.Message + " " + undo);
                message = "Не удалось включить защищённый DNS: " + ex.Message;
                return false;
            }
        }

        static bool Resolves(string host)
        {
            for (int i = 0; i < 5; i++)
            {
                try { if (Dns.GetHostAddresses(host).Length > 0) return true; } catch { }
                Thread.Sleep(2000);
            }
            return false;
        }

        // Адаптеры, через которые идёт интернет
        static List<NetworkInterface> ActiveAdapters()
        {
            return NetworkInterface.GetAllNetworkInterfaces().Where(n =>
                n.OperationalStatus == OperationalStatus.Up
                && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel
                && n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork)).ToList();
        }

        static string ReadNameServer(string guid)
        {
            using (var k = Registry.LocalMachine.OpenSubKey(InterfacesKey + guid))
                return k == null ? "" : (k.GetValue("NameServer") as string ?? "");
        }

        static void ApplySystem(AppPaths paths)
        {
            var adapters = ActiveAdapters();
            if (adapters.Count == 0) throw new Exception("не найден сетевой адаптер");

            // Сначала копия прежнего, потом изменения: откат возможен с любого места
            var backup = new List<string> { "mode" + Sep + "system" };
            foreach (var a in adapters) backup.Add("adapter" + Sep + a.Id + Sep + a.Name + Sep + ReadNameServer(a.Id));
            string global;
            Util.Run("netsh.exe", "dns show global", out global);
            backup.Add("global" + Sep + (Regex.IsMatch(global, @"DoH\s*:\s*(yes|да)", RegexOptions.IgnoreCase) ? "yes" : "no"));
            foreach (var server in new[] { Primary, Secondary })
            {
                string shown;
                bool existed = Util.Run("netsh.exe", "dns show encryption server=" + server, out shown) == 0 && shown.Contains("http");
                backup.Add("encryption" + Sep + server + Sep + (existed ? "existed" : "added"));
            }
            Util.WriteLinesAtomic(BackupFile(paths), backup);

            // Запасной обычный DNS разрешён (udpfallback): если защищённый откажет, интернет не пропадёт
            SetEncryption(Primary, PrimaryDoh);
            SetEncryption(Secondary, SecondaryDoh);
            Check(Util.Run("netsh.exe", "dns set global doh=yes"), "dns set global");
            foreach (var a in adapters) SetAdapterDns(a.Name, Primary, Secondary);
        }

        static void SetEncryption(string server, string template)
        {
            var args = "server=" + server + " dohtemplate=" + template + " autoupgrade=yes udpfallback=yes";
            if (Util.Run("netsh.exe", "dns set encryption " + args) == 0) return;
            Check(Util.Run("netsh.exe", "dns add encryption " + args), "dns add encryption");
        }

        static void SetAdapterDns(string name, params string[] servers)
        {
            for (int i = 0; i < servers.Length; i++)
            {
                var args = i == 0
                    ? "interface ipv4 set dnsservers name=\"" + name + "\" source=static address=" + servers[i] + " register=primary validate=no"
                    : "interface ipv4 add dnsservers name=\"" + name + "\" address=" + servers[i] + " index=" + (i + 1) + " validate=no";
                Check(Util.Run("netsh.exe", args), "настройка адаптера " + name);
            }
        }

        static void Check(int exit, string what)
        {
            if (exit != 0) throw new Exception(what + ": код " + exit);
        }

        static void ApplyBrowsers(AppPaths paths)
        {
            var backup = new List<string> { "mode" + Sep + "browser" };
            foreach (var p in BrowserPolicies)
            {
                object old = null;
                using (var k = Registry.LocalMachine.OpenSubKey(p.Item1)) if (k != null) old = k.GetValue(p.Item2);
                var value = old == null ? "none" : old is int ? "d:" + old : "s:" + old;
                backup.Add("policy" + Sep + p.Item1 + Sep + p.Item2 + Sep + value);
            }
            Util.WriteLinesAtomic(BackupFile(paths), backup);
            foreach (var p in BrowserPolicies)
                using (var k = Registry.LocalMachine.CreateSubKey(p.Item1))
                    k.SetValue(p.Item2, p.Item3);
        }

        // Вернуть то, что было. Файл копии удаляем, только если всё вернулось; иначе можно нажать ещё раз
        public static bool Restore(AppPaths paths, out string message)
        {
            var file = BackupFile(paths);
            if (!File.Exists(file)) { message = "Защищённый DNS не включён, возвращать нечего."; return true; }

            var errors = new List<string>();
            var lines = File.ReadAllLines(file).Select(l => l.Split(Sep)).ToList();
            // Адаптеры первыми: без них шифрование уже не используется
            foreach (var p in lines.Where(l => l[0] == "adapter")) Try(errors, () => RestoreAdapter(p[1], p[2], p[3]));
            foreach (var p in lines.Where(l => l[0] == "encryption")) Try(errors, () => RestoreEncryption(p[1], p[2]));
            foreach (var p in lines.Where(l => l[0] == "global"))
                Try(errors, () => Check(Util.Run("netsh.exe", "dns set global doh=" + p[1]), "dns set global"));
            foreach (var p in lines.Where(l => l[0] == "policy")) Try(errors, () => RestorePolicy(p[1], p[2], p[3]));

            Util.Run("ipconfig.exe", "/flushdns");
            if (errors.Count > 0)
            {
                Logger.Write("DNS: откат с ошибками: " + string.Join("; ", errors));
                message = "Вернул не всё: " + string.Join("; ", errors) + "\nМожно нажать ещё раз или поставить DNS вручную: Параметры → Сеть → DNS → Автоматически.";
                return false;
            }
            File.Delete(file);
            Logger.Write("DNS: настройки возвращены как было");
            message = "DNS возвращён как было.";
            return true;
        }

        static void Try(List<string> errors, Action a)
        {
            try { a(); } catch (Exception ex) { errors.Add(ex.Message); }
        }

        static void RestoreAdapter(string guid, string savedName, string nameServer)
        {
            // Адаптер могли переименовать, поэтому ищем по его коду. Отключённый - пробуем по старому имени
            var now = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Id == guid);
            var name = now != null ? now.Name : savedName;
            var servers = nameServer.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (servers.Length > 0) SetAdapterDns(name, servers);
            else Check(Util.Run("netsh.exe", "interface ipv4 set dnsservers name=\"" + name + "\" source=dhcp"), "настройка адаптера " + name);
        }

        static void RestoreEncryption(string server, string state)
        {
            if (state == "added") Check(Util.Run("netsh.exe", "dns delete encryption server=" + server), "dns delete encryption");
            else Check(Util.Run("netsh.exe", "dns set encryption server=" + server + " autoupgrade=no udpfallback=no"), "dns set encryption");
        }

        static void RestorePolicy(string key, string name, string value)
        {
            using (var k = Registry.LocalMachine.OpenSubKey(key, true))
            {
                if (k == null) return;
                if (value == "none") k.DeleteValue(name, false);
                else if (value.StartsWith("d:")) k.SetValue(name, int.Parse(value.Substring(2)));
                else k.SetValue(name, value.Substring(2));
            }
        }
    }
}
