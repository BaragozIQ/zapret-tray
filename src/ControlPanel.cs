// Окно по правой кнопке на значке: слева разделы, справа карточки. Логики здесь нет: окно рисует готовые пункты меню
// из TrayContext.BuildMenu и нажимает их через PerformClick. Новый пункт меню появляется здесь сам
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ZapretTray
{
    // Страница со своим содержимым: подсказки, мои сайты. Прямоугольники кнопок страница запоминает сама при рисовании
    interface IPanelView
    {
        int Extra { get; }                                          // сколько шапка выше обычной
        void Activate(Control host, bool on);
        void DrawHeader(Graphics g, Rectangle r, Point mouse);
        int Draw(Graphics g, int x, int top, Point mouse);          // возвращает высоту содержимого
        bool HeaderHot(Point p);
        bool Hot(Point p);
        bool Click(Point p);                                        // true - содержимое сменилось, прокрутку в начало
    }

    class ControlPanel : Form
    {
        public const int CardWidth = 380;
        const int SideWidth = 190, Pad = 20, Gap = 12, CardHeight = 62, GroupGap = 14;
        const int PanelWidth = SideWidth + Pad * 2 + CardWidth * 2 + Gap, HeaderHeight = 62, PanelHeight = HeaderHeight + HintsView.Available + Pad;
        // Вся раскладка в точках при масштабе 100% (LW на LH). На экране окно больше в UiScale раз: OnPaint увеличивает рисунок, мышь пересчитывается обратно
        const int LW = PanelWidth, LH = PanelHeight;
        public static float UserScale = 1f;   // "Размер окна" в настройках
        public static readonly int[] ScaleStops = { 75, 90, 100, 115, 125, 150, 175, 200 };
        // Карточка "Размер окна" в настройках рисуется ползунком. Значение применяется, когда отпустил мышь
        public ToolStripMenuItem ScaleItem;
        public Action<int> ScaleChanged;
        bool scaleCapped;      // окно упёрлось в край экрана: крупнее уже не станет
        int scaleDrag = -1;    // тянут ползунок: какое деление сейчас под мышью
        public float UiScale = 1f;            // итог: масштаб Windows и размер окна, не больше экрана
        public float FontScale = 1f;          // какую часть масштаба шрифтам добавляем сами (остальное даёт DPI)

        [DllImport("user32.dll")]
        static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

        class DpiScope : IDisposable
        {
            readonly IntPtr old;
            public DpiScope() { try { old = SetThreadDpiAwarenessContext((IntPtr)(-2)); } catch { } }   // DPI_AWARENESS_CONTEXT_SYSTEM_AWARE
            public void Dispose() { try { if (old != IntPtr.Zero) SetThreadDpiAwarenessContext(old); } catch { } }
        }

        // Только это окно знает настоящий масштаб экрана и рисует чётко. Остальные окна программы Windows по-прежнему растягивает сама.
        // Окно и его поля ввода должны создаваться внутри: потому показ и смена страниц идут через Aware()
        public static IDisposable Aware() { return new DpiScope(); }

        public void ApplyScale()
        {
            float dpi;
            using (var g = CreateGraphics()) dpi = g.DpiX;
            var screen = Screen.FromPoint(Visible ? Location : Cursor.Position).WorkingArea;
            float s = dpi / 96f * UserScale;
            float fit = Math.Min(screen.Width * 0.96f / LW, screen.Height * 0.96f / LH);
            scaleCapped = s > fit + 0.001f;
            s = Math.Min(s, fit);
            UiScale = Math.Max(0.5f, s);
            FontScale = UiScale * 96f / dpi;
            // Size, а не ClientSize: у окна без рамки они равны, но ClientSize после создания окна прибавляет место под заголовок, которого нет
            Size = new Size((int)Math.Round(LW * UiScale), (int)Math.Round(LH * UiScale));
            SyncViews();
            Invalidate();
        }

        // Сменили "Размер окна", пока окно открыто
        public void Rescale()
        {
            using (Aware())
            {
                ApplyScale();
                var screen = Screen.FromControl(this).WorkingArea;
                Left = Math.Max(screen.Left, Math.Min(Left, screen.Right - Width));
                Top = Math.Max(screen.Top, Math.Min(Top, screen.Bottom - Height));
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyScale();
        }

        Point L(Point p) { return new Point((int)(p.X / UiScale), (int)(p.Y / UiScale)); }

        Rectangle Dev(Rectangle r)
        {
            return Rectangle.FromLTRB((int)Math.Floor(r.Left * UiScale) - 1, (int)Math.Floor(r.Top * UiScale) - 1, (int)Math.Ceiling(r.Right * UiScale) + 1, (int)Math.Ceiling(r.Bottom * UiScale) + 1);
        }

        // Логический прямоугольник в точки окна: для полей ввода, которые лежат поверх рисунка
        public Rectangle ToDevice(Rectangle r)
        {
            return new Rectangle((int)Math.Round(r.Left * UiScale), (int)Math.Round(r.Top * UiScale), (int)Math.Round(r.Width * UiScale), (int)Math.Round(r.Height * UiScale));
        }

        readonly InfoItem status, health, stats;
        readonly Func<Color> statusColor;
        readonly Func<string> pings;
        readonly ToolStripMenuItem[] sections;
        readonly ToolStripMenuItem toggle, exit;
        readonly ToolStripMenuItem[] shortcuts;
        readonly Action refresh;
        readonly Dictionary<ToolStripMenuItem, IPanelView> views;
        Point mouse;

        readonly Font titleFont = new Font("Segoe UI Semibold", 13f);
        readonly Font heroFont = HeroFont();
        readonly Font valueFont = new Font("Segoe UI Semibold", 11f);
        readonly Font numberFont = new Font("Segoe UI Semibold", 20f);
        readonly Font cardFont = new Font("Segoe UI Semibold", 10f);

        // Открытые подразделы. Пусто - главная страница
        readonly List<ToolStripMenuItem> path = new List<ToolStripMenuItem>();
        readonly List<KeyValuePair<Rectangle, ToolStripMenuItem>> cards = new List<KeyValuePair<Rectangle, ToolStripMenuItem>>();
        readonly Rectangle homeTab = new Rectangle(10, 66, SideWidth - 20, 42);
        object hover;
        int scroll, contentHeight;

        static Palette P { get { return Theme.Current; } }

        public ControlPanel(InfoItem status, InfoItem health, InfoItem stats, Func<Color> statusColor, Func<string> pings,
            ToolStripMenuItem[] sections, ToolStripMenuItem[] shortcuts, ToolStripMenuItem toggle, ToolStripMenuItem exit, Action refresh,
            Dictionary<ToolStripMenuItem, IPanelView> views)
        {
            this.status = status;
            this.health = health;
            this.stats = stats;
            this.statusColor = statusColor;
            this.pings = pings;
            this.sections = sections;
            this.shortcuts = shortcuts;
            this.toggle = toggle;
            this.exit = exit;
            this.refresh = refresh;
            this.views = views;

            FormBorderStyle = FormBorderStyle.None;
            // Обычное окно: остаётся открытым при переключении в игру, сворачивается на панель задач
            ShowInTaskbar = true;
            Text = AppInfo.Name;
            Icon = Theme.AppIcon();
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.None;
            Font = MenuKit.Font;
            BackColor = P.Back;
            ClientSize = new Size(PanelWidth, PanelHeight);
            KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Escape) return;
                if (path.Count > 1) Back(); else Close();
            };
        }

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int cmd);

        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        // Свернули любым способом, хоть кликом по панели задач: окно прячется к значку у часов
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState != FormWindowState.Minimized) return;
            Hide();
            WindowState = FormWindowState.Normal;
        }

        // Без этого стиля окно без рамки не сворачивается кликом по панели задач
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.Style |= 0x20000;   // WS_MINIMIZEBOX
                return cp;
            }
        }

        // Повторная правая кнопка на значке: показать уже открытое окно
        public void Restore()
        {
            // После сворачивания Windows оставляет окну крошечный размер: возвращаем свой, когда окно уже снова обычное
            bool wasHidden = !Visible;
            using (Aware())
            {
                if (!Visible) Show();
                // WindowState у спрятанного окна врёт: спрашиваем Windows, свёрнуто ли оно на самом деле
                if (IsIconic(Handle)) ShowWindow(Handle, 9);   // SW_RESTORE
                ApplyScale();
            }
            if (wasHidden) Theme.RoundCorners(this);
            SetForegroundWindow(Handle);
            Activate();
        }

        Rectangle CloseButton { get { return new Rectangle(LW - 42, 6, 36, 28); } }
        Rectangle MinimizeButton { get { return new Rectangle(LW - 80, 6, 36, 28); } }

        void DrawWindowButtons(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            bool overClose = Equals(hover, "close"), overMin = Equals(hover, "min");
            if (overMin) Box(g, MinimizeButton, P.Hover, 7);
            if (overClose) Box(g, CloseButton, Theme.Bad, 7);
            int cy = CloseButton.Top + CloseButton.Height / 2;
            using (var p = new Pen(overMin ? P.Text : P.SubText, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(p, MinimizeButton.Left + 12, cy, MinimizeButton.Right - 12, cy);
            using (var p = new Pen(overClose ? Color.White : P.SubText, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                int cx = CloseButton.Left + CloseButton.Width / 2;
                g.DrawLine(p, cx - 5, cy - 5, cx + 5, cy + 5);
                g.DrawLine(p, cx + 5, cy - 5, cx - 5, cy + 5);
            }
        }

        // Куда пользователь перетащил окно. Живёт, пока программа запущена
        static Point? lastPosition;

        // По центру экрана, где курсор, или там, куда окно перетащили
        public void ShowCentered()
        {
            // Координаты экрана тоже внутри: снаружи Windows отдаёт их в растянутом виде
            using (Aware())
            {
                CreateControl();
                var at = lastPosition ?? Point.Empty;
                var screen = Screen.FromPoint(lastPosition.HasValue ? at : Cursor.Position).WorkingArea;
                ApplyScale();
                if (!lastPosition.HasValue) at = new Point(screen.Left + (screen.Width - Width) / 2, screen.Top + (screen.Height - Height) / 2);
                Left = Math.Max(screen.Left, Math.Min(at.X, screen.Right - Width));
                Top = Math.Max(screen.Top, Math.Min(at.Y, screen.Bottom - Height));
                Show();
            }
            Theme.RoundCorners(this);
            SetForegroundWindow(Handle);
            Activate();
        }

        // Тащим за любое пустое место: Windows сама двигает окно, как за заголовок
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            var hit = HitTest(L(e.Location));
            if (hit != null && hit == ScaleItem && ScaleChanged != null)
            {
                scaleDrag = NearestStop(L(e.Location).X);
                Capture = true;
                Invalidate();
                return;
            }
            if (hit != null) return;
            ReleaseCapture();
            SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);   // WM_NCLBUTTONDOWN, HTCAPTION
        }

        protected override void OnMove(EventArgs e)
        {
            base.OnMove(e);
            if (Visible) lastPosition = Location;
        }

        // Для проверки без значка: какой раздел показать
        public void OpenSection(ToolStripMenuItem section)
        {
            path.Clear();
            if (section != null) path.Add(section);
            scroll = 0;
            SyncViews();
        }

        IPanelView CurrentView
        {
            get
            {
                IPanelView v;
                return path.Count > 0 && views.TryGetValue(path[0], out v) ? v : null;
            }
        }

        void SyncViews()
        {
            var current = CurrentView;
            using (Aware()) foreach (var v in views.Values) v.Activate(this, v == current);
        }

        Rectangle SectionTab(int i)
        {
            return new Rectangle(10, homeTab.Bottom + 6 + i * 48, SideWidth - 20, 42);
        }

        // Внизу колонки, под линией: включение, день и ночь, выход
        Rectangle ToggleTab { get { return new Rectangle(10, LH - 148, SideWidth - 20, 42); } }
        Rectangle ThemeTab { get { return new Rectangle(10, LH - 100, SideWidth - 20, 42); } }
        Rectangle ExitTab { get { return new Rectangle(10, LH - 52, SideWidth - 20, 42); } }

        public Action ToggleTheme;

        Rectangle BackButton { get { return new Rectangle(SideWidth + Pad, 14, 30, 30); } }

        Rectangle ContentArea
        {
            get
            {
                var view = CurrentView;
                int top = HeaderHeight + (view != null ? view.Extra : 0);
                return new Rectangle(SideWidth, top, LW - SideWidth, LH - top);
            }
        }

        // ---------- мышь ----------

        object HitTest(Point p)
        {
            if (CloseButton.Contains(p)) return "close";
            if (MinimizeButton.Contains(p)) return "min";
            if (ToggleTheme != null && ThemeTab.Contains(p)) return "theme";
            if (path.Count == 0 && health.Available && gameHelp.Contains(p)) return "help";
            if (path.Count == 0 && health.Available && RecheckTile != null)
            {
                if (statusRect.Contains(p)) return "status";
                for (int i = 0; i < tileRects.Length; i++)
                    if (tileRects[i].Contains(p) && TileClickable(i)) return "tile" + i;
            }
            if (homeTab.Contains(p)) return homeTab;
            if (ToggleTab.Contains(p)) return toggle;
            if (ExitTab.Contains(p)) return exit;
            for (int i = 0; i < sections.Length; i++)
                if (SectionTab(i).Contains(p)) return sections[i];
            if (path.Count > 1 && BackButton.Contains(p)) return "back";
            var view = CurrentView;
            if (view != null)
            {
                if (view.HeaderHot(p) || (ContentArea.Contains(p) && view.Hot(p))) return "view";
                return null;
            }
            if (ContentArea.Contains(p))
                foreach (var c in cards)
                    if (c.Key.Contains(p.X, p.Y + scroll)) return c.Value;
            return null;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (scaleDrag < 0) return;
            int value = ScaleStops[scaleDrag];
            scaleDrag = -1;
            Capture = false;
            Invalidate();
            if (value != CurrentStopValue()) ScaleChanged(value);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            mouse = L(e.Location);
            if (scaleDrag >= 0)
            {
                int stop = NearestStop(mouse.X);
                if (stop != scaleDrag) { scaleDrag = stop; Invalidate(); }
                return;
            }
            var hit = HitTest(L(e.Location));
            // Над плиткой с графиком перерисовываем и без смены наведения: подпись едет за мышью
            if (Equals(hit, hover) && CurrentView == null && !(hit is string && ((string)hit).StartsWith("tile"))) return;
            hover = hit;
            var item = hit as ToolStripMenuItem;
            Cursor = hit != null && (item == null || item.Enabled) ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hover = null;
            Cursor = Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            scroll = Math.Max(0, Math.Min(MaxScroll, scroll - e.Delta / 2));
            Invalidate();
        }

        int MaxScroll { get { return Math.Max(0, contentHeight - ContentArea.Height); } }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            var hit = HitTest(L(e.Location));
            if (hit == null) return;
            if (hit is Rectangle) OpenSection(null);
            else if (hit as string == "close") { Close(); return; }
            else if (hit as string == "min") Hide();
            else if (hit as string == "help") { }
            else if (hit as string == "theme") ToggleTheme();
            else if (hit as string == "back") Back();
            else if (hit as string == "status") { for (int i = 0; i < 4; i++) if (TileHasResult(i)) StartTileCheck(i); }
            else if (hit is string && ((string)hit).StartsWith("tile")) TileClick(((string)hit)[4] - '0');
            else if (hit as string == "view") { if (CurrentView.Click(L(e.Location))) scroll = 0; }
            else if (hit == ScaleItem && ScaleChanged != null) { }
            else Press((ToolStripMenuItem)hit);
            Invalidate();
        }

        void Back()
        {
            if (path.Count > 0) path.RemoveAt(path.Count - 1);
            scroll = 0;
            SyncViews();
            Invalidate();
        }

        void Press(ToolStripMenuItem item)
        {
            if (!item.Enabled) return;
            if (views.ContainsKey(item))
            {
                OpenSection(item);
                return;
            }
            if (item.HasDropDownItems)
            {
                if (Array.IndexOf(sections, item) >= 0) path.Clear();
                path.Add(item);
                scroll = 0;
                SyncViews();
                return;
            }
            item.PerformClick();
            if (IsDisposed) return;
            refresh();
            // Часть пунктов пересоздаётся при обновлении. Подраздел, которого больше нет, закрываем
            while (path.Count > 0 && path[path.Count - 1].Owner == null) path.RemoveAt(path.Count - 1);
            SyncViews();
        }

        // ---------- рисование ----------

        const TextFormatFlags Line = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;

        // Цифры крупным техничным шрифтом. Его нет на старых Windows - тогда обычный
        static Font HeroFont()
        {
            var font = new Font("Bahnschrift", 16f, FontStyle.Bold);
            return font.Name == "Bahnschrift" ? font : new Font("Segoe UI Semibold", 16f);
        }

        public static Color Mix(Color a, Color b, float part)
        {
            return Color.FromArgb(a.A, (int)(a.R + (b.R - a.R) * part), (int)(a.G + (b.G - a.G) * part), (int)(a.B + (b.B - a.B) * part));
        }

        static Bitmap backdrop;
        static Palette backdropTheme;

        // Фон окна: тёмный, с едва заметным свечением в углу. Рисуем один раз, шапка берёт отсюда же свой кусок
        Bitmap Backdrop()
        {
            if (backdrop != null && backdropTheme == P && backdrop.Width == LW && backdrop.Height == LH) return backdrop;
            if (backdrop != null) backdrop.Dispose();
            backdrop = new Bitmap(LW, LH);
            backdropTheme = P;
            using (var g = Graphics.FromImage(backdrop))
            {
                g.Clear(P.Back);
                Glow(g, new Rectangle(LW - 360, -300, 720, 600), P.Check, 42);
                Glow(g, new Rectangle(SideWidth - 240, LH - 220, 560, 440), P.Accent2, 24);
            }
            return backdrop;
        }

        // Мягкое свечение: ярче в центре, к краям сходит на нет
        public static void Glow(Graphics g, Rectangle r, Color color, int alpha)
        {
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(r);
                using (var b = new PathGradientBrush(path))
                {
                    b.CenterColor = Color.FromArgb((int)(alpha * P.Shine), color);
                    b.SurroundColors = new[] { Color.FromArgb(0, color) };
                    g.FillPath(b, path);
                }
            }
        }

        // Активная кнопка: перелив из основного цвета в синий
        public static void Accent(Graphics g, Rectangle r, int radius)
        {
            var area = r;
            area.Inflate(1, 1);
            using (var path = ThemedRenderer.RoundRect(r, radius))
            using (var b = new LinearGradientBrush(area, P.Check, P.Accent2, 0f)) g.FillPath(b, path);
        }

        // Цветной значок со свечением. Линейная иконка растёт вместе с квадратом
        public static void Badge(Graphics g, Rectangle r, Color color, string icon, bool enabled = true)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (enabled)
            {
                var halo = r;
                halo.Inflate(r.Width / 2, r.Height / 2);
                Glow(g, halo, color, 60);
            }
            var area = r;
            area.Inflate(1, 1);
            int radius = r.Width / 4;
            using (var path = ThemedRenderer.RoundRect(r, radius))
            {
                if (enabled)
                    using (var b = new LinearGradientBrush(area, Mix(color, Color.White, 0.2f * P.Shine), Mix(color, Color.Black, 0.14f), 55f)) g.FillPath(b, path);
                else
                    using (var b = new SolidBrush(P.Tile)) g.FillPath(b, path);
            }
            var state = g.Save();
            float scale = Math.Max(1f, r.Width / 30f);
            g.TranslateTransform(r.Left + r.Width / 2f, r.Top + r.Height / 2f);
            g.ScaleTransform(scale, scale);
            ThemedRenderer.DrawIcon(g, icon, -8, 0, enabled ? Color.White : P.Disabled);
            g.Restore(state);
        }

        // Картинка игры со сглаженными углами. Выключенная игра приглушена
        public static void GamePicture(Graphics g, Image image, Rectangle r, int radius, bool dim = false)
        {
            var state = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            using (var path = ThemedRenderer.RoundRect(r, radius))
            using (var b = new TextureBrush(image, WrapMode.Clamp))
            {
                b.TranslateTransform(r.Left, r.Top);
                b.ScaleTransform((float)r.Width / image.Width, (float)r.Height / image.Height);
                g.FillPath(b, path);
                if (dim)
                    using (var shade = new SolidBrush(Color.FromArgb(165, P.Card))) g.FillPath(shade, path);
            }
            g.Restore(state);
        }

        // Заголовок страницы: светящаяся чёрточка слева и текст
        public static void PageTitle(Graphics g, string text, Font font, int x, int y, int height, int width)
        {
            var bar = new Rectangle(x, y + (height - 18) / 2, 4, 18);
            var area = bar;
            area.Inflate(0, 1);
            using (var path = ThemedRenderer.RoundRect(bar, 2))
            using (var b = new LinearGradientBrush(area, P.Check, P.Accent2, 90f)) g.FillPath(b, path);
            TextRenderer.DrawText(g, text, font, new Rectangle(x + 12, y, width - 12, height), P.Text, Line);
        }

        public static void Box(Graphics g, Rectangle r, Color color, int radius)
        {
            using (var path = ThemedRenderer.RoundRect(r, radius))
            using (var b = new SolidBrush(color)) g.FillPath(b, path);
        }

        // Карточка: заливка и тонкая рамка
        public static void Card(Graphics g, Rectangle r, Color fill, int radius, Color? border = null)
        {
            // Сверху чуть светлее, как стекло. Запас в один пиксель: иначе перелив заворачивается на краях
            var area = r;
            area.Inflate(1, 1);
            var edge = border ?? P.Separator;
            using (var path = ThemedRenderer.RoundRect(r, radius))
            {
                using (var b = new LinearGradientBrush(area, Mix(fill, Color.White, 0.05f * P.Shine), fill, 90f)) g.FillPath(b, path);
                using (var line = new LinearGradientBrush(area, Mix(edge, Color.White, 0.14f * P.Shine), edge, 90f))
                using (var p = new Pen(line)) g.DrawPath(p, path);
            }
        }

        static string Icon_(ToolStripItem item)
        {
            string icon;
            return MenuKit.Icons.TryGetValue(item, out icon) ? icon : "list";
        }

        static Color BadgeColor(ToolStripMenuItem item)
        {
            var kind = item.Tag as string;
            if (kind == ItemKind.PowerOff) return Theme.Bad;
            if (kind == ItemKind.PowerOn) return Theme.On;
            switch (Icon_(item))
            {
                case "games": case "plus": case "shield": return Color.FromArgb(32, 170, 92);
                case "sites": case "save": case "load": case "refresh": return Color.FromArgb(14, 136, 232);
                case "fast": case "folder": case "bell": case "sun": return Color.FromArgb(230, 126, 34);
                case "ping": case "connection": case "list": case "config": return Color.FromArgb(0, 150, 160);
                case "dns": case "undo": case "moon": case "report": return Color.FromArgb(142, 84, 214);
                case "trash": return Color.FromArgb(218, 60, 60);
                default: return Color.FromArgb(96, 110, 135);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // Свёрнутое окно имеет нулевой размер, а Windows всё равно может попросить его нарисовать
            if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.ScaleTransform(UiScale, UiScale);   // дальше всё рисуется в точках масштаба 100%
            g.DrawImage(Backdrop(), 0, 0, LW, LH);   // тема могла смениться, пока окно открыто
            DrawSidebar(g);
            if (path.Count == 0) DrawHome(g);
            else if (CurrentView != null) DrawView(g);
            else DrawSection(g);
            DrawWindowButtons(g);
            using (var p = new Pen(P.Border)) g.DrawRectangle(p, 0, 0, LW - 1, LH - 1);
        }

        void DrawSidebar(Graphics g)
        {
            using (var b = new SolidBrush(Mix(P.Back, P.Card, 0.6f))) g.FillRectangle(b, 0, 0, SideWidth, LH);
            using (var p = new Pen(P.Separator)) g.DrawLine(p, SideWidth - 1, 0, SideWidth - 1, LH);
            // Логотип
            Glow(g, new Rectangle(4, 4, 56, 56), Theme.On, 80);
            Box(g, new Rectangle(16, 16, 32, 32), Theme.On, 16);
            TextRenderer.DrawText(g, "Z", valueFont, new Rectangle(16, 16, 32, 32), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, AppInfo.Name, MenuKit.Bold, new Rectangle(56, 16, SideWidth - 62, 32), P.Text, Line);

            DrawTab(g, homeTab, "Главная", "home", path.Count == 0, Equals(hover, homeTab));
            for (int i = 0; i < sections.Length; i++)
            {
                var s = sections[i];
                DrawTab(g, SectionTab(i), s.Text.TrimEnd('.'), Icon_(s), path.Count > 0 && path[0] == s, hover == s);
            }

            int lineY = ToggleTab.Top - 8;
            using (var p = new Pen(P.Separator)) g.DrawLine(p, 16, lineY, SideWidth - 16, lineY);
            bool on = (toggle.Tag as string) != ItemKind.PowerOn;
            // Включение - кнопка, как "День/Ночь": значок и слово показывают, что сейчас, нажатие меняет
            if (hover == toggle) Box(g, ToggleTab, P.Hover, 9);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int powerY = ToggleTab.Top + ToggleTab.Height / 2;
            if (on) Glow(g, new Rectangle(ToggleTab.Left + 2, powerY - 18, 36, 36), Theme.On, 90);
            ThemedRenderer.DrawIcon(g, "power", ToggleTab.Left + 12, powerY, on ? Theme.On : P.Disabled);
            TextRenderer.DrawText(g, on ? "Включён" : "Выключен", MenuKit.Bold, new Rectangle(ToggleTab.Left + 38, ToggleTab.Top, ToggleTab.Width - 42, ToggleTab.Height), on ? P.Text : P.SubText, Line);
            if (ToggleTheme != null) DrawThemeTab(g);
            DrawTab(g, ExitTab, exit.Text, "exit", false, hover == exit);

            // Статус и версия видны всегда
            int y = lineY - 56;
            Glow(g, new Rectangle(14, y + 2, 21, 21), statusColor(), 90);
            using (var b = new SolidBrush(statusColor())) g.FillEllipse(b, 20, y + 8, 9, 9);
            TextRenderer.DrawText(g, status.Value.Split((char)9)[0], MenuKit.Small, new Rectangle(36, y, SideWidth - 44, 26), P.SubText, Line);
            TextRenderer.DrawText(g, "Версия " + AppInfo.Version, MenuKit.Small, new Rectangle(20, y + 24, SideWidth - 28, 22), P.Disabled, Line);
        }

        // День и ночь: значок показывает, какая тема сейчас, нажатие меняет её
        void DrawThemeTab(Graphics g)
        {
            var r = ThemeTab;
            bool dark = P == Palette.Dark;
            if (Equals(hover, "theme")) Box(g, r, P.Hover, 9);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int cx = r.Left + 20, cy = r.Top + r.Height / 2;
            if (dark)
            {
                // Месяц: круг без сдвинутого круга
                using (var moon = new GraphicsPath())
                using (var bite = new GraphicsPath())
                {
                    moon.AddEllipse(cx - 8, cy - 8, 16, 16);
                    bite.AddEllipse(cx - 4, cy - 11, 15, 15);
                    using (var region = new Region(moon))
                    using (var b = new SolidBrush(Color.FromArgb(150, 180, 255)))
                    {
                        region.Exclude(bite);
                        g.FillRegion(b, region);
                    }
                }
            }
            else
            {
                var sun = Color.FromArgb(232, 150, 20);
                using (var b = new SolidBrush(sun)) g.FillEllipse(b, cx - 4.5f, cy - 4.5f, 9, 9);
                using (var p = new Pen(sun, 1.7f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    for (int i = 0; i < 8; i++)
                    {
                        double a = i * Math.PI / 4;
                        g.DrawLine(p, cx + (float)(Math.Cos(a) * 6.8), cy + (float)(Math.Sin(a) * 6.8), cx + (float)(Math.Cos(a) * 9), cy + (float)(Math.Sin(a) * 9));
                    }
            }
            TextRenderer.DrawText(g, dark ? "Ночь" : "День", MenuKit.Bold, new Rectangle(r.Left + 38, r.Top, r.Width - 42, r.Height), P.Text, Line);
        }

        void DrawTab(Graphics g, Rectangle r, string text, string icon, bool active, bool hovered, Color? accent = null)
        {
            if (active) Accent(g, r, 9);
            else if (hovered) Box(g, r, P.Hover, 9);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            ThemedRenderer.DrawIcon(g, icon, r.Left + 12, r.Top + r.Height / 2, active ? Color.White : accent ?? P.SubText);
            TextRenderer.DrawText(g, text, MenuKit.Bold, new Rectangle(r.Left + 38, r.Top, r.Width - 42, r.Height), active ? Color.White : accent ?? P.Text, Line);
        }

        void DrawHome(Graphics g)
        {
            cards.Clear();
            contentHeight = 0;
            const int actionHeight = 84, top = HeaderHeight;
            PageTitle(g, "Главная", titleFont, SideWidth + Pad, 12, 34, CardWidth);
            int x = SideWidth + Pad, w = CardWidth * 2 + Gap;
            int actionsTop = LH - Pad - actionHeight;
            int h = actionsTop - Gap - top;

            if (health.Available)
            {
                // Два ровных ряда плиток прямо на фоне окна, как карточки на других страницах
                // Сверху главное: работает ли и как. Ниже - что именно открывается
                int rest = h - Gap, servicesHeight = rest * 11 / 20, numbersHeight = rest - servicesHeight;
                DrawNumbers(g, x, top, w, numbersHeight);
                DrawServices(g, x, top + numbersHeight + Gap, w, servicesHeight);
            }
            else
                DrawOffStatus(g, x, top, w, h);

            AddCard(g, shortcuts[0], new Rectangle(x, actionsTop, CardWidth, actionHeight), 0);
            AddCard(g, shortcuts[1], new Rectangle(x + CardWidth + Gap, actionsTop, CardWidth, actionHeight), 0);
            if (Equals(hover, "help") && health.Available) DrawGameHelp(g);
        }

        Rectangle gameHelp;
        const int TimesHeight = 72;

        // Плитка по нажатию проверяется заново. Сама проверка в TrayContext, здесь только вид: кружок вместо результата
        public Func<int, bool> TileChecking;
        public Action<int> RecheckTile;
        const int MinSpinMs = 800, PopMs = 260;   // быстрый ответ не должен мелькнуть: кружок виден хотя бы столько
        // Номера: 0-3 сервисы, 4-6 скорость, пинг, время работы. Плитка статуса проверяет все сервисы
        readonly Rectangle[] tileRects = new Rectangle[7];
        readonly int[] spinStart = { -1, -1, -1, -1, -1, -1, -1 }, popStart = { -1, -1, -1, -1, -1, -1, -1 };
        Rectangle statusRect;
        Timer tileTimer;

        bool TileHasResult(int i)
        {
            if (i >= 4) return true;
            var parts = health.Value.Split((char)9);
            return i < parts.Length && parts[i].Length > 0;   // "⋯" тоже: нажатие запомнится и сработает, как только освободится
        }

        // Игры и сайты с прочерком нажимаются: открывают свой раздел, где их добавить
        bool TileClickable(int i)
        {
            if (TileHasResult(i)) return true;
            var parts = health.Value.Split((char)9);
            return i >= 2 && (i >= parts.Length || parts[i].Length == 0);
        }

        void TileClick(int i)
        {
            if (TileHasResult(i)) StartTileCheck(i);
            else if (i - 2 < sections.Length) Press(sections[i - 2]);
        }

        static int Elapsed(int since) { return unchecked(Environment.TickCount - since) & int.MaxValue; }

        void StartTileCheck(int i)
        {
            if (spinStart[i] >= 0) return;
            RecheckTile(i);
            ShowTileCheck(i);
        }

        // Кружок в плитке. Проверку запускает тот, кто вызвал: нажатие или правка списка
        public void ShowTileCheck(int i)
        {
            if (spinStart[i] >= 0) return;
            spinStart[i] = Environment.TickCount & int.MaxValue;
            popStart[i] = -1;
            if (tileTimer == null)
            {
                tileTimer = new Timer { Interval = 30 };
                tileTimer.Tick += (s, e) => TileAnimate();
                Disposed += (s, e) => tileTimer.Dispose();
            }
            tileTimer.Start();
        }

        void TileAnimate()
        {
            bool active = false;
            for (int i = 0; i < spinStart.Length; i++)
            {
                if (spinStart[i] >= 0 && Elapsed(spinStart[i]) >= MinSpinMs && !TileChecking(i))
                {
                    spinStart[i] = -1;
                    popStart[i] = Environment.TickCount & int.MaxValue;
                }
                if (popStart[i] >= 0 && Elapsed(popStart[i]) >= PopMs) popStart[i] = -1;
                if (spinStart[i] < 0 && popStart[i] < 0) continue;
                active = true;
                if (path.Count == 0) Invalidate(Dev(tileRects[i]));
            }
            if (path.Count == 0) Invalidate(Dev(statusRect));
            if (active) return;
            tileTimer.Stop();
            Invalidate();
        }

        // Кружок загрузки: хвост цвета плитки плавно сходит на нет
        static void DrawSpinner(Graphics g, Color color, int cx, int cy, int ms)
        {
            const int Steps = 26, Radius = 12;
            float head = ms * 0.42f;
            var box = new RectangleF(cx - Radius, cy - Radius, Radius * 2, Radius * 2);
            using (var track = new Pen(Color.FromArgb(38, color), 3f)) g.DrawEllipse(track, box);
            for (int n = 0; n < Steps; n++)
                using (var p = new Pen(Color.FromArgb(255 * (n + 1) / Steps, color), 3f) { StartCap = LineCap.Flat, EndCap = n == Steps - 1 ? LineCap.Round : LineCap.Flat })
                    g.DrawArc(p, box, head + n * 11f, 12f);
        }
        static readonly Font TimeLabel = new Font("Segoe UI", 10.5f), TimeValue = new Font("Segoe UI Semibold", 10.5f);

        // "отклик 183 мс": слева название серым, справа значение
        void DrawTimeRow(Graphics g, string text, int x, int y, int w)
        {
            int space = text.IndexOf((char)32);
            if (space <= 0) return;
            var label = char.ToUpper(text[0]) + text.Substring(1, space - 1);
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            TextRenderer.DrawText(g, label, TimeLabel, new Rectangle(x, y, w, 26), P.SubText, flags | TextFormatFlags.Left);
            TextRenderer.DrawText(g, text.Substring(space + 1), TimeValue, new Rectangle(x, y, w, 26), P.Text, flags | TextFormatFlags.Right);
        }

        // Запущенная игра из "Моих игр": имя процесса или null. Плитка показывает её значок и название
        public Func<string> RunningGame;

        static Image LoadIcon(string name)
        {
            try
            {
                var stream = typeof(ControlPanel).Assembly.GetManifestResourceStream(name);
                return stream != null ? Image.FromStream(stream) : null;
            }
            catch { return null; }
        }

        static readonly Image ArcIcon = LoadIcon("arc.png"), WardogsIcon = LoadIcon("wardogs.png");

        // Значок и название по имени процесса. Значка нет - остаётся джойстик
        static void GameLook(string exe, out Image icon, out string name)
        {
            icon = null;
            name = exe;
            var key = exe.Replace(" ", "");   // в списке игра называется "ARC Raiders", а процесс без пробела
            if (key.IndexOf("wardogs", StringComparison.OrdinalIgnoreCase) >= 0) { icon = WardogsIcon; name = "Wardogs"; }
            else if (key.IndexOf("arcraiders", StringComparison.OrdinalIgnoreCase) >= 0 || key.IndexOf("pioneergame", StringComparison.OrdinalIgnoreCase) >= 0) { icon = ArcIcon; name = "ARC Raiders"; }
        }

        // Пояснение рисуем последним, поверх плиток
        void DrawGameHelp(Graphics g)
        {
            const string text = "Пинг появится, когда запустишь игру из «Моих игр».";
            const int padX = 14, padY = 10, textWidth = 230;
            var flags = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak;
            // Размер считаем от текста, поэтому он всегда внутри рамки
            var size = TextRenderer.MeasureText(g, text, MenuKit.Font, new Size(textWidth, 0), flags);
            int width = size.Width + padX * 2, height = size.Height + padY * 2;
            var box = new Rectangle(Math.Max(SideWidth + 8, Math.Min(gameHelp.Right - width, LW - width - 10)), gameHelp.Bottom + 6, width, height);
            Card(g, box, P.Hover, 10);
            TextRenderer.DrawText(g, text, MenuKit.Font, new Rectangle(box.Left + padX, box.Top + padY, size.Width, size.Height), P.Text, flags);
        }

        static readonly string[] ServiceNames = { "YouTube", "Discord", "Игры", "Мои сайты" };
        static readonly Color[] ServiceColors = { Color.FromArgb(230, 33, 23), Color.FromArgb(88, 101, 242), Color.FromArgb(240, 136, 30), Color.FromArgb(14, 136, 232) };
        static readonly Color[] NumberColors = { Color.FromArgb(18, 165, 148), Color.FromArgb(142, 84, 233), Color.FromArgb(222, 70, 128) };

        // Плитки: крупный значок и результат сверху, название снизу. Пустая часть - плитки нет, остальные делят ширину поровну
        void DrawServices(Graphics g, int x, int y, int w, int h)
        {
            var parts = health.Value.Split((char)9);
            var times = pings().Split((char)9);
            // Всегда 4 плитки, чтобы ряды совпадали. Проверять нечего - вместо результата прочерк
            int count = ServiceNames.Length;
            int tile = (w - Gap * (count - 1)) / count, n = 0;
            for (int i = 0; i < count; i++)
            {
                var part = i < parts.Length ? parts[i] : "";
                // Игра запущена и подключена: просто галочка, а не счёт встроенных игр
                if (i == 2 && RunningGame != null && RunningGame() != null) part = "✓";
                int left = x + (tile + Gap) * n++, mid = left + tile / 2, cy = y + (h - TimesHeight - 96) / 2 + 21;   // значок, название и результат по центру над табличкой
                tileRects[i] = new Rectangle(left, y, tile, h);
                bool hot = Equals(hover, "tile" + i), spinning = spinStart[i] >= 0;
                Card(g, tileRects[i], hot ? P.Hover : P.Card, 10, hot ? P.Check : (Color?)null);

                // По центру плитки столбиком: значок, название, результат
                var state = g.Save();
                var exe = i == 2 && RunningGame != null ? RunningGame() : null;
                Image gameIcon = null;
                string title = ServiceNames[i];
                if (exe != null) GameLook(exe, out gameIcon, out title);
                // Пока идёт проверка, свечение значка дышит
                Glow(g, new Rectangle(mid - 54, cy - 48, 108, 96), ServiceColors[i], spinning ? 90 + (int)(70 * Math.Sin(Elapsed(spinStart[i]) * 0.006)) : 60);
                if (gameIcon != null) GamePicture(g, gameIcon, new Rectangle(mid - 21, cy - 21, 42, 42), 10);   // квадратный значок целиком, не обрезаем
                else
                {
                    g.TranslateTransform(mid - 27, cy);
                    g.ScaleTransform(3f, 3f);
                    ThemedRenderer.DrawService(g, i, 0, 0);
                }
                g.Restore(state);

                var centered = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
                TextRenderer.DrawText(g, title, valueFont, new Rectangle(left + 6, cy + 28, tile - 12, 28), P.Text, centered);

                int ry = cy + 72;
                if (spinning) DrawSpinner(g, Mix(ServiceColors[i], P.Text, 0.2f), mid, ry, Elapsed(spinStart[i]));
                else if (part.Length == 0 && exe != null) { }   // игра запущена, встроенных адресов проверять не нужно: главное - пинг
                else if (part.Length == 0)
                    TextRenderer.DrawText(g, "—", valueFont, new Rectangle(left + 6, ry - 14, tile - 12, 28), P.Disabled, centered);
                else if (part.Length > 1)
                {
                    // Счётчик: цвет как в маленькой плитке
                    var n2 = part.Split(new[] { (char)47 });
                    var color = n2.Length == 2 && n2[0] == n2[1] ? Theme.On : n2[0] == "0" ? Theme.Bad : Theme.Busy;
                    TextRenderer.DrawText(g, part, valueFont, new Rectangle(left + 6, ry - 14, tile - 12, 28), color, centered);
                }
                else
                {
                    var mark = g.Save();
                    // Новый результат появляется с лёгким отскоком
                    float pop = popStart[i] >= 0 ? 1f - Math.Min(1f, Elapsed(popStart[i]) / (float)PopMs) : 0f;
                    float scale = 1.6f * (1f + 0.5f * pop * pop);
                    g.TranslateTransform(mid - 8 * (scale / 1.6f), ry);
                    g.ScaleTransform(scale, scale);
                    ThemedRenderer.DrawResult(g, part, 0, 0, 12, new Rectangle(0, -8, 12, 16));
                    g.Restore(mark);
                }

                // Пинг за час - над табличкой, вместо разделительной линии
                int graphTop = cy + 88, graphBottom = y + h - TimesHeight;
                bool graph = DrawGraph(g, tileRects[i], new Rectangle(left + 16, graphTop, tile - 32, graphBottom - graphTop), ServiceColors[i], i, 60);

                // Под результатом: у игр пинг до сервера, он важнее и крупнее, у остальных отклик сайта
                var time = i < times.Length ? times[i] : "";
                // Табличка внизу плитки: у всех на одном уровне
                if (time.Length > 0)
                {
                    int tableTop = y + h - TimesHeight;
                    if (!graph)
                        using (var pen = new Pen(hot ? Mix(P.Separator, P.SubText, 0.3f) : P.Separator)) g.DrawLine(pen, left + 16, tableTop, left + tile - 16, tableTop);   // цвет наведения совпадает с линией
                    var lines = time.Split((char)10);
                    int rowTop = tableTop + (TimesHeight - lines.Length * 26) / 2;
                    for (int line = 0; line < lines.Length; line++)
                        DrawTimeRow(g, lines[line], left + 16, rowTop + line * 26, tile - 32);
                }

                // Вопросик в углу плитки игр: наведёшь - объяснит, откуда берётся пинг
                if (i == 2)
                {
                    gameHelp = new Rectangle(left + tile - 30, y + 8, 22, 22);
                    using (var pen = new Pen(Equals(hover, "help") ? P.SubText : P.Disabled, 1.3f)) g.DrawEllipse(pen, gameHelp.Left + 2, gameHelp.Top + 2, 17, 17);
                    TextRenderer.DrawText(g, "?", MenuKit.Small, gameHelp, Equals(hover, "help") ? P.SubText : P.Disabled,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                }
            }
        }

        static readonly string[] NumberNames = { "Скорость", "Пинг", "Работает" };

        // Нижний ряд: статус с конфигом и цифры с подписями. Четыре плитки, как в верхнем ряду
        void DrawNumbers(Graphics g, int x, int y, int w, int h)
        {
            var numbers = stats.Value.Split((char)9);
            int count = Math.Min(numbers.Length, NumberNames.Length);
            int tile = (w - Gap * count) / (count + 1);
            var centered = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;

            statusRect = new Rectangle(x, y, tile, h);
            bool statusHot = Equals(hover, "status");
            Card(g, statusRect, statusHot ? P.Hover : P.Card, 10, statusHot ? P.Check : (Color?)null);
            var parts = status.Value.Split((char)9);
            var color = statusColor();
            int cx = x + tile / 2, dotY = y + h / 2 - 46;
            using (var b = new SolidBrush(Color.FromArgb(60, color))) g.FillEllipse(b, cx - 11, dotY - 3, 22, 22);
            using (var b = new SolidBrush(color)) g.FillEllipse(b, cx - 6, dotY + 2, 12, 12);
            // Пока проверяются сервисы, вокруг точки идёт кружок
            for (int s = 0; s < 4; s++)
                if (spinStart[s] >= 0)
                {
                    DrawSpinner(g, Mix(color, P.Text, 0.2f), cx, dotY + 8, Elapsed(spinStart[s]));
                    break;
                }
            // Длинный текст подбора: мельче и на всю высоту, чтобы влез целиком
            bool longText = parts[0].Length > 22;
            TextRenderer.DrawText(g, parts[0], longText ? MenuKit.Bold : valueFont, new Rectangle(x + 8, dotY + 22, tile - 16, parts.Length > 1 ? 44 : 70), P.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
            if (parts.Length > 1)
                TextRenderer.DrawText(g, parts[1], MenuKit.Small, new Rectangle(x + 8, dotY + 66, tile - 16, 22), P.SubText, centered);

            for (int i = 0; i < count; i++)
            {
                int left = x + (tile + Gap) * (i + 1), number = i + 4;
                tileRects[number] = new Rectangle(left, y, tile, h);
                bool hot = Equals(hover, "tile" + number);
                Card(g, tileRects[number], hot ? P.Hover : P.Card, 10, hot ? P.Check : (Color?)null);
                Glow(g, new Rectangle(left + tile / 2 - 54, y + h / 2 - 88, 108, 96), NumberColors[i], 60);
                if (i < 2)
                {
                    var card = tileRects[number];
                    DrawGraph(g, card, new Rectangle(card.Left + 16, card.Top + h / 2 + 56, card.Width - 32, h - h / 2 - 66), NumberColors[i],
                        i == 0 ? 4 : 5, i == 0 ? 100 * 1024 : 60);
                }
                var state = g.Save();
                g.TranslateTransform(left + tile / 2 - 27, y + h / 2 - 40);
                g.ScaleTransform(3f, 3f);
                if (i == 0) ThemedRenderer.DrawSpeedometer(g, SpeedLevel(numbers[i]), 0, 0);
                else if (i == 1) ThemedRenderer.DrawSignal(g, PingBars(numbers[i]), 0, 0);
                else ThemedRenderer.DrawClock(g, UptimeMinutes(numbers[i]), 0, 0);
                g.Restore(state);
                if (spinStart[number] >= 0) DrawSpinner(g, Mix(NumberColors[i], P.Text, 0.2f), left + tile / 2, y + h / 2 + 6, Elapsed(spinStart[number]));
                else TextRenderer.DrawText(g, numbers[i], heroFont, new Rectangle(left + 6, y + h / 2 - 16, tile - 12, 44), P.Text, centered);
                TextRenderer.DrawText(g, NumberNames[i], MenuKit.Font, new Rectangle(left + 8, y + h / 2 + 26, tile - 16, 24), P.SubText, centered);
            }
        }

        // Первое число в строке: "↓ 8,4 МБ/с" - 8.4, "32 мс" - 32. Числа нет - минус один
        static double NumberIn(string text)
        {
            var m = System.Text.RegularExpressions.Regex.Match(text, @"\d+([.,]\d+)?");
            return m.Success ? double.Parse(m.Value.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture) : -1;
        }

        // Шкала спидометра логарифмическая: 1 КБ/с в начале, 100 МБ/с в конце, иначе обычный трафик не сдвинет стрелку
        static double SpeedLevel(string text)
        {
            double value = NumberIn(text);
            if (value <= 0) return 0;
            double kb = text.Contains("МБ") ? value * 1024 : value;
            return Math.Max(0, Math.Min(1, Math.Log10(Math.Max(kb, 1)) / 5));
        }

        // "2 ч 14 мин" - 134. "<1 мин" и прочерк - 0: стрелки смотрят на 12
        static double UptimeMinutes(string text)
        {
            var m = System.Text.RegularExpressions.Regex.Match(text, @"^(?:(\d+) ч )?(\d+) мин$");
            if (!m.Success) return 0;
            return (m.Groups[1].Success ? int.Parse(m.Groups[1].Value) * 60 : 0) + int.Parse(m.Groups[2].Value);
        }

        // История для графиков: 0-3 - сервисы (пинг), 4 - скорость (байт в секунду), 5 - пинг. Замер раз в 10 секунд, час. -1 - не ответил
        public Func<int, long[]> TileHistory;
        public Func<int, DateTime[]> TileTimes;   // когда сделан каждый замер (UTC)

        // Волна за последний час. Замеры усредняем кусками (за час - по 10 штук, 100 секунд), иначе 360 точек на 180 пикселях - сплошной шум.
        // Мало замеров (только запустили) - без усреднения, чтобы график появился почти сразу. Кусок, где ничего не ответило, - разрыв.
        // floor - низ шкалы сверху: ровный пинг 10 мс не должен выглядеть горами. Мышь над плиткой - точка и подпись с числом.
        // false - рисовать нечего (мало замеров)
        bool DrawGraph(Graphics g, Rectangle card, Rectangle area, Color color, int tile, double floor)
        {
            var history = TileHistory == null ? null : TileHistory(tile);
            var times = TileTimes == null ? null : TileTimes(tile);
            if (history == null || area.Height < 12) return false;
            int chunk = Math.Max(1, history.Length / 36);
            var points = new List<double>();
            var pointTimes = new List<DateTime>();
            for (int k = 0; k < history.Length; k += chunk)
            {
                double sum = 0;
                int count = 0, end = Math.Min(k + chunk, history.Length);
                for (int m = k; m < end; m++)
                    if (history[m] >= 0) { sum += history[m]; count++; }
                points.Add(count > 0 ? sum / count : -1);
                pointTimes.Add(times != null && times.Length == history.Length ? times[end - 1] : DateTime.UtcNow);
            }
            if (System.Linq.Enumerable.Count(points, v => v >= 0) < 2) return false;
            double top = Math.Max(floor, System.Linq.Enumerable.Max(points) * 1.15);
            float step = (float)area.Width / Math.Max(1, points.Count - 1);
            Func<int, PointF> at = k => new PointF(area.Left + k * step, area.Bottom - (float)(Math.Min(points[k], top) / top * area.Height));
            var state = g.Save();
            using (var clip = ThemedRenderer.RoundRect(new Rectangle(card.Left + 1, card.Top + 1, card.Width - 2, card.Height - 2), 9)) g.SetClip(clip, CombineMode.Intersect);
            var segment = new List<PointF>();
            for (int k = 0; k <= points.Count; k++)
            {
                if (k < points.Count && points[k] >= 0)
                {
                    segment.Add(at(k));
                    continue;
                }
                if (segment.Count >= 2)
                    using (var path = new GraphicsPath())
                    {
                        path.AddCurve(segment.ToArray(), 0.4f);
                        path.AddLine(segment[segment.Count - 1].X, area.Bottom + 2, segment[0].X, area.Bottom + 2);
                        path.CloseFigure();
                        using (var b = new LinearGradientBrush(new Rectangle(area.Left, area.Top - 2, area.Width, area.Height + 4), Color.FromArgb(80, color), Color.FromArgb(0, color), 90f))
                            g.FillPath(b, path);
                        using (var pen = new Pen(Color.FromArgb(200, color), 1.8f) { LineJoin = LineJoin.Round })
                            g.DrawCurve(pen, segment.ToArray(), 0.4f);
                    }
                segment.Clear();
            }

            // Мышь над плиткой и по ширине графика: ближайшая точка, линия вниз и подпись "45 мс · 12 мин назад"
            int hot = -1;
            if (Equals(hover, "tile" + tile) && mouse.X >= area.Left - step / 2 && mouse.X <= area.Right + step / 2 && mouse.Y >= area.Top - 30 && mouse.Y <= card.Bottom)
                hot = Math.Max(0, Math.Min(points.Count - 1, (int)Math.Round((mouse.X - area.Left) / step)));
            int dot = hot >= 0 ? hot : points.Count - 1;
            if (points[dot] >= 0)
            {
                var p = at(dot);
                if (hot >= 0)
                    using (var pen = new Pen(Color.FromArgb(120, color), 1f) { DashStyle = DashStyle.Dot }) g.DrawLine(pen, p.X, area.Top - 4, p.X, area.Bottom);
                using (var b = new SolidBrush(Color.FromArgb(70, color))) g.FillEllipse(b, p.X - 6, p.Y - 6, 12, 12);
                using (var b = new SolidBrush(color)) g.FillEllipse(b, p.X - 3, p.Y - 3, 6, 6);
            }
            g.Restore(state);
            if (hot >= 0) DrawGraphLabel(g, card, area, at(hot).X, GraphValue(tile, points[hot]) + " · " + Ago(pointTimes[hot]));
            return true;
        }

        static string GraphValue(int tile, double value)
        {
            if (value < 0) return "нет ответа";
            if (tile != 4) return (int)Math.Round(value) + " мс";
            return value >= 1024 * 1024 ? (value / 1024 / 1024).ToString("0.0") + " МБ/с" : (value / 1024).ToString("0") + " КБ/с";
        }

        static string Ago(DateTime when)
        {
            int minutes = (int)(DateTime.UtcNow - when).TotalMinutes;
            return minutes < 1 ? "сейчас" : minutes + " мин назад";
        }

        // Подпись над графиком: тёмная плашка, не вылезает за плитку
        void DrawGraphLabel(Graphics g, Rectangle card, Rectangle area, float x, string text)
        {
            var size = TextRenderer.MeasureText(text, MenuKit.Small);
            int w = size.Width + 14, h = 22;
            int left = Math.Max(card.Left + 6, Math.Min(card.Right - 6 - w, (int)x - w / 2));
            var r = new Rectangle(left, area.Top - h - 4, w, h);
            Box(g, r, Mix(P.Back, P.Text, 0.08f), 6);
            using (var path = ThemedRenderer.RoundRect(r, 6))
            using (var pen = new Pen(P.Separator)) g.DrawPath(pen, path);
            TextRenderer.DrawText(g, text, MenuKit.Small, r, P.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }

        static int PingBars(string text)
        {
            double ms = NumberIn(text);
            return ms < 0 ? 0 : ms < 60 ? 4 : ms < 120 ? 3 : ms < 250 ? 2 : 1;
        }

        // Выключено: плиток нет, только крупный статус по центру карточки
        void DrawOffStatus(Graphics g, int x, int y, int w, int h)
        {
            Card(g, new Rectangle(x, y, w, h), P.Card, 10);
            var color = statusColor();
            int cx = x + w / 2, cy = y + h / 2 - 34;
            using (var b = new SolidBrush(Color.FromArgb(60, color))) g.FillEllipse(b, cx - 14, cy - 14, 28, 28);
            using (var b = new SolidBrush(color)) g.FillEllipse(b, cx - 8, cy - 8, 16, 16);
            TextRenderer.DrawText(g, status.Value.Split((char)9)[0], heroFont, new Rectangle(x + 20, cy + 22, w - 40, 44), P.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        }

        void DrawView(Graphics g)
        {
            var view = CurrentView;
            var area = ContentArea;
            g.SetClip(area);
            contentHeight = view.Draw(g, SideWidth + Pad, area.Top - scroll, mouse) + Pad;
            g.ResetClip();
            scroll = Math.Min(scroll, MaxScroll);
            CoverHeader(g, area);
            view.DrawHeader(g, new Rectangle(SideWidth + Pad, 14, CardWidth * 2 + Gap, HeaderHeight - 14), mouse);
            DrawScrollBar(g, area);
        }

        // TextRenderer не слушается границ области рисования, и при прокрутке текст лезет на шапку.
        // Поэтому шапку рисуем после содержимого, поверх него
        void CoverHeader(Graphics g, Rectangle area)
        {
            var top = new Rectangle(SideWidth, 0, LW - SideWidth, area.Top);
            g.DrawImage(Backdrop(), top, top, GraphicsUnit.Pixel);
        }

        void DrawScrollBar(Graphics g, Rectangle area)
        {
            if (MaxScroll <= 0) return;
            int barHeight = Math.Max(30, area.Height * area.Height / contentHeight);
            int barTop = area.Top + (area.Height - barHeight) * scroll / MaxScroll;
            Box(g, new Rectangle(LW - 8, barTop, 4, barHeight), P.Hover, 2);
        }

        void DrawSection(Graphics g)
        {
            var section = path[path.Count - 1];
            cards.Clear();
            var area = ContentArea;
            g.SetClip(area);
            int y = HeaderHeight, col = 0;
            foreach (ToolStripItem it in section.DropDownItems)
            {
                if (it is ToolStripSeparator)
                {
                    if (col == 1) { col = 0; y += CardHeight + Gap; }
                    var caption = it.Tag as string;
                    if (caption == null) { y += GroupGap; continue; }
                    if (y > HeaderHeight) y += 6;
                    // Подпись группы заглавными и линия до конца ряда
                    var upper = caption.ToUpper();
                    int captionX = SideWidth + Pad + 2, lineX = captionX + TextRenderer.MeasureText(upper, MenuKit.Small).Width + 6;
                    TextRenderer.DrawText(g, upper, MenuKit.Small, new Rectangle(captionX, y - scroll, CardWidth, 24), P.SubText, Line);
                    using (var p = new Pen(P.Separator)) g.DrawLine(p, lineX, y - scroll + 12, SideWidth + Pad + CardWidth * 2 + Gap, y - scroll + 12);
                    y += 28;
                    continue;
                }
                var item = it as ToolStripMenuItem;
                if (item == null) continue;
                AddCard(g, item, new Rectangle(SideWidth + Pad + col * (CardWidth + Gap), y, CardWidth, CardHeight), scroll);
                col++;
                if (col == 2) { col = 0; y += CardHeight + Gap; }
            }
            if (col == 1) y += CardHeight + Gap;
            contentHeight = y - HeaderHeight + Pad;
            scroll = Math.Min(scroll, MaxScroll);
            g.ResetClip();

            CoverHeader(g, area);
            int titleX = SideWidth + Pad;
            if (path.Count > 1)
            {
                Card(g, BackButton, Equals(hover, "back") ? P.Hover : P.Card, 8);
                using (var p = new Pen(P.Text, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    int cx = BackButton.Left + 13, my = BackButton.Top + 15;
                    g.DrawLine(p, cx + 3, my - 5, cx - 2, my);
                    g.DrawLine(p, cx - 2, my, cx + 3, my + 5);
                }
                titleX += 40;
            }
            PageTitle(g, section.Text.TrimEnd('.'), titleFont, titleX, 12, 34, CardWidth * 2 - 40);
            DrawScrollBar(g, area);
        }

        // Карточка пункта меню: значок, название, примечание из скобок, справа стрелка, галочка или переключатель
        void AddCard(Graphics g, ToolStripMenuItem item, Rectangle r, int offset)
        {
            cards.Add(new KeyValuePair<Rectangle, ToolStripMenuItem>(r, item));
            r.Y -= offset;
            bool enabled = item.Enabled;
            bool hot = hover == item && enabled, chosen = item.Checked && !IsSwitch(item);
            Card(g, r, hot ? P.Hover : chosen ? Mix(P.Card, P.Check, 0.14f) : P.Card, 10, chosen || hot ? P.Check : (Color?)null);

            var badge = new Rectangle(r.Left + 11, r.Top + (r.Height - 40) / 2, 40, 40);
            // У известной игры вместо значка её настоящая картинка
            Image picture = null;
            if (Icon_(item) == "games")
            {
                string unused;
                GameLook(item.Text, out picture, out unused);
            }
            if (picture != null) GamePicture(g, picture, badge, 10, IsSwitch(item) && !item.Checked);
            else Badge(g, badge, BadgeColor(item), Icon_(item), enabled);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int right = r.Right - 12;
            if (item == ScaleItem && ScaleChanged != null)
            {
                DrawScaleSlider(g, r);
                right = SliderTrack(r).Left - 16;
            }
            else if (IsSwitch(item))
            {
                DrawSwitch(g, new Rectangle(right - 34, r.Top + (r.Height - 20) / 2, 34, 20), item.Checked);
                right -= 42;
            }
            else if (item.HasDropDownItems)
            {
                using (var p = new Pen(P.SubText, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    int cx = right - 6, cy = r.Top + r.Height / 2;
                    g.DrawLine(p, cx - 2, cy - 5, cx + 3, cy);
                    g.DrawLine(p, cx + 3, cy, cx - 2, cy + 5);
                }
                right -= 22;
            }
            else if (item.Checked)
            {
                ThemedRenderer.DrawResult(g, "✓", right - 12, r.Top + r.Height / 2, 12, r);
                right -= 24;
            }

            // "Текст (примечание)" делим на две строки
            var text = item.Text.TrimEnd('.');
            string note = null;
            int open = text.IndexOf('(');
            if (open > 0 && text.EndsWith(")"))
            {
                note = text.Substring(open + 1, text.Length - open - 2);
                text = text.Substring(0, open).TrimEnd();
            }
            // Конфиги: "general (ALT10)". Крупно показываем то, чем они отличаются
            if (text == "general" && note != null)
            {
                text = note;
                note = null;
            }
            if (note == null && !string.IsNullOrEmpty(item.ToolTipText)) note = item.ToolTipText;
            if (item == ScaleItem && ScaleChanged != null) note = ScaleNote();
            var color = !enabled ? P.Disabled : P.Text;
            int textLeft = badge.Right + 12, textWidth = right - textLeft;
            if (note == null)
                TextRenderer.DrawText(g, text, cardFont, new Rectangle(textLeft, r.Top, textWidth, r.Height), color, Line);
            else
            {
                TextRenderer.DrawText(g, text, cardFont, new Rectangle(textLeft, r.Top + 10, textWidth, 22), color, Line);
                TextRenderer.DrawText(g, note, MenuKit.Small, new Rectangle(textLeft, r.Top + 31, textWidth, 20), P.SubText, Line);
            }
        }

        // Ползунок "Размер окна": дорожка с делениями, под ними проценты
        static Rectangle SliderTrack(Rectangle card)
        {
            return new Rectangle(card.Right - 186, card.Top + card.Height / 2 - 2, 168, 4);
        }

        int CurrentStopValue()
        {
            int percent = (int)Math.Round(UserScale * 100), best = 0;
            for (int i = 1; i < ScaleStops.Length; i++)
                if (Math.Abs(ScaleStops[i] - percent) < Math.Abs(ScaleStops[best] - percent)) best = i;
            return ScaleStops[best];
        }

        float StopX(Rectangle track, int i)
        {
            return track.Left + track.Width * i / (float)(ScaleStops.Length - 1);
        }

        int NearestStop(int x)
        {
            var card = cards.Find(c => c.Value == ScaleItem).Key;
            var track = SliderTrack(card);
            int best = 0;
            for (int i = 1; i < ScaleStops.Length; i++)
                if (Math.Abs(StopX(track, i) - x) < Math.Abs(StopX(track, best) - x)) best = i;
            return best;
        }

        void DrawScaleSlider(Graphics g, Rectangle card)
        {
            var track = SliderTrack(card);
            int chosen = scaleDrag >= 0 ? scaleDrag : Array.IndexOf(ScaleStops, CurrentStopValue());
            float knob = StopX(track, chosen);
            Box(g, track, Mix(P.Tile, P.SubText, 0.3f), 2);
            if (knob > track.Left)
                Accent(g, new Rectangle(track.Left, track.Top, (int)(knob - track.Left) + 1, track.Height), 2);
            for (int i = 0; i < ScaleStops.Length; i++)
            {
                float x = StopX(track, i);
                using (var b = new SolidBrush(i <= chosen ? Color.White : Mix(P.Tile, P.SubText, 0.6f)))
                    g.FillEllipse(b, x - 2, track.Top, 4, 4);
            }
            using (var b = new SolidBrush(Color.FromArgb(70, P.Check))) g.FillEllipse(b, knob - 11, track.Top + 2 - 11, 22, 22);
            using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, knob - 7, track.Top + 2 - 7, 14, 14);
            using (var p = new Pen(P.Check, 2f)) g.DrawEllipse(p, knob - 7, track.Top + 2 - 7, 14, 14);
        }

        // Подпись под названием: что выбрано, и честно - если экран больше не вмещает
        public string ScaleNote()
        {
            int value = scaleDrag >= 0 ? ScaleStops[scaleDrag] : CurrentStopValue();
            return value + "%" + (scaleCapped && scaleDrag < 0 ? ", больше не влезет" : "");
        }

        static bool IsSwitch(ToolStripMenuItem item)
        {
            return item.CheckOnClick || (item.Tag as string) == ItemKind.Toggle;
        }

        public static void DrawSwitch(Graphics g, Rectangle r, bool on)
        {
            if (on) Accent(g, r, r.Height / 2 - 1);
            else Box(g, r, Mix(P.Tile, P.SubText, 0.35f), r.Height / 2 - 1);   // в светлой теме чистый Tile сливается с белым кружком
            int d = r.Height - 6, x = on ? r.Right - d - 3 : r.Left + 3;
            g.FillEllipse(Brushes.White, x, r.Top + 3, d, d);
        }
    }
}
