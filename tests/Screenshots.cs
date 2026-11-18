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
        { ControlPanel.NoAnimation = true;
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
            var set = I("Настройки", "settings", null);
            var hints = I("Подсказки", "hints", "Что делать, если что-то не работает");
            var checks = I("Обязательные проверки", "ping", "Под какие сайты подбирать конфиг");
            var f1 = I("Подобрать конфиг заново", "fast", "Если сайт или игра перестали открываться");
            var f2 = I("Отправить баг", "load", "Отчёт сам уйдёт разработчику");
            var tg = I("Выключить", "power", null);
            var ex = I("Выход", "exit", null);
            menu.Items.AddRange(new ToolStripItem[] { games, sites, set, f1, f2, tg, ex });

            games.DropDownItems.Add(I("Добавить игру...", "plus", "Выбери запущенную игру из списка"));
            games.DropDownItems.Add(MenuKit.Sep("Встроенные игры"));
            var arc = I("ARC Raiders", "games", "Клик включает или выключает обход");
            arc.Checked = true;
            arc.Tag = ItemKind.Toggle;
            var wardogs = I("Wardogs", "games", "Клик включает или выключает обход");
            wardogs.Tag = ItemKind.Toggle;
            games.DropDownItems.Add(arc);
            games.DropDownItems.Add(wardogs);


            var watchdog = I("Сторож", "shield", "Сам чинит");
            watchdog.Tag = ItemKind.Toggle;
            watchdog.Checked = true;
            var theme = I("Тема", "theme", "Тёмная, светлая или как в Windows");
            theme.DropDownItems.Add(I("Тёмная", "moon", null));
            var size = I("Размер окна", "monitor", null);
            foreach (var v in ControlPanel.ScaleStops) size.DropDownItems.Add(I(v + "%", "monitor", null));
            var behavior = I("Поведение", "config", "Сторож, автозапуск, уведомления");
            behavior.DropDownItems.Add(watchdog);
            // Настройки одной страницей, как в Tray.BuildMenu
            var version = I("Версия " + AppInfo.Version, "refresh", "zapret 1.9.0");
            var versionButton = I("Проверить", "refresh", null);
            var modes = new[] { I("Спрашивать", "refresh", "Новая версия:"), I("Сама", "refresh", null), I("Вручную", "refresh", null) };
            modes[0].Checked = true;
            var trouble = I("Что-то не работает", "report", "Твой номер 4821-7735-0912 · нажми, чтобы скопировать");
            var copyReport = I("Скопировать отчёт", "report", null);
            var sendBug = I("Отправить баг", "load", null);
            var manual = I("Выбрать конфиг вручную", "list", null);
            set.DropDownItems.AddRange(new ToolStripItem[] {
                MenuKit.Sep("Обновления"), version, modes[0], modes[1], modes[2],
                MenuKit.Sep("Подключение"), I("Подобрать конфиг заново", "fast", "Если сайт или игра перестали открываться"), I("Подобрать конфиг с лучшим пингом", "ping", "Проверит все и выберет быстрый, это дольше"),
                I("Исправить DNS", "dns", "Если YouTube не находится"), I("Вернуть DNS как было", "undo", "Настройки DNS станут как раньше"), manual,
                MenuKit.Sep("Помощь"), hints, trouble, copyReport, sendBug,
                MenuKit.Sep("Вид"), theme, size, MenuKit.Sep("Работа"), behavior, checks,
                MenuKit.Sep("Перенос настроек"), I("Сохранить настройки", "save", "В файл, чтобы взять на другой компьютер"), I("Подгрузить настройки", "load", "Взять из сохранённого файла"),
                MenuKit.Sep("Программа"), I("Открыть папку", "folder", "Файлы и журнал программы"), I("Удалить программу", "trash", "Сначала спросит подтверждение") });

            var list = new List<Tuple<string, string, bool>> { Tuple.Create("rutracker.org", "list", false), Tuple.Create("example.com", "list", true), Tuple.Create("1.2.3.4", "ipset", false) };
            var hintsView = new HintsView();
            var p = new ControlPanel(st, he, sp, () => Theme.On, () => "отклик 183 мс\tотклик 120 мс\tотклик 90 мс\nпинг 44 мс\tотклик 200 мс\nпинг 28 мс",
                new[] { games, sites, set }, new[] { f1, f2 }, tg, ex, () => { },
                new Dictionary<ToolStripMenuItem, IPanelView> { { sites, new SitesView(() => list, (x, y) => { }, (x, y) => { }, x => { }) }, { hints, hintsView },
                    { checks, new ChecksView(() => new List<Check> { new Check("https://youtube.com", true), new Check("https://discord.com", true), new Check("https://arcraiders.com", false) }, x => { }, (x, y) => { }, x => { }) } });
            p.Wide.Add(version);
            p.Wide.Add(trouble);
            p.Wide.Add(hints);
            p.Segments[version] = modes;
            p.Buttons[version] = new[] { versionButton };
            p.Buttons[trouble] = new[] { copyReport, sendBug };
            p.Hidden.Add(manual);
            p.Hidden.Add(copyReport);
            p.Hidden.Add(sendBug);
            foreach (var m in modes) p.Hidden.Add(m);
            bool hasUpdate = false;
            p.HasUpdate = () => hasUpdate;
            // Плашка статуса: конфиги и действия под ними
            foreach (var c in new[] { "general", "general (ALT)", "general (ALT2)", "general (ALT3)", "general (ALT4)", "general (ALT5)", "general (ALT6)", "general (ALT7)", "general (ALT8)", "general (ALT9)", "general (ALT10)", "general (ALT11)", "general (FAKE TLS AUTO)", "general (SIMPLE FAKE)" })
            {
                var ci = I(c, "config", null);
                ci.Checked = c == "general (ALT11)";
                manual.DropDownItems.Add(ci);
            }
            p.ConfigMenu = manual;
            p.PillActions = new[] { f1, I("Подобрать конфиг с лучшим пингом", "ping", null), tg };
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
                p.OpenSection(set); Save(p, name + "settings");
                // Полоску прокрутки тянут мышью; клик по пустому месту полоски перематывает туда
                Func<Point, Point> D = pt => p.ToDevice(new Rectangle(pt, new Size(1, 1))).Location;
                Action<string, Point> M = (what, pt) => typeof(Control).GetMethod(what, hidden).Invoke(p, new object[] { new MouseEventArgs(MouseButtons.Left, 1, D(pt).X, D(pt).Y, 0) });
                var grab = p.ScrollThumbCenter;
                M("OnMouseDown", grab); M("OnMouseMove", new Point(grab.X, grab.Y + 150)); M("OnMouseUp", new Point(grab.X, grab.Y + 150));
                Ok(p.Scroll > 0, mode + " полоску прокрутки можно тянуть мышью");
                int dragged = p.Scroll;
                M("OnMouseDown", new Point(grab.X, 80)); M("OnMouseUp", new Point(grab.X, 80));
                Ok(p.Scroll < dragged, mode + " клик по полоске выше ползунка мотает вверх");
                typeof(ControlPanel).GetField("scroll", hidden).SetValue(p, 0);
                // Низ настроек: прокручено до конца
                typeof(ControlPanel).GetField("scroll", hidden).SetValue(p, 10000);
                Save(p, name + "settings-bottom");
                // Нашлась новая версия: стрелка в карточке, "Обновить", точка на "Настройках"
                hasUpdate = true;
                version.Text = "Версия " + AppInfo.Version + " → 9.9";
                version.ToolTipText = "Вышла новая. Без тебя не поставлю";
                versionButton.Text = "Обновить";
                p.OpenSection(set); Save(p, name + "settings-update");
                hasUpdate = false;
                p.OpenByName("Обязательные проверки"); Save(p, name + "checks");
                p.OpenByName("Подсказки"); Save(p, name + "hints");
                Ok(p.OpenByName("Подсказки"), "подсказки открываются из настроек по названию");
                Ok(!p.OpenByName("Подобрать конфиг заново"), "по названию не нажимается обычный пункт");
                p.OpenSection(sites); Save(p, name + "sites");
                // Плашка «Работает · ALT» раскрывает список конфигов, клик мимо закрывает
                p.OpenSection(null); p.Refresh();
                M("OnMouseClick", p.PillCenter);
                Ok(p.PillOpen, mode + " плашка статуса раскрывает список конфигов");
                Save(p, name + "home-pill");
                M("OnMouseClick", new Point(300, 600));
                Ok(!p.PillOpen, mode + " клик мимо закрывает список конфигов");
                p.OpenSection(sites); p.Refresh();
                Ok(p.PillCenter.X > 0, mode + " плашка есть и на «Моих сайтах»");
            }
            p.Close();
        }

        static void Dialogs(string mode)
        {
            Theme.Mode = mode;
            // Окно вопроса "Вышла новая версия" - конструктор закрытый, снимаем через него напрямую
            var ask = (Form)typeof(WhatsNewDialog).GetConstructor(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, new[] { typeof(string), typeof(string) }, null)
                .Invoke(new object[] { "9.9", AppInfo.Version });
            ask.StartPosition = FormStartPosition.Manual;
            ask.Location = new Point(-4000, -4000);
            ask.Show();
            Application.DoEvents();
            Save(ask, mode + "-update-ask");
            ask.Close();
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
