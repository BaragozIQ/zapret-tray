// Темы меню трея: светлая, тёмная и "как в Windows". Рисуем меню сами - скруглённая подсветка, тонкие разделители
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ZapretTray
{
    class Palette
    {
        public Color Back, Hover, Text, SubText, Disabled, Separator, Border, Check;

        public static readonly Palette Dark = new Palette
        {
            Back = Color.FromArgb(43, 45, 49), Hover = Color.FromArgb(58, 60, 66),
            Text = Color.FromArgb(242, 243, 245), SubText = Color.FromArgb(181, 186, 193), Disabled = Color.FromArgb(110, 114, 122),
            Separator = Color.FromArgb(63, 65, 71), Border = Color.FromArgb(30, 31, 34), Check = Color.FromArgb(88, 101, 242)
        };

        public static readonly Palette Light = new Palette
        {
            Back = Color.FromArgb(249, 249, 249), Hover = Color.FromArgb(234, 234, 234),
            Text = Color.FromArgb(26, 26, 26), SubText = Color.FromArgb(95, 95, 95), Disabled = Color.FromArgb(160, 160, 160),
            Separator = Color.FromArgb(224, 224, 224), Border = Color.FromArgb(208, 208, 208), Check = Color.FromArgb(0, 103, 192)
        };
    }

    static class Theme
    {
        public const string System = "system", Light = "light", Dark = "dark";

        public static readonly Color On = Color.FromArgb(35, 165, 90);
        public static readonly Color Busy = Color.FromArgb(240, 178, 50);
        public static readonly Color Off = Color.FromArgb(128, 132, 142);

        // Что выбрал пользователь: system / light / dark
        public static string Mode = System;

        public static bool WindowsIsLight()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    return v == null || (int)v != 0;
                }
            }
            catch { return true; }
        }

        public static Palette Current
        {
            get
            {
                if (Mode == Dark) return Palette.Dark;
                if (Mode == Light) return Palette.Light;
                return WindowsIsLight() ? Palette.Light : Palette.Dark;
            }
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // Тёмный (или светлый) заголовок у обычного окна. Windows 10 2004+ / 11; на старых просто ничего не произойдёт.
        public static void UseDarkTitleBar(Form f, bool dark)
        {
            try
            {
                int on = dark ? 1 : 0;
                DwmSetWindowAttribute(f.Handle, 20, ref on, sizeof(int));   // DWMWA_USE_IMMERSIVE_DARK_MODE
            }
            catch { }
        }

        // Windows 11: скруглённые углы и тень у выпадающего меню (на Windows 10 просто ничего не произойдёт)
        public static void RoundCorners(ToolStripDropDown dd)
        {
            try
            {
                int round = 2;   // DWMWCP_ROUND
                DwmSetWindowAttribute(dd.Handle, 33, ref round, sizeof(int));
            }
            catch { }
        }
    }

    // Пометки пунктов меню через Tag
    static class ItemKind
    {
        public const string Status = "status";   // первая строка статуса с цветной точкой
        public const string Info = "info";       // серые строки под статусом
    }

    // Сетка меню. Весь текст во всех меню начинается с одной линии (Left). По бокам отступ вдвое больше вертикального.
    // Галочки и стрелки - справа, поэтому текст никогда не сдвигается. Текст рисуем сами (ThemedRenderer), WinForms только меряет
    static class MenuKit
    {
        public const int V = 5;                 // отступ текста сверху/снизу
        public const int H = V * 2;             // отступ текста от края подсветки по бокам
        public const int Inset = 4;             // подсветка отступает от края меню
        public const int Left = Inset + H;      // линия текста (14)
        public const int Mark = 16;             // место справа под галочку / стрелку
        public const int Dot = 8;               // точка статуса (справа)

        public static readonly Font Font = new Font("Segoe UI", 9.75f);
        public static readonly Font Bold = new Font("Segoe UI Semibold", 9.75f);

        // Отступы нужны WinForms только для расчёта размеров: справа - место под галочку/стрелку
        static readonly Padding ItemPad = new Padding(Left, V, Left + Mark, V);

        public static ToolStripMenuItem Item(string text, EventHandler click)
        {
            var item = click != null ? new ToolStripMenuItem(text, null, click) : new ToolStripMenuItem(text);
            item.Padding = ItemPad;
            return item;
        }

        // Статус (жирный, с цветной точкой справа) и серые строки под ним: не нажимаются
        public static ToolStripMenuItem Info(string text, string kind)
        {
            var item = new ToolStripMenuItem(text) { Enabled = false, Tag = kind };
            if (kind == ItemKind.Status) item.Font = Bold;
            item.Padding = kind == ItemKind.Status ? new Padding(Left, V, Left + Mark, 1) : new Padding(Left, 0, Left + Mark, 1);
            return item;
        }

        // Постоянная ширина главного меню: строки статуса всё время меняются, и без этого меню прыгает.
        // Длинный текст обрезается многоточием (ThemedRenderer рисует с EndEllipsis)
        public const int MenuWidth = 300;

        public static void FixWidth(ToolStripDropDown dd, int width)
        {
            dd.MinimumSize = new Size(width, 0);
            dd.MaximumSize = new Size(width, 4000);
        }

        public static ToolStripSeparator Sep()
        {
            return new ToolStripSeparator { Margin = new Padding(0, 3, 0, 3) };
        }

        public static void Menu(ToolStripDropDownMenu dd)
        {
            dd.ShowImageMargin = false;
            dd.ShowCheckMargin = false;
            dd.Font = Font;
            dd.Padding = new Padding(0, Inset, 0, Inset);
            dd.HandleCreated += (s, e) => Theme.RoundCorners(dd);
        }

        // Подменю настраиваем один раз при создании: если менять размеры перед открытием,
        // Windows уже посчитала место, и подменю съезжает. После открытия придвигаем вплотную к родителю
        public static void SubMenu(ToolStripMenuItem item)
        {
            var dd = (ToolStripDropDownMenu)item.DropDown;
            Menu(dd);
            // Windows ставит подменю уже после Opened - поправляем следующим сообщением окна
            dd.Opened += (s, e) => { Snap(item, dd); dd.BeginInvoke((Action)(() => Snap(item, dd))); };
        }

        // WinForms прибавляет к месту подменю боковые отступы пункта - получается щель, и мышка в неё проваливается.
        // Придвигаем вплотную с нахлёстом 2px
        static void Snap(ToolStripMenuItem item, ToolStripDropDown dd)
        {
            var parent = item.Owner as ToolStripDropDown;
            if (parent == null) return;
            var pb = parent.Bounds;
            var screen = Screen.FromRectangle(pb).WorkingArea;
            // Открываем в сторону, где больше места. Меню стоит на одном месте, поэтому все подменю уходят в одну сторону.
            bool left = (pb.Left - screen.Left) >= (screen.Right - pb.Right);
            int x = left ? pb.Left - dd.Width + 2 : pb.Right - 2;
            // Если на выбранной стороне не влезает - уходим на другую
            if (x < screen.Left || x + dd.Width > screen.Right)
                x = left ? pb.Right - 2 : pb.Left - dd.Width + 2;
            // dd.Left у открытого подменю WinForms игнорирует - двигаем само окно
            if (x != dd.Left && x >= screen.Left && x + dd.Width <= screen.Right)
                SetWindowPos(dd.Handle, IntPtr.Zero, x, dd.Top, 0, 0, 0x0001 | 0x0004 | 0x0010);   // NOSIZE | NOZORDER | NOACTIVATE
        }

        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    }

    class ThemedRenderer : ToolStripRenderer
    {
        public Color StatusColor = Theme.Off;

        static Palette P { get { return Theme.Current; } }

        protected override void Initialize(ToolStrip ts)
        {
            base.Initialize(ts);
            ts.BackColor = P.Back;
            ts.ForeColor = P.Text;
        }

        protected override void InitializeItem(ToolStripItem item)
        {
            base.InitializeItem(item);
            item.ForeColor = P.Text;
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (var b = new SolidBrush(P.Back)) e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            var r = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            using (var p = new Pen(P.Border)) e.Graphics.DrawRectangle(p, r);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            using (var b = new SolidBrush(P.Back)) e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (e.Item.Selected && e.Item.Enabled)
            {
                var r = new Rectangle(MenuKit.Inset, 1, W(e.Item) - MenuKit.Inset * 2, e.Item.Height - 2);
                using (var path = RoundRect(r, 6))
                using (var b = new SolidBrush(P.Hover)) g.FillPath(b, path);
            }
        }

        // Справа: стрелка подменю, галочка или точка статуса - на одной вертикали, с тем же отступом, что и текст слева.
        // WinForms обрезает рисование пункта по области без отступов, а метка стоит в правом отступе - снимаем обрезку
        void DrawMark(Graphics g, ToolStripMenuItem mi)
        {
            g.ResetClip();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int cx = W(mi) - MenuKit.Left - MenuKit.Mark / 2, cy = mi.Height / 2;
            if ((string)mi.Tag == ItemKind.Status)
            {
                int d = MenuKit.Dot;
                using (var b = new SolidBrush(StatusColor)) g.FillEllipse(b, cx - d / 2, cy - d / 2, d, d);
            }
            else if (mi.HasDropDownItems)
            {
                using (var p = new Pen(mi.Enabled ? P.SubText : P.Disabled, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawLine(p, cx - 2, cy - 4, cx + 2, cy);
                    g.DrawLine(p, cx + 2, cy, cx - 2, cy + 4);
                }
            }
            else if (mi.Checked)
            {
                using (var p = new Pen(P.Check, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    g.DrawLines(p, new[] { new Point(cx - 5, cy), new Point(cx - 2, cy + 3), new Point(cx + 5, cy - 4) });
            }
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (var p = new Pen(P.Separator)) e.Graphics.DrawLine(p, MenuKit.Left, y, W(e.Item) - MenuKit.Left, y);
        }

        // Текст рисуем сами: всегда с линии Left и строго по центру строки (WinForms ставит его как придётся)
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            var kind = e.Item.Tag as string;
            var font = kind == ItemKind.Status ? MenuKit.Bold : MenuKit.Font;
            var color = kind == ItemKind.Status ? P.Text : kind == ItemKind.Info ? P.SubText : e.Item.Enabled ? P.Text : P.Disabled;
            var pad = e.Item.Padding;
            var r = new Rectangle(MenuKit.Left, pad.Top, W(e.Item) - MenuKit.Left * 2 - MenuKit.Mark, e.Item.Height - pad.Top - pad.Bottom);
            TextRenderer.DrawText(e.Graphics, e.Text, font, r, color,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            var mi = e.Item as ToolStripMenuItem;
            if (mi != null) DrawMark(e.Graphics, mi);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            // Стрелку рисуем сами в OnRenderMenuItemBackground (здесь рисование обрезается по маленькой области)
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            // Галочку рисуем сами в OnRenderMenuItemBackground
        }

        // Видимая ширина пункта: WinForms делает пункты шире окна меню, правый край уходит за границу
        static int W(ToolStripItem item)
        {
            var o = item.Owner;
            return o == null ? item.Width : Math.Min(item.Width, o.Width - 2 * item.Bounds.Left);
        }

        static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
