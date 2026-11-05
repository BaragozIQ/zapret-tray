// Окна выбора приложения ("Добавить из установленных", "Добавить запущенную") в стиле программы:
// тёмное/светлое, скруглённое, со значками. Возвращают PickItem с именем процесса, путём и иконкой.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ZapretTray
{
    class PickItem
    {
        public string Title, Sub;   // крупная подпись и серая под ней
        public string Exe, ExePath; // имя процесса без .exe и путь к файлу (для иконки)
        public Image Icon;          // значок строки
    }

    // Простая кнопка в цветах темы
    class FlatButton : Button
    {
        readonly Palette pal = Theme.Current;
        readonly bool primary;
        bool hover;
        public FlatButton(string text, bool primary)
        {
            this.primary = primary;
            Text = text;
            FlatStyle = FlatStyle.Flat;
            Font = primary ? MenuKit.Bold : MenuKit.Font;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : pal.Back);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color back = primary ? pal.Check : pal.Hover;
            if (hover && Enabled) back = primary ? ControlPanel.Mix(pal.Check, Color.White, 0.14f) : pal.Separator;
            using (var path = ThemedRenderer.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), 7))
            using (var b = new SolidBrush(Enabled ? back : pal.Hover)) g.FillPath(b, path);
            var color = !Enabled ? pal.Disabled : primary ? Color.White : pal.Text;
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }
    }

    class PickerForm : Form
    {
        readonly Palette pal = Theme.Current;
        readonly string hint;
        readonly Func<List<PickItem>> load;
        List<PickItem> all = new List<PickItem>();
        readonly ListBox list;
        readonly TextBox search;
        public PickItem Result;
        const int W = 468, Pad = 16;

        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        PickerForm(string title, string hint, Func<List<PickItem>> load, bool canRefresh, Func<PickItem> browse)
        {
            this.hint = hint;
            this.load = load;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            TopMost = true;
            DoubleBuffered = true;
            Font = MenuKit.Font;
            BackColor = pal.Back;
            Text = title;
            ClientSize = new Size(W, 520);

            var close = new FlatButton("✕", false) { Left = W - 12 - 30, Top = 12, Width = 30, Height = 28, TabStop = false };
            close.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };

            search = new TextBox { Left = Pad, Top = 86, Width = W - Pad * 2, BorderStyle = BorderStyle.FixedSingle, BackColor = pal.Hover, ForeColor = pal.Text, Font = MenuKit.Font };
            search.TextChanged += (s, e) => Rebind();

            list = new ListBox { Left = Pad, Top = 118, Width = W - Pad * 2, Height = 340, IntegralHeight = false, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 46, BorderStyle = BorderStyle.None, BackColor = pal.Card, ForeColor = pal.Text };
            list.DrawItem += DrawRow;
            list.DoubleClick += (s, e) => { if (list.SelectedIndex >= 0) { Accept(); } };

            var add = new FlatButton("Добавить", true) { Left = W - Pad - 120, Top = 470, Width = 120, Height = 38 };
            add.Click += (s, e) => Accept();
            var cancel = new FlatButton("Отмена", false) { Left = W - Pad - 120 - 96 - 8, Top = 470, Width = 96, Height = 38 };
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Controls.AddRange(new Control[] { close, search, list, add, cancel });

            if (canRefresh)
            {
                var refresh = new FlatButton("Обновить", false) { Left = Pad, Top = 470, Width = 110, Height = 38 };
                refresh.Click += (s, e) => Reload();
                Controls.Add(refresh);
            }
            // Вкладка "выбрать файл программы": своя кнопка, закрывает окно с выбранным файлом
            if (browse != null)
            {
                var pick = new FlatButton("Выбрать файл...", false) { Left = Pad, Top = 470, Width = 150, Height = 38 };
                pick.Click += (s, e) => { var it = browse(); if (it != null) { Result = it; DialogResult = DialogResult.OK; Close(); } };
                Controls.Add(pick);
            }

            AcceptButton = add;
            CancelButton = cancel;
            Reload();
        }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; }   // CS_DROPSHADOW
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.RoundCorners(this); }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = new Pen(pal.Border)) g.DrawRectangle(p, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            // Шапка: кружок Z, заголовок, подсказка
            var logo = new Rectangle(Pad, 18, 34, 34);
            using (var b = new SolidBrush(Theme.On)) g.FillEllipse(b, logo);
            using (var f = new Font("Segoe UI", 16, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString("Z", f, Brushes.White, new RectangleF(logo.X, logo.Y + 1, logo.Width, logo.Height), sf);
            TextRenderer.DrawText(g, Text, new Font("Segoe UI Semibold", 12f), new Point(logo.Right + 12, 20), pal.Text, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, hint, MenuKit.Small, new Rectangle(Pad, 58, W - Pad * 2, 22), pal.SubText, TextFormatFlags.NoPadding | TextFormatFlags.WordEllipsis);
        }

        // За пустые места и шапку окно таскается
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); }
        }

        void DrawRow(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            var it = (PickItem)list.Items[e.Index];
            bool selected = (e.State & DrawItemState.Selected) != 0;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(pal.Card)) g.FillRectangle(b, e.Bounds);
            if (selected)
                using (var path = ThemedRenderer.RoundRect(new Rectangle(e.Bounds.Left + 2, e.Bounds.Top + 2, e.Bounds.Width - 4, e.Bounds.Height - 4), 8))
                using (var b = new SolidBrush(pal.Hover)) g.FillPath(b, path);
            if (it.Icon != null) g.DrawImage(it.Icon, e.Bounds.Left + 10, e.Bounds.Top + 7, 32, 32);
            else ThemedRenderer.DrawIcon(g, "games", e.Bounds.Left + 18, e.Bounds.Top + e.Bounds.Height / 2, pal.SubText);
            int tx = e.Bounds.Left + 52;
            TextRenderer.DrawText(g, it.Title, MenuKit.Bold, new Rectangle(tx, e.Bounds.Top + 6, e.Bounds.Width - tx - 6, 20), pal.Text, TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            if (!string.IsNullOrEmpty(it.Sub))
                TextRenderer.DrawText(g, it.Sub, MenuKit.Small, new Rectangle(tx, e.Bounds.Top + 24, e.Bounds.Width - tx - 6, 18), pal.SubText, TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }

        void Reload()
        {
            all = load() ?? new List<PickItem>();
            Rebind();
        }

        void Rebind()
        {
            var q = search.Text.Trim();
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var it in all)
                if (q.Length == 0 || it.Title.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || (it.Exe ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                    list.Items.Add(it);
            if (list.Items.Count > 0) list.SelectedIndex = 0;
            list.EndUpdate();
        }

        void Accept()
        {
            if (list.SelectedIndex < 0) return;
            Result = (PickItem)list.Items[list.SelectedIndex];
            DialogResult = DialogResult.OK;
            Close();
        }

        // Значки, кроме выбранного, освобождаем
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            foreach (var it in all)
                if (it != Result && it.Icon != null) try { it.Icon.Dispose(); } catch { }
        }

        public static PickItem Show(string title, string hint, Func<List<PickItem>> load, bool canRefresh, Func<PickItem> browse = null)
        {
            using (var f = new PickerForm(title, hint, load, canRefresh, browse))
                return f.ShowDialog() == DialogResult.OK ? f.Result : null;
        }
    }

    // Список установленных программ со значками
    static class InstalledPicker
    {
        public static PickItem Show()
        {
            var items = AppScan.Installed().Select(a => new PickItem { Title = a.Name, Sub = a.Exe + ".exe", Exe = a.Exe, ExePath = a.ExePath, Icon = SmallIcon(a.ExePath) }).ToList();
            if (items.Count == 0)
            {
                MessageBox.Show("Не нашёл установленных программ в меню Пуск. Запусти приложение и добавь его через «Добавить запущенную».",
                    AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return null;
            }
            return PickerForm.Show("Добавить из установленных", "Выбери программу из списка или укажи её файл кнопкой «Выбрать файл».", () => items, false, Browse);
        }

        // Кнопка "Выбрать файл..." в этом же окне: указать .exe на диске
        static PickItem Browse()
        {
            using (var d = new OpenFileDialog { Title = "Выбери .exe приложения", Filter = "Программы (*.exe)|*.exe", CheckFileExists = true })
            {
                if (d.ShowDialog() != DialogResult.OK) return null;
                var exe = Path.GetFileNameWithoutExtension(d.FileName);
                return new PickItem { Title = exe, Sub = d.FileName, Exe = exe, ExePath = d.FileName, Icon = SmallIcon(d.FileName) };
            }
        }

        static Image SmallIcon(string path)
        {
            try { using (var ico = Icon.ExtractAssociatedIcon(path)) return ico != null ? ico.ToBitmap() : null; }
            catch { return null; }
        }
    }

    // Список запущенных программ с окнами
    static class ProcessPicker
    {
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        public static PickItem Show()
        {
            return PickerForm.Show("Добавить запущенную", "Запусти игру и выбери её. Дальше её сервера добавятся сами.", Load, true);
        }

        static List<PickItem> Load()
        {
            int self = Process.GetCurrentProcess().Id;
            return Process.GetProcesses()
                .Where(p => { try { return p.MainWindowHandle != IntPtr.Zero && p.Id != self; } catch { return false; } })
                .GroupBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase)
                .Select(grp => grp.First())
                .OrderBy(p => p.ProcessName, StringComparer.CurrentCultureIgnoreCase)
                .Select(From).ToList();
        }

        static PickItem From(Process p)
        {
            string path = null, title = "";
            try { title = p.MainWindowTitle ?? ""; } catch { }
            try { path = p.MainModule.FileName; } catch { }
            return new PickItem { Title = p.ProcessName, Sub = title, Exe = p.ProcessName, ExePath = path, Icon = IconFor(p, path) };
        }

        // Иконку берём из окна (работает и без доступа к файлу), иначе из exe
        static Image IconFor(Process p, string path)
        {
            try
            {
                IntPtr h = p.MainWindowHandle;
                if (h != IntPtr.Zero)
                {
                    var r = SendMessage(h, 0x7F, (IntPtr)1, IntPtr.Zero);          // WM_GETICON ICON_BIG
                    if (r == IntPtr.Zero) r = SendMessage(h, 0x7F, (IntPtr)2, IntPtr.Zero);  // ICON_SMALL2
                    if (r != IntPtr.Zero) using (var ico = Icon.FromHandle(r)) return ico.ToBitmap();
                }
            }
            catch { }
            if (path != null) try { using (var ico = Icon.ExtractAssociatedIcon(path)) return ico != null ? ico.ToBitmap() : null; } catch { }
            return null;
        }
    }
}
