// Темы меню трея: светлая, тёмная и "как в Windows". Рисуем меню сами - скруглённая подсветка, тонкие разделители
using System;
using System.Runtime.CompilerServices;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ZapretTray
{
    class Palette
    {
        public Color Back, Hover, Text, SubText, Disabled, Separator, Border, Check, Card, Tile;
        public Color Accent2;       // второй цвет акцентного перелива
        public float Shine = 1f;    // сила свечения и переливов: на тёмном фоне они заметнее, поэтому там слабее

        public static readonly Palette Dark = new Palette
        {
            Back = Color.FromArgb(28, 28, 28), Hover = Color.FromArgb(52, 52, 52), Card = Color.FromArgb(40, 40, 40), Tile = Color.FromArgb(56, 56, 56),
            Text = Color.FromArgb(240, 240, 240), SubText = Color.FromArgb(172, 172, 172), Disabled = Color.FromArgb(112, 112, 112),
            Separator = Color.FromArgb(52, 52, 52), Border = Color.FromArgb(58, 58, 58), Check = Color.FromArgb(0, 103, 192),
            Accent2 = Color.FromArgb(24, 120, 214), Shine = 0.3f
        };

        public static readonly Palette Light = new Palette
        {
            Back = Color.FromArgb(249, 249, 249), Hover = Color.FromArgb(234, 234, 234), Card = Color.FromArgb(240, 240, 241), Tile = Color.FromArgb(252, 252, 252),
            Text = Color.FromArgb(26, 26, 26), SubText = Color.FromArgb(95, 95, 95), Disabled = Color.FromArgb(160, 160, 160),
            Separator = Color.FromArgb(224, 224, 224), Border = Color.FromArgb(208, 208, 208), Check = Color.FromArgb(0, 103, 192),
            Accent2 = Color.FromArgb(40, 130, 240)
        };
    }

    static class Theme
    {
        public const string System = "system", Light = "light", Dark = "dark";

        public static readonly Color On = Color.FromArgb(35, 165, 90);
        public static readonly Color Busy = Color.FromArgb(240, 178, 50);
        public static readonly Color Off = Color.FromArgb(128, 132, 142);
        public static readonly Color Bad = Color.FromArgb(218, 60, 60);

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

        // Значок программы для заголовка окна: без него WinForms ставит свой стандартный
        public static Icon AppIcon()
        {
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { return null; }
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

        // Windows 11: скруглённые углы и тень у выпадающего меню и окон без рамки (на Windows 10 просто ничего не произойдёт)
        public static void RoundCorners(Control c)
        {
            try
            {
                int round = 2;   // DWMWCP_ROUND
                DwmSetWindowAttribute(c.Handle, 33, ref round, sizeof(int));
            }
            catch { }
        }
    }

    // TextRenderer Windows не слушается масштаба Graphics. Эта обёртка с тем же именем подменяет его во всей программе:
    // переводит прямоугольник в точки экрана и увеличивает шрифт. Без масштаба работает как обычный.
    // Мерить всегда на 96 точках на дюйм: так размеры совпадают с раскладкой окна при любом масштабе
    static class TextRenderer
    {
        static readonly System.Collections.Generic.Dictionary<Tuple<Font, int>, Font> scaled = new System.Collections.Generic.Dictionary<Tuple<Font, int>, Font>();
        static Graphics measure;

        public static Font Scaled(Font font, float factor)
        {
            int key = (int)Math.Round(factor * 100);
            if (key == 100) return font;
            Font f;
            var k = Tuple.Create(font, key);
            if (!scaled.TryGetValue(k, out f)) scaled[k] = f = new Font(font.FontFamily, font.Size * key / 100f, font.Style, font.Unit);
            return f;
        }

        static Graphics Measure
        {
            get
            {
                if (measure == null) measure = Graphics.FromImage(new Bitmap(1, 1));
                return measure;
            }
        }

        static bool Plain(IDeviceContext dc, out Graphics g)
        {
            g = dc as Graphics;
            return g == null || g.Transform.IsIdentity;
        }

        // Прямоугольник в точках экрана и во сколько раз увеличить шрифт (часть масштаба Windows уже даёт сама, через DPI)
        static Rectangle Device(Graphics g, Rectangle r, out float fontFactor)
        {
            using (var m = g.Transform)
            {
                var p = new[] { new PointF(r.Left, r.Top), new PointF(r.Right, r.Bottom) };
                m.TransformPoints(p);
                fontFactor = m.Elements[0] * 96f / g.DpiX;
                return Rectangle.FromLTRB((int)Math.Round(p[0].X), (int)Math.Round(p[0].Y), (int)Math.Round(p[1].X), (int)Math.Round(p[1].Y));
            }
        }

        public static void DrawText(IDeviceContext dc, string text, Font font, Rectangle bounds, Color fore, TextFormatFlags flags)
        {
            Graphics g;
            if (Plain(dc, out g)) { System.Windows.Forms.TextRenderer.DrawText(dc, text, font, bounds, fore, flags); return; }
            float k;
            var r = Device(g, bounds, out k);
            var state = g.Save();
            g.ResetTransform();
            System.Windows.Forms.TextRenderer.DrawText(g, text, Scaled(font, k), r, fore, flags);
            g.Restore(state);
        }

        public static void DrawText(IDeviceContext dc, string text, Font font, Rectangle bounds, Color fore, Color back, TextFormatFlags flags)
        {
            Graphics g;
            if (Plain(dc, out g)) { System.Windows.Forms.TextRenderer.DrawText(dc, text, font, bounds, fore, back, flags); return; }
            float k;
            var r = Device(g, bounds, out k);
            var state = g.Save();
            g.ResetTransform();
            System.Windows.Forms.TextRenderer.DrawText(g, text, Scaled(font, k), r, fore, back, flags);
            g.Restore(state);
        }

        public static void DrawText(IDeviceContext dc, string text, Font font, Point at, Color fore)
        {
            DrawText(dc, text, font, at, fore, TextFormatFlags.Default);
        }

        public static void DrawText(IDeviceContext dc, string text, Font font, Point at, Color fore, TextFormatFlags flags)
        {
            Graphics g;
            if (Plain(dc, out g)) { System.Windows.Forms.TextRenderer.DrawText(dc, text, font, at, fore, flags); return; }
            DrawText(dc, text, font, new Rectangle(at, MeasureText(text, font, Size.Empty, flags)), fore, flags);
        }

        public static void DrawText(IDeviceContext dc, string text, Font font, Rectangle bounds, Color fore)
        {
            DrawText(dc, text, font, bounds, fore, TextFormatFlags.Default);
        }

        public static Size MeasureText(string text, Font font) { return System.Windows.Forms.TextRenderer.MeasureText(Measure, text, font); }
        public static Size MeasureText(string text, Font font, Size proposed) { return System.Windows.Forms.TextRenderer.MeasureText(Measure, text, font, proposed); }
        public static Size MeasureText(string text, Font font, Size proposed, TextFormatFlags flags) { return System.Windows.Forms.TextRenderer.MeasureText(Measure, text, font, proposed, flags); }
        public static Size MeasureText(IDeviceContext dc, string text, Font font) { return MeasureText(text, font); }
        public static Size MeasureText(IDeviceContext dc, string text, Font font, Size proposed) { return MeasureText(text, font, proposed); }
        public static Size MeasureText(IDeviceContext dc, string text, Font font, Size proposed, TextFormatFlags flags) { return MeasureText(text, font, proposed, flags); }
    }

    // Пометки пунктов меню через Tag
    static class ItemKind
    {
        public const string Status = "status";   // первая строка: цветная точка, статус, справа конфиг (после \t)
        public const string Health = "health";   // значки YouTube, Discord, "Мои сайты" с результатом: части разделены \t
        public const string Info = "info";       // серые строки под статусом
        public const string Stats = "stats";     // серая строка с колонками: части текста разделены \t
        public const string Toggle = "toggle";    // в окне с разделами показывается переключателем
        public const string PowerOff = "power-off", PowerOn = "power-on";   // красная "Выключить", зелёная "Включить"
    }

    // Строка верхнего блока. Значение хранится отдельно от Text: когда меняется Text, WinForms пересчитывает ширину пункта
    // и двигает открытое подменю вслед за ней. Пока Text постоянный, меняются только нарисованные цифры
    class InfoItem : ToolStripMenuItem
    {
        public string Value = "";
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
        public const int Dot = 8;               // точка статуса
        public const int ItemV = 8;             // воздух над и под текстом пункта
        public const int MenuV = 7;             // воздух сверху и снизу меню

        public static readonly Font Font = new Font("Segoe UI", 9.75f);
        public static readonly Font Bold = new Font("Segoe UI Semibold", 9.75f);
        public static readonly Font Title = new Font("Segoe UI Semibold", 11f);
        public static readonly Font Small = new Font("Segoe UI", 8.75f);
        public const int IconWidth = 24;        // место под иконку слева от текста

        // Какие пункты с иконкой. Часть подменю пересоздаётся при каждом открытии, поэтому старые пункты таблица не держит
        public static readonly ConditionalWeakTable<ToolStripItem, string> Icons = new ConditionalWeakTable<ToolStripItem, string>();

        public static ToolStripMenuItem Icon(ToolStripMenuItem item, string icon)
        {
            Icons.Add(item, icon);
            return item;
        }

        // Значок и пояснение второй строкой в карточке окна с разделами
        public static ToolStripMenuItem Icon(ToolStripMenuItem item, string icon, string note)
        {
            item.ToolTipText = note;
            return Icon(item, icon);
        }

        // Отступы нужны WinForms только для расчёта размеров. Пункт рисуется в буфер своего размера, и всё, что за ним, пропадает.
        // Ширина пункта зависит от текста, поэтому справа отступ больше любого меню: пункт всегда шире окна, а видимую ширину берём от окна
        const int Stretch = 400;
        static readonly Padding ItemPad = new Padding(Left, ItemV, Stretch, ItemV);

        public static ToolStripMenuItem Item(string text, EventHandler click)
        {
            var item = click != null ? new ToolStripMenuItem(text, null, click) : new ToolStripMenuItem(text);
            item.Padding = ItemPad;
            return item;
        }

        // Статус (жирный, с цветной точкой справа) и серые строки под ним: не нажимаются
        public static InfoItem Info(string text, string kind)
        {
            var item = new InfoItem { Text = "-", Value = text, Enabled = false, Tag = kind };
            if (kind == ItemKind.Status) item.Font = Title;
            item.Margin = Padding.Empty;
            item.Padding = kind == ItemKind.Status ? new Padding(Left, 12, Stretch, 7)
                : kind == ItemKind.Health ? new Padding(Left, 7, Stretch, 7) : new Padding(Left, 4, Stretch, 11);
            return item;
        }

        // Постоянная ширина главного меню: строки статуса всё время меняются, и без этого меню прыгает.
        // Длинный текст обрезается многоточием (ThemedRenderer рисует с EndEllipsis)
        public const int MenuWidth = 360;

        // Постоянная ширина подменю: подобрана под самый длинный текст, с запасом на отступы (Left * 2 + Mark)
        public const int BehaviorWidth = 340, ThemeWidth = 190, GameWidth = 160, RepairWidth = 300, ManualWidth = 270, SettingsWidth = 390, GamesWidth = 400;

        public static void FixWidth(ToolStripDropDown dd, int width)
        {
            dd.MinimumSize = new Size(width, 0);
            dd.MaximumSize = new Size(width, 4000);
        }

        // caption - подпись группы в окне с разделами
        public static ToolStripSeparator Sep(string caption = null)
        {
            return new ToolStripSeparator { Margin = new Padding(0, 5, 0, 5), Tag = caption };
        }

        public static void Menu(ToolStripDropDownMenu dd)
        {
            dd.ShowImageMargin = false;
            dd.ShowCheckMargin = false;
            dd.Font = Font;
            dd.Padding = new Padding(0, MenuV, 0, MenuV);
            dd.HandleCreated += (s, e) => Theme.RoundCorners(dd);
        }

        // Подменю настраиваем один раз при создании: если менять размеры перед открытием,
        // Windows уже посчитала место, и подменю съезжает. После открытия придвигаем вплотную к родителю
        public static void SubMenu(ToolStripMenuItem item, int width)
        {
            var dd = (ToolStripDropDownMenu)item.DropDown;
            Menu(dd);
            FixWidth(dd, width);
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
            if (x < screen.Left || x + dd.Width > screen.Right) x = dd.Left;

            // По высоте: первый пункт подменю напротив своего пункта. Длинное подменю поднимаем, чтобы снизу оно не торчало из-под меню
            int y = parent.PointToScreen(item.Bounds.Location).Y - MenuV;
            if (y + dd.Height > pb.Bottom) y = pb.Bottom - dd.Height;
            if (y + dd.Height > screen.Bottom) y = screen.Bottom - dd.Height;
            if (y < screen.Top) y = screen.Top;

            // dd.Left у открытого подменю WinForms игнорирует - двигаем само окно
            if (x != dd.Left || y != dd.Top)
                SetWindowPos(dd.Handle, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010);   // NOSIZE | NOZORDER | NOACTIVATE
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
            var kind = e.Item.Tag as string;
            if (kind == ItemKind.Status || kind == ItemKind.Health || kind == ItemKind.Stats)
            {
                DrawCard(g, e.Item, kind);
                return;
            }
            if (e.Item.Selected && e.Item.Enabled)
            {
                var r = new Rectangle(MenuKit.Inset, 1, W(e.Item) - MenuKit.Inset * 2, e.Item.Height - 2);
                using (var path = RoundRect(r, 6))
                using (var b = new SolidBrush(P.Hover)) g.FillPath(b, path);
                // Цветная полоска слева у выбранного пункта
                using (var path = RoundRect(new Rectangle(MenuKit.Inset + 3, e.Item.Height / 2 - 7, 3, 14), 1))
                using (var b = new SolidBrush(P.Check)) g.FillPath(b, path);
            }
        }

        // Статус, значки и скорость - одна карточка: у строк общий фон, скруглённый сверху и снизу.
        // Лишнее за границей строки обрезаем, поэтому прямоугольник можно рисовать с запасом
        static void DrawCard(Graphics g, ToolStripItem item, string kind)
        {
            const int extra = 14;
            int x = MenuKit.Inset, w = W(item) - x * 2, h = item.Height;
            bool alone = kind == ItemKind.Status && !HasDetails(item);
            var r = kind == ItemKind.Status ? new Rectangle(x, 0, w, alone ? h : h + extra)
                : kind == ItemKind.Health ? new Rectangle(x, -extra, w, h + extra * 2)
                : new Rectangle(x, -extra, w, h + extra);
            g.SetClip(new Rectangle(0, 0, W(item), h));
            using (var path = RoundRect(r, 8))
            using (var b = new SolidBrush(P.Card)) g.FillPath(b, path);
            g.ResetClip();
        }

        static bool HasDetails(ToolStripItem item)
        {
            foreach (ToolStripItem other in item.Owner.Items)
                if ((other.Tag as string) == ItemKind.Stats) return other.Available;
            return false;
        }

        // Справа: стрелка подменю или галочка - на одной вертикали, с тем же отступом, что и текст слева.
        // WinForms обрезает рисование пункта по области без отступов, а метка стоит в правом отступе - снимаем обрезку
        void DrawMark(Graphics g, ToolStripMenuItem mi)
        {
            g.ResetClip();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int cx = W(mi) - MenuKit.Left - MenuKit.Mark / 2, cy = mi.Height / 2;
            if (mi.HasDropDownItems)
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
            // В главном меню группы разделяет воздух, линии только в подменю
            if (e.Item.Owner is ContextMenuStrip) return;
            int y = e.Item.Height / 2;
            using (var p = new Pen(P.Separator)) e.Graphics.DrawLine(p, MenuKit.Left, y, W(e.Item) - MenuKit.Left, y);
        }

        // Текст рисуем сами: всегда с линии Left и строго по центру строки (WinForms ставит его как придётся)
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            // Второй вызов с пустым текстом - место под горячую клавишу. Он закрасил бы уже нарисованную строку
            if (e.Text.Length == 0) return;
            var kind = e.Item.Tag as string;
            var info = e.Item as InfoItem;
            var text = info != null ? info.Value : e.Text;
            var font = kind == ItemKind.Status ? MenuKit.Title : kind == ItemKind.Stats ? MenuKit.Small : MenuKit.Font;
            var color = kind == ItemKind.PowerOff ? Theme.Bad : kind == ItemKind.PowerOn ? Theme.On
                : kind == ItemKind.Status ? P.Text : kind == ItemKind.Info || kind == ItemKind.Stats ? P.SubText : e.Item.Enabled ? P.Text : P.Disabled;
            var pad = e.Item.Padding;
            var r = new Rectangle(MenuKit.Left, pad.Top, W(e.Item) - MenuKit.Left * 2 - MenuKit.Mark, e.Item.Height - pad.Top - pad.Bottom);
            var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
            string icon;
            if (MenuKit.Icons.TryGetValue(e.Item, out icon))
            {
                e.Graphics.ResetClip();
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                bool power = kind == ItemKind.PowerOff || kind == ItemKind.PowerOn;
                DrawIcon(e.Graphics, icon, MenuKit.Left, e.Item.Height / 2, power ? color : !e.Item.Enabled ? P.Disabled : e.Item.Selected ? P.Text : P.SubText);
                r = new Rectangle(r.Left + MenuKit.IconWidth, r.Top, r.Width - MenuKit.IconWidth, r.Height);
            }
            if (kind == ItemKind.Status)
            {
                DrawStatus(e.Graphics, text, font, r, color, flags);
                return;
            }
            if (kind == ItemKind.Health)
            {
                DrawHealth(e.Graphics, text, r);
                return;
            }
            if (kind == ItemKind.Stats)
            {
                // Колонки стоят на месте, что бы ни показывали цифры
                // Те же колонки, что у плиток: цифры стоят под значками
                var parts = text.Split('\t');
                e.Graphics.ResetClip();
                int col = Column(r);
                for (int i = 0; i < parts.Length; i++)
                    TextRenderer.DrawText(e.Graphics, parts[i], font, new Rectangle(r.Left + col * i + 7, r.Top, col - 8, r.Height), color, flags);
            }
            else
                TextRenderer.DrawText(e.Graphics, text, font, r, color, flags);
            var mi = e.Item as ToolStripMenuItem;
            if (mi != null) DrawMark(e.Graphics, mi);
        }

        static int Column(Rectangle r) { return (r.Width + MenuKit.Mark) / 3; }

        // Точка с ореолом, статус и справа конфиг в капсуле
        void DrawStatus(Graphics g, string text, Font font, Rectangle r, Color color, TextFormatFlags flags)
        {
            g.ResetClip();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int d = MenuKit.Dot, dx = r.Left + 3, cy = r.Top + r.Height / 2;
            using (var b = new SolidBrush(Color.FromArgb(60, StatusColor))) g.FillEllipse(b, dx - 3, cy - d / 2 - 3, d + 6, d + 6);
            using (var b = new SolidBrush(StatusColor)) g.FillEllipse(b, dx, cy - d / 2, d, d);

            int textLeft = dx + d + 9, right = r.Right + MenuKit.Mark - 6;
            var parts = text.Split('\t');
            int chip = 0;
            if (parts.Length > 1)
            {
                chip = Math.Min(TextRenderer.MeasureText(parts[1], MenuKit.Small).Width + 12, (right - textLeft) / 2);
                var box = new Rectangle(right - chip, cy - 11, chip, 22);
                using (var path = RoundRect(box, 7))
                using (var b = new SolidBrush(P.Tile)) g.FillPath(b, path);
                TextRenderer.DrawText(g, parts[1], MenuKit.Small, new Rectangle(box.Left + 5, box.Top, box.Width - 8, box.Height), P.SubText, flags);
                chip += 6;
            }
            TextRenderer.DrawText(g, parts[0], font, new Rectangle(textLeft, r.Top, right - textLeft - chip, r.Height), color, flags);
        }

        // Плитки: YouTube, Discord, игры, мои сайты. Части идут всегда в этом порядке, пустая часть - плитки нет.
        // Плитки равной ширины, а значок с результатом стоит по центру плитки: ✓, ✗, ⋯ или "3/4"
        public static void DrawHealth(Graphics g, string text, Rectangle r)
        {
            g.ResetClip();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            const int gap = 6, icon = 18;
            var parts = text.Split('\t');
            int count = 0;
            foreach (var part in parts) if (part.Length > 0) count++;
            if (count == 0) return;
            int full = r.Width + MenuKit.Mark, tile = (full - gap * (count - 1)) / count, cy = r.Top + r.Height / 2, n = 0;
            int space = Math.Max(11, tile / 7);   // в широкой плитке значок и результат стоят свободнее
            for (int i = 0; i < parts.Length && i < 4; i++)
            {
                if (parts[i].Length == 0) continue;
                int left = r.Left + (tile + gap) * n++;
                using (var path = RoundRect(new Rectangle(left, r.Top - 3, tile, r.Height + 6), 7))
                using (var b = new SolidBrush(P.Tile)) g.FillPath(b, path);

                int width = ResultWidth(parts[i]);
                int x = left + (tile - icon - space - width) / 2;
                if (i == 0) DrawYouTube(g, x, cy);
                else if (i == 1) DrawDiscord(g, x, cy);
                else if (i == 2) DrawGamepad(g, x, cy);
                else DrawGlobe(g, x, cy);
                DrawResult(g, parts[i], x + icon + space, cy, width + 4, r);
            }
        }

        static int ResultWidth(string result)
        {
            if (result == "✓" || result == "✗") return 10;
            if (result == "⋯") return 13;
            return TextRenderer.MeasureText(result, MenuKit.Font, Size.Empty, TextFormatFlags.NoPadding).Width;
        }

        // Линейные иконки пунктов меню: 16 на 16, слева от текста
        public static void DrawIcon(Graphics g, string icon, int x, int cy, Color color)
        {
            using (var p = new Pen(color, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            using (var b = new SolidBrush(color))
            {
                switch (icon)
                {
                    case "games":
                        using (var path = RoundRect(new Rectangle(x, cy - 5, 16, 10), 4)) g.DrawPath(p, path);
                        g.DrawLine(p, x + 3, cy, x + 7, cy);
                        g.DrawLine(p, x + 5, cy - 2, x + 5, cy + 2);
                        g.FillEllipse(b, x + 10, cy - 2, 3, 3);
                        break;
                    case "sites":
                        g.DrawEllipse(p, x + 1, cy - 7, 14, 14);
                        g.DrawEllipse(p, x + 5, cy - 7, 6, 14);
                        g.DrawLine(p, x + 1, cy, x + 15, cy);
                        break;
                    case "connection":
                        g.DrawArc(p, x, cy - 5, 16, 16, 225, 90);
                        g.DrawArc(p, x + 3, cy - 1, 10, 10, 225, 90);
                        g.FillEllipse(b, x + 6.5f, cy + 3.5f, 3, 3);
                        break;
                    case "settings":
                        g.DrawEllipse(p, x + 6, cy - 2, 4, 4);
                        g.DrawEllipse(p, x + 3, cy - 5, 10, 10);
                        for (int i = 0; i < 8; i++)
                        {
                            double a = i * Math.PI / 4;
                            g.DrawLine(p, x + 8 + (float)(Math.Cos(a) * 5), cy + (float)(Math.Sin(a) * 5), x + 8 + (float)(Math.Cos(a) * 7), cy + (float)(Math.Sin(a) * 7));
                        }
                        break;
                    case "hints":
                        g.DrawEllipse(p, x + 1, cy - 7, 14, 14);
                        g.DrawArc(p, x + 5.5f, cy - 4.5f, 5, 5, 180, 240);
                        g.DrawLine(p, x + 8, cy + 0.5f, x + 8, cy + 1.5f);
                        g.FillEllipse(b, x + 7, cy + 3, 2, 2);
                        break;
                    case "power":
                        g.DrawArc(p, x + 2, cy - 6, 12, 12, -55, 290);
                        g.DrawLine(p, x + 8, cy - 7, x + 8, cy - 1);
                        break;
                    case "home":
                        g.DrawLines(p, new[] { new Point(x + 1, cy), new Point(x + 8, cy - 6), new Point(x + 15, cy) });
                        g.DrawLines(p, new[] { new Point(x + 3, cy - 1), new Point(x + 3, cy + 6), new Point(x + 13, cy + 6), new Point(x + 13, cy - 1) });
                        break;
                    case "fast":
                        g.DrawLines(p, new[] { new Point(x + 9, cy - 7), new Point(x + 4, cy + 1), new Point(x + 8, cy + 1), new Point(x + 7, cy + 7), new Point(x + 12, cy - 1), new Point(x + 8, cy - 1), new Point(x + 9, cy - 7) });
                        break;
                    case "ping":
                        g.DrawArc(p, x + 1, cy - 6, 14, 14, 180, 180);
                        g.DrawLine(p, x + 1, cy + 1, x + 15, cy + 1);
                        g.DrawLine(p, x + 8, cy + 1, x + 11, cy - 3);
                        break;
                    case "list":
                        for (int i = -1; i <= 1; i++)
                        {
                            g.FillEllipse(b, x + 1, cy + i * 5 - 1, 3, 3);
                            g.DrawLine(p, x + 6, cy + i * 5, x + 15, cy + i * 5);
                        }
                        break;
                    case "config":
                        g.DrawLine(p, x + 1, cy - 4, x + 15, cy - 4);
                        g.DrawLine(p, x + 1, cy + 4, x + 15, cy + 4);
                        g.FillEllipse(b, x + 9, cy - 6, 4, 4);
                        g.FillEllipse(b, x + 3, cy + 2, 4, 4);
                        break;
                    case "dns":
                        g.DrawRectangle(p, x + 1, cy - 6, 14, 5);
                        g.DrawRectangle(p, x + 1, cy + 1, 14, 5);
                        g.FillEllipse(b, x + 3, cy - 5, 3, 3);
                        g.FillEllipse(b, x + 3, cy + 2, 3, 3);
                        break;
                    case "undo":
                        g.DrawArc(p, x + 3, cy - 4, 11, 10, -90, 240);
                        g.DrawLine(p, x + 8, cy - 4, x + 3, cy - 4);
                        g.DrawLines(p, new[] { new Point(x + 6, cy - 7), new Point(x + 3, cy - 4), new Point(x + 6, cy - 1) });
                        break;
                    case "report":
                        g.DrawRectangle(p, x + 3, cy - 7, 10, 14);
                        g.DrawLine(p, x + 5, cy - 3, x + 11, cy - 3);
                        g.DrawLine(p, x + 5, cy, x + 11, cy);
                        g.DrawLine(p, x + 5, cy + 3, x + 9, cy + 3);
                        break;
                    case "monitor":
                        g.DrawRectangle(p, x + 1, cy - 6, 14, 9);
                        g.DrawLine(p, x + 8, cy + 3, x + 8, cy + 6);
                        g.DrawLine(p, x + 5, cy + 6, x + 11, cy + 6);
                        break;
                    case "sun":
                        g.DrawEllipse(p, x + 5, cy - 3, 6, 6);
                        for (int i = 0; i < 8; i++)
                        {
                            double a = i * Math.PI / 4 + Math.PI / 8;
                            g.DrawLine(p, x + 8 + (float)(Math.Cos(a) * 5.5), cy + (float)(Math.Sin(a) * 5.5), x + 8 + (float)(Math.Cos(a) * 7), cy + (float)(Math.Sin(a) * 7));
                        }
                        break;
                    case "moon":
                        g.DrawArc(p, x + 2, cy - 6, 12, 12, 40, 260);
                        g.DrawArc(p, x + 7, cy - 7, 9, 10, 110, 120);
                        break;
                    case "theme":
                        g.DrawEllipse(p, x + 1, cy - 7, 14, 14);
                        g.FillPie(b, x + 1, cy - 7, 14, 14, -90, 180);
                        break;
                    case "shield":
                        g.DrawLines(p, new[] { new Point(x + 8, cy - 7), new Point(x + 14, cy - 5), new Point(x + 14, cy), new Point(x + 8, cy + 7), new Point(x + 2, cy), new Point(x + 2, cy - 5), new Point(x + 8, cy - 7) });
                        break;
                    case "play":
                        g.DrawLines(p, new[] { new Point(x + 4, cy - 6), new Point(x + 13, cy), new Point(x + 4, cy + 6), new Point(x + 4, cy - 6) });
                        break;
                    case "refresh":
                        g.DrawArc(p, x + 2, cy - 6, 12, 12, -60, 300);
                        g.DrawLines(p, new[] { new Point(x + 8, cy - 7), new Point(x + 11, cy - 5), new Point(x + 9, cy - 2) });
                        break;
                    case "bell":
                        g.DrawArc(p, x + 3, cy - 6, 10, 10, 180, 180);
                        g.DrawLines(p, new[] { new Point(x + 3, cy - 1), new Point(x + 3, cy + 3), new Point(x + 1, cy + 4), new Point(x + 15, cy + 4), new Point(x + 13, cy + 3), new Point(x + 13, cy - 1) });
                        g.FillEllipse(b, x + 6, cy + 5, 4, 3);
                        break;
                    case "save":
                        g.DrawLine(p, x + 8, cy - 7, x + 8, cy + 2);
                        g.DrawLines(p, new[] { new Point(x + 4, cy - 2), new Point(x + 8, cy + 2), new Point(x + 12, cy - 2) });
                        g.DrawLines(p, new[] { new Point(x + 1, cy + 3), new Point(x + 1, cy + 7), new Point(x + 15, cy + 7), new Point(x + 15, cy + 3) });
                        break;
                    case "load":
                        g.DrawLine(p, x + 8, cy + 2, x + 8, cy - 7);
                        g.DrawLines(p, new[] { new Point(x + 4, cy - 3), new Point(x + 8, cy - 7), new Point(x + 12, cy - 3) });
                        g.DrawLines(p, new[] { new Point(x + 1, cy + 3), new Point(x + 1, cy + 7), new Point(x + 15, cy + 7), new Point(x + 15, cy + 3) });
                        break;
                    case "folder":
                        g.DrawLines(p, new[] { new Point(x + 1, cy + 6), new Point(x + 1, cy - 6), new Point(x + 6, cy - 6), new Point(x + 8, cy - 3), new Point(x + 15, cy - 3), new Point(x + 15, cy + 6), new Point(x + 1, cy + 6) });
                        break;
                    case "trash":
                        g.DrawLine(p, x + 2, cy - 4, x + 14, cy - 4);
                        g.DrawLine(p, x + 6, cy - 7, x + 10, cy - 7);
                        g.DrawLines(p, new[] { new Point(x + 4, cy - 4), new Point(x + 5, cy + 7), new Point(x + 11, cy + 7), new Point(x + 12, cy - 4) });
                        break;
                    case "plus":
                        g.DrawEllipse(p, x + 1, cy - 7, 14, 14);
                        g.DrawLine(p, x + 8, cy - 3, x + 8, cy + 3);
                        g.DrawLine(p, x + 5, cy, x + 11, cy);
                        break;
                    default:   // выход: дверь и стрелка
                        g.DrawLines(p, new[] { new Point(x + 7, cy - 6), new Point(x + 2, cy - 6), new Point(x + 2, cy + 6), new Point(x + 7, cy + 6) });
                        g.DrawLine(p, x + 6, cy, x + 15, cy);
                        g.DrawLines(p, new[] { new Point(x + 12, cy - 3), new Point(x + 15, cy), new Point(x + 12, cy + 3) });
                        break;
                }
            }
        }

        public static void DrawResult(Graphics g, string result, int x, int cy, int width, Rectangle r)
        {
            if (result == "✓")
            {
                using (var p = new Pen(Theme.On, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    g.DrawLines(p, new[] { new Point(x, cy), new Point(x + 3, cy + 3), new Point(x + 10, cy - 4) });
            }
            else if (result == "✗")
            {
                using (var p = new Pen(Theme.Bad, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawLine(p, x + 1, cy - 4, x + 9, cy + 4);
                    g.DrawLine(p, x + 9, cy - 4, x + 1, cy + 4);
                }
            }
            else if (result == "⋯")
            {
                using (var b = new SolidBrush(P.SubText))
                    for (int i = 0; i < 3; i++) g.FillEllipse(b, x + i * 5, cy - 1, 3, 3);
            }
            else
            {
                // Счётчик "открылось/всего"
                var n = result.Split('/');
                var color = n.Length == 2 && n[0] == n[1] ? Theme.On : n[0] == "0" ? Theme.Bad : Theme.Busy;
                TextRenderer.DrawText(g, result, MenuKit.Font, new Rectangle(x, r.Top, width, r.Height), color,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            }
        }

        // Значок сервиса по номеру плитки: YouTube, Discord, игры, мои сайты
        public static void DrawService(Graphics g, int i, int x, int cy)
        {
            if (i == 0) DrawYouTube(g, x, cy);
            else if (i == 1) DrawDiscord(g, x, cy);
            else if (i == 2) DrawGamepad(g, x, cy);
            else DrawGlobe(g, x, cy);
        }

        static void DrawYouTube(Graphics g, int x, int cy)
        {
            using (var path = RoundRect(new Rectangle(x, cy - 7, 18, 14), 4))
            using (var b = new SolidBrush(Color.FromArgb(230, 33, 23))) g.FillPath(b, path);
            g.FillPolygon(Brushes.White, new[] { new PointF(x + 7, cy - 3.5f), new PointF(x + 7, cy + 3.5f), new PointF(x + 12.5f, cy) });
        }

        static void DrawDiscord(Graphics g, int x, int cy)
        {
            var color = Color.FromArgb(88, 101, 242);
            using (var path = RoundRect(new Rectangle(x, cy - 7, 18, 14), 4))
            using (var b = new SolidBrush(color)) g.FillPath(b, path);
            using (var path = RoundRect(new Rectangle(x + 3, cy - 4, 12, 8), 3)) g.FillPath(Brushes.White, path);
            using (var b = new SolidBrush(color))
            {
                g.FillEllipse(b, x + 5, cy - 2, 3, 3);
                g.FillEllipse(b, x + 10, cy - 2, 3, 3);
            }
        }

        static void DrawGlobe(Graphics g, int x, int cy)
        {
            using (var path = RoundRect(new Rectangle(x, cy - 7, 18, 14), 4))
            using (var b = new SolidBrush(Color.FromArgb(14, 136, 232))) g.FillPath(b, path);
            using (var p = new Pen(Color.White, 1.1f))
            {
                g.DrawEllipse(p, x + 4, cy - 5, 10, 10);
                g.DrawEllipse(p, x + 7, cy - 5, 4, 10);
                g.DrawLine(p, x + 4, cy, x + 14, cy);
            }
        }

        // Джойстик: шар на палке и основание. Геймпад на маленьком значке слишком похож на значок Discord
        static void DrawGamepad(Graphics g, int x, int cy)
        {
            using (var path = RoundRect(new Rectangle(x, cy - 7, 18, 14), 4))
            using (var b = new SolidBrush(Color.FromArgb(240, 136, 30))) g.FillPath(b, path);
            using (var p = new Pen(Color.White, 1.6f)) g.DrawLine(p, x + 9, cy - 2, x + 9, cy + 3);
            g.FillEllipse(Brushes.White, x + 6.5f, cy - 5.5f, 5, 5);
            using (var path = RoundRect(new Rectangle(x + 4, cy + 2, 10, 3), 1)) g.FillPath(Brushes.White, path);
        }

        // Спидометр: стрелка от 0 до 1
        public static void DrawSpeedometer(Graphics g, double level, int x, int cy)
        {
            using (var path = RoundRect(new Rectangle(x, cy - 7, 18, 14), 4))
            using (var b = new SolidBrush(Color.FromArgb(18, 165, 148))) g.FillPath(b, path);
            using (var p = new Pen(Color.FromArgb(120, Color.White), 1.1f)) g.DrawArc(p, x + 3.5f, cy - 4.5f, 11, 11, 180, 180);
            using (var p = new Pen(Color.White, 1.1f)) g.DrawArc(p, x + 3.5f, cy - 4.5f, 11, 11, 180, (float)(180 * level));
            double angle = Math.PI * (1 + level);
            using (var p = new Pen(Color.White, 1.1f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(p, x + 9, cy + 1, x + 9 + (float)(Math.Cos(angle) * 4), cy + 1 + (float)(Math.Sin(angle) * 4));
            g.FillEllipse(Brushes.White, x + 8, cy, 2, 2);
        }

        // Полоски связи: сколько горит, от 0 до 4
        public static void DrawSignal(Graphics g, int bars, int x, int cy)
        {
            using (var path = RoundRect(new Rectangle(x, cy - 7, 18, 14), 4))
            using (var b = new SolidBrush(Color.FromArgb(142, 84, 233))) g.FillPath(b, path);
            using (var dim = new SolidBrush(Color.FromArgb(90, Color.White)))
                for (int i = 0; i < 4; i++)
                {
                    int height = 3 + i * 2;
                    g.FillRectangle(i < bars ? Brushes.White : dim, x + 3.75f + i * 2.75f, cy + 4.5f - height, 1.75f, height);
                }
        }

        // Часы: сколько минут показать. Часовая стрелка делает круг за 12 часов, минутная за час
        public static void DrawClock(Graphics g, double minutes, int x, int cy)
        {
            using (var path = RoundRect(new Rectangle(x, cy - 7, 18, 14), 4))
            using (var b = new SolidBrush(Color.FromArgb(222, 70, 128))) g.FillPath(b, path);
            double hourAngle = 2 * Math.PI * minutes / 720 - Math.PI / 2, minuteAngle = 2 * Math.PI * minutes / 60 - Math.PI / 2;
            using (var p = new Pen(Color.White, 1.1f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawEllipse(p, x + 3.5f, cy - 5.5f, 11, 11);
                g.DrawLine(p, x + 9, cy, x + 9 + (float)(Math.Cos(minuteAngle) * 4), cy + (float)(Math.Sin(minuteAngle) * 4));
                p.Width = 1.4f;
                g.DrawLine(p, x + 9, cy, x + 9 + (float)(Math.Cos(hourAngle) * 2.4), cy + (float)(Math.Sin(hourAngle) * 2.4));
            }
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            // Стрелку рисуем сами в OnRenderMenuItemBackground (здесь рисование обрезается по маленькой области)
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            // Галочку рисуем сами в OnRenderMenuItemBackground
        }

        // Видимая ширина пункта: WinForms меняет ширину пунктов вслед за текстом, поэтому считаем только от окна меню
        static int W(ToolStripItem item)
        {
            var o = item.Owner;
            return o == null ? item.Width : o.Width - 2 * item.Bounds.Left;
        }

        public static GraphicsPath RoundRect(Rectangle r, int radius)
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
