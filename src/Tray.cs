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

        ToolStripMenuItem statusItem, speedItem, uptimeItem, toggleItem, reselectItem, fastItem, bestItem, manualItem;
        ToolStripMenuItem sitesItem, checkNowItem, settingsItem, themeItem, autoupdateItem, notifyItem, updateItem;
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
        ToolStripMenuItem gamesItem, watchdogItem, autostartItem;

        bool wantOn;            // пользователь хочет, чтобы zapret работал
        bool busy;              // идёт подбор
        string busyText = "";
        volatile int selectGen;         // номер текущего подбора; сменился - подбор отменён
        readonly object selectLock = new object();
        bool watchFailedOnce;
        bool checking;

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
                if (lastVersion != null) Notify("Обновлено до версии " + AppInfo.Version + ". Все настройки на месте.");
                settings.Set("version", AppInfo.Version);
            }

            flows.FlowSeen += OnFlow;
            RefreshWatchedGames();

            if (Util.ServiceExists("zapret"))
            {
                Notify("Установлена служба zapret - она мешает. Запусти Setup.exe и отметь 'Удалить службу zapret'.", ToolTipIcon.Warning);
            }

            ReapplyIpset();
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
            speedItem = Info("", ItemKind.Info);
            uptimeItem = Info("", ItemKind.Info);

            gamesItem = Item("Мои игры", null);

            reselectItem = Item("Подобрать заново", null);
            fastItem = Item("Быстро", (s, e) => { wantOn = true; RunSelect("fast", false, true); });
            bestItem = Item("С минимальным пингом", (s, e) => { wantOn = true; RunSelect("best", false, true); });
            manualItem = Item("Выбрать конфиг вручную", null);
            reselectItem.DropDownItems.AddRange(new ToolStripItem[] { fastItem, bestItem, MenuKit.Sep(), manualItem });

            sitesItem = Item("Мои сайты", null);
            checkNowItem = Item("Проверить сейчас", (s, e) => CheckNow());

            // Настройки
            settingsItem = Item("Настройки", null);
            themeItem = Item("Тема", null);
            foreach (var t in new[] { Tuple.Create(Theme.System, "Как в Windows"), Tuple.Create(Theme.Light, "Светлая"), Tuple.Create(Theme.Dark, "Тёмная") })
            {
                var mode = t.Item1;
                themeItem.DropDownItems.Add(Item(t.Item2, (s, e) => SetTheme(mode)));
                themeItem.DropDownItems[themeItem.DropDownItems.Count - 1].Tag = mode;
            }
            watchdogItem = Check("Сторож (сам чинит, если сломалось)", "watchdog");
            autostartItem = Item("Запускать вместе с Windows", null);
            autostartItem.CheckOnClick = true;
            autostartItem.Click += (s, e) => SetAutostart(autostartItem.Checked);
            autoupdateItem = Check("Обновляться автоматически", "autoupdate");
            notifyItem = Check("Уведомления (ошибки показываются всегда)", "notify");
            updateItem = Item("Проверить обновления", (s, e) => CheckUpdate(true));
            settingsItem.DropDownItems.AddRange(new ToolStripItem[] {
                themeItem, MenuKit.Sep(),
                watchdogItem, autostartItem, autoupdateItem, notifyItem, MenuKit.Sep(),
                updateItem,
                Item("Открыть папку", (s, e) => Process.Start("explorer.exe", "\"" + paths.Root + "\"")),
                Item("Удалить программу...", (s, e) => Uninstall())
            });

            toggleItem = Item("Выключить", (s, e) => Toggle());

            menu.Items.AddRange(new ToolStripItem[] {
                statusItem, speedItem, uptimeItem, MenuKit.Sep(),
                gamesItem, reselectItem,
                Item("Добавить сайт / IP...", (s, e) => AddEntry(false)),
                Item("Исключить сайт / IP...", (s, e) => AddEntry(true)),
                sitesItem, checkNowItem, MenuKit.Sep(),
                settingsItem, MenuKit.Sep(),
                toggleItem,
                Item("Выход", (s, e) => ExitApp())
            });

            foreach (var sub in new[] { gamesItem, reselectItem, manualItem, sitesItem, settingsItem, themeItem }) MenuKit.SubMenu(sub);

            menu.Opening += (s, e) => { ApplyTheme(); RefreshMenu(); };
            ApplyTheme();
            tray.ContextMenuStrip = menu;
        }

        static ToolStripMenuItem Item(string text, EventHandler click) { return MenuKit.Item(text, click); }

        static ToolStripMenuItem Info(string text, string kind) { return MenuKit.Info(text, kind); }

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
            fastItem.Enabled = bestItem.Enabled = manualItem.Enabled = !busy;
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
                var item = Item(name, (s, e) => UseConfig(file));
                item.Checked = running && name == current;
                manualItem.DropDownItems.Add(item);
            }

            // Мои сайты: добавленные и исключённые, клик - убрать из списка
            sitesItem.DropDownItems.Clear();
            var entries = MySites();
            if (entries.Count == 0)
            {
                var empty = Item("Пока пусто - добавь через «Добавить сайт / IP»", null);
                empty.Enabled = false;
                sitesItem.DropDownItems.Add(empty);
            }
            foreach (var en in entries)
            {
                var entry = en;
                sitesItem.DropDownItems.Add(Item((entry.Item3 ? "−  " : "+  ") + entry.Item1 + (entry.Item3 ? "   (исключён)" : ""), (s, e) => RemoveSite(entry.Item1, entry.Item2)));
            }

            gamesItem.DropDownItems.Clear();
            gamesItem.DropDownItems.Add(Item("Добавить игру...", (s, e) => AddGame()));

            // Встроенные игры: обход по их доменам, клик - включить/выключить
            gamesItem.DropDownItems.Add(MenuKit.Sep());
            var mine = new HashSet<string>(Util.ReadEntries(paths.ListGeneralUser), StringComparer.OrdinalIgnoreCase);
            foreach (var p in GamePresets)
            {
                var preset = p;
                var item = Item(preset.Item1, (s, e) => TogglePreset(preset));
                item.Checked = preset.Item2.All(mine.Contains);
                gamesItem.DropDownItems.Add(item);
            }

            if (games.Games.Count > 0) gamesItem.DropDownItems.Add(MenuKit.Sep());
            foreach (var g in games.Games.ToList())
            {
                var game = g;
                var item = Item(game.Exe + "   (адресов: " + game.Subnets.Count + (game.Enabled ? ")" : ", выкл)"), null);
                var enable = Item("Включена", (s, e) => ToggleGame(game));
                enable.Checked = game.Enabled;
                item.DropDownItems.Add(enable);
                item.DropDownItems.Add(Item("Удалить", (s, e) => RemoveGame(game)));
                MenuKit.SubMenu(item);
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
                if (p == null) return "";
                var t = DateTime.Now - p.StartTime;
                if (t.TotalMinutes < 1) return "работает меньше минуты";
                if (t.TotalHours < 1) return "работает " + (int)t.TotalMinutes + " мин";
                return "работает " + (int)t.TotalHours + " ч " + t.Minutes + " мин";
            }
            catch { return ""; }
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
            renderer.StatusColor = busy ? Theme.Busy : running ? Theme.On : Theme.Off;
            statusItem.Text = StatusText();
            // Строки всегда на месте (иначе меню прыгает по высоте) - когда показать нечего, пишем прочерк
            speedItem.Text = speedText.Length > 0 ? speedText : "↓ —   ↑ —";
            uptimeItem.Text = busy ? "подождите немного..." : running ? UptimeText() : "не запущен";
            if (menu.Visible) { statusItem.Invalidate(); speedItem.Invalidate(); uptimeItem.Invalidate(); }
            var text = "zapret: " + StatusText() + (speedText.Length > 0 ? "\n" + speedText : "");
            tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
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
                speedText = "↓ " + FormatSpeed(Math.Max(0, rx - rxPrev) / sec) + "   ↑ " + FormatSpeed(Math.Max(0, tx - txPrev) / sec)
                            + (pingMs >= 0 ? "   пинг " + pingMs + " мс" : "");
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
            if (vpn != null)
            {
                Notify("Запущен VPN (" + vpn + "). Пока он включён, трафик идёт через него, и подбор конфига будет неточным.", ToolTipIcon.Warning);
            }
            busy = true;
            busyText = mode == "best" ? "Ищу конфиг с минимальным пингом..." : "Подбираю конфиг...";
            UpdateState();

            int gen = ++selectGen;
            bool best = mode == "best";
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
                if (notifyResult) Notify("zapret работает: " + CurrentConfig());
            }
            else
            {
                Notify("Не удалось подобрать рабочий конфиг" + (error != null ? ": " + error : "") + ".\nПроверь интернет или попробуй «с минимальным пингом».", ToolTipIcon.Warning);
            }
        }

        // Перезапустить текущий конфиг (после правки списков), без подбора
        void RestartCurrent()
        {
            if (busy || !wantOn) return;
            var names = Util.ReadEntries(paths.BestConfig);
            var bat = names.Count > 0 ? Path.Combine(paths.Core, names[0]) : null;
            if (bat == null || !File.Exists(bat))
            {
                RunSelect("fast", false, true);
                return;
            }
            Util.KillWinws();
            Selector.StartConfig(paths, bat);
        }

        // ---------- сторож ----------

        void WatchdogTick()
        {
            if (!wantOn || busy || checking || !watchdogItem.Checked) return;
            bool running = Util.WinwsRunning();
            checking = true;
            Task.Factory.StartNew(() =>
            {
                if (!Util.InternetUp()) return 0;                 // нет интернета - не наша проблема
                if (!running) return 2;                           // winws упал
                var a = Task.Factory.StartNew(() => Util.HttpWorks("https://discord.com", 8000));
                var b = Task.Factory.StartNew(() => Util.HttpWorks("https://www.youtube.com", 8000));
                return a.Result && b.Result ? 0 : 1;
            }).ContinueWith(t => OnUi(() =>
            {
                checking = false;
                int r = t.Result;
                if (r == 0)
                {
                    watchFailedOnce = false;
                    watchTimer.Interval = WatchIntervalMs;
                }
                else if (r == 2 || watchFailedOnce)
                {
                    watchFailedOnce = false;
                    watchTimer.Interval = WatchIntervalMs;
                    if (!wantOn || busy) return;
                    Notify(r == 2 ? "zapret остановился - запускаю заново." : "Discord/YouTube перестали открываться - подбираю другой конфиг.", ToolTipIcon.Warning);
                    RunSelect("fast", false, true);
                }
                else
                {
                    // Один сбой может быть случайным - перепроверяем через минуту
                    watchFailedOnce = true;
                    watchTimer.Interval = RecheckIntervalMs;
                }
            }));
        }

        // ---------- свои сайты / IP ----------

        void AddEntry(bool exclude)
        {
            var title = exclude ? "Исключить сайт / IP" : "Добавить сайт / IP";
            var hint = exclude
                ? "Сайт или IP, который zapret ломает и который надо оставить без обхода.\nНапример: cursor.com или 1.2.3.4"
                : "Сайт или IP, который не открывается без обхода.\nНапример: example.com, 1.2.3.4 или 1.2.3.0/24";
            var text = InputDialog.Show(title, hint);
            if (text == null) return;

            string value;
            bool isIp;
            if (!Util.ParseEntry(text, out value, out isIp))
            {
                MessageBox.Show("Не понял, что это. Введи домен (example.com) или IP (1.2.3.4).", title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            bool changed;
            string note = "";
            if (exclude)
            {
                changed = Util.AppendUnique(isIp ? paths.IpsetExcludeUser : paths.ListExcludeUser, value);
                // Если раньше сами добавили - убираем из списка обхода
                if (isIp) { Util.RemoveEntry(paths.IpsetUser, value); ReapplyIpset(); }
                else Util.RemoveEntry(paths.ListGeneralUser, value);
            }
            else if (isIp)
            {
                changed = Util.AppendUnique(paths.IpsetUser, value);
                Util.RemoveEntry(paths.IpsetExcludeUser, value);
                note = ReapplyIpset();
            }
            else
            {
                changed = Util.AppendUnique(paths.ListGeneralUser, value);
                Util.RemoveEntry(paths.ListExcludeUser, value);
            }

            if (!changed && note.Length == 0)
            {
                Notify(value + " уже есть в списке.", ToolTipIcon.Info, true);
                return;
            }
            RestartCurrent();
            Notify((exclude ? "Исключено: " : "Добавлено: ") + value + (note.Length > 0 ? "\n" + note : ""), note.Length > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info, true);
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
            Notify(preset.Item1 + ": обход " + (on ? "выключен." : "включён."), ToolTipIcon.Info, true);
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
            return result;
        }

        void RemoveSite(string value, string file)
        {
            if (MessageBox.Show("Убрать " + value + " из списка?", AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            Util.RemoveEntry(file, value);
            if (file == paths.IpsetUser) ReapplyIpset();
            RestartCurrent();
            Notify("Убрано из списка: " + value, ToolTipIcon.Info, true);
        }

        // Проверить сейчас: открываются ли Discord и YouTube
        void CheckNow()
        {
            checkNowItem.Enabled = false;
            Task.Factory.StartNew(() =>
            {
                var a = Task.Factory.StartNew(() => Util.HttpWorks("https://discord.com", 8000));
                var b = Task.Factory.StartNew(() => Util.HttpWorks("https://www.youtube.com", 8000));
                return Tuple.Create(a.Result, b.Result);
            }).ContinueWith(t => OnUi(() =>
            {
                checkNowItem.Enabled = true;
                bool d = t.Result.Item1, y = t.Result.Item2;
                var text = "Discord " + (d ? "✓" : "✗") + "     YouTube " + (y ? "✓" : "✗");
                if (d && y)
                {
                    Notify(text, ToolTipIcon.Info, true);
                    return;
                }
                // Не открывается - по клику сразу подбираем конфиг получше
                Offer(text + "\nНажми, чтобы подобрать конфиг получше.", () => { wantOn = true; RunSelect("fast", false, true); });
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
            Notify("Включён конфиг " + Path.GetFileNameWithoutExtension(file) + ".\nЕсли что-то не открывается - «Проверить сейчас».", ToolTipIcon.Info, true);
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

        void AddGame()
        {
            var exe = ProcessPicker.Show();
            if (exe == null) return;
            var g = games.Add(exe);
            RefreshWatchedGames();
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
            var note = ReapplyIpset();
            if (note.Length > 0) Notify(note, ToolTipIcon.Warning);
        }

        void RemoveGame(Game g)
        {
            if (MessageBox.Show("Удалить " + g.Exe + " и все собранные адреса?", AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            games.Remove(g);
            RefreshWatchedGames();
            ReapplyIpset();
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
        void OnFlow(int pid, byte[] ip)
        {
            var exes = watchedExes;
            if (exes.Count == 0 || !Util.IsPublic(ip)) return;
            var name = ProcessName(pid);
            if (name.Length == 0 || !exes.Contains(name)) return;
            var g = games.Find(name);
            if (g != null && games.AddSubnet(g, Util.Subnet24(ip)))
            {
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
                foreach (var ip in TcpTable.RemoteAddresses(pids))
                {
                    if (Util.IsPublic(ip) && games.AddSubnet(g, Util.Subnet24(ip))) gamesDirty = true;
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
                        Notify("Сервера " + g.Exe + " сохранены (всего адресов: " + g.Subnets.Count + ").");
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
                    if (manual) Notify("Не удалось скачать обновление: " + t.Exception.InnerException.Message, ToolTipIcon.Warning);
                    return;
                }
                Notify("Ставлю обновление " + rel.Version + "... zapret перезапустится сам.", ToolTipIcon.Info, true);
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
            Task.Factory.StartNew(() => ZapretUpdater.Prepare(rel)).ContinueWith(t => OnUi(() =>
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
                        Notify("Конфиги zapret обновлены до " + rel.Version + ". Работает: " + CurrentConfig());
                        return;
                    }
                    try { ZapretUpdater.Restore(paths); } catch (Exception ex) { Log("откат не удался: " + ex); }
                    if (wantOn)
                    {
                        settings.Set("zapret_skip", rel.Version);   // эту версию больше сами не пробуем
                        Log("zapret " + rel.Version + ": новые конфиги не заработали, вернул старые");
                        Notify("Новые конфиги zapret " + rel.Version + " у тебя не заработали - вернул прежние.", ToolTipIcon.Warning);
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
            try { File.AppendAllText(Path.Combine(paths.Utils, "tray.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "  " + text + "\r\n"); }
            catch { }
        }

        void ExitApp()
        {
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
