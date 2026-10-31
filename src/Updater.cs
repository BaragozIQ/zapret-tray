// Автообновление: смотрим последний релиз на GitHub, качаем zip и отдаём его Setup.exe /update
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace ZapretTray
{
    static class Updater
    {
        public class Release
        {
            public string Version;
            public string ZipUrl;
        }

        // null - нет связи или релиза; иначе последний релиз (может быть и не новее нашего)
        public static Release GetLatest()
        {
            try
            {
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2
                using (var wc = new WebClient())
                {
                    wc.Headers[HttpRequestHeader.UserAgent] = "ZapretTray/" + AppInfo.Version;
                    wc.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
                    wc.Encoding = System.Text.Encoding.UTF8;
                    var json = wc.DownloadString("https://api.github.com/repos/" + AppInfo.Repo + "/releases/latest");
                    var tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"v?([^\"]+)\"");
                    var zip = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+\\.zip)\"");
                    if (!tag.Success || !zip.Success) return null;
                    return new Release { Version = tag.Groups[1].Value, ZipUrl = zip.Groups[1].Value };
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
            var dir = Path.Combine(Path.GetTempPath(), "ZapretTrayUpdate");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            var zipPath = Path.Combine(dir, "update.zip");
            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.UserAgent] = "ZapretTray/" + AppInfo.Version;
                wc.DownloadFile(rel.ZipUrl, zipPath);
            }
            var outDir = Path.Combine(dir, "x");
            ZipFile.ExtractToDirectory(zipPath, outDir);
            var setup = Directory.GetFiles(outDir, AppInfo.SetupExe, SearchOption.AllDirectories).FirstOrDefault();
            if (setup == null) throw new Exception("в архиве обновления нет " + AppInfo.SetupExe);
            return setup;
        }

        // Setup.exe сам остановит трей, заменит файлы и запустит новую версию
        public static void RunSetup(string setupPath, string installDir)
        {
            Process.Start(new ProcessStartInfo(setupPath, "/update \"" + installDir.TrimEnd('\\') + "\"") { UseShellExecute = true });
        }
    }
}
