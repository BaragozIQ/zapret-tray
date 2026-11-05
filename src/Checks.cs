// Проверки для подбора конфига: под обязательные программа подбирает zapret и по ним решает, что "всё работает".
// Необязательные просто проверяются и показываются (пойдёт - хорошо, нет - не страшно). Файл: lists\tray-checks.txt, строки "url|1".
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ZapretTray
{
    class Check
    {
        public string Url;
        public bool Required;
        public Check(string url, bool required) { Url = url; Required = required; }
    }

    class CheckStore
    {
        // По умолчанию: главные сервисы обязательны, игровые сайты-витрины - нет (они и так редко отвечают на проверку)
        public static readonly Check[] Defaults =
        {
            new Check("https://discord.com", true),
            new Check("https://gateway.discord.gg", true),
            new Check("https://cdn.discordapp.com", true),
            new Check("https://updates.discord.com", true),
            new Check("https://www.youtube.com", true),
            new Check("https://i.ytimg.com", true),
            new Check("https://redirector.googlevideo.com", true),
            new Check("https://www.google.com", true),
            new Check("https://arcraiders.com", false),
            new Check("https://id.embark.games", false),
        };

        readonly AppPaths paths;
        readonly object sync = new object();
        public readonly List<Check> Items = new List<Check>();

        public CheckStore(AppPaths paths)
        {
            this.paths = paths;
            var lines = Util.ReadEntries(paths.ChecksList);
            if (lines.Count == 0)
            {
                foreach (var d in Defaults) Items.Add(new Check(d.Url, d.Required));
                Save();
                return;
            }
            foreach (var line in lines)
            {
                var parts = line.Split('|');
                var url = Normalize(parts[0]);
                if (url == null || Items.Any(c => string.Equals(c.Url, url, StringComparison.OrdinalIgnoreCase))) continue;
                Items.Add(new Check(url, parts.Length < 2 || parts[1] == "1"));
            }
            if (Items.Count == 0) foreach (var d in Defaults) Items.Add(new Check(d.Url, d.Required));
        }

        // Приводим к виду https://host: добавить можно и "youtube.com", и "https://youtube.com/..."
        public static string Normalize(string raw)
        {
            if (raw == null) return null;
            var s = raw.Trim();
            if (s.Length == 0) return null;
            int scheme = s.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0) s = s.Substring(scheme + 3);
            s = s.TrimStart('/');
            int slash = s.IndexOf('/');
            if (slash >= 0) s = s.Substring(0, slash);
            s = s.Trim().TrimEnd('.');
            if (s.Length == 0 || s.IndexOf(' ') >= 0 || s.IndexOf('.') < 0) return null;
            return "https://" + s.ToLowerInvariant();
        }

        public List<Check> All() { lock (sync) return Items.Select(c => new Check(c.Url, c.Required)).ToList(); }

        public bool Add(string url)
        {
            var u = Normalize(url);
            if (u == null) return false;
            lock (sync)
            {
                if (Items.Any(c => string.Equals(c.Url, u, StringComparison.OrdinalIgnoreCase))) return false;
                Items.Add(new Check(u, true));
                Save();
            }
            return true;
        }

        public void SetRequired(string url, bool required)
        {
            lock (sync)
            {
                var c = Items.FirstOrDefault(x => string.Equals(x.Url, url, StringComparison.OrdinalIgnoreCase));
                if (c == null) return;
                c.Required = required;
                Save();
            }
        }

        public void Remove(string url)
        {
            lock (sync)
            {
                Items.RemoveAll(c => string.Equals(c.Url, url, StringComparison.OrdinalIgnoreCase));
                Save();
            }
        }

        void Save()
        {
            Util.WriteLinesAtomic(paths.ChecksList, Items.Select(c => c.Url + "|" + (c.Required ? "1" : "0")));
        }
    }
}
