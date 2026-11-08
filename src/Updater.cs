// Автообновление: смотрим последний релиз на GitHub, качаем zip и отдаём его Setup.exe /update
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace ZapretTray
{
    static class Updater
    {
        // Открытая часть ключа подписи. Закрытая лежит только у владельца: %USERPROFILE%\.zapret-tray\signing-key.xml
        const string PublicKey = "<RSAKeyValue><Modulus>wwgb3fVrm+BvlMWMB37VdDEDu5BX45eKHXQhvzGSmf9nuMgPr3cHuf7VQ4Ev1b1H1PT52pLaFxJPM2EIu1t9hpCekGrFsECiykREyWnTINq+LhcAil7xDCpe7O1xONgllyp+2l0FqT290nVo5ZqbWZALTggCTlqzryNFZaNwLF52kleWoKHL0wVIh3vDLx2AUfxHhpZiC807YwsZLg/eSaMfW7beOJVX35sEOUpxRG5oV6uGjJeakalFriRhe/jLekKwiM1RI8p576Qaou2UxgTHeNzdGOWdBYKVnrgmXzP36SHh9c4XPZdcXyvUNs4k3i3iDxEaYSkye4bEFNgkNboQIbpAoSPmxZmpPQyG/VmYQ8GBd5m2winLOGdK+SMrhZEUeIX8GLulqGQK30ZDsdD9boYGYbBDhLg5O2X6MdHUFqTsCuuRU3w0Lgmj702WiJPti46qN3oCs3GqsA7csuLJnHPWHeVeoGLcUV6gRD23LHEOcatxJ0Kleqme/ELJ</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";

        public class Release
        {
            public string Version;
            public string ZipUrl;
            public string SigUrl;
        }

        // Архив подписан владельцем и не изменён
        public static bool SignatureValid(byte[] data, byte[] signature)
        {
            try
            {
                using (var rsa = new RSACryptoServiceProvider())
                {
                    rsa.FromXmlString(PublicKey);
                    return rsa.VerifyData(data, "SHA256", signature);
                }
            }
            catch { return false; }
        }

        // null - нет связи или релиза; иначе последний релиз (может быть и не новее нашего)
        public static Release GetLatest()
        {
            return GetLatest(AppInfo.Repo);
        }

        public static Release GetLatest(string repo)
        {
            try
            {
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2
                using (var wc = new WebClient())
                {
                    wc.Headers[HttpRequestHeader.UserAgent] = "ZapretTray/" + AppInfo.Version;
                    wc.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
                    wc.Encoding = System.Text.Encoding.UTF8;
                    var json = wc.DownloadString("https://api.github.com/repos/" + repo + "/releases/latest");
                    var tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"v?([^\"]+)\"");
                    var zip = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+\\.zip)\"");
                    if (!tag.Success || !zip.Success) return null;
                    var sig = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+\\.zip\\.sig)\"");
                    return new Release { Version = tag.Groups[1].Value, ZipUrl = zip.Groups[1].Value, SigUrl = sig.Success ? sig.Groups[1].Value : null };
                }
            }
            catch { return null; }
        }

        public static bool IsNewer(string remote, string local)
        {
            Version r, l;
            if (!System.Version.TryParse(remote, out r) || !System.Version.TryParse(local, out l)) return false;
            return r > l;
        }

        // Скачать и распаковать релиз, вернуть путь к Setup.exe из него
        public static string Download(Release rel)
        {
            // Этот Setup.exe запустится с правами администратора: без верной подписи не ставим
            if (rel.SigUrl == null) throw new Exception("у обновления нет подписи");
            var outDir = DownloadAndExtract(rel.ZipUrl, "ZapretTrayUpdate", rel.SigUrl);
            var setup = Directory.GetFiles(outDir, AppInfo.SetupExe, SearchOption.AllDirectories).FirstOrDefault();
            if (setup == null) throw new Exception("в архиве обновления нет " + AppInfo.SetupExe);
            return setup;
        }

        // Скачать zip во временную папку и распаковать, вернуть папку с содержимым
        public static string DownloadAndExtract(string url, string dirName, string sigUrl = null)
        {
            var dir = Path.Combine(Path.GetTempPath(), dirName);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            var zipPath = Path.Combine(dir, "download.zip");
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2
            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.UserAgent] = "ZapretTray/" + AppInfo.Version;
                wc.DownloadFile(url, zipPath);
                if (sigUrl != null && !SignatureValid(File.ReadAllBytes(zipPath), wc.DownloadData(sigUrl)))
                {
                    Directory.Delete(dir, true);
                    throw new Exception("подпись обновления не совпала, не ставлю");
                }
            }
            var outDir = Path.Combine(dir, "x");
            ZipFile.ExtractToDirectory(zipPath, outDir);
            return outDir;
        }

        // Setup.exe сам остановит трей, заменит файлы и запустит новую версию
        public static void RunSetup(string setupPath, string installDir)
        {
            Process.Start(new ProcessStartInfo(setupPath, "/update \"" + installDir.TrimEnd('\\') + "\"") { UseShellExecute = true });
        }
    }

    // Свежие конфиги zapret прямо от разработчиков (Flowseal): скачать, внести наши правки, заменить, при неудаче - вернуть
    static class ZapretUpdater
    {
        public const string Repo = "Flowseal/zapret-discord-youtube";

        // Что заменяем в lists. ipset-all.txt не трогаем: у разработчиков там заглушка, у нас - полный список
        static readonly string[] Lists = { "list-general.txt", "list-exclude.txt", "ipset-exclude.txt", "list-google.txt" };

        // Байт в байт, чтобы не испортить кодировку батников
        static readonly System.Text.Encoding Raw = System.Text.Encoding.GetEncoding(28591);

        static string Backup(AppPaths paths) { return Path.Combine(paths.Root, "core_backup"); }

        public static string LocalVersion(AppPaths paths)
        {
            try
            {
                var m = Regex.Match(File.ReadAllText(paths.ServiceBat), "LOCAL_VERSION=([0-9.]+)");
                return m.Success ? m.Groups[1].Value : "0";
            }
            catch { return "0"; }
        }

        // Чужой архив не подписан нами, а конфиги запускаются с правами администратора. Поэтому в конфиге разрешён только запуск
        // winws из папки bin с его параметрами: ничего другого он выполнить не сможет. Проверять после правки /min -> /b
        static readonly Regex[] AllowedLines =
        {
            new Regex(@"^\s*$"),
            new Regex(@"^@echo off\s*$", RegexOptions.IgnoreCase),
            new Regex(@"^chcp \d+( > ?nul)?\s*$", RegexOptions.IgnoreCase),
            new Regex(@"^::[^&|<>]*$"),
            new Regex(@"^echo:\s*$", RegexOptions.IgnoreCase),
            new Regex(@"^cd /d (""%~dp0""|%BIN%)\s*$", RegexOptions.IgnoreCase),
            new Regex(@"^call service\.bat \w+\s*$", RegexOptions.IgnoreCase),
            new Regex(@"^set ""(BIN=%~dp0bin\\|LISTS=%~dp0lists\\)""\s*$", RegexOptions.IgnoreCase),
            new Regex(@"^start ""zapret: %~n0"" /b ""%BIN%winws\.exe""( --[^&|<>`]*)?\^?\s*$", RegexOptions.IgnoreCase),
            new Regex(@"^--[^&|<>`]*\^?\s*$"),
        };

        // Игры «напрямую»: в игровых правилах (все высокие порты) их сервера исключены - никаких подделок, как без zapret.
        // Порты 80/443 не трогаем: вход и сайты игры идут через обход, как раньше
        public const string DirectExclude = " --ipset-exclude=\"%LISTS%tray-ipset-direct.txt\"";

        public static string PatchDirect(string bat)
        {
            if (bat.IndexOf("tray-ipset-direct.txt", StringComparison.OrdinalIgnoreCase) >= 0) return bat;
            bat = Regex.Replace(bat, @"(--filter-udp=%GameFilterUDP%)(?=\s)", "$1" + DirectExclude.Replace("$", "$$"), RegexOptions.IgnoreCase);
            return Regex.Replace(bat, @"(--filter-tcp=%GameFilterTCP%)(?=\s)", "$1" + DirectExclude.Replace("$", "$$"), RegexOptions.IgnoreCase);
        }

        // Место для параметров от программы: --wf-iface, когда трафик идёт через VPN-туннель.
        // Без него winws подмешивает поддельные пакеты прямо в туннель, а VPN отправляет их серверу как настоящие данные
        // (хрипит голос, рвутся соединения). Переменную задаёт Selector.StartConfig; не задана - в батнике она пустая
        public const string ExtraVar = "%TrayWinws%";

        public static string PatchIface(string bat)
        {
            if (bat.IndexOf(ExtraVar, StringComparison.OrdinalIgnoreCase) >= 0) return bat;
            var re = new Regex(@"(""%BIN%winws\.exe""[^\r\n]*?--wf-udp=[^\s\^]+)", RegexOptions.IgnoreCase);
            if (!re.IsMatch(bat)) re = new Regex(@"(""%BIN%winws\.exe""[^\r\n]*?--wf-tcp=[^\s\^]+)", RegexOptions.IgnoreCase);
            return re.Replace(bat, "$1 " + ExtraVar, 1);
        }

        // Установленные конфиги без этих правок (старая установка или старое обновление) - поправить на месте
        public static void EnsureDirect(AppPaths paths)
        {
            try
            {
                foreach (var bat in Directory.GetFiles(paths.Core, "general*.bat"))
                {
                    var text = File.ReadAllText(bat, Raw);
                    var patched = PatchIface(PatchDirect(text));
                    if (patched != text) File.WriteAllText(bat, patched, Raw);
                }
            }
            catch (Exception ex) { Logger.Write("правка конфигов для игр напрямую: " + ex.Message); }
        }

        // Первая строка, которой в конфиге быть не должно, или null
        public static string UnsafeLine(string bat)
        {
            return bat.Replace("\r\n", "\n").Split('\n').FirstOrDefault(l => !AllowedLines.Any(r => r.IsMatch(l)));
        }

        // Скачать и подготовить: вернуть папку с уже поправленными файлами. Бросает исключение, если что-то не так.
        // Берём только конфиги, списки и .bin. Движок (winws, WinDivert) и код service.bat приходят лишь в наших подписанных релизах
        public static string Prepare(Updater.Release rel, AppPaths paths)
        {
            var outDir = Updater.DownloadAndExtract(rel.ZipUrl, "ZapretCoreUpdate");
            var root = Directory.GetFiles(outDir, "service.bat", SearchOption.AllDirectories)
                .Select(Path.GetDirectoryName)
                .FirstOrDefault(d => File.Exists(Path.Combine(d, @"bin\winws.exe")));
            if (root == null) throw new Exception("в архиве нет service.bat и bin\\winws.exe");

            var bats = Directory.GetFiles(root, "general*.bat");
            if (bats.Length < 3) throw new Exception("в архиве мало конфигов (" + bats.Length + ")");
            foreach (var bat in bats)
            {
                // Движок без своего окна: start "..." /min -> /b
                var text = File.ReadAllText(bat, Raw);
                var patched = Regex.Replace(text, "(start\\s+\"[^\"]*\"\\s+)/min(\\s)", "$1/b$2", RegexOptions.IgnoreCase);
                if (patched == text) throw new Exception("не понял устройство конфига " + Path.GetFileName(bat));
                patched = PatchIface(PatchDirect(patched));
                var bad = UnsafeLine(patched);
                if (bad != null) throw new Exception("в конфиге " + Path.GetFileName(bat) + " лишняя команда: " + bad);
                File.WriteAllText(bat, patched, Raw);
            }

            // Конфиги должны звать из service.bat только то, что есть в нашей урезанной версии
            foreach (var bat in bats)
            {
                foreach (Match m in Regex.Matches(File.ReadAllText(bat, Raw), @"call\s+service\.bat\s+(\w+)", RegexOptions.IgnoreCase))
                    if (!Commands.Contains(m.Groups[1].Value.ToLowerInvariant()))
                        throw new Exception("конфиг " + Path.GetFileName(bat) + " зовёт неизвестную команду service.bat " + m.Groups[1].Value);
            }
            // service.bat - наш: из чужого берём только номер версии
            var service = Path.Combine(root, "service.bat");
            var version = Regex.Match(File.ReadAllText(service, Raw), "LOCAL_VERSION=([0-9.]+)");
            if (!version.Success) throw new Exception("в service.bat нет LOCAL_VERSION");
            File.WriteAllText(service, Regex.Replace(File.ReadAllText(paths.ServiceBat, Raw), "LOCAL_VERSION=[0-9.]+", "LOCAL_VERSION=" + version.Groups[1].Value), Raw);

            foreach (var f in Directory.GetFiles(Path.Combine(root, "bin")))
                if (!f.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)) File.Delete(f);
            return root;
        }

        // Команды service.bat, которые вызывают конфиги general*.bat
        static readonly string[] Commands = { "status_zapret", "check_updates", "load_game_filter", "load_user_lists" };

        // Заменить файлы, старые сложить в core_backup
        public static void Apply(AppPaths paths, string src)
        {
            var bak = Backup(paths);
            if (Directory.Exists(bak)) Directory.Delete(bak, true);
            Directory.CreateDirectory(Path.Combine(bak, "bin"));
            Directory.CreateDirectory(Path.Combine(bak, "lists"));

            foreach (var f in Directory.GetFiles(paths.Core, "general*.bat")) File.Copy(f, Path.Combine(bak, Path.GetFileName(f)));
            File.Copy(paths.ServiceBat, Path.Combine(bak, "service.bat"));
            foreach (var f in Directory.GetFiles(paths.Bin)) File.Copy(f, Path.Combine(bak, "bin", Path.GetFileName(f)));
            foreach (var name in Lists)
            {
                var f = Path.Combine(paths.Lists, name);
                if (File.Exists(f)) File.Copy(f, Path.Combine(bak, "lists", name));
            }
            File.WriteAllText(Path.Combine(bak, "complete"), "");   // бэкап целый - можно откатываться

            CopyCore(src, paths);
        }

        // Вернуть то, что было до обновления
        public static void Restore(AppPaths paths)
        {
            var bak = Backup(paths);
            if (!File.Exists(Path.Combine(bak, "complete")))
            {
                // Упали ещё во время бэкапа - файлы не трогали
                if (Directory.Exists(bak)) Directory.Delete(bak, true);
                return;
            }
            CopyCore(bak, paths);
            Directory.Delete(bak, true);
        }

        public static void DropBackup(AppPaths paths)
        {
            var bak = Backup(paths);
            if (Directory.Exists(bak)) Directory.Delete(bak, true);
        }

        public static bool BackupExists(AppPaths paths)
        {
            return Directory.Exists(Backup(paths));
        }

        static void CopyCore(string src, AppPaths paths)
        {
            foreach (var f in Directory.GetFiles(paths.Core, "general*.bat")) File.Delete(f);
            foreach (var f in Directory.GetFiles(src, "general*.bat")) File.Copy(f, Path.Combine(paths.Core, Path.GetFileName(f)), true);
            File.Copy(Path.Combine(src, "service.bat"), paths.ServiceBat, true);
            foreach (var f in Directory.GetFiles(Path.Combine(src, "bin"))) CopyIfChanged(f, Path.Combine(paths.Bin, Path.GetFileName(f)));
            foreach (var name in Lists)
            {
                var f = Path.Combine(src, "lists", name);
                if (File.Exists(f)) CopyIfChanged(f, Path.Combine(paths.Lists, name));
            }
        }

        // Одинаковые файлы не трогаем - так не упрёмся в занятый WinDivert.dll / драйвер
        static void CopyIfChanged(string src, string dst)
        {
            if (File.Exists(dst) && new FileInfo(src).Length == new FileInfo(dst).Length
                && File.ReadAllBytes(src).SequenceEqual(File.ReadAllBytes(dst))) return;
            File.Copy(src, dst, true);
        }
    }
}
