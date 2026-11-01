// Setup.exe - установка и удаление Zapret Tray
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace ZapretTray
{
    static class SetupProgram
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Тихое обновление из трея: Setup.exe /update "<папка установки>"
            if (args.Length >= 2 && args[0].Equals("/update", StringComparison.OrdinalIgnoreCase))
            {
                Installer.SilentUpdate(args[1]);
                return;
            }

            var installed = Installer.FindInstalled();
            // Тема та же, что выбрана в меню программы
            if (installed != null) Theme.Mode = new Settings(new AppPaths(installed).Settings).Get("theme") ?? Theme.System;
            if (args.Any(a => a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase)))
            {
                if (installed == null)
                {
                    StyledDialog.Tell(null, AppInfo.Name + " не установлен.");
                    return;
                }
                if (!StyledDialog.Ask(null, "Удалить " + AppInfo.Name + " из папки\n" + installed + " ?\n\nzapret будет выключен, свои списки и игры тоже удалятся.")) return;
                Installer.Uninstall(installed);
                StyledDialog.Tell(null, "Готово, " + AppInfo.Name + " удалён.");
                return;
            }
            Application.Run(new SetupForm(installed));
        }
    }

    static class Installer
    {
        public static string SourceDir { get { return AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'); } }

        // В архиве для друзей всё, кроме Setup.exe, лежит в подпапке files
        public static string PayloadDir
        {
            get
            {
                var files = Path.Combine(SourceDir, "files");
                return File.Exists(Path.Combine(files, AppInfo.TrayExe)) ? files : SourceDir;
            }
        }

        // Путь установленной программы - из задачи планировщика
        public static string FindInstalled()
        {
            string xml;
            if (Util.Run("schtasks.exe", "/query /tn \"" + AppInfo.RunTask + "\" /xml", out xml) != 0) return null;
            var m = Regex.Match(xml, "<Command>(.*?)</Command>");
            if (!m.Success) return null;
            var exe = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim('"');
            var dir = Path.GetDirectoryName(exe);
            return Directory.Exists(dir) ? dir : null;
        }

        // Старые копии zapret: папки на корнях дисков, где есть bin\winws.exe (на 1-2 уровня вглубь)
        public static List<string> FindOldCopies(string exclude1, string exclude2)
        {
            var result = new List<string>();
            var skip = new[] { "Windows", "Program Files", "Program Files (x86)", "ProgramData", "$Recycle.Bin", "System Volume Information", "Users" };
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            {
                string[] tops;
                try { tops = Directory.GetDirectories(drive.RootDirectory.FullName); } catch { continue; }
                foreach (var top in tops)
                {
                    if (skip.Contains(Path.GetFileName(top), StringComparer.OrdinalIgnoreCase)) continue;
                    if (SameOrInside(exclude1, top) || SameOrInside(exclude2, top)) continue;
                    if (HasWinws(top)) { result.Add(top); continue; }
                    string[] subs;
                    try { subs = Directory.GetDirectories(top); } catch { continue; }
                    if (subs.Any(HasWinws)) result.Add(top);
                }
            }
            return result;
        }

        static bool HasWinws(string dir)
        {
            try { return File.Exists(Path.Combine(dir, @"bin\winws.exe")); } catch { return false; }
        }

        // path совпадает с dir или лежит внутри него
        static bool SameOrInside(string path, string dir)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var p = Path.GetFullPath(path).TrimEnd('\\') + "\\";
            var d = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
            return p.StartsWith(d, StringComparison.OrdinalIgnoreCase);
        }

        public static void StopEverything()
        {
            Tasks.End(AppInfo.RunTask);
            Tasks.End(AppInfo.AutoTask);
            int self = Process.GetCurrentProcess().Id;
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AppInfo.TrayExe)))
            {
                if (p.Id == self) continue;
                try { p.Kill(); p.WaitForExit(3000); } catch { }
            }
            Util.KillWinws();
        }

        // Без окон: остановить трей и winws, заменить файлы (свои списки и настройки сохраняются), запустить трей снова.
        // Задачу планировщика не трогаем через /end - трей запущен из неё, и вместе с ним мог бы погибнуть и этот процесс.
        public static void SilentUpdate(string target)
        {
            var log = Path.Combine(Path.GetTempPath(), "ZapretTrayUpdate.log");
            try
            {
                if (!File.Exists(Path.Combine(target, AppInfo.TrayExe))) throw new Exception("программа не найдена в " + target);
                Thread.Sleep(1500);   // даём трею спокойно запустить нас и выйти
                int self = Process.GetCurrentProcess().Id;
                foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AppInfo.TrayExe)))
                {
                    if (p.Id == self) continue;
                    try { p.Kill(); p.WaitForExit(5000); } catch { }
                }
                // Файлы движка освобождаются не сразу: winws мог ещё завершаться или его успел запустить подбор
                for (int attempt = 1; ; attempt++)
                {
                    Util.KillWinws();
                    Thread.Sleep(500 * attempt);
                    try
                    {
                        CopyFiles(target);
                        break;
                    }
                    catch (IOException)
                    {
                        if (attempt >= 5) throw;
                    }
                }
                File.WriteAllText(log, DateTime.Now + " OK -> " + AppInfo.Version);
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(log, DateTime.Now + " FAILED: " + ex); } catch { }
            }
            // В любом случае поднимаем трей обратно - новый или старый
            var trayName = Path.GetFileNameWithoutExtension(AppInfo.TrayExe);
            if (Tasks.RunNow(AppInfo.RunTask))
            {
                for (int i = 0; i < 10 && Process.GetProcessesByName(trayName).Length == 0; i++) Thread.Sleep(500);
            }
            if (Process.GetProcessesByName(trayName).Length == 0)
            {
                try { Process.Start(new ProcessStartInfo(Path.Combine(target, AppInfo.TrayExe)) { UseShellExecute = true }); } catch { }
            }
        }

        // Дописать в dst строки из src, которых там нет (вместе с комментарием-заголовком над ними)
        // Старые дефолтные исключения, уехавшие во встроенный список - убираем их из пользовательского файла
        static readonly string[] OldBundledExclude =
            { "cursor.sh", "cursor.com", "cursor-cdn.com", "cursorapi.com", "cursorvm.com", "todesktop.com" };

        static void RemoveBundledExclude(string dst)
        {
            if (!File.Exists(dst)) return;
            var drop = new HashSet<string>(OldBundledExclude, StringComparer.OrdinalIgnoreCase);
            var kept = File.ReadAllLines(dst)
                .Where(l => !drop.Contains(l.Trim()) && l.Trim().IndexOf("Cursor", StringComparison.OrdinalIgnoreCase) < 0)
                .ToList();
            Util.WriteLinesAtomic(dst, kept);
        }

        static void MergeLines(string src, string dst)
        {
            var have = new HashSet<string>(Util.ReadEntries(dst), StringComparer.OrdinalIgnoreCase);
            // Выключенное пользователем ("#off домен") заново не включаем
            foreach (var l in File.ReadAllLines(dst))
                if (l.Trim().StartsWith(Util.OffPrefix, StringComparison.OrdinalIgnoreCase)) have.Add(l.Trim().Substring(Util.OffPrefix.Length).Trim());
            var add = new List<string>();
            string header = null;
            foreach (var raw in File.ReadAllLines(src))
            {
                var line = raw.Trim();
                if (line.StartsWith("#")) { header = line; continue; }
                if (line.Length == 0 || have.Contains(line)) continue;
                if (header != null) { add.Add(header); header = null; }
                add.Add(line);
                have.Add(line);
            }
            if (add.Count == 0) return;
            var lines = File.ReadAllLines(dst).ToList();
            while (lines.Count > 0 && lines[lines.Count - 1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
            lines.AddRange(add);
            Util.WriteLinesAtomic(dst, lines);
        }

        public static void RemoveZapretService()
        {
            foreach (var name in new[] { "zapret", "WinDivert", "WinDivert14" })
            {
                Util.Run("sc.exe", "stop \"" + name + "\"");
                Util.Run("sc.exe", "delete \"" + name + "\"");
            }
        }

        // Копирование с сохранением файлов пользователя при переустановке
        public static void CopyFiles(string target)
        {
            // Setup.exe запущен из самой установленной папки - копировать нечего
            if (string.Equals(SourceDir, target, StringComparison.OrdinalIgnoreCase)) return;
            Directory.CreateDirectory(target);
            foreach (var exe in new[] { AppInfo.TrayExe, AppInfo.SetupExe })
            {
                var src = exe == AppInfo.SetupExe ? Application.ExecutablePath : Path.Combine(PayloadDir, exe);
                var dst = Path.Combine(target, exe);
                if (!string.Equals(Path.GetFullPath(src), Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase))
                    File.Copy(src, dst, true);
            }

            var srcCore = Path.Combine(PayloadDir, "core");
            var dstCore = Path.Combine(target, "core");
            var keep = new HashSet<string>(AppPaths.UserFiles, StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.GetFiles(srcCore, "*", SearchOption.AllDirectories))
            {
                var rel = file.Substring(srcCore.Length + 1);
                var dst = Path.Combine(dstCore, rel);
                if (keep.Contains(rel) && File.Exists(dst))
                {
                    // Свой список сайтов не затираем, но новые сайты из сборки (например, для игр) дописываем
                    if (string.Equals(rel, @"lists\list-general-user.txt", StringComparison.OrdinalIgnoreCase)) MergeLines(file, dst);
                    // Раньше дефолтные исключения Cursor лежали в пользовательском файле и мусорили в списке "Мои сайты".
                    // Теперь они во встроенном list-exclude.txt - вычищаем их из пользовательского у тех, кто уже поставил.
                    if (string.Equals(rel, @"lists\list-exclude-user.txt", StringComparison.OrdinalIgnoreCase)) RemoveBundledExclude(dst);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                File.Copy(file, dst, true);
            }
            // Лишнее из прошлых версий: программа это не использует, а антивирусы на такие скрипты ругаются
            foreach (var rel in new[] { @"utils\check_updates.enabled", @"utils\test zapret.ps1", @"utils\auto select.ps1", @"utils\targets.txt", @"lists\ipset-all.txt.backup" })
            {
                var f = Path.Combine(dstCore, rel);
                try { if (File.Exists(f)) File.Delete(f); } catch { }
            }
            var results = Path.Combine(dstCore, @"utils\test results");
            try { if (Directory.Exists(results)) Directory.Delete(results, true); } catch { }
        }

        public static void Uninstall(string dir)
        {
            StopEverything();
            // Папка сейчас удалится вместе с копией настроек DNS - возвращаем их до этого
            string dnsMessage;
            DnsFix.Restore(new AppPaths(dir), out dnsMessage);
            Tasks.Delete(AppInfo.RunTask);
            Tasks.Delete(AppInfo.AutoTask);
            foreach (var lnk in new[] { Shortcut.DesktopPath, Shortcut.StartMenuPath })
            {
                try { if (File.Exists(lnk)) File.Delete(lnk); } catch { }
            }
            // Setup.exe может лежать в этой же папке - удаляем после выхода
            var psi = new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"" + dir + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetTempPath()
            };
            Process.Start(psi);
        }
    }

    // ---------- оформление: те же цвета, шрифт и скругления, что в меню трея ----------

    // Окно без системной рамки, как меню: скруглённые углы, тень, тонкая граница. Таскается за любое пустое место
    class StyledForm : Form
    {
        protected readonly Palette Pal = Theme.Current;

        [DllImport("user32.dll")]
        static extern bool ReleaseCapture();
        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        public StyledForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            Font = MenuKit.Font;
            BackColor = Pal.Back;
            ForeColor = Pal.Text;
            DoubleBuffered = true;
            Text = AppInfo.Name;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x20000;   // CS_DROPSHADOW
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.RoundCorners(this);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var p = new Pen(Pal.Border)) e.Graphics.DrawRectangle(p, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            ReleaseCapture();
            SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);   // WM_NCLBUTTONDOWN, HTCAPTION
        }
    }

    enum ButtonKind { Primary, Secondary, Ghost }

    class StyledButton : Button
    {
        readonly Palette pal;
        readonly ButtonKind kind;
        bool hover;

        public StyledButton(string text, Palette pal, ButtonKind kind)
        {
            this.pal = pal;
            this.kind = kind;
            Text = text;
            Font = kind == ButtonKind.Primary ? MenuKit.Bold : MenuKit.Font;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : pal.Back);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Color back = kind == ButtonKind.Primary ? pal.Check : kind == ButtonKind.Secondary ? pal.Hover : pal.Back;
            if (hover && Enabled) back = kind == ButtonKind.Primary ? Style.Mix(pal.Check, Color.White, 0.14) : kind == ButtonKind.Secondary ? pal.Separator : pal.Hover;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = ThemedRenderer.RoundRect(r, 6))
            {
                using (var b = new SolidBrush(back)) g.FillPath(b, path);
                if (Focused && kind != ButtonKind.Ghost && ShowFocusCues)
                    using (var p = new Pen(pal.Text)) g.DrawPath(p, path);
            }
            var color = !Enabled ? pal.Disabled : kind == ButtonKind.Primary ? Color.White : kind == ButtonKind.Ghost && !hover ? pal.SubText : pal.Text;
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }
    }

    // Строка с переключателем: текст слева, переключатель справа, подсветка как у пункта меню
    class ToggleRow : Control
    {
        readonly Palette pal;
        readonly bool pathText;
        bool isChecked, hover;

        public bool Checked { get { return isChecked; } set { isChecked = value; Invalidate(); } }

        public ToggleRow(string text, bool on, Palette pal, bool pathText = false)
        {
            this.pal = pal;
            this.pathText = pathText;
            Text = text;
            isChecked = on;
            Font = MenuKit.Font;
            Height = Style.RowHeight;
            TabStop = true;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnClick(EventArgs e) { Checked = !Checked; Focus(); base.OnClick(e); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Space) Checked = !Checked;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(pal.Back);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            if ((hover || Focused) && Enabled)
                using (var path = ThemedRenderer.RoundRect(new Rectangle(0, 1, Width - 1, Height - 3), 6))
                using (var b = new SolidBrush(pal.Hover)) g.FillPath(b, path);

            const int sw = 34, sh = 18, knob = 12;
            int sx = Width - MenuKit.H - sw, sy = (Height - sh) / 2;
            var track = isChecked ? (Enabled ? pal.Check : Style.Mix(pal.Check, pal.Back, 0.6)) : pal.Disabled;
            using (var path = ThemedRenderer.RoundRect(new Rectangle(sx, sy, sw, sh), sh / 2))
            using (var b = new SolidBrush(track)) g.FillPath(b, path);
            int kx = isChecked ? sx + sw - knob - 3 : sx + 3;
            using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, kx, sy + 3, knob, knob);

            var r = new Rectangle(MenuKit.H, 0, sx - MenuKit.H - 8, Height);
            var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine
                        | (pathText ? TextFormatFlags.PathEllipsis : TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, Text, Font, r, Enabled ? pal.Text : pal.Disabled, flags);
        }
    }

    class StepBar : Control
    {
        readonly Palette pal;
        int value;

        public int Value { get { return value; } set { this.value = value; Invalidate(); } }

        public StepBar(Palette pal)
        {
            this.pal = pal;
            Height = 6;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(pal.Back);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = ThemedRenderer.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), 2))
            using (var b = new SolidBrush(pal.Hover)) g.FillPath(b, path);
            int w = Math.Max(Height, (Width - 1) * value / 100);
            using (var path = ThemedRenderer.RoundRect(new Rectangle(0, 0, w, Height - 1), 2))
            using (var b = new SolidBrush(pal.Check)) g.FillPath(b, path);
        }
    }

    static class Style
    {
        public const int RowHeight = 34;
        public const int M = 24;   // поля окна

        public static readonly Font Title = new Font("Segoe UI Semibold", 14f);
        public static readonly Font Section = new Font("Segoe UI Semibold", 8.25f);

        public static Color Mix(Color a, Color b, double t)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }
    }

    // Вопросы и сообщения в том же стиле, вместо серых окон Windows
    class StyledDialog : StyledForm
    {
        const int W = 400;

        StyledDialog(string text, bool question)
        {
            ShowInTaskbar = false;
            var size = TextRenderer.MeasureText(text, MenuKit.Font, new Size(W - Style.M * 2, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            var label = new Label { Text = text, Left = Style.M, Top = Style.M, Width = W - Style.M * 2, Height = size.Height + 4, ForeColor = Pal.Text, UseMnemonic = false };
            int y = label.Bottom + 20;

            var ok = new StyledButton(question ? "Да" : "OK", Pal, ButtonKind.Primary) { Width = 96, Height = 34, Top = y, Left = W - Style.M - 96, DialogResult = question ? DialogResult.Yes : DialogResult.OK };
            Controls.Add(label);
            Controls.Add(ok);
            AcceptButton = ok;
            if (question)
            {
                var no = new StyledButton("Нет", Pal, ButtonKind.Secondary) { Width = 96, Height = 34, Top = y, Left = ok.Left - 104, DialogResult = DialogResult.No };
                Controls.Add(no);
                CancelButton = no;
                // Главная кнопка по Enter - только там, где нечего терять
                AcceptButton = no;
                ActiveControl = no;
            }
            else CancelButton = ok;
            ClientSize = new Size(W, y + 34 + 20);
        }

        static DialogResult Show(IWin32Window owner, string text, bool question)
        {
            using (var d = new StyledDialog(text, question))
            {
                d.StartPosition = owner != null ? FormStartPosition.CenterParent : FormStartPosition.CenterScreen;
                return d.ShowDialog(owner);
            }
        }

        public static void Tell(IWin32Window owner, string text) { Show(owner, text, false); }
        public static bool Ask(IWin32Window owner, string text) { return Show(owner, text, true) == DialogResult.Yes; }
    }

    class SetupForm : StyledForm
    {
        const int W = 460;

        readonly string installed;
        readonly TextBox pathBox;
        readonly Label pathHint, statusLabel;
        readonly ToggleRow autostartBox, desktopBox, watchdogBox, autoupdateBox, serviceBox;
        readonly List<ToggleRow> oldBoxes = new List<ToggleRow>();
        readonly StyledButton installButton, uninstallButton, closeButton;
        readonly Label installedNote;
        readonly StepBar bar;

        public SetupForm(string installed)
        {
            this.installed = installed;
            Text = AppInfo.Name + " " + AppInfo.Version + " - установка";

            closeButton = new StyledButton("✕", Pal, ButtonKind.Ghost) { Left = W - 44, Top = 10, Width = 34, Height = 30, TabStop = false };
            closeButton.Click += (s, e) => Close();
            Controls.Add(closeButton);
            CancelButton = closeButton;

            int y = 86;
            Controls.Add(MakeSection("ПАПКА", y, Pal.SubText));
            y += 22;

            var pathPanel = new RoundBox(Pal) { Left = Style.M, Top = y, Width = W - Style.M * 2 - 92, Height = 34 };
            pathBox = new TextBox
            {
                Left = MenuKit.H, Top = 8, Width = pathPanel.Width - MenuKit.H * 2,
                BorderStyle = BorderStyle.None, BackColor = Pal.Hover, ForeColor = Pal.Text,
                Font = MenuKit.Font, Text = installed ?? @"C:\ZapretTray"
            };
            pathBox.TextChanged += (s, e) => UpdatePathHint();
            pathPanel.Controls.Add(pathBox);
            Controls.Add(pathPanel);

            var browse = new StyledButton("Обзор", Pal, ButtonKind.Secondary) { Left = W - Style.M - 84, Top = y, Width = 84, Height = 34 };
            browse.Click += (s, e) => Browse();
            Controls.Add(browse);
            y += 38;

            // Место под подсказку есть всегда, чтобы окно не прыгало
            pathHint = new Label { Left = Style.M, Top = y, Width = W - Style.M * 2, Height = 20, ForeColor = Theme.Busy, Font = Style.Section, UseMnemonic = false };
            Controls.Add(pathHint);
            y += 28;

            Controls.Add(MakeSection("НАСТРОЙКИ", y, Pal.SubText));
            y += 20;
            autostartBox = AddRow("Запускать вместе с Windows", true, ref y);
            desktopBox = AddRow("Ярлык на рабочем столе", true, ref y);
            watchdogBox = AddRow("Сам чинить, если перестало работать", true, ref y);
            autoupdateBox = AddRow("Обновляться автоматически", true, ref y);
            y += 12;

            bool hasService = Util.ServiceExists("zapret");
            var oldDirs = Installer.FindOldCopies(installed, Installer.SourceDir);
            serviceBox = new ToggleRow("Удалить службу zapret", hasService, Pal);
            if (hasService || oldDirs.Count > 0)
            {
                Controls.Add(MakeSection("НАЙДЕН СТАРЫЙ ZAPRET — МОЖНО УДАЛИТЬ", y, Theme.Busy));
                y += 20;
                if (hasService) AddRow(serviceBox, ref y);

                // Много папок бывает редко: больше четырёх строк - прокрутка
                var panel = new Panel { Left = Style.M - MenuKit.H, Top = y, Width = W - (Style.M - MenuKit.H) * 2, Height = Math.Min(oldDirs.Count, 4) * Style.RowHeight, AutoScroll = oldDirs.Count > 4 };
                int rowWidth = panel.Width - (oldDirs.Count > 4 ? SystemInformation.VerticalScrollBarWidth : 0);
                int rowTop = 0;
                foreach (var dir in oldDirs)
                {
                    var row = new ToggleRow("Удалить " + dir, false, Pal, true) { Left = 0, Top = rowTop, Width = rowWidth, Tag = dir };
                    rowTop += Style.RowHeight;
                    panel.Controls.Add(row);
                    oldBoxes.Add(row);
                }
                if (oldDirs.Count > 0) { Controls.Add(panel); y += panel.Height; }
                y += 12;
            }

            // Откат DNS здесь тоже: сработает, даже если сама программа не запускается
            if (installed != null && DnsFix.IsApplied(new AppPaths(installed)))
            {
                var dnsUndo = new StyledButton("Вернуть DNS как было", Pal, ButtonKind.Secondary) { Left = Style.M, Top = y, Width = W - Style.M * 2, Height = 34 };
                dnsUndo.Click += (s, e) =>
                {
                    string message;
                    DnsFix.Restore(new AppPaths(installed), out message);
                    StyledDialog.Tell(this, message);
                    dnsUndo.Enabled = DnsFix.IsApplied(new AppPaths(installed));
                };
                Controls.Add(dnsUndo);
                y += 42;
            }

            y += 4;
            installButton = new StyledButton(installed != null ? "Переустановить" : "Установить", Pal, ButtonKind.Primary)
            {
                Left = Style.M, Top = y, Height = 40, Width = installed != null ? W - Style.M * 2 - 132 : W - Style.M * 2
            };
            installButton.Click += (s, e) => Install();
            uninstallButton = new StyledButton("Удалить", Pal, ButtonKind.Secondary) { Left = W - Style.M - 124, Top = y, Height = 40, Width = 124, Visible = installed != null };
            uninstallButton.Click += (s, e) => DoUninstall();
            Controls.Add(installButton);
            Controls.Add(uninstallButton);
            AcceptButton = installButton;

            // Шаги установки показываются на месте кнопок
            statusLabel = new Label { Left = Style.M, Top = y, Width = W - Style.M * 2, Height = 22, ForeColor = Pal.SubText, Visible = false, UseMnemonic = false };
            bar = new StepBar(Pal) { Left = Style.M, Top = y + 28, Width = W - Style.M * 2, Visible = false };
            Controls.Add(statusLabel);
            Controls.Add(bar);
            y += 40;

            installedNote = new Label { Left = Style.M, Top = y + 8, Width = W - Style.M * 2, Height = 20, ForeColor = Pal.SubText, Font = Style.Section, Text = "Твои списки и игры сохранятся", Visible = installed != null };
            Controls.Add(installedNote);
            if (installed != null) y += 28;

            ClientSize = new Size(W, y + 22);
            ActiveControl = installButton;
            UpdatePathHint();
        }

        Label MakeSection(string text, int y, Color color)
        {
            return new Label { Text = text, Left = Style.M, Top = y, Width = W - Style.M * 2, Height = 18, ForeColor = color, Font = Style.Section, UseMnemonic = false };
        }

        ToggleRow AddRow(string text, bool on, ref int y)
        {
            return AddRow(new ToggleRow(text, on, Pal), ref y);
        }

        // Текст строки встаёт на одну линию с заголовком: подсветка выступает влево на свой отступ, как в меню
        ToggleRow AddRow(ToggleRow row, ref int y)
        {
            row.Left = Style.M - MenuKit.H;
            row.Top = y;
            row.Width = W - (Style.M - MenuKit.H) * 2;
            Controls.Add(row);
            y += Style.RowHeight;
            return row;
        }

        // Шапка рисуется прямо на окне, поэтому за неё можно таскать
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var logo = new Rectangle(Style.M, 22, 40, 40);
            using (var b = new SolidBrush(Theme.On)) g.FillEllipse(b, logo);
            using (var f = new Font("Segoe UI", 19, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString("Z", f, Brushes.White, new RectangleF(logo.X, logo.Y + 1, logo.Width, logo.Height), sf);

            int x = logo.Right + 12;
            TextRenderer.DrawText(g, AppInfo.Name, Style.Title, new Point(x, 19), Pal.Text, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, "YouTube и Discord без VPN. Версия " + AppInfo.Version, MenuKit.Font, new Point(x, 46), Pal.SubText, TextFormatFlags.NoPadding);
        }

        void UpdatePathHint()
        {
            var p = pathBox.Text;
            bool bad = p.Contains(" ") || Regex.IsMatch(p, @"[^\x00-\x7F]") || p.IndexOf("OneDrive", StringComparison.OrdinalIgnoreCase) >= 0;
            pathHint.Text = bad ? "Лучше без русских букв, пробелов и OneDrive: может не работать" : "";
        }

        void Browse()
        {
            using (var d = new FolderBrowserDialog { Description = "Куда установить", SelectedPath = pathBox.Text })
            {
                if (d.ShowDialog(this) == DialogResult.OK) pathBox.Text = d.SelectedPath;
            }
        }

        void Status(string text, int percent)
        {
            statusLabel.Text = text;
            bar.Value = percent;
            statusLabel.Refresh();
            bar.Refresh();
        }

        // На время установки вместо кнопок - текущий шаг и полоса
        void SetBusy(bool busy)
        {
            foreach (Control c in Controls) c.Enabled = !busy;
            statusLabel.Enabled = bar.Enabled = true;
            installButton.Visible = !busy;
            uninstallButton.Visible = !busy && installed != null;
            installedNote.Visible = !busy && installed != null;
            statusLabel.Visible = bar.Visible = busy;
            Refresh();
        }

        void Install()
        {
            string target;
            try { target = Path.GetFullPath(pathBox.Text.Trim()).TrimEnd('\\'); }
            catch { StyledDialog.Tell(this, "Неправильная папка."); return; }

            if (Regex.IsMatch(target, @"[^\x00-\x7F]") || target.Contains(" "))
            {
                if (!StyledDialog.Ask(this, "В пути есть русские буквы или пробелы - zapret может не работать. Всё равно ставить?")) return;
            }

            var toDelete = oldBoxes.Where(b => b.Checked).Select(b => (string)b.Tag).ToList();
            if (toDelete.Any(d => target.StartsWith(d.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(d, target, StringComparison.OrdinalIgnoreCase)))
            {
                StyledDialog.Tell(this, "Папка установки находится внутри папки, отмеченной на удаление. Выбери другую.");
                return;
            }
            if (toDelete.Count > 0 && !StyledDialog.Ask(this, "Будут удалены целиком:\n\n" + string.Join("\n", toDelete) + "\n\nТочно?")) return;

            SetBusy(true);
            UseWaitCursor = true;
            try
            {
                Status("Останавливаю zapret...", 10);
                Installer.StopEverything();
                Thread.Sleep(500);

                if (serviceBox.Checked)
                {
                    Status("Удаляю службу zapret...", 20);
                    Installer.RemoveZapretService();
                }

                foreach (var dir in toDelete)
                {
                    Status("Удаляю " + dir + "...", 35);
                    try { Directory.Delete(dir, true); }
                    catch (Exception ex) { StyledDialog.Tell(this, "Не удалось удалить " + dir + ":\n" + ex.Message); }
                }

                // Переустановка в другую папку - старую копию программы убираем
                if (installed != null && !string.Equals(installed, target, StringComparison.OrdinalIgnoreCase)
                    && !Installer.SourceDir.StartsWith(installed.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(Installer.SourceDir, installed, StringComparison.OrdinalIgnoreCase))
                {
                    string dnsMessage;
                    DnsFix.Restore(new AppPaths(installed), out dnsMessage);
                    try { Directory.Delete(installed, true); } catch { }
                }

                Status("Копирую файлы...", 55);
                Installer.CopyFiles(target);
                var paths = new AppPaths(target);

                Status("Настраиваю запуск...", 80);
                string err;
                if (!Tasks.Create(AppInfo.RunTask, paths.TrayExePath, false, out err))
                    throw new Exception("не удалось создать задачу планировщика:\n" + err);
                if (!Tasks.Create(AppInfo.AutoTask, paths.TrayExePath, true, out err))
                    throw new Exception("не удалось создать задачу автозапуска:\n" + err);
                if (!autostartBox.Checked) Tasks.SetEnabled(AppInfo.AutoTask, false);

                var st = new Settings(paths.Settings);
                st.SetBool("watchdog", watchdogBox.Checked);
                st.SetBool("autoupdate", autoupdateBox.Checked);

                Shortcut.Create(Shortcut.StartMenuPath, paths.TrayExePath, AppInfo.Name);
                if (desktopBox.Checked) Shortcut.Create(Shortcut.DesktopPath, paths.TrayExePath, AppInfo.Name);
                else if (File.Exists(Shortcut.DesktopPath)) File.Delete(Shortcut.DesktopPath);

                Status("Запускаю...", 100);
                Tasks.RunNow(AppInfo.RunTask);

                UseWaitCursor = false;
                StyledDialog.Tell(this, "Готово!\n\nЗначок zapret появится возле часов (может быть спрятан под стрелочкой ^).\n" +
                                        "Первый запуск может занять пару минут - zapret подбирает рабочий конфиг.\n\n" +
                                        "Правый клик по значку - меню, двойной клик - включить/выключить.");
                Close();
            }
            catch (Exception ex)
            {
                UseWaitCursor = false;
                SetBusy(false);
                StyledDialog.Tell(this, "Ошибка установки: " + ex.Message);
            }
        }

        void DoUninstall()
        {
            if (!StyledDialog.Ask(this, "Удалить " + AppInfo.Name + " из папки\n" + installed + " ?\n\nzapret будет выключен, свои списки и игры тоже удалятся.")) return;
            Installer.Uninstall(installed);
            StyledDialog.Tell(this, "Готово, программа удалена.");
            Close();
        }
    }

    // Подложка поля ввода со скруглёнными углами
    class RoundBox : Panel
    {
        readonly Palette pal;

        public RoundBox(Palette pal)
        {
            this.pal = pal;
            BackColor = pal.Back;
            DoubleBuffered = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = ThemedRenderer.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), 6))
            {
                using (var b = new SolidBrush(pal.Hover)) g.FillPath(b, path);
                using (var p = new Pen(pal.Separator)) g.DrawPath(p, path);
            }
        }
    }
}
