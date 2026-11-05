// Снимки всех экранов для проверки глазами: главная с графиками, разделы, настройки с ползунком, окна бага, "Что нового"
// и приветствия. Тёмная и светлая тема, размеры 75, 100, 150 и 200%. Данные выдуманные. Картинки - в папку screens рядом с result.txt
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace ZapretTray
{
    static class Shots
    {
        static int pass, fail;
        static readonly List<string> log = new List<string>();
        static string dir;

        static void Ok(bool c, string n) { if (c) pass++; else fail++; log.Add((c ? "OK   " : "FAIL ") + n); }

        static void Save(Form f, string name)
        {
            f.Refresh();
            Application.DoEvents();
            var file = Path.Combine(dir, name + ".png");
            using (var b = new Bitmap(f.Width, f.Height))
            {
                f.DrawToBitmap(b, new Rectangle(0, 0, f.Width, f.Height));
                b.Save(file, ImageFormat.Png);
            }
            Ok(new FileInfo(file).Length > 5000, "снимок " + name);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main()
        {
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            dir = Path.GetFullPath("screens");
            Directory.CreateDirectory(dir);
            foreach (var mode in new[] { Theme.Dark, Theme.Light })
            {
                foreach (var scale in new[] { 75, 100, 150, 200 })
                    try { Panel(mode, scale); } catch (Exception ex) { Ok(false, mode + " " + scale + "%: " + ex.Message); }
                try { Dialogs(mode); } catch (Exception ex) { Ok(false, mode + " окна: " + ex.Message); }
            }
            log.Add("Картинки: " + dir);
            File.WriteAllLines("result.txt", log.Concat(new[] { "ИТОГО ok=" + pass + " fail=" + fail }));
        }

        static void Panel(string mode, int scale)
        {
            Theme.Mode = mode;
            ControlPanel.UserScale = scale / 100f;
            Func<string, string, string, ToolStripMenuItem> I = (t, ic, n) => MenuKit.Icon(MenuKit.Item(t, null), ic, n);
            var st = MenuKit.Info("Работает\tALT", ItemKind.Status);
            var he = MenuKit.Info("✓\t✓\t1/2\t✓", ItemKind.Health);
            var sp = MenuKit.Info("↓ 8,4 МБ/с\t32 мс\t3 ч 40 мин", ItemKind.Stats);
            var menu = new ContextMenuStrip();
            menu.Items.AddRange(new ToolStripItem[] { st, he, sp });
            var games = I("Мои игры", "games", null);
            var sites = I("Мои сайты", "sites", null);
            var conn = I("Подключение", "connection", null);
            var set = I("Настройки", "settings", null);
            var hints = I("Подсказки", "hints", null);
            var f1 = I("Подобрать конфиг заново", "fast", "Если сайт или игра перестали открываться");
            var f2 = I("Отправить баг", "load", "Отчёт сам уйдёт разработчику");
            var tg = I("Выключить", "power", null);
            var ex = I("Выход", "exit", null);
            menu.Items.AddRange(new ToolStripItem[] { games, sites, conn, set, hints, f1, f2, tg, ex });

            games.DropDownItems.Add(I("Добавить игру...", "plus", "Выбери запущенную игру из списка"));
            games.DropDownItems.Add(MenuKit.Sep("Встроенные игры"));
            var arc = I("ARC Raiders", "games", "Клик включает или выключает обход");
            arc.Checked = true;
            arc.Tag = ItemKind.Toggle;
            var wardogs = I("Wardogs", "games", "Клик включает или выключает обход");
            wardogs.Tag = ItemKind.Toggle;
            games.DropDownItems.Add(arc);
            games.DropDownItems.Add(wardogs);

            conn.DropDownItems.Add(I("Подобрать конфиг заново", "fast", "Если сайт или игра перестали открываться"));
            conn.DropDownItems.Add(I("Подобрать конфиг с лучшим пингом", "ping", "Проверит все и выберет быстрый, это дольше"));
            conn.DropDownItems.Add(MenuKit.Sep("DNS"));
            conn.DropDownItems.Add(I("Исправить DNS", "dns", "С откатом"));
            conn.DropDownItems.Add(I("Вернуть DNS как было", "undo", null));

            var watchdog = I("Сторож", "shield", "Сам чинит");
            watchdog.Tag = ItemKind.Toggle;
            watchdog.Checked = true;
            var theme = I("Тема", "theme", "Тёмная, светлая или как в Windows");
            theme.DropDownItems.Add(I("Тёмная", "moon", null));
            var size = I("Размер окна", "monitor", null);
            foreach (var v in ControlPanel.ScaleStops) size.DropDownItems.Add(I(v + "%", "monitor", null));
            var behavior = I("Поведение", "sliders", "Сторож, автозапуск, обновления");
            behavior.DropDownItems.Add(watchdog);
            set.DropDownItems.AddRange(new ToolStripItem[] {
                MenuKit.Sep("Вид"), theme, size, MenuKit.Sep("Работа"), behavior, I("Проверить обновления", "refresh", "сейчас " + AppInfo.Version),
                MenuKit.Sep("Перенос настроек"), I("Сохранить настройки", "save", "В файл, чтобы взять на другой компьютер"), I("Подгрузить настройки", "load", "Взять из сохранённого файла"),
                MenuKit.Sep("Программа"), I("Открыть папку", "folder", "Файлы и журнал программы"), I("Удалить программу", "trash", "Сначала спросит подтверждение") });

            var list = new List<Tuple<string, string, bool>> { Tuple.Create("rutracker.org", "list", false), Tuple.Create("example.com", "list", true), Tuple.Create("1.2.3.4", "ipset", false) };
            var hintsView = new HintsView();
            var p = new ControlPanel(st, he, sp, () => Theme.On, () => "отклик 183 мс\tотклик 120 мс\tотклик 90 мс\nпинг 44 мс\tотклик 200 мс\nпинг 28 мс",
                new[] { games, sites, conn, set, hints }, new[] { f1, f2 }, tg, ex, () => { },
                new Dictionary<ToolStripMenuItem, IPanelView> { { sites, new SitesView(() => list, (x, y) => { }, (x, y) => { }, x => { }) }, { hints, hintsView } });
            p.ToggleTheme = () => { };
            p.ScaleItem = size;
            p.ScaleChanged = v => { };
            // Час замеров: ровный пинг, пара скачков, короткий обрыв; скорость - всплеск в конце
            var rnd = new Random(7);
            var ping = new long[360];
            var speed = new long[360];
            var times = new DateTime[360];
            for (int k = 0; k < 360; k++)
            {
                ping[k] = k > 200 && k < 212 ? -1 : 30 + rnd.Next(0, 12) + (k > 120 && k < 135 ? 70 : 0) + (k == 250 ? 140 : 0);
                speed[k] = rnd.Next(5, 60) * 1024 + (k > 300 ? 2 * 1024 * 1024 : 0);
                times[k] = DateTime.UtcNow.AddSeconds(-10 * (359 - k));
            }
            p.TileHistory = t => t == 4 ? speed : ping;
            p.TileTimes = t => times;
            p.StartPosition = FormStartPosition.Manual;
            p.Location = new Point(-4000, -4000);
            p.Show();
            Application.DoEvents();

            string name = mode + "-" + scale + "-";
            Save(p, name + "home");
            // Подсказка у вопросика в плитке игр
            var hidden = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            typeof(ControlPanel).GetField("hover", hidden).SetValue(p, "help");
            Save(p, name + "home-help");
            typeof(ControlPanel).GetField("hover", hidden).SetValue(p, null);
            if (scale == 100)
            {
                p.OpenSection(games); Save(p, name + "games");
                p.OpenSection(conn); Save(p, name + "connection");
                p.OpenSection(set); Save(p, name + "settings");
                p.OpenSection(sites); Save(p, name + "sites");
                p.OpenSection(hints); Save(p, name + "hints");
            }
            p.Close();
        }

        static void Dialogs(string mode)
        {
            Theme.Mode = mode;
            foreach (var f in new Form[] { new BugDialog(), new WhatsNewDialog("0.7"), new WhatsNewDialog(WhatsNewDialog.Welcome) })
            {
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new Point(-4000, -4000);
                f.Show();
                Application.DoEvents();
                Save(f, mode + "-" + (f is BugDialog ? "bug" : f.Text == "Zapret Tray" ? "welcome" : "whatsnew"));
                f.Close();
            }
        }
    }
}
