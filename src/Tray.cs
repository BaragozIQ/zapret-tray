// ZapretTray.exe - значок в трее: вкл/выкл, подбор конфига, сторож, автозапуск, свои сайты и игры
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ZapretTray
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            // Без прав админа: запускаемся через задачу планировщика (без окна UAC), иначе просим права
            if (!Util.IsAdmin())
            {
                if (Tasks.Exists(AppInfo.RunTask) && Tasks.RunNow(AppInfo.RunTask)) return;
                try
                {
                    Process.Start(new ProcessStartInfo(Application.ExecutablePath) { Verb = "runas", UseShellExecute = true });
                }
                catch { }
                return;
            }

            bool created;
            using (new Mutex(true, "Global\\ZapretTray.Instance", out created))
            {
                if (!created) return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayContext());
            }
        }
    }

    class TrayContext : ApplicationContext
    {
        const int WatchIntervalMs = 10 * 60 * 1000;
        const int RecheckIntervalMs = 60 * 1000;
        const int SoonIntervalMs = 15 * 1000;
        const int MaxCheckedSites = 10;

        readonly AppPaths paths = new AppPaths(AppDomain.CurrentDomain.BaseDirectory);
        readonly Settings settings;
        readonly GameStore games;
        readonly FlowWatcher flows = new FlowWatcher();
        readonly Control ui = new Control();

        readonly NotifyIcon tray = new NotifyIcon();
        readonly Icon iconOn, iconOff, iconBusy;
        readonly System.Windows.Forms.Timer stateTimer = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer watchTimer = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer gameTimer = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer updateTimer = new System.Windows.Forms.Timer();
        bool updating;

        InfoItem statusItem, healthItem, statsItem;
        ToolStripMenuItem toggleItem, repairItem, fastItem, bestItem, manualItem;
        ToolStripMenuItem sitesItem, reportItem, settingsItem, themeItem, autoupdateItem, notifyItem, updateItem, dnsFixItem, dnsUndoItem;
        bool dnsBusy, dnsOffered, watchVpnLogged;

        // Значки в меню: результат последней проверки, null - проверки ещё не было
        static readonly string[] MainSites = { "https://www.youtube.com", "https://discord.com" };
        const int HealthCacheSec = 30;
        string healthMarks;
        bool healthProblem, healthVpn, healthChecking;
        int healthGen, healthSites, healthGames;
        DateTime healthAt = DateTime.MinValue;

        ContextMenuStrip menu;
        ThemedRenderer renderer;
        Palette lastPalette;
        Action pendingClick;            // что сделать по клику на последнее уведомление
        Action<bool> afterSelect;       // кто ждёт результат подбора (обновление конфигов)

        // Скорость сети: разница счётчиков байт между тиками
        long rxPrev = -1, txPrev = -1;
        DateTime speedAt;
        string speedText = "";
        long pingMs = -1;
        DateTime pingAt = DateTime.MinValue;
        bool pinging;
        ToolStripMenuItem gamesItem, behaviorItem, watchdogItem, autostartItem, hintsItem, exitItem;
        ControlPanel panel;

        bool wantOn;            // пользователь хочет, чтобы zapret работал
        bool busy;              // идёт подбор
        string busyText = "";
        volatile int selectGen;         // номер текущего подбора; сменился - подбор отменён
        readonly object selectLock = new object();
        bool watchFailedOnce;
        bool checking;
        bool selectFailShown;

        // pid -> имя процесса (кэш для слежения за играми)
        readonly Dictionary<int, string> pidNames = new Dictionary<int, string>();
        readonly object pidLock = new object();
        volatile HashSet<string> watchedExes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        volatile bool gamesDirty;
        readonly HashSet<string> gamesRunning = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> gamesLearnedThisSession = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public TrayContext()
        {
            ui.CreateControl();
            var h = ui.Handle;
            Logger.File_ = Path.Combine(paths.Utils, "tray.log");
            Logger.Write("запуск, версия " + AppInfo.Version);
            settings = new Settings(paths.Settings);
            games = new GameStore(paths);

            iconOn = MakeIcon(Color.FromArgb(46, 170, 80));
            iconOff = MakeIcon(Color.FromArgb(150, 150, 150));
            iconBusy = MakeIcon(Color.FromArgb(235, 175, 30));

            Theme.Mode = settings.Get("theme") ?? Theme.System;
            BuildMenu();
            tray.Icon = iconOff;
            tray.Text = AppInfo.Name;
            tray.Visible = true;
            tray.DoubleClick += (s, e) => Toggle();
            tray.BalloonTipClicked += (s, e) =>
            {
                var a = pendingClick;
                pendingClick = null;
                if (a != null) a();
            };
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += (s, e) => OnUi(ApplyTheme);
            Microsoft.Win32.SystemEvents.PowerModeChanged += (s, e) =>
            {
                if (e.Mode == Microsoft.Win32.PowerModes.Resume) OnUi(CheckSoon);
            };
            System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += (s, e) => OnUi(CheckSoon);

            // Обновление конфигов прервалось на середине (выключили комп и т.п.) - возвращаем старые
            if (ZapretUpdater.BackupExists(paths))
            {
                try { ZapretUpdater.Restore(paths); } catch { }
            }

            stateTimer.Interval = 2000;
            stateTimer.Tick += (s, e) => UpdateState();
            stateTimer.Start();

            watchTimer.Interval = WatchIntervalMs;
            watchTimer.Tick += (s, e) => WatchdogTick();
            watchTimer.Start();

            gameTimer.Interval = 4000;
            gameTimer.Tick += (s, e) => GamesTick();
            gameTimer.Start();

            // Первая проверка обновлений через 2 минуты после запуска, дальше раз в час смотрим, прошли ли сутки
            updateTimer.Interval = 2 * 60 * 1000;
            updateTimer.Tick += (s, e) => { updateTimer.Interval = 60 * 60 * 1000; CheckUpdate(false); };
            updateTimer.Start();

            var lastVersion = settings.Get("version");
            if (lastVersion != AppInfo.Version)
            {
                if (lastVersion != null) Logger.Write("обновлено до версии " + AppInfo.Version);
                settings.Set("version", AppInfo.Version);
            }

            flows.FlowSeen += OnFlow;
            RefreshWatchedGames();

            if (Util.ServiceExists("zapret"))
            {
                Notify("Установлена служба zapret - она мешает. Запусти Setup.exe и отметь 'Удалить службу zapret'.", ToolTipIcon.Warning);
            }

            ReapplyIpset();
            SyncGameFilter();
            TurnOn();
        }

        // ---------- значок и меню ----------

        static Icon MakeIcon(Color color)
        {
            var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var b = new SolidBrush(color)) g.FillEllipse(b, 2, 2, 28, 28);
                using (var p = new Pen(Color.FromArgb(60, 0, 0, 0), 2)) g.DrawEllipse(p, 2, 2, 28, 28);
                using (var f = new Font("Segoe UI", 15, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString("Z", f, Brushes.White, new RectangleF(0, 1, 32, 32), sf);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }

        void BuildMenu()
        {
            menu = new ContextMenuStrip();
            MenuKit.Menu(menu);
            MenuKit.FixWidth(menu, MenuKit.MenuWidth);

            statusItem = Info("...", ItemKind.Status);
            healthItem = Info("", ItemKind.Health);
            statsItem = Info("", ItemKind.Stats);

            gamesItem = MenuKit.Icon(Item("Мои игры", null), "games");

            fastItem = MenuKit.Icon(Item("Подобрать: быстро", (s, e) => { wantOn = true; RunSelect("fast", false, true); }), "fast", "Найдёт первый рабочий конфиг");
            bestItem = MenuKit.Icon(Item("Подобрать: минимальный пинг", (s, e) => { wantOn = true; RunSelect("best", false, true); }), "ping", "Проверит все и выберет лучший, это дольше");
            manualItem = MenuKit.Icon(Item("Выбрать конфиг вручную", null), "list", "Если знаешь, какой нужен");
            sitesItem = MenuKit.Icon(Item("Мои сайты...", (s, e) => OpenSites()), "sites");
            reportItem = MenuKit.Icon(Item("Что-то не работает", (s, e) => MakeReport()), "report", "Скопирует отчёт, вставь его в чат");
            dnsFixItem = MenuKit.Icon(Item("Исправить DNS", (s, e) => ApplyDnsFix()), "dns", "Если YouTube не находится");
            dnsUndoItem = MenuKit.Icon(Item("Вернуть DNS как было", (s, e) => UndoDnsFix()), "undo", "Настройки DNS станут как раньше");

            // Всё, что нужно только при поломке
            repairItem = MenuKit.Icon(Item("Подключение", null), "connection");
            repairItem.DropDownItems.AddRange(new ToolStripItem[] {
                MenuKit.Sep("Конфиг"), fastItem, bestItem, manualItem,
                MenuKit.Sep("DNS"), dnsFixItem, dnsUndoItem,
                MenuKit.Sep("Не помогло"), reportItem
            });

            // Настройки
            settingsItem = MenuKit.Icon(Item("Настройки", null), "settings");
            themeItem = MenuKit.Icon(Item("Тема", null), "theme", "Тёмная, светлая или как в Windows");
            foreach (var t in new[] { Tuple.Create(Theme.System, "Как в Windows", "monitor"), Tuple.Create(Theme.Light, "Светлая", "sun"), Tuple.Create(Theme.Dark, "Тёмная", "moon") })
            {
                var mode = t.Item1;
                themeItem.DropDownItems.Add(MenuKit.Icon(Item(t.Item2, (s, e) => SetTheme(mode)), t.Item3));
                themeItem.DropDownItems[themeItem.DropDownItems.Count - 1].Tag = mode;
            }
            watchdogItem = MenuKit.Icon(Check("Сторож (сам чинит, если сломалось)", "watchdog"), "shield");
            autostartItem = MenuKit.Icon(Item("Запускать вместе с Windows", null), "play", "Включается сама при входе");
            autostartItem.CheckOnClick = true;
            autostartItem.Click += (s, e) => SetAutostart(autostartItem.Checked);
            autoupdateItem = MenuKit.Icon(Check("Обновляться автоматически", "autoupdate"), "refresh", "Раз в день, настройки сохраняются");
            notifyItem = MenuKit.Icon(Check("Уведомления", "notify"), "bell", "Важные придут в любом случае");

            // Галочки "как программа себя ведёт" - отдельной группой
            behaviorItem = MenuKit.Icon(Item("Поведение", null), "config", "Сторож, автозапуск, обновления");
            behaviorItem.DropDownItems.AddRange(new ToolStripItem[] { watchdogItem, autostartItem, autoupdateItem, notifyItem });
            updateItem = MenuKit.Icon(Item("Проверить обновления", (s, e) => CheckUpdate(true)), "refresh");
            settingsItem.DropDownItems.AddRange(new ToolStripItem[] {
                MenuKit.Sep("Вид и работа"), themeItem, behaviorItem, MenuKit.Sep("Перенос настроек"),
                MenuKit.Icon(Item("Сохранить настройки...", (s, e) => SaveSettings()), "save", "В файл, чтобы взять на другой компьютер"),
                MenuKit.Icon(Item("Подгрузить настройки...", (s, e) => LoadSettings()), "load", "Взять из сохранённого файла"), MenuKit.Sep("Программа"),
                updateItem,
                MenuKit.Icon(Item("Открыть папку", (s, e) => Process.Start("explorer.exe", "\"" + paths.Root + "\"")), "folder", "Файлы и журнал программы"),
                MenuKit.Icon(Item("Удалить программу...", (s, e) => Uninstall()), "trash", "Сначала спросит подтверждение")
            });

            toggleItem = MenuKit.Icon(Item("Выключить", (s, e) => Toggle()), "power");
            hintsItem = MenuKit.Icon(Item("Подсказки...", (s, e) => OpenHints()), "hints");
            exitItem = MenuKit.Icon(Item("Выход", (s, e) => ExitApp()), "exit", "Закрыть программу, zapret выключится");

            menu.Items.AddRange(new ToolStripItem[] {
                statusItem, healthItem, statsItem, MenuKit.Sep(),
                gamesItem, sitesItem, MenuKit.Sep(),
                repairItem, settingsItem,
                hintsItem, MenuKit.Sep(),
                toggleItem,
                exitItem
            });

            MenuKit.SubMenu(gamesItem, MenuKit.GamesWidth);
            MenuKit.SubMenu(repairItem, MenuKit.RepairWidth);
            MenuKit.SubMenu(manualItem, MenuKit.ManualWidth);
            MenuKit.SubMenu(settingsItem, MenuKit.SettingsWidth);
            MenuKit.SubMenu(themeItem, MenuKit.ThemeWidth);
            MenuKit.SubMenu(behaviorItem, MenuKit.BehaviorWidth);

            menu.Opening += (s, e) => { ApplyTheme(); CheckHealth(); RefreshMenu(); };
            ApplyTheme();
            tray.MouseUp += (s, e) => { if (e.Button == MouseButtons.Right) OpenPanel(); };
        }

        // Правая кнопка: окно с разделами. Оно рисует те же пункты меню. Если окно не открылось, показываем обычное меню
        void OpenPanel()
        {
            if (panel != null) return;
            ApplyTheme();
            CheckHealth();
            RefreshMenu();
            try
            {
                panel = new ControlPanel(statusItem, healthItem, statsItem, () => renderer.StatusColor,
                    new[] { gamesItem, sitesItem, repairItem, settingsItem, hintsItem }, new[] { fastItem, reportItem }, toggleItem, exitItem, () => { RefreshMenu(); },
                    new Dictionary<ToolStripMenuItem, IPanelView>
                    {
                        { sitesItem, new SitesView(MySites,
                            (value, isIp) => { bool c; ApplyEntry(value, isIp, false, out c); },
                            (entry, exclude) => { bool c; ApplyEntry(entry.Item1, IsIpFile(entry.Item2), exclude, out c); },
                            entry => RemoveSite(entry.Item1, entry.Item2)) },
                        { hintsItem, new HintsView() }
                    });
                panel.FormClosed += (s, e) => panel = null;
                panel.ShowCentered();
            }
            catch (Exception ex)
            {
                Logger.Write("окно: " + ex.Message);
                panel = null;
                menu.Show(Cursor.Position);
            }
        }

        static ToolStripMenuItem Item(string text, EventHandler click) { return MenuKit.Item(text, click); }

        static InfoItem Info(string text, string kind) { return MenuKit.Info(text, kind); }

        ToolStripMenuItem Check(string text, string key)
        {
            var item = Item(text, null);
            item.CheckOnClick = true;
            item.Checked = settings.GetBool(key, true);
            item.CheckedChanged += (s, e) => settings.SetBool(key, item.Checked);
            return item;
        }


        void SetTheme(string mode)
        {
            Theme.Mode = mode;
            settings.Set("theme", mode);
            ApplyTheme();
        }

        // Тема могла смениться (выбрали в меню или Windows переключилась день/ночь) - перерисовываем
        void ApplyTheme()
        {
            var p = Theme.Current;
            if (p == lastPalette && renderer != null) return;
            lastPalette = p;
            var color = renderer != null ? renderer.StatusColor : Theme.Off;
            renderer = new ThemedRenderer { StatusColor = color };
            ToolStripManager.Renderer = renderer;
            menu.Renderer = renderer;
            Theme.RoundCorners(menu);
        }

        void RefreshMenu()
        {
            UpdateLabels();
            toggleItem.Text = wantOn ? "Выключить" : "Включить";
            toggleItem.ToolTipText = wantOn ? "Обход блокировок остановится" : "Обход блокировок заработает";
            toggleItem.Tag = wantOn ? ItemKind.PowerOff : ItemKind.PowerOn;
            fastItem.Enabled = bestItem.Enabled = manualItem.Enabled = !busy;
            bool dnsApplied = DnsFix.IsApplied(paths);
            dnsFixItem.Enabled = !dnsApplied && !dnsBusy;
            dnsUndoItem.Enabled = dnsApplied && !dnsBusy;
            autostartItem.Checked = Tasks.IsEnabled(AppInfo.AutoTask);
            updateItem.Text = "Проверить обновления (сейчас " + AppInfo.Version + ", zapret " + ZapretUpdater.LocalVersion(paths) + ")";
            foreach (ToolStripMenuItem t in themeItem.DropDownItems) t.Checked = (string)t.Tag == Theme.Mode;

            // Выбор конфига вручную: все general*.bat, текущий отмечен
            manualItem.DropDownItems.Clear();
            var current = CurrentConfig();
            bool running = Util.WinwsRunning();
            foreach (var bat in ConfigFiles())
            {
                var name = Path.GetFileNameWithoutExtension(bat);
                var file = Path.GetFileName(bat);
                var item = MenuKit.Icon(Item(name, (s, e) => UseConfig(file)), "config");
                item.Checked = running && name == current;
                manualItem.DropDownItems.Add(item);
            }

            gamesItem.DropDownItems.Clear();
            gamesItem.DropDownItems.Add(MenuKit.Icon(Item("Добавить игру...", (s, e) => AddGame()), "plus", "Выбери запущенную игру из списка"));

            // Встроенные игры: обход по их доменам, клик - включить/выключить
            gamesItem.DropDownItems.Add(MenuKit.Sep("Встроенные игры"));
            var mine = new HashSet<string>(Util.ReadEntries(paths.ListGeneralUser), StringComparer.OrdinalIgnoreCase);
            foreach (var p in GamePresets)
            {
                var preset = p;
                var item = MenuKit.Icon(Item(preset.Item1, (s, e) => TogglePreset(preset)), "games", "Клик включает или выключает обход");
                item.Checked = preset.Item2.All(mine.Contains);
                item.Tag = ItemKind.Toggle;
                gamesItem.DropDownItems.Add(item);
            }

            if (games.Games.Count > 0) gamesItem.DropDownItems.Add(MenuKit.Sep("Твои игры"));
            foreach (var g in games.Games.ToList())
            {
                var game = g;
                var item = MenuKit.Icon(Item(game.Exe + "   (адресов: " + game.Subnets.Count + (game.Enabled ? ")" : ", выкл)"), null), "games");
                var enable = MenuKit.Icon(Item("Включена", (s, e) => ToggleGame(game)), "power", "Галочка: адреса игры используются");
                enable.Checked = game.Enabled;
                enable.Tag = ItemKind.Toggle;
                item.DropDownItems.Add(enable);
                item.DropDownItems.Add(MenuKit.Icon(Item("Удалить", (s, e) => RemoveGame(game)), "trash", "Вместе с собранными адресами"));
                MenuKit.SubMenu(item, MenuKit.GameWidth);
                gamesItem.DropDownItems.Add(item);
            }
        }

        string CurrentConfig()
        {
            var names = Util.ReadEntries(paths.BestConfig);
            return names.Count > 0 ? Path.GetFileNameWithoutExtension(names[0]) : null;
        }

        string StatusText()
        {
            if (busy) return busyText;
            if (Util.WinwsRunning())
            {
                var cfg = CurrentConfig();
                return "Работает" + (cfg != null ? "  ·  " + cfg : "");
            }
            return wantOn ? "Не запущен" : "Выключен";
        }

        // Сколько работает winws: "3 ч 12 мин"
        static string UptimeText()
        {
            try
            {
                var p = Process.GetProcessesByName("winws").FirstOrDefault();
                if (p == null) return "—";
                var t = DateTime.Now - p.StartTime;
                if (t.TotalMinutes < 1) return "<1 мин";
                if (t.TotalHours < 1) return (int)t.TotalMinutes + " мин";
                return (int)t.TotalHours + " ч " + t.Minutes + " мин";
            }
            catch { return "—"; }
        }

        // Первая строка меню: статус и справа конфиг
        string MenuStatus(bool running)
        {
            if (busy) return busyText;
            if (!running) return wantOn ? "Не запущен" : "Выключен";
            var title = healthProblem ? "Есть проблема" : healthVpn ? "Работает через VPN" : "Работает";
            var cfg = CurrentConfig();
            return cfg != null ? title + "\t" + cfg : title;
        }

        string HealthDots()
        {
            return "⋯\t⋯\t" + (healthGames > 0 ? "⋯" : "") + "\t" + (healthSites > 0 ? "⋯" : "");
        }

        // Открыли меню: проверяем сайты в фоне. Меню не ждёт, значки обновятся прямо в нём
        void CheckHealth()
        {
            var presets = EnabledPresets();
            healthSites = UserSites().Count;
            healthGames = presets.Count;
            if (busy || healthChecking || !Util.WinwsRunning()) return;
            if (healthMarks != null && (DateTime.UtcNow - healthAt).TotalSeconds < HealthCacheSec) return;
            ResetHealth();
            healthChecking = true;
            int gen = healthGen;
            var sites = UserSites();
            Task.Factory.StartNew(() =>
            {
                var vpn = Util.RunningVpn();
                var gameUrls = presets.SelectMany(p => p.Item2).Select(d => "https://" + d);
                return Tuple.Create(SiteCheck.RunAll(MainSites.Concat(sites).Concat(gameUrls), 8), vpn != null);
            }).ContinueWith(t => OnUi(() =>
            {
                healthChecking = false;
                if (t.IsFaulted)
                {
                    Logger.Write("значки: " + t.Exception.InnerException.Message);
                    return;
                }
                if (gen != healthGen) return;   // пока проверяли, zapret перезапустился: результат уже не про него
                var res = t.Result.Item1;
                var main = res.Take(MainSites.Length).ToList();
                var mine = res.Skip(MainSites.Length).Take(sites.Count).ToList();
                var gameSites = res.Skip(MainSites.Length + sites.Count).ToList();
                // Игра считается рабочей, если отвечает хотя бы один её адрес: не все адреса игр открываются как сайт
                int gamesOk = presets.Count(p => p.Item2.Any(d => gameSites.Any(x => x.Opened && x.Url == "https://" + d)));
                healthMarks = string.Join("\t", main.Select(x => x.Opened ? "✓" : "✗"))
                    + "\t" + (presets.Count == 0 ? "" : presets.Count > 1 ? gamesOk + "/" + presets.Count : gamesOk == 1 ? "✓" : "✗")
                    + "\t" + (mine.Count > 0 ? mine.Count(x => x.Opened) + "/" + mine.Count : "");
                healthProblem = main.Any(x => !x.Opened);
                healthVpn = t.Result.Item2;
                healthAt = DateTime.UtcNow;
                UpdateLabels();
            }));
        }

        // zapret перезапустился или выключился: прошлый результат больше не верен
        void ResetHealth()
        {
            healthGen++;
            healthMarks = null;
            healthProblem = healthVpn = false;
        }

        void UpdateState()
        {
            UpdateSpeed();
            UpdateLabels();
        }

        void UpdateLabels()
        {
            bool running = Util.WinwsRunning();
            tray.Icon = busy ? iconBusy : running ? iconOn : iconOff;
            renderer.StatusColor = busy || (running && healthProblem) ? Theme.Busy : running ? Theme.On : Theme.Off;
            statusItem.Value = MenuStatus(running);
            // Выключен - показать нечего, строки убираем
            // В открытом меню высоту не меняем, иначе всё прыгает
            if (!menu.Visible) healthItem.Visible = statsItem.Visible = running || busy;
            healthItem.Value = busy || healthMarks == null ? HealthDots() : healthMarks;
            var down = speedText.Length > 0 ? speedText.Split(new[] { (char)9 })[0] : "↓ —";
            statsItem.Value = down + "\t" + (pingMs >= 0 ? pingMs + " мс" : "—") + "\t" + (running ? UptimeText() : "—");
            if (menu.Visible) { statusItem.Invalidate(); healthItem.Invalidate(); statsItem.Invalidate(); }
            if (panel != null) panel.Invalidate();
            // Подсказка Windows сама подгоняет ширину под самую длинную строку. Строка статуса длиннее строк с цифрами,
            // поэтому от цифр подсказка не растягивается
            var text = "zapret: " + StatusText();
            if (running && !busy && speedText.Length > 0)
            {
                var stats = speedText.Split('\t');
                text += "\n" + stats[0] + "   " + stats[1] + "\n" + stats[2];
            }
            SetTrayText(text);
        }

        // Свойство Text у значка не принимает больше 63 символов, а самой Windows хватает 127.
        // Длинное имя конфига в 63 не влезает, поэтому пишем в обход проверки
        void SetTrayText(string text)
        {
            if (text.Length > 127) text = text.Substring(0, 127);
            try
            {
                var hidden = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var type = typeof(NotifyIcon);
                type.GetField("text", hidden).SetValue(tray, text);
                if ((bool)type.GetField("added", hidden).GetValue(tray))
                    type.GetMethod("UpdateIcon", hidden).Invoke(tray, new object[] { true });
            }
            catch { tray.Text = text.Length > 63 ? text.Substring(0, 63) : text; }
        }

        void UpdateSpeed()
        {
            long rx = 0, tx = 0;
            try
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                    var st = ni.GetIPStatistics();
                    rx += st.BytesReceived;
                    tx += st.BytesSent;
                }
            }
            catch { return; }

            var now = DateTime.UtcNow;
            if (rxPrev >= 0)
            {
                double sec = Math.Max(0.5, (now - speedAt).TotalSeconds);
                speedText = "↓ " + FormatSpeed(Math.Max(0, rx - rxPrev) / sec) + "\t↑ " + FormatSpeed(Math.Max(0, tx - txPrev) / sec)
                            + "\tпинг " + (pingMs >= 0 ? pingMs + " мс" : "—");
            }
            rxPrev = rx;
            txPrev = tx;
            speedAt = now;

            // Пинг - в фоне раз в 10 секунд, чтобы не подвешивать меню
            if (!pinging && (now - pingAt).TotalSeconds >= 10)
            {
                pinging = true;
                pingAt = now;
                Task.Factory.StartNew(() =>
                {
                    try
                    {
                        using (var p = new System.Net.NetworkInformation.Ping())
                        {
                            var r = p.Send("8.8.8.8", 2000);
                            return r.Status == System.Net.NetworkInformation.IPStatus.Success ? r.RoundtripTime : -1;
                        }
                    }
                    catch { return -1L; }
                }).ContinueWith(t => OnUi(() => { pingMs = t.Result; pinging = false; }));
            }
        }

        static string FormatSpeed(double bytesPerSec)
        {
            if (bytesPerSec >= 1024 * 1024) return (bytesPerSec / 1024 / 1024).ToString("0.0") + " МБ/с";
            return (bytesPerSec / 1024).ToString("0") + " КБ/с";
        }

        // Обычные уведомления можно выключить в настройках; ошибки, предупреждения и force - показываются всегда
        void Notify(string text, ToolTipIcon icon = ToolTipIcon.Info, bool force = false)
        {
            pendingClick = null;
            if (!force && icon == ToolTipIcon.Info && !notifyItem.Checked) return;
            tray.ShowBalloonTip(5000, AppInfo.Name, text, icon);
        }

        // Уведомление, по клику на которое что-то произойдёт
        void Offer(string text, Action onClick)
        {
            Notify(text, ToolTipIcon.Info, true);
            pendingClick = onClick;
        }

        void OnUi(Action a)
        {
            try { ui.BeginInvoke(a); } catch { }
        }

        // ---------- включение / выключение ----------

        void Toggle()
        {
            if (wantOn) TurnOff(); else TurnOn();
        }

        void TurnOn()
        {
            wantOn = true;
            busy = true;
            busyText = "Жду интернет...";
            UpdateState();
            Task.Factory.StartNew(() =>
            {
                // После входа в Windows сеть поднимается не сразу
                for (int i = 0; i < 24 && !Util.InternetUp(); i++) Thread.Sleep(5000);
            }).ContinueWith(t => OnUi(() =>
            {
                busy = false;
                if (wantOn) RunSelect("fast", false, false);
            }));
        }

        void TurnOff()
        {
            wantOn = false;
            CancelSelect();
            busy = false;
            Util.KillWinws();
            ResetHealth();
            UpdateState();
            // Выключили посреди проверки новых конфигов - считаем, что не проверены, и возвращаем старые
            var after = afterSelect;
            afterSelect = null;
            if (after != null) after(false);
        }

        // Подбор конфига в фоне (Selector). false - подбор не запустился
        bool RunSelect(string mode, bool visible, bool notifyResult)
        {
            if (busy) return false;
            if (Util.ServiceExists("zapret"))
            {
                Notify("Мешает служба zapret. Запусти Setup.exe и отметь 'Удалить службу zapret'.", ToolTipIcon.Warning);
                return false;
            }
            var vpn = Util.RunningVpn();
            ResetHealth();
            busy = true;
            busyText = mode == "best" ? "Ищу конфиг с минимальным пингом..." : "Подбираю конфиг...";
            UpdateState();

            int gen = ++selectGen;
            bool best = mode == "best";
            Logger.Write("подбор: старт, режим " + mode + (vpn != null ? ", запущен VPN " + vpn : ""));
            Task.Factory.StartNew(() =>
            {
                string error = null;
                bool ok = false;
                // Отменённый подбор может ещё доделывать замер - новый ждёт его, чтобы они не гасили winws друг другу
                lock (selectLock)
                {
                    if (gen != selectGen) return Tuple.Create(false, (string)null);
                    try { ok = Selector.Run(paths, best, () => gen != selectGen, text => OnUi(() => { if (gen == selectGen) { busyText = text; UpdateLabels(); } }), out error); }
                    catch (Exception ex) { ok = false; error = ex.Message; }
                }
                return Tuple.Create(ok, error);
            }, TaskCreationOptions.LongRunning).ContinueWith(t => OnUi(() => SelectDone(gen, t.Result.Item1, t.Result.Item2, notifyResult)));
            return true;
        }

        // Остановить идущий подбор: он сам заметит смену поколения и выйдет
        void CancelSelect()
        {
            selectGen++;
        }

        void SelectDone(int gen, bool ok, string error, bool notifyResult)
        {
            if (gen != selectGen) return;   // уже выключили или запустили новый подбор
            busy = false;
            ok = ok && Util.WinwsRunning();
            if (error != null) Log("подбор: " + error);
            UpdateState();
            var after = afterSelect;
            afterSelect = null;
            if (after != null)
            {
                after(ok);
                return;
            }
            if (ok)
            {
                selectFailShown = false;
                if (notifyResult) Notify("zapret работает: " + CurrentConfig());
            }
            else ReportSelectFailed(error, notifyResult);
        }

        // Единственное уведомление без нажатия: рабочего конфига нет. Сам подбор повторяется,
        // поэтому показываем один раз, пока снова не заработает. Нет интернета - это не наша проблема, молчим
        void ReportSelectFailed(string error, bool manual)
        {
            if (selectFailShown && !manual) return;
            Task.Factory.StartNew(() =>
            {
                if (!Util.InternetUp()) return 0;
                return SiteCheck.RunAll(MainSites, 8).Any(x => x.DnsProblem) && !DnsFix.IsApplied(paths) ? 2 : 1;
            }).ContinueWith(t => OnUi(() =>
            {
                if (busy || !wantOn) return;
                if (t.Result == 0)
                {
                    Logger.Write("подбор: нет интернета");
                    if (manual) Notify("Нет интернета, подобрать конфиг не получилось.", ToolTipIcon.Warning);
                    return;
                }
                selectFailShown = true;
                if (t.Result == 2)
                    Offer("Не могу найти рабочий конфиг: DNS Windows не находит адреса. Нажми, чтобы исправить DNS.", ApplyDnsFix);
                else
                    Notify("Не могу найти рабочий конфиг" + (error != null ? ": " + error : "") + ".\nПопробуй «Подключение» → «Что-то не работает».", ToolTipIcon.Warning);
            }));
        }

        // Перезапустить текущий конфиг (после правки списков), без подбора
        void RestartCurrent()
        {
            if (busy || !wantOn) return;
            var names = Util.ReadEntries(paths.BestConfig);
            var bat = names.Count > 0 ? Path.Combine(paths.Core, names[0]) : null;
            if (bat == null || !File.Exists(bat))
            {
                RunSelect("fast", false, false);
                return;
            }
            ResetHealth();
            Util.KillWinws();
            Selector.StartConfig(paths, bat);
        }

        // ---------- сторож ----------

        // Проснулись или сменилась сеть: проверяем скоро, а не через 10 минут.
        // Пауза нужна, чтобы сеть успела подняться; новое событие просто отодвигает проверку
        void CheckSoon()
        {
            healthAt = DateTime.MinValue;
            watchTimer.Stop();
            watchTimer.Interval = SoonIntervalMs;
            watchTimer.Start();
        }

        void WatchdogTick()
        {
            if (!wantOn || busy || checking || !watchdogItem.Checked)
            {
                watchTimer.Interval = WatchIntervalMs;
                return;
            }

            // С VPN проверка показывает его сеть, а не работу zapret: ждём, ничего не трогаем
            var vpn = Util.RunningVpn();
            if (vpn != null)
            {
                if (!watchVpnLogged) Logger.Write("сторож: запущен VPN " + vpn + ", проверка на паузе");
                watchVpnLogged = true;
                watchFailedOnce = false;
                watchTimer.Interval = WatchIntervalMs;
                return;
            }
            watchVpnLogged = false;

            bool running = Util.WinwsRunning();
            checking = true;
            Task.Factory.StartNew(() =>
            {
                if (!Util.InternetUp()) return 0;                 // нет интернета - не наша проблема
                if (!running) return 2;                           // winws упал
                var res = SiteCheck.RunAll(MainSites, 8);
                foreach (var x in res.Where(x => x.Status != SiteStatus.Ok)) Logger.Write("сторож: " + x.Url + ": " + x.Text);
                if (res.Any(x => x.Status == SiteStatus.NoConnect)) return 1;   // только это чинится другим конфигом
                return res.Any(x => x.DnsProblem) ? 3 : 0;
            }).ContinueWith(t => OnUi(() =>
            {
                checking = false;
                int r = t.Result;
                if (r == 0 || r == 3)
                {
                    watchFailedOnce = false;
                    watchTimer.Interval = WatchIntervalMs;
                    if (r == 3) OfferDnsFix();
                }
                else if (r == 2 || watchFailedOnce)
                {
                    watchFailedOnce = false;
                    watchTimer.Interval = WatchIntervalMs;
                    if (!wantOn || busy) return;
                    Logger.Write(r == 2 ? "сторож: zapret остановился, запускаю заново" : "сторож: Discord или YouTube не открываются, подбираю другой конфиг");
                    RunSelect("fast", false, false);
                }
                else
                {
                    // Один сбой может быть случайным - перепроверяем через минуту
                    watchFailedOnce = true;
                    watchTimer.Interval = RecheckIntervalMs;
                }
            }));
        }

        // Включённые встроенные игры: только у них есть адреса, которые можно проверить
        List<Tuple<string, string[]>> EnabledPresets()
        {
            var mine = new HashSet<string>(Util.ReadEntries(paths.ListGeneralUser), StringComparer.OrdinalIgnoreCase);
            return GamePresets.Where(p => p.Item2.All(mine.Contains)).ToList();
        }

        // Сайты, которые пользователь добавил сам: без встроенных игр и заглушки
        List<string> UserSites()
        {
            var preset = new HashSet<string>(GamePresets.SelectMany(p => p.Item2), StringComparer.OrdinalIgnoreCase);
            return Util.ReadEntries(paths.ListGeneralUser)
                .Where(s => !preset.Contains(s) && !s.EndsWith(".example.abc", StringComparison.OrdinalIgnoreCase))
                .Take(MaxCheckedSites)
                .Select(s => "https://" + s)
                .ToList();
        }

        // ---------- свои сайты / IP ----------

        // Кладёт сайт/IP в нужный файл и убирает из противоположного. Возвращает note (предупреждение по ipset), changed - было ли изменение.
        string ApplyEntry(string value, bool isIp, bool exclude, out bool changed)
        {
            string note = "";
            if (exclude)
            {
                changed = Util.AppendUnique(isIp ? paths.IpsetExcludeUser : paths.ListExcludeUser, value);
                // Если раньше сами добавили - убираем из списка обхода
                if (isIp) { changed |= Util.RemoveEntry(paths.IpsetUser, value); note = ReapplyIpset(); }
                else changed |= Util.RemoveEntry(paths.ListGeneralUser, value);
            }
            else if (isIp)
            {
                changed = Util.AppendUnique(paths.IpsetUser, value);
                changed |= Util.RemoveEntry(paths.IpsetExcludeUser, value);
                note = ReapplyIpset();
            }
            else
            {
                changed = Util.AppendUnique(paths.ListGeneralUser, value);
                changed |= Util.RemoveEntry(paths.ListExcludeUser, value);
            }
            if (changed || note.Length > 0) RestartCurrent();
            return note;
        }

        // Игры, которые работают из коробки: название и домены для обхода (лежат в list-general-user.txt)
        static readonly Tuple<string, string[]>[] GamePresets =
        {
            Tuple.Create("ARC Raiders", new[] { "embark.games", "arcraiders.com" }),
            Tuple.Create("Wardogs", new[] { "live.wardogs.bulkhead.pragmaengine.com", "firstlook.gg", "api.epicgames.dev" }),
        };

        static readonly HashSet<string> PresetDomains =
            new HashSet<string>(GamePresets.SelectMany(p => p.Item2), StringComparer.OrdinalIgnoreCase);

        // Выключенный домен остаётся в файле как "#off домен": zapret его не видит, а установщик не допишет его заново
        void TogglePreset(Tuple<string, string[]> preset)
        {
            var file = paths.ListGeneralUser;
            bool on = preset.Item2.All(d => Util.ReadEntries(file).Contains(d, StringComparer.OrdinalIgnoreCase));
            var lines = File.Exists(file) ? File.ReadAllLines(file).ToList() : new List<string>();
            foreach (var d in preset.Item2)
            {
                var plain = lines.FindIndex(l => string.Equals(l.Trim(), d, StringComparison.OrdinalIgnoreCase));
                var off = lines.FindIndex(l => string.Equals(l.Trim(), Util.OffPrefix + d, StringComparison.OrdinalIgnoreCase));
                if (on && plain >= 0) lines[plain] = Util.OffPrefix + d;
                else if (!on && off >= 0) lines[off] = d;
                else if (!on && plain < 0) lines.Add(d);
            }
            Util.WriteLinesAtomic(file, lines);
            RestartCurrent();
        }

        // Мои сайты: (значение, файл, исключён ли). Заглушки из шаблонов zapret и домены встроенных игр не показываем
        List<Tuple<string, string, bool>> MySites()
        {
            var result = new List<Tuple<string, string, bool>>();
            foreach (var src in new[] {
                Tuple.Create(paths.ListGeneralUser, false), Tuple.Create(paths.IpsetUser, false),
                Tuple.Create(paths.ListExcludeUser, true), Tuple.Create(paths.IpsetExcludeUser, true) })
            {
                foreach (var e in Util.ReadEntries(src.Item1))
                {
                    if (e.EndsWith(".example.abc") || e == "domain.example.abc" || e.StartsWith("203.0.113.") || PresetDomains.Contains(e)) continue;
                    result.Add(Tuple.Create(e, src.Item1, src.Item2));
                }
            }
            // По алфавиту: включённые и выключенные лежат в разных файлах, без сортировки сайт при переключении прыгает
            return result.OrderBy(e => e.Item1, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // Убрать сайт/IP из списка (вызывается из окна "Мои сайты")
        void RemoveSite(string value, string file)
        {
            Util.RemoveEntry(file, value);
            if (file == paths.IpsetUser || file == paths.IpsetExcludeUser) ReapplyIpset();
            RestartCurrent();
        }

        bool IsIpFile(string file)
        {
            return string.Equals(file, paths.IpsetUser, StringComparison.OrdinalIgnoreCase)
                || string.Equals(file, paths.IpsetExcludeUser, StringComparison.OrdinalIgnoreCase);
        }

        // ---------- окно "Мои сайты": добавить, исключить, переключить режим, убрать - всё в одном месте ----------
        Form sitesForm;
        void OpenSites()
        {
            if (sitesForm != null) { sitesForm.Activate(); return; }
            var pal = Theme.Current;

            var f = new Form();
            sitesForm = f;
            f.Text = "Мои сайты";
            f.Icon = Theme.AppIcon();
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.StartPosition = FormStartPosition.CenterScreen;
            f.MinimizeBox = f.MaximizeBox = false;
            f.ShowInTaskbar = false;
            f.AutoScaleMode = AutoScaleMode.Font;
            f.Font = new Font("Segoe UI", 9.75f);
            f.ClientSize = new Size(560, 470);
            f.BackColor = pal.Back;
            f.ForeColor = pal.Text;
            Theme.UseDarkTitleBar(f, pal == Palette.Dark);
            f.FormClosed += (s, e) => sitesForm = null;

            var addLabel = new Label { Text = "Добавить сайт или IP:", Left = 20, Top = 18, Width = 520, Height = 20, ForeColor = pal.SubText };
            var addBox = new TextBox
            {
                Left = 20, Top = 42, Width = 390, Height = 26,
                BorderStyle = BorderStyle.FixedSingle, BackColor = pal.Hover, ForeColor = pal.Text
            };
            var addBtn = MakeButton("Добавить", pal, true);
            addBtn.Left = 420; addBtn.Top = 41; addBtn.Width = 120; addBtn.Height = 28;

            var hint = new Label
            {
                Left = 20, Top = 78, Width = 520, Height = 20, ForeColor = pal.SubText,
                Text = "«Обход» — сайт не открывался.   «Исключить» — zapret его ломал."
            };

            var list = new FlowLayoutPanel
            {
                Left = 20, Top = 106, Width = 520, Height = 310,
                FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
                BackColor = pal.Back, BorderStyle = BorderStyle.None
            };

            var closeBtn = MakeButton("Закрыть", pal, false);
            closeBtn.Left = 440; closeBtn.Top = 428; closeBtn.Width = 100; closeBtn.Height = 30;
            closeBtn.Click += (s, e) => f.Close();

            Action rebuild = null;
            rebuild = delegate
            {
                list.SuspendLayout();
                list.Controls.Clear();
                var entries = MySites();
                if (entries.Count == 0)
                {
                    list.Controls.Add(new Label
                    {
                        Text = "Пока пусто. Впиши сайт сверху и нажми «Добавить».",
                        ForeColor = pal.Disabled, Width = 480, Height = 40, Margin = new Padding(4, 12, 4, 0)
                    });
                }
                else
                {
                    foreach (var en in entries) list.Controls.Add(MakeSiteRow(en, pal, rebuild, list.ClientSize.Width));
                }
                list.ResumeLayout();
            };

            Action add = delegate
            {
                string value; bool isIp;
                if (!Util.ParseEntry(addBox.Text, out value, out isIp))
                {
                    MessageBox.Show(f, "Не понял, что это. Впиши домен (youtube.com) или IP (1.2.3.4).", "Мои сайты", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                bool changed;
                ApplyEntry(value, isIp, false, out changed);   // по умолчанию - на обход, режим потом переключается в строке
                addBox.Clear();
                addBox.Focus();
                rebuild();
            };
            addBtn.Click += (s, e) => add();
            addBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; add(); } };

            f.Controls.AddRange(new Control[] { addLabel, addBox, addBtn, hint, list, closeBtn });
            f.AcceptButton = addBtn;
            f.CancelButton = closeBtn;
            rebuild();
            f.Show();
            f.Activate();
        }

        // Одна строка списка: название + переключатель Обход/Исключить + крестик
        Control MakeSiteRow(Tuple<string, string, bool> entry, Palette pal, Action refresh, int width)
        {
            var value = entry.Item1;
            var exclude = entry.Item3;
            var isIp = IsIpFile(entry.Item2);

            var row = new Panel { Width = width - 6, Height = 44, Margin = new Padding(0, 0, 0, 6), BackColor = pal.Hover };

            var name = new Label
            {
                Text = value, Left = 12, Top = 0, Width = row.Width - 300, Height = row.Height,
                TextAlign = ContentAlignment.MiddleLeft, ForeColor = pal.Text, AutoEllipsis = true
            };

            var bypass = MakeSegment("Обход", pal, !exclude);
            var excl = MakeSegment("Исключить", pal, exclude);
            bypass.Left = row.Width - 232; bypass.Top = 7;
            excl.Left = bypass.Right; excl.Top = 7;

            var del = new Button
            {
                Text = "✕", Left = row.Width - 38, Top = 7, Width = 30, Height = 30,
                FlatStyle = FlatStyle.Flat, BackColor = pal.Hover, ForeColor = pal.SubText, TabStop = false
            };
            del.FlatAppearance.BorderSize = 0;
            del.FlatAppearance.MouseOverBackColor = Color.FromArgb(200, 70, 70);

            bypass.Click += (s, e) => { if (exclude) { bool c; ApplyEntry(value, isIp, false, out c); refresh(); } };
            excl.Click += (s, e) => { if (!exclude) { bool c; ApplyEntry(value, isIp, true, out c); refresh(); } };
            del.Click += (s, e) => { RemoveSite(value, entry.Item2); refresh(); };

            row.Controls.AddRange(new Control[] { name, bypass, excl, del });
            return row;
        }

        static Button MakeSegment(string text, Palette pal, bool active)
        {
            var b = new Button
            {
                Text = text, Width = 97, Height = 30, FlatStyle = FlatStyle.Flat, TabStop = false,
                BackColor = active ? pal.Check : pal.Back,
                ForeColor = active ? Color.White : pal.SubText
            };
            b.FlatAppearance.BorderSize = active ? 0 : 1;
            b.FlatAppearance.BorderColor = pal.Separator;
            b.FlatAppearance.MouseOverBackColor = active ? pal.Check : pal.Separator;
            return b;
        }

        static Button MakeButton(string text, Palette pal, bool accent)
        {
            var b = new Button
            {
                Text = text, FlatStyle = FlatStyle.Flat,
                BackColor = accent ? pal.Check : pal.Hover,
                ForeColor = accent ? Color.White : pal.Text
            };
            b.FlatAppearance.BorderSize = accent ? 0 : 1;
            b.FlatAppearance.BorderColor = pal.Separator;
            return b;
        }

        // Подсказки живут в окне с разделами. Сюда попадаем только из запасного меню
        void OpenHints()
        {
            try { Process.Start(Path.Combine(paths.Root, "README.txt")); } catch (Exception ex) { Logger.Write("подсказки: " + ex.Message); }
        }

        // Перенос настроек: сайты, исключения, игры и галочки одним файлом
        void SaveSettings()
        {
            using (var d = new SaveFileDialog
            {
                Title = "Сохранить настройки", FileName = "zapret-settings.txt", Filter = "Настройки Zapret Tray (*.txt)|*.txt",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
            })
            {
                if (d.ShowDialog() != DialogResult.OK) return;
                var values = new Dictionary<string, string>
                {
                    { SettingsFile.Theme, Theme.Mode },
                    { SettingsFile.Watchdog, watchdogItem.Checked ? "1" : "0" },
                    { SettingsFile.Autostart, Tasks.IsEnabled(AppInfo.AutoTask) ? "1" : "0" },
                    { SettingsFile.Autoupdate, autoupdateItem.Checked ? "1" : "0" },
                    { SettingsFile.Notify, notifyItem.Checked ? "1" : "0" }
                };
                try
                {
                    SettingsFile.Save(d.FileName, paths, games.Games.ToList(), values);
                    Notify("Настройки сохранены: " + Path.GetFileName(d.FileName), ToolTipIcon.Info, true);
                }
                catch (Exception ex) { Notify("Не удалось сохранить настройки: " + ex.Message, ToolTipIcon.Warning); }
            }
        }

        void LoadSettings()
        {
            using (var d = new OpenFileDialog
            {
                Title = "Подгрузить настройки", Filter = "Настройки Zapret Tray (*.txt)|*.txt",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
            })
            {
                if (d.ShowDialog() != DialogResult.OK) return;
                try
                {
                    var imported = SettingsFile.Read(d.FileName, paths);
                    if (MessageBox.Show("Заменить твои сайты, исключения, игры и галочки настройками из файла?", AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                    SettingsFile.ApplyLists(imported, paths);
                    games.Replace(imported.Games);
                    ApplyValues(imported.Values);
                    RefreshWatchedGames();
                    var note = ReapplyIpset();
                    SyncGameFilter();
                    RestartCurrent();
                    Logger.Write("настройки подгружены из файла");
                    Notify(note.Length > 0 ? note : "Настройки подгружены.", note.Length > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info, true);
                }
                catch (Exception ex) { Notify("Не удалось подгрузить настройки: " + ex.Message, ToolTipIcon.Warning); }
            }
        }

        void ApplyValues(Dictionary<string, string> values)
        {
            string v;
            if (values.TryGetValue(SettingsFile.Theme, out v) && (v == Theme.System || v == Theme.Light || v == Theme.Dark)) SetTheme(v);
            if (values.TryGetValue(SettingsFile.Watchdog, out v)) watchdogItem.Checked = v == "1";
            if (values.TryGetValue(SettingsFile.Autoupdate, out v)) autoupdateItem.Checked = v == "1";
            if (values.TryGetValue(SettingsFile.Notify, out v)) notifyItem.Checked = v == "1";
            if (values.TryGetValue(SettingsFile.Autostart, out v)) SetAutostart(v == "1");
        }

        // Само предлагает один раз за запуск: настройки сети меняем только по клику
        void OfferDnsFix()
        {
            if (dnsOffered || DnsFix.IsApplied(paths)) return;
            dnsOffered = true;
            Offer("DNS Windows не находит YouTube, zapret тут ни при чём. Нажми, чтобы исправить.", ApplyDnsFix);
        }

        void ApplyDnsFix() { RunDnsAction(DnsFix.Apply); }

        void UndoDnsFix() { RunDnsAction(DnsFix.Restore); }

        delegate bool DnsAction(AppPaths paths, out string message);

        void RunDnsAction(DnsAction action)
        {
            if (dnsBusy) return;
            dnsBusy = true;
            Task.Factory.StartNew(() =>
            {
                string message;
                bool ok;
                try { ok = action(paths, out message); }
                catch (Exception ex) { ok = false; message = ex.Message; }
                return Tuple.Create(ok, message);
            }).ContinueWith(t => OnUi(() =>
            {
                dnsBusy = false;
                Notify(t.Result.Item2, t.Result.Item1 ? ToolTipIcon.Info : ToolTipIcon.Warning, true);
            }));
        }

        // Отчёт для пересылки: сразу в буфер обмена, копия - файлом на рабочем столе
        void MakeReport()
        {
            reportItem.Enabled = false;
            Notify("Проверяю всё и собираю отчёт, это до минуты...", ToolTipIcon.Info, true);
            var status = StatusText();
            var sites = UserSites();
            Task.Factory.StartNew(() => ProblemReport.Build(paths, status, sites)).ContinueWith(t => OnUi(() =>
            {
                reportItem.Enabled = true;
                if (t.IsFaulted)
                {
                    Logger.Write("отчёт: " + t.Exception.InnerException.Message);
                    Notify("Не удалось собрать отчёт: " + t.Exception.InnerException.Message, ToolTipIcon.Warning);
                    return;
                }
                try
                {
                    Clipboard.SetText(t.Result);
                    Notify("Отчёт скопирован. Вставь его в чат: Ctrl+V.", ToolTipIcon.Info, true);
                }
                catch (Exception ex)
                {
                    // Буфер бывает занят другой программой
                    Logger.Write("отчёт: буфер обмена: " + ex.Message);
                    Notify("Не удалось скопировать. Отчёт на рабочем столе: zapret-report.txt.", ToolTipIcon.Warning);
                }
            }));
        }

        static readonly Regex ConfigName = new Regex(@"^general.*\.bat$", RegexOptions.IgnoreCase);

        IEnumerable<string> ConfigFiles()
        {
            // Сортировка как у людей: ALT2 перед ALT10
            return Directory.GetFiles(paths.Core, "general*.bat")
                .Where(f => ConfigName.IsMatch(Path.GetFileName(f)))
                .OrderBy(f => Regex.Replace(Path.GetFileName(f), @"\d+", m => m.Value.PadLeft(8, '0')));
        }

        // Выбрать конфиг вручную: он становится первым в запомненных и сразу запускается
        void UseConfig(string file)
        {
            if (busy) return;
            var saved = Util.ReadEntries(paths.BestConfig).Where(n => !string.Equals(n, file, StringComparison.OrdinalIgnoreCase));
            Util.WriteLinesAtomic(paths.BestConfig, new[] { file }.Concat(saved).Take(3));
            wantOn = true;
            RestartCurrent();
        }

        // Пересобрать ipset-all.txt из своих IP и включённых игр. Возвращает предупреждение или ""
        string ReapplyIpset()
        {
            var wanted = Util.ReadEntries(paths.IpsetUser).Concat(games.EnabledSubnets());
            string message;
            try { Ipset.Rebuild(paths, wanted, out message); }
            catch (Exception ex) { message = "Не удалось обновить список IP: " + ex.Message; }
            return message;
        }

        // ---------- игры ----------

        // Игровой фильтр обрабатывает все высокие порты для всего списка IP и может давать лаги.
        // Включён, только пока в «Моих играх» есть включённая игра. true - состояние изменилось, конфиг надо перезапустить
        bool SyncGameFilter()
        {
            var file = Path.Combine(paths.Utils, "game_filter.enabled");
            bool want = games.EnabledExes().Count > 0;
            if (want == File.Exists(file)) return false;
            try
            {
                if (want) File.WriteAllText(file, "all\r\n");
                else File.Delete(file);
                Logger.Write("игровой фильтр: " + (want ? "включён" : "выключен"));
                return true;
            }
            catch (Exception ex)
            {
                Logger.Write("игровой фильтр: " + ex.Message);
                return false;
            }
        }

        void ApplyGameFilter()
        {
            if (SyncGameFilter()) RestartCurrent();
        }

        void AddGame()
        {
            var exe = ProcessPicker.Show();
            if (exe == null) return;
            var g = games.Add(exe);
            RefreshWatchedGames();
            ApplyGameFilter();
            if (flows.Error != null)
            {
                Notify("Игра добавлена, но слежение не запустилось: " + flows.Error, ToolTipIcon.Warning);
                return;
            }
            SnapshotTcp();
            var vpn = Util.RunningVpn();
            if (vpn != null)
            {
                Notify("Игра " + exe + " добавлена, но запущен VPN (" + vpn + "): пока он включён, настоящие сервера игры не видны. Выключи его, пока играешь.", ToolTipIcon.Warning);
                return;
            }
            Notify("Игра " + exe + " добавлена. Пока она запущена, её сервера будут добавляться сами.\nМожно просто играть.", ToolTipIcon.Info, true);
        }

        void ToggleGame(Game g)
        {
            games.SetEnabled(g, !g.Enabled);
            RefreshWatchedGames();
            ApplyGameFilter();
            var note = ReapplyIpset();
            if (note.Length > 0) Notify(note, ToolTipIcon.Warning);
        }

        void RemoveGame(Game g)
        {
            if (MessageBox.Show("Удалить " + g.Exe + " и все собранные адреса?", AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            games.Remove(g);
            RefreshWatchedGames();
            ReapplyIpset();
            ApplyGameFilter();
        }

        void RefreshWatchedGames()
        {
            watchedExes = games.EnabledExes();
            if (watchedExes.Count > 0) flows.Start(paths.Bin);
        }

        string ProcessName(int pid)
        {
            lock (pidLock)
            {
                string name;
                if (pidNames.TryGetValue(pid, out name)) return name;
                try { name = Process.GetProcessById(pid).ProcessName; } catch { name = ""; }
                if (pidNames.Count > 5000) pidNames.Clear();
                pidNames[pid] = name;
                return name;
            }
        }

        // Поток WinDivert: новое соединение какого-то процесса
        void OnFlow(int pid, byte[] ip, int port)
        {
            var exes = watchedExes;
            if (exes.Count == 0 || !Util.IsGameServer(ip, port)) return;
            var name = ProcessName(pid);
            if (name.Length == 0 || !exes.Contains(name)) return;
            var g = games.Find(name);
            if (g != null && games.AddSubnet(g, Util.Subnet24(ip)))
            {
                Logger.Write("игра " + g.Exe + ": новый адрес, порт " + port);
                lock (gamesLearnedThisSession) gamesLearnedThisSession.Add(g.Exe);
                gamesDirty = true;
            }
        }

        void SnapshotTcp()
        {
            foreach (var g in games.Games.Where(x => x.Enabled).ToList())
            {
                var pids = new HashSet<int>(Process.GetProcessesByName(g.Exe).Select(p => p.Id));
                if (pids.Count == 0) continue;
                foreach (var r in TcpTable.RemoteAddresses(pids))
                {
                    if (Util.IsGameServer(r.Item1, r.Item2) && games.AddSubnet(g, Util.Subnet24(r.Item1))) gamesDirty = true;
                }
            }
        }

        void GamesTick()
        {
            if (gamesDirty)
            {
                gamesDirty = false;
                foreach (var g in games.Games.ToList()) games.SaveSubnets(g);
                // winws сам перечитывает изменившиеся списки - перезапуск посреди матча не нужен
                ReapplyIpset();
            }

            // Игра закрылась после того, как мы что-то добавили - перезапуск для надёжности, раз матча уже нет
            foreach (var g in games.Games.Where(x => x.Enabled).ToList())
            {
                bool running = Process.GetProcessesByName(g.Exe).Length > 0;
                if (running && !gamesRunning.Contains(g.Exe))
                {
                    gamesRunning.Add(g.Exe);
                    SnapshotTcp();
                }
                else if (!running && gamesRunning.Remove(g.Exe))
                {
                    bool learned;
                    lock (gamesLearnedThisSession) learned = gamesLearnedThisSession.Remove(g.Exe);
                    if (learned)
                    {
                        RestartCurrent();
                    }
                }
            }
        }

        // ---------- прочее ----------

        void SetAutostart(bool enabled)
        {
            bool ok = Tasks.Exists(AppInfo.AutoTask)
                ? Tasks.SetEnabled(AppInfo.AutoTask, enabled)
                : enabled && CreateAutoTask();
            if (!ok && enabled) Notify("Не удалось включить автозапуск.", ToolTipIcon.Error);
        }

        bool CreateAutoTask()
        {
            string err;
            return Tasks.Create(AppInfo.AutoTask, paths.TrayExePath, true, out err);
        }


        void Uninstall()
        {
            if (!File.Exists(paths.SetupExePath))
            {
                MessageBox.Show("Не найден " + AppInfo.SetupExe + " в папке программы.", AppInfo.Name);
                return;
            }
            Process.Start(new ProcessStartInfo(paths.SetupExePath, "/uninstall") { UseShellExecute = true });
        }

        // ---------- обновления ----------

        // manual - нажали в меню: проверяем сразу и говорим результат; иначе - тихо и не чаще раза в сутки
        void CheckUpdate(bool manual)
        {
            if (updating) return;
            if (!manual)
            {
                long last;
                if (long.TryParse(settings.Get("update_check"), out last) && DateTime.UtcNow.Ticks - last < TimeSpan.FromHours(20).Ticks) return;
                if (busy) return;   // не мешаем подбору конфига, попробуем через час
            }
            updating = true;
            var localZapret = ZapretUpdater.LocalVersion(paths);
            Task.Factory.StartNew(() => Tuple.Create(Updater.GetLatest(), Updater.GetLatest(ZapretUpdater.Repo)))
            .ContinueWith(t => OnUi(() =>
            {
                updating = false;
                var app = t.Result.Item1;
                var core = t.Result.Item2;
                if (app != null || core != null) settings.Set("update_check", DateTime.UtcNow.Ticks.ToString());
                bool auto = autoupdateItem.Checked || manual;

                // Сначала сама программа: в новой версии могут прийти и новые конфиги
                if (app != null && Updater.IsNewer(app.Version, AppInfo.Version))
                {
                    if (auto) InstallAppUpdate(app, manual);
                    else Offer("Есть новая версия программы " + app.Version + ". Нажми, чтобы обновить.", () => InstallAppUpdate(app, true));
                    return;
                }
                if (core != null && Updater.IsNewer(core.Version, localZapret) && (manual || core.Version != settings.Get("zapret_skip")))
                {
                    if (auto) InstallZapretUpdate(core, manual);
                    else Offer("Вышли новые конфиги zapret " + core.Version + ". Нажми, чтобы обновить.", () => InstallZapretUpdate(core, true));
                    return;
                }
                if (manual)
                {
                    Notify(app == null && core == null ? "Не получилось проверить обновления - нет связи с GitHub."
                        : "Всё свежее: программа " + AppInfo.Version + ", zapret " + localZapret + ".", ToolTipIcon.Info, true);
                }
            }));
        }

        void InstallAppUpdate(Updater.Release rel, bool manual)
        {
            if (updating) return;
            updating = true;
            Task.Factory.StartNew(() => Updater.Download(rel)).ContinueWith(t => OnUi(() =>
            {
                updating = false;
                if (t.IsFaulted)
                {
                    Log("обновление " + rel.Version + ": " + t.Exception.InnerException.Message);
                    if (manual) Notify("Не удалось скачать обновление: " + t.Exception.InnerException.Message, ToolTipIcon.Warning);
                    return;
                }
                if (manual) Notify("Ставлю обновление " + rel.Version + "... zapret перезапустится сам.", ToolTipIcon.Info, true);
                try { Updater.RunSetup(t.Result, paths.Root); }
                catch (Exception ex) { Notify("Не удалось запустить обновление: " + ex.Message, ToolTipIcon.Error); }
            }));
        }

        // Новые конфиги zapret: скачать и поправить -> заменить -> подобрать конфиг. Не заработало - вернуть старые
        void InstallZapretUpdate(Updater.Release rel, bool manual)
        {
            if (updating) return;
            if (busy || (!wantOn && !manual))
            {
                if (manual) Notify("Сейчас идёт подбор конфига - попробуй чуть позже.", ToolTipIcon.Info, true);
                return;   // автоматически попробуем в следующий раз
            }
            updating = true;
            Task.Factory.StartNew(() => ZapretUpdater.Prepare(rel, paths)).ContinueWith(t => OnUi(() =>
            {
                updating = false;
                if (t.IsFaulted)
                {
                    var err = t.Exception.InnerException.Message;
                    Log("zapret " + rel.Version + ": " + err);
                    if (manual) Notify("Не удалось подготовить новые конфиги zapret: " + err, ToolTipIcon.Warning);
                    return;
                }
                if (busy) return;

                // Останавливаем всё, что держит файлы
                CancelSelect();
                Util.KillWinws();
                flows.Stop();
                try
                {
                    ZapretUpdater.Apply(paths, t.Result);
                }
                catch (Exception ex)
                {
                    Log("zapret " + rel.Version + ": замена не удалась: " + ex);
                    try { ZapretUpdater.Restore(paths); } catch { }
                    RefreshWatchedGames();
                    if (manual) Notify("Не удалось обновить конфиги zapret: " + ex.Message, ToolTipIcon.Warning);
                    if (wantOn) RunSelect("fast", false, false);
                    return;
                }
                RefreshWatchedGames();
                ReapplyIpset();

                wantOn = true;
                afterSelect = ok =>
                {
                    if (ok)
                    {
                        ZapretUpdater.DropBackup(paths);
                        settings.Set("zapret_skip", "");
                        Log("zapret обновлён до " + rel.Version);
                        return;
                    }
                    try { ZapretUpdater.Restore(paths); } catch (Exception ex) { Log("откат не удался: " + ex); }
                    if (wantOn)
                    {
                        settings.Set("zapret_skip", rel.Version);   // эту версию больше сами не пробуем
                        Log("zapret " + rel.Version + ": новые конфиги не заработали, вернул старые");
                        RunSelect("fast", false, false);
                    }
                };
                if (RunSelect("fast", false, false))
                {
                    busyText = "Проверяю новые конфиги zapret...";
                    UpdateLabels();
                }
                else
                {
                    var after = afterSelect;
                    afterSelect = null;
                    if (after != null) after(false);
                }
            }));
        }

        void Log(string text)
        {
            Logger.Write(text);
        }

        void ExitApp()
        {
            if (panel != null) panel.Close();
            TurnOff();
            flows.Stop();
            tray.Visible = false;
            tray.Dispose();
            ExitThread();
        }
    }

    // Простое окно ввода одной строки
    static class InputDialog
    {
        public static string Show(string title, string hint)
        {
            using (var f = new Form())
            {
                f.Text = title;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterScreen;
                f.MinimizeBox = f.MaximizeBox = false;
                f.TopMost = true;
                f.AutoScaleMode = AutoScaleMode.Font;
                f.Font = new Font("Segoe UI", 9);
                f.ClientSize = new Size(420, 130);

                var label = new Label { Text = hint, Left = 12, Top = 10, Width = 396, Height = 40 };
                var box = new TextBox { Left = 12, Top = 56, Width = 396 };
                var ok = new Button { Text = "OK", Left = 232, Top = 92, Width = 84, DialogResult = DialogResult.OK };
                var cancel = new Button { Text = "Отмена", Left = 324, Top = 92, Width = 84, DialogResult = DialogResult.Cancel };
                f.Controls.AddRange(new Control[] { label, box, ok, cancel });
                f.AcceptButton = ok;
                f.CancelButton = cancel;
                return f.ShowDialog() == DialogResult.OK && box.Text.Trim().Length > 0 ? box.Text : null;
            }
        }
    }

    // Выбор запущенной игры из списка программ с окнами
    static class ProcessPicker
    {
        public static string Show()
        {
            var items = Process.GetProcesses()
                .Where(p => { try { return p.MainWindowHandle != IntPtr.Zero && p.Id != Process.GetCurrentProcess().Id; } catch { return false; } })
                .Select(p => new { Name = p.ProcessName, Title = SafeTitle(p) })
                .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(x => x.Name)
                .ToList();

            using (var f = new Form())
            {
                f.Text = "Добавить игру";
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterScreen;
                f.MinimizeBox = f.MaximizeBox = false;
                f.TopMost = true;
                f.Font = new Font("Segoe UI", 9);
                f.ClientSize = new Size(460, 420);

                var label = new Label
                {
                    Text = "Запусти игру и выбери её в списке. Дальше её сервера будут добавляться сами, пока она запущена.",
                    Left = 12, Top = 10, Width = 436, Height = 36
                };
                var list = new ListBox { Left = 12, Top = 50, Width = 436, Height = 316, IntegralHeight = false };
                foreach (var x in items) list.Items.Add(x.Name + (x.Title.Length > 0 ? "   —   " + x.Title : ""));
                var refresh = new Button { Text = "Обновить", Left = 12, Top = 380, Width = 90 };
                var ok = new Button { Text = "Добавить", Left = 266, Top = 380, Width = 88, DialogResult = DialogResult.OK };
                var cancel = new Button { Text = "Отмена", Left = 360, Top = 380, Width = 88, DialogResult = DialogResult.Cancel };
                refresh.Click += (s, e) => { f.DialogResult = DialogResult.Retry; f.Close(); };
                list.DoubleClick += (s, e) => { if (list.SelectedIndex >= 0) { f.DialogResult = DialogResult.OK; f.Close(); } };
                f.Controls.AddRange(new Control[] { label, list, refresh, ok, cancel });
                f.AcceptButton = ok;
                f.CancelButton = cancel;

                var result = f.ShowDialog();
                if (result == DialogResult.Retry) return Show();
                if (result != DialogResult.OK || list.SelectedIndex < 0) return null;
                return items[list.SelectedIndex].Name;
            }
        }

        static string SafeTitle(Process p)
        {
            try { return p.MainWindowTitle ?? ""; } catch { return ""; }
        }
    }
}
