// ZapretTray.exe - значок в трее: вкл/выкл, подбор конфига, сторож, автозапуск, свои сайты и игры
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
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

        ToolStripMenuItem statusItem, speedItem, toggleItem, fastItem, bestItem, addItem, excludeItem;

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
        Process selectProc;
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

            BuildMenu();
            tray.Icon = iconOff;
            tray.Text = AppInfo.Name;
            tray.Visible = true;
            tray.DoubleClick += (s, e) => Toggle();

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
            var menu = new ContextMenuStrip();
            statusItem = new ToolStripMenuItem("...") { Enabled = false };
            speedItem = new ToolStripMenuItem("") { Enabled = false };
            toggleItem = new ToolStripMenuItem("Выключить", null, (s, e) => Toggle());
            toggleItem.Font = new Font(toggleItem.Font, FontStyle.Bold);
            fastItem = new ToolStripMenuItem("Подобрать заново (быстро)", null, (s, e) => { wantOn = true; RunSelect("fast", false, true); });
            bestItem = new ToolStripMenuItem("Подобрать с минимальным пингом...", null, (s, e) => { wantOn = true; RunSelect("best", true, true); });
            addItem = new ToolStripMenuItem("Добавить сайт / IP...", null, (s, e) => AddEntry(false));
            excludeItem = new ToolStripMenuItem("Исключить сайт / IP...", null, (s, e) => AddEntry(true));
            gamesItem = new ToolStripMenuItem("Мои игры");
            watchdogItem = new ToolStripMenuItem("Сторож (сам чинит, если сломалось)") { CheckOnClick = true };
            watchdogItem.Checked = settings.GetBool("watchdog", true);
            watchdogItem.CheckedChanged += (s, e) => settings.SetBool("watchdog", watchdogItem.Checked);
            autostartItem = new ToolStripMenuItem("Запускать вместе с Windows") { CheckOnClick = true };
            autostartItem.Click += (s, e) => SetAutostart(autostartItem.Checked);

            menu.Items.Add(statusItem);
            menu.Items.Add(speedItem);
            menu.Items.Add(toggleItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(fastItem);
            menu.Items.Add(bestItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(addItem);
            menu.Items.Add(excludeItem);
            menu.Items.Add(gamesItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(watchdogItem);
            menu.Items.Add(autostartItem);
            menu.Items.Add(new ToolStripMenuItem("Открыть папку", null, (s, e) => Process.Start("explorer.exe", "\"" + paths.Root + "\"")));
            menu.Items.Add(new ToolStripMenuItem("Проверить обновления (сейчас " + AppInfo.Version + ")", null, (s, e) => CheckUpdate(true)));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Удалить программу...", null, (s, e) => Uninstall()));
            menu.Items.Add(new ToolStripMenuItem("Выход", null, (s, e) => ExitApp()));

            menu.Opening += (s, e) => RefreshMenu();
            tray.ContextMenuStrip = menu;
        }

        void RefreshMenu()
        {
            statusItem.Text = StatusText();
            toggleItem.Text = wantOn ? "Выключить" : "Включить";
            fastItem.Enabled = bestItem.Enabled = !busy;
            autostartItem.Checked = Tasks.IsEnabled(AppInfo.AutoTask);

            gamesItem.DropDownItems.Clear();
            gamesItem.DropDownItems.Add(new ToolStripMenuItem("Добавить игру...", null, (s, e) => AddGame()));
            if (games.Games.Count > 0) gamesItem.DropDownItems.Add(new ToolStripSeparator());
            foreach (var g in games.Games.ToList())
            {
                var game = g;
                var item = new ToolStripMenuItem(game.Exe + "   (адресов: " + game.Subnets.Count + ")");
                item.Checked = game.Enabled;
                var enable = new ToolStripMenuItem("Включена", null, (s, e) => ToggleGame(game)) { Checked = game.Enabled };
                var remove = new ToolStripMenuItem("Удалить", null, (s, e) => RemoveGame(game));
                item.DropDownItems.Add(enable);
                item.DropDownItems.Add(remove);
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
                return "Работает" + (cfg != null ? ": " + cfg : "");
            }
            return wantOn ? "Не запущен" : "Выключен";
        }

        void UpdateState()
        {
            bool running = Util.WinwsRunning();
            tray.Icon = busy ? iconBusy : running ? iconOn : iconOff;
            UpdateSpeed();
            statusItem.Text = StatusText();
            speedItem.Text = speedText;
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

        void Notify(string text, ToolTipIcon icon = ToolTipIcon.Info)
        {
            tray.ShowBalloonTip(5000, AppInfo.Name, text, icon);
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
            if (selectProc != null)
            {
                try { if (!selectProc.HasExited) selectProc.Kill(); } catch { }
                selectProc = null;
            }
            busy = false;
            Util.KillWinws();
            UpdateState();
        }

        // Запуск "auto select.ps1": fast - скрыто, best - в окне, чтобы было видно таблицу
        void RunSelect(string mode, bool visible, bool notifyResult)
        {
            if (busy) return;
            if (Util.ServiceExists("zapret"))
            {
                Notify("Мешает служба zapret. Запусти Setup.exe и отметь 'Удалить службу zapret'.", ToolTipIcon.Warning);
                return;
            }
            var vpn = Util.RunningVpn();
            if (vpn != null)
            {
                Notify("Запущен VPN (" + vpn + "). Пока он включён, трафик идёт через него, и подбор конфига будет неточным.", ToolTipIcon.Warning);
            }
            busy = true;
            busyText = mode == "best" ? "Ищу конфиг с минимальным пингом..." : "Подбираю конфиг...";
            UpdateState();

            var args = "-NoProfile -ExecutionPolicy Bypass -File \"" + paths.AutoSelect + "\" -Mode " + mode + (visible ? "" : " -NoWait");
            var psi = new ProcessStartInfo("powershell.exe", args) { WorkingDirectory = paths.Core };
            if (visible)
            {
                psi.UseShellExecute = true;
            }
            else
            {
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
            }
            try
            {
                var p = Process.Start(psi);
                p.EnableRaisingEvents = true;
                selectProc = p;
                p.Exited += (s, e) => OnUi(() => SelectDone(p, notifyResult));
            }
            catch (Exception ex)
            {
                busy = false;
                Notify("Не удалось запустить подбор: " + ex.Message, ToolTipIcon.Error);
            }
        }

        void SelectDone(Process p, bool notifyResult)
        {
            if (selectProc != p) return;   // уже выключили вручную
            selectProc = null;
            busy = false;
            int code = -1;
            try { code = p.ExitCode; } catch { }
            UpdateState();
            if (code == 0 && Util.WinwsRunning())
            {
                if (notifyResult) Notify("zapret работает: " + CurrentConfig());
            }
            else
            {
                Notify("Не удалось подобрать рабочий конфиг. Проверь интернет или попробуй 'с минимальным пингом'.", ToolTipIcon.Warning);
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
            var psi = new ProcessStartInfo("cmd.exe", "/c \"\"" + bat + "\"\"") { WorkingDirectory = paths.Core, UseShellExecute = false, CreateNoWindow = true };
            psi.EnvironmentVariables["NO_UPDATE_CHECK"] = "1";
            try { Process.Start(psi); } catch { }
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
                Notify(value + " уже есть в списке.");
                return;
            }
            RestartCurrent();
            Notify((exclude ? "Исключено: " : "Добавлено: ") + value + (note.Length > 0 ? "\n" + note : ""), note.Length > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info);
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
            Notify("Игра " + exe + " добавлена. Пока она запущена, её сервера будут добавляться сами.\nМожно просто играть.");
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
            Task.Factory.StartNew(() =>
            {
                var rel = Updater.GetLatest();
                if (rel == null || !Updater.IsNewer(rel.Version, AppInfo.Version)) return Tuple.Create(rel, (string)null, (string)null);
                try { return Tuple.Create(rel, Updater.Download(rel), (string)null); }
                catch (Exception ex) { return Tuple.Create(rel, (string)null, ex.Message); }
            }).ContinueWith(t => OnUi(() =>
            {
                updating = false;
                var rel = t.Result.Item1;
                var setup = t.Result.Item2;
                var error = t.Result.Item3;
                if (rel != null) settings.Set("update_check", DateTime.UtcNow.Ticks.ToString());

                if (setup != null)
                {
                    Notify("Ставлю обновление " + rel.Version + "... zapret перезапустится сам.");
                    try { Updater.RunSetup(setup, paths.Root); }
                    catch (Exception ex) { Notify("Не удалось запустить обновление: " + ex.Message, ToolTipIcon.Error); }
                }
                else if (error != null)
                {
                    if (manual) Notify("Не удалось скачать обновление: " + error, ToolTipIcon.Warning);
                }
                else if (manual)
                {
                    Notify(rel == null ? "Не получилось проверить обновления - нет связи с GitHub." : "У тебя последняя версия (" + AppInfo.Version + ").");
                }
            }));
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
