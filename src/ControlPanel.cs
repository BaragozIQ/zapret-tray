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

        readonly InfoItem status, health, stats;
        readonly Func<Color> statusColor;
        readonly ToolStripMenuItem[] sections;
        readonly ToolStripMenuItem toggle, exit;
        readonly ToolStripMenuItem[] shortcuts;
        readonly Action refresh;
        readonly Dictionary<ToolStripMenuItem, IPanelView> views;
        Point mouse;

        readonly Font titleFont = new Font("Segoe UI Semibold", 13f);
        readonly Font heroFont = new Font("Segoe UI Semibold", 16f);
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

        public ControlPanel(InfoItem status, InfoItem health, InfoItem stats, Func<Color> statusColor,
            ToolStripMenuItem[] sections, ToolStripMenuItem[] shortcuts, ToolStripMenuItem toggle, ToolStripMenuItem exit, Action refresh,
            Dictionary<ToolStripMenuItem, IPanelView> views)
        {
            this.status = status;
            this.health = health;
            this.stats = stats;
            this.statusColor = statusColor;
            this.sections = sections;
            this.shortcuts = shortcuts;
            this.toggle = toggle;
            this.exit = exit;
            this.refresh = refresh;
            this.views = views;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
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
            // Закрываем после текущего сообщения: окно могло потерять фокус прямо посреди нажатия
            Deactivate += (s, e) => BeginInvoke((Action)(() => { if (!IsDisposed) Close(); }));
        }

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        // Куда пользователь перетащил окно. Живёт, пока программа запущена
        static Point? lastPosition;

        // По центру экрана, где курсор, или там, куда окно перетащили
        public void ShowCentered()
        {
            var at = lastPosition ?? Point.Empty;
            var screen = Screen.FromPoint(lastPosition.HasValue ? at : Cursor.Position).WorkingArea;
            if (!lastPosition.HasValue) at = new Point(screen.Left + (screen.Width - Width) / 2, screen.Top + (screen.Height - Height) / 2);
            Left = Math.Max(screen.Left, Math.Min(at.X, screen.Right - Width));
            Top = Math.Max(screen.Top, Math.Min(at.Y, screen.Bottom - Height));
            Show();
            Theme.RoundCorners(this);
            SetForegroundWindow(Handle);
            Activate();
        }

        // Тащим за любое пустое место: Windows сама двигает окно, как за заголовок
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || HitTest(e.Location) != null) return;
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
            foreach (var v in views.Values) v.Activate(this, v == current);
        }

        Rectangle SectionTab(int i)
        {
            return new Rectangle(10, homeTab.Bottom + 6 + i * 48, SideWidth - 20, 42);
        }

        // Внизу колонки, под линией: выключить и выход
        Rectangle ToggleTab { get { return new Rectangle(10, ClientSize.Height - 100, SideWidth - 20, 42); } }
        Rectangle ExitTab { get { return new Rectangle(10, ClientSize.Height - 52, SideWidth - 20, 42); } }

        Rectangle BackButton { get { return new Rectangle(SideWidth + Pad, 14, 30, 30); } }

        Rectangle ContentArea
        {
            get
            {
                var view = CurrentView;
                int top = HeaderHeight + (view != null ? view.Extra : 0);
                return new Rectangle(SideWidth, top, ClientSize.Width - SideWidth, ClientSize.Height - top);
            }
        }

        // ---------- мышь ----------

        object HitTest(Point p)
        {
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

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            mouse = e.Location;
            var hit = HitTest(e.Location);
            if (Equals(hit, hover) && CurrentView == null) return;
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
            var hit = HitTest(e.Location);
            if (hit == null) return;
            if (hit is Rectangle) OpenSection(null);
            else if (hit as string == "back") Back();
            else if (hit as string == "view") { if (CurrentView.Click(e.Location)) scroll = 0; }
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

        public static void Box(Graphics g, Rectangle r, Color color, int radius)
        {
            using (var path = ThemedRenderer.RoundRect(r, radius))
            using (var b = new SolidBrush(color)) g.FillPath(b, path);
        }

        // Карточка: заливка и тонкая рамка
        public static void Card(Graphics g, Rectangle r, Color fill, int radius, Color? border = null)
        {
            using (var path = ThemedRenderer.RoundRect(r, radius))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                using (var p = new Pen(border ?? P.Separator)) g.DrawPath(p, path);
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
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(P.Back);   // тема могла смениться, пока окно открыто
            DrawSidebar(g);
            if (path.Count == 0) DrawHome(g);
            else if (CurrentView != null) DrawView(g);
            else DrawSection(g);
            using (var p = new Pen(P.Border)) g.DrawRectangle(p, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        }

        void DrawSidebar(Graphics g)
        {
            using (var b = new SolidBrush(P.Card)) g.FillRectangle(b, 0, 0, SideWidth, ClientSize.Height);
            // Логотип
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
            DrawTab(g, ToggleTab, toggle.Text, "power", false, hover == toggle, on ? Theme.Bad : Theme.On);
            DrawTab(g, ExitTab, exit.Text, "exit", false, hover == exit);

            // Статус и версия видны всегда
            int y = lineY - 56;
            using (var b = new SolidBrush(statusColor())) g.FillEllipse(b, 20, y + 8, 9, 9);
            TextRenderer.DrawText(g, status.Value.Split((char)9)[0], MenuKit.Small, new Rectangle(36, y, SideWidth - 44, 26), P.SubText, Line);
            TextRenderer.DrawText(g, "Версия " + AppInfo.Version, MenuKit.Small, new Rectangle(20, y + 24, SideWidth - 28, 22), P.Disabled, Line);
        }

        void DrawTab(Graphics g, Rectangle r, string text, string icon, bool active, bool hovered, Color? accent = null)
        {
            if (active || hovered) Box(g, r, active ? P.Check : P.Hover, 9);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            ThemedRenderer.DrawIcon(g, icon, r.Left + 12, r.Top + r.Height / 2, active ? Color.White : accent ?? P.SubText);
            TextRenderer.DrawText(g, text, MenuKit.Bold, new Rectangle(r.Left + 38, r.Top, r.Width - 42, r.Height), active ? Color.White : accent ?? P.Text, Line);
        }

        void DrawHome(Graphics g)
        {
            cards.Clear();
            contentHeight = 0;
            const int actionHeight = 84, top = 20;
            int x = SideWidth + Pad, w = CardWidth * 2 + Gap;
            int actionsTop = ClientSize.Height - Pad - actionHeight;
            int h = actionsTop - Gap - top;

            if (health.Available)
            {
                // Два ровных ряда плиток прямо на фоне окна, как карточки на других страницах
                int rest = h - Gap, servicesHeight = rest * 11 / 20;
                DrawServices(g, x, top, w, servicesHeight);
                DrawNumbers(g, x, top + servicesHeight + Gap, w, rest - servicesHeight);
            }
            else
                DrawOffStatus(g, x, top, w, h);

            AddCard(g, shortcuts[0], new Rectangle(x, actionsTop, CardWidth, actionHeight), 0);
            AddCard(g, shortcuts[1], new Rectangle(x + CardWidth + Gap, actionsTop, CardWidth, actionHeight), 0);
        }

        static readonly string[] ServiceNames = { "YouTube", "Discord", "Игры", "Мои сайты" };

        // Плитки: крупный значок и результат сверху, название снизу. Пустая часть - плитки нет, остальные делят ширину поровну
        void DrawServices(Graphics g, int x, int y, int w, int h)
        {
            var parts = health.Value.Split((char)9);
            // Всегда 4 плитки, чтобы ряды совпадали. Проверять нечего - вместо результата прочерк
            int count = ServiceNames.Length;
            int tile = (w - Gap * (count - 1)) / count, n = 0;
            for (int i = 0; i < count; i++)
            {
                var part = i < parts.Length ? parts[i] : "";
                int left = x + (tile + Gap) * n++, mid = left + tile / 2, cy = y + h / 2 - 32;
                Card(g, new Rectangle(left, y, tile, h), P.Card, 10);

                // По центру плитки столбиком: значок, название, результат
                var state = g.Save();
                g.TranslateTransform(mid - 27, cy);
                g.ScaleTransform(3f, 3f);
                ThemedRenderer.DrawService(g, i, 0, 0);
                g.Restore(state);

                var centered = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
                TextRenderer.DrawText(g, ServiceNames[i], valueFont, new Rectangle(left + 6, cy + 28, tile - 12, 28), P.Text, centered);

                int ry = cy + 72;
                if (part.Length == 0)
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
                    g.TranslateTransform(mid - 8, ry);
                    g.ScaleTransform(1.6f, 1.6f);
                    ThemedRenderer.DrawResult(g, part, 0, 0, 12, new Rectangle(0, -8, 12, 16));
                    g.Restore(mark);
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

            Card(g, new Rectangle(x, y, tile, h), P.Card, 10);
            var parts = status.Value.Split((char)9);
            var color = statusColor();
            int cx = x + tile / 2, dotY = y + h / 2 - 46;
            using (var b = new SolidBrush(Color.FromArgb(60, color))) g.FillEllipse(b, cx - 11, dotY - 3, 22, 22);
            using (var b = new SolidBrush(color)) g.FillEllipse(b, cx - 6, dotY + 2, 12, 12);
            // Длинный текст подбора: мельче и на всю высоту, чтобы влез целиком
            bool longText = parts[0].Length > 22;
            TextRenderer.DrawText(g, parts[0], longText ? MenuKit.Bold : valueFont, new Rectangle(x + 8, dotY + 22, tile - 16, parts.Length > 1 ? 44 : 70), P.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
            if (parts.Length > 1)
                TextRenderer.DrawText(g, parts[1], MenuKit.Small, new Rectangle(x + 8, dotY + 66, tile - 16, 22), P.SubText, centered);

            for (int i = 0; i < count; i++)
            {
                int left = x + (tile + Gap) * (i + 1);
                Card(g, new Rectangle(left, y, tile, h), P.Card, 10);
                TextRenderer.DrawText(g, numbers[i], heroFont, new Rectangle(left + 6, y + h / 2 - 34, tile - 12, 44), P.Text, centered);
                TextRenderer.DrawText(g, NumberNames[i], MenuKit.Font, new Rectangle(left + 8, y + h / 2 + 10, tile - 16, 24), P.SubText, centered);
            }
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
            using (var b = new SolidBrush(P.Back)) g.FillRectangle(b, SideWidth, 0, ClientSize.Width - SideWidth, area.Top);
        }

        void DrawScrollBar(Graphics g, Rectangle area)
        {
            if (MaxScroll <= 0) return;
            int barHeight = Math.Max(30, area.Height * area.Height / contentHeight);
            int barTop = area.Top + (area.Height - barHeight) * scroll / MaxScroll;
            Box(g, new Rectangle(ClientSize.Width - 8, barTop, 4, barHeight), P.Hover, 2);
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
                    TextRenderer.DrawText(g, caption, MenuKit.Small, new Rectangle(SideWidth + Pad + 2, y - scroll, CardWidth, 24), P.SubText, Line);
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
                Box(g, BackButton, Equals(hover, "back") ? P.Hover : P.Card, 8);
                using (var p = new Pen(P.Text, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    int cx = BackButton.Left + 13, my = BackButton.Top + 15;
                    g.DrawLine(p, cx + 3, my - 5, cx - 2, my);
                    g.DrawLine(p, cx - 2, my, cx + 3, my + 5);
                }
                titleX += 40;
            }
            TextRenderer.DrawText(g, section.Text.TrimEnd('.'), titleFont, new Rectangle(titleX, 12, CardWidth * 2 - 40, 34), P.Text, Line);
            DrawScrollBar(g, area);
        }

        // Карточка пункта меню: значок, название, примечание из скобок, справа стрелка, галочка или переключатель
        void AddCard(Graphics g, ToolStripMenuItem item, Rectangle r, int offset)
        {
            cards.Add(new KeyValuePair<Rectangle, ToolStripMenuItem>(r, item));
            r.Y -= offset;
            bool enabled = item.Enabled;
            Card(g, r, hover == item && enabled ? P.Hover : P.Card, 10, item.Checked && !IsSwitch(item) ? P.Check : (Color?)null);

            var badge = new Rectangle(r.Left + 12, r.Top + (r.Height - 30) / 2, 30, 30);
            Box(g, badge, enabled ? BadgeColor(item) : P.Tile, 8);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            ThemedRenderer.DrawIcon(g, Icon_(item), badge.Left + 7, badge.Top + 15, enabled ? Color.White : P.Disabled);

            int right = r.Right - 12;
            if (IsSwitch(item))
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

        static bool IsSwitch(ToolStripMenuItem item)
        {
            return item.CheckOnClick || (item.Tag as string) == ItemKind.Toggle;
        }

        public static void DrawSwitch(Graphics g, Rectangle r, bool on)
        {
            Box(g, r, on ? P.Check : P.Tile, r.Height / 2 - 1);
            int d = r.Height - 6, x = on ? r.Right - d - 3 : r.Left + 3;
            g.FillEllipse(Brushes.White, x, r.Top + 3, d, d);
        }
    }
}
