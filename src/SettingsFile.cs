// Перенос настроек: один текстовый файл с сайтами, исключениями, играми и галочками.
// Файл приходит снаружи, поэтому при чтении каждую строку проверяем: чужое в списки и пути не попадёт
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ZapretTray
{
    class ImportedSettings
    {
        public readonly Dictionary<string, List<string>> Lists = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        public readonly List<Game> Games = new List<Game>();
        public readonly Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    static class SettingsFile
    {
        const string Header = "ZapretTray settings 1";
        const long MaxSize = 5 * 1024 * 1024;

        public const string Theme = "theme", Watchdog = "watchdog", Autostart = "autostart", Autoupdate = "autoupdate", Notify = "notify";
        static readonly string[] AllowedValues = { Theme, Watchdog, Autostart, Autoupdate, Notify };

        static readonly Regex GameName = new Regex(@"^\w[\w .\-]{0,99}$");

        // Что переносим: файл и хранятся ли в нём IP (иначе домены). Рабочий конфиг не переносим: в другой сети нужен свой
        static IEnumerable<Tuple<string, bool>> ListFiles(AppPaths paths)
        {
            yield return Tuple.Create(paths.ListGeneralUser, false);
            yield return Tuple.Create(paths.ListExcludeUser, false);
            yield return Tuple.Create(paths.IpsetUser, true);
            yield return Tuple.Create(paths.IpsetExcludeUser, true);
        }

        public static void Save(string file, AppPaths paths, IEnumerable<Game> games, IDictionary<string, string> values)
        {
            var o = new StringBuilder();
            o.AppendLine(Header);
            o.AppendLine("[settings]");
            foreach (var kv in values) o.AppendLine(kv.Key + "=" + kv.Value);
            foreach (var list in ListFiles(paths))
            {
                o.AppendLine("[list " + Path.GetFileName(list.Item1) + "]");
                // Берём и "#off домен": так помечены выключенные встроенные игры
                if (File.Exists(list.Item1))
                    foreach (var line in File.ReadAllLines(list.Item1))
                        if (line.Trim().Length > 0 && (!line.StartsWith("#") || line.StartsWith(Util.OffPrefix))) o.AppendLine(line.Trim());
            }
            foreach (var g in games)
            {
                o.AppendLine("[game " + g.Exe + "|" + (g.Enabled ? "1" : "0") + (g.Direct ? "|1" : "") + "]");
                foreach (var s in g.Subnets.OrderBy(x => x)) o.AppendLine(s);
            }
            File.WriteAllText(file, o.ToString(), new UTF8Encoding(false));
        }

        // Не наш файл - исключение. Строки, которые не прошли проверку, просто пропускаем
        public static ImportedSettings Read(string file, AppPaths paths)
        {
            if (new FileInfo(file).Length > MaxSize) throw new InvalidDataException("файл слишком большой");
            var lines = File.ReadAllLines(file);
            if (lines.Length == 0 || lines[0].Trim().TrimStart('﻿') != Header) throw new InvalidDataException("это не файл настроек Zapret Tray");

            var isIp = ListFiles(paths).ToDictionary(l => Path.GetFileName(l.Item1), l => l.Item2, StringComparer.OrdinalIgnoreCase);
            var result = new ImportedSettings();
            string section = null;
            Game game = null;
            foreach (var raw in lines.Skip(1))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    section = line.Substring(1, line.Length - 2);
                    game = section.StartsWith("game ") ? ReadGame(section.Substring(5), result) : null;
                    if (section.StartsWith("list ")) section = section.Substring(5);
                    else if (section != "settings") section = null;
                    continue;
                }
                if (game != null) AddSubnet(game, line);
                else if (section == "settings") AddValue(result, line);
                else if (section != null && isIp.ContainsKey(section)) AddEntry(result, section, line, isIp[section]);
            }
            return result;
        }

        static Game ReadGame(string text, ImportedSettings result)
        {
            var parts = text.Split('|');
            if (!GameName.IsMatch(parts[0])) return null;
            var g = result.Games.FirstOrDefault(x => string.Equals(x.Exe, parts[0], StringComparison.OrdinalIgnoreCase));
            if (g != null) return g;
            g = new Game { Exe = parts[0], Enabled = parts.Length < 2 || parts[1] == "1", Direct = parts.Length > 2 && parts[2] == "1" };
            result.Games.Add(g);
            return g;
        }

        static void AddSubnet(Game game, string line)
        {
            var ip = Util.ParseSubnet(line);
            if (ip != null && !Util.InNotGameNetwork(ip)) game.Subnets.Add(line);
        }

        static void AddValue(ImportedSettings result, string line)
        {
            int eq = line.IndexOf('=');
            if (eq <= 0) return;
            var key = line.Substring(0, eq).Trim();
            if (AllowedValues.Contains(key, StringComparer.OrdinalIgnoreCase)) result.Values[key] = line.Substring(eq + 1).Trim();
        }

        static void AddEntry(ImportedSettings result, string list, string line, bool wantIp)
        {
            bool off = line.StartsWith(Util.OffPrefix, StringComparison.OrdinalIgnoreCase);
            string value;
            bool entryIsIp;
            if (!Util.ParseEntry(off ? line.Substring(Util.OffPrefix.Length) : line, out value, out entryIsIp) || entryIsIp != wantIp) return;
            if (off && wantIp) return;
            List<string> entries;
            if (!result.Lists.TryGetValue(list, out entries)) result.Lists[list] = entries = new List<string>();
            entries.Add(off ? Util.OffPrefix + value : value);
        }

        // Список без единой строки не трогаем: в рабочих файлах всегда лежит заглушка для zapret
        public static void ApplyLists(ImportedSettings imported, AppPaths paths)
        {
            foreach (var list in ListFiles(paths))
            {
                List<string> entries;
                if (imported.Lists.TryGetValue(Path.GetFileName(list.Item1), out entries)) Util.WriteLinesAtomic(list.Item1, entries);
            }
        }
    }
}
