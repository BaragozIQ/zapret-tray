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
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var r = new Rectangle(4, 1, e.Item.Width - 8, e.Item.Height - 2);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = RoundRect(r, 5))
            using (var b = new SolidBrush(P.Hover)) e.Graphics.FillPath(b, path);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (var p = new Pen(P.Separator)) e.Graphics.DrawLine(p, 10, y, e.Item.Width - 10, y);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            var kind = e.Item.Tag as string;
            if (kind == ItemKind.Status)
            {
                // Цветная точка перед текстом статуса
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                int d = 9;
                int x = e.TextRectangle.X;
                int y = e.Item.Height / 2 - d / 2;
                using (var b = new SolidBrush(StatusColor)) e.Graphics.FillEllipse(b, x, y, d, d);
                var r = e.TextRectangle;
                r.X += d + 7;
                using (var f = new Font(e.TextFont, FontStyle.Bold))
                    TextRenderer.DrawText(e.Graphics, e.Text, f, new Rectangle(r.X, r.Y, e.Item.Width - r.X, r.Height), P.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                return;
            }
            var color = kind == ItemKind.Info ? P.SubText : e.Item.Enabled ? P.Text : P.Disabled;
            TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, e.TextRectangle, color, e.TextFormat);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            // Аккуратная стрелка подменю ›
            var r = e.ArrowRectangle;
            int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = new Pen(e.Item.Enabled ? P.SubText : P.Disabled, 1.6f))
            {
                e.Graphics.DrawLine(p, cx - 2, cy - 4, cx + 2, cy);
                e.Graphics.DrawLine(p, cx + 2, cy, cx - 2, cy + 4);
            }
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            // Галочка ✓ без рамки
            var r = e.ImageRectangle;
            int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = new Pen(P.Check, 2f))
            {
                e.Graphics.DrawLines(p, new[] { new Point(cx - 5, cy), new Point(cx - 1, cy + 4), new Point(cx + 6, cy - 4) });
            }
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
