// Подсказки: три вкладки с карточками "что нужно - куда нажимать". Страница окна ControlPanel.
// Новая функция в меню = новая карточка здесь
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ZapretTray
{
    class HintsView : IPanelView
    {
        // Всё влезает без прокрутки: самая длинная вкладка "Настроить", 5 рядов. Под неё подобрана высота окна ControlPanel
        const int Gap = 12, RowGap = 12, CardWidth = ControlPanel.CardWidth, CardHeight = 106, LegendHeight = 150;
        public const int Available = 5 * CardHeight + 4 * RowGap;

        class Hint
        {
            public string Icon, Title, Note;
            public Color Color;
            public string[] Steps;
            public Hint(string icon, Color color, string title, string note, params string[] steps)
            {
                Icon = icon; Color = color; Title = title; Note = note; Steps = steps;
            }
        }

        static readonly Color Blue = Color.FromArgb(14, 136, 232), Orange = Color.FromArgb(230, 126, 34), Teal = Color.FromArgb(0, 150, 160),
            Green = Color.FromArgb(32, 170, 92), Purple = Color.FromArgb(142, 84, 214), Slate = Color.FromArgb(96, 110, 135), Red = Color.FromArgb(218, 60, 60);

        static readonly Hint[] Broken =
        {
            new Hint("sites", Blue, "Не открывается сайт", "Не помогло: Подключение → Подобрать: быстро",
                "Мои сайты", "Впиши сайт или IP", "Добавить"),
            new Hint("undo", Orange, "Сайт сломался от программы", "Переключатель в строке этого сайта",
                "Мои сайты", "Выключи"),
            new Hint("ping", Teal, "Всё лагает", "Жди, пока Z снова станет зелёным",
                "Подключение", "Подобрать: минимальный пинг"),
            new Hint("games", Green, "Лагает игра", "Пока игра запущена, на главной виден её пинг",
                "Мои игры", "Добавить игру", "Запусти игру"),
            new Hint("dns", Purple, "Крестик у YouTube", "Не помогло: там же нажми «Исправить DNS»",
                "Подключение", "Подобрать: быстро"),
            new Hint("undo", Purple, "После «Исправить DNS» стало хуже", "Настройки DNS станут как раньше",
                "Подключение", "Вернуть DNS как было"),
            new Hint("shield", Slate, "Включён VPN", "С ним значки показывают сеть VPN, а не zapret",
                "Выключи VPN"),
            new Hint("report", Red, "Ничего не понятно", "Отчёт скопируется сам, вставь его в чат",
                "Подключение", "Что-то не работает", "Ctrl+V")
        };

        static readonly Hint[] Setup =
        {
            new Hint("list", Teal, "Выбрать конфиг самому", "В списке галочка стоит у текущего",
                "Подключение", "Выбрать конфиг вручную"),
            new Hint("games", Green, "Встроенные игры", "Клик включает или выключает обход игры",
                "Мои игры", "ARC Raiders или Wardogs"),
            new Hint("games", Green, "Выключить или убрать свою игру", "«Удалить» стирает и собранные адреса",
                "Мои игры", "Имя игры", "Включена или Удалить"),
            new Hint("sites", Blue, "Убрать сайт из списка", "Крестик справа в строке сайта",
                "Мои сайты", "Нажми ✕"),
            new Hint("theme", Slate, "Тёмная или светлая тема", "Чтобы как в Windows: Настройки → Тема",
                "День или Ночь внизу слева"),
            new Hint("config", Slate, "Сторож, автозапуск, уведомления", "И обновления: убери или поставь галочку",
                "Настройки", "Поведение", "Нужная галочка"),
            new Hint("save", Blue, "Перенести на другой компьютер", "Там: Настройки → Подгрузить настройки",
                "Настройки", "Сохранить настройки"),
            new Hint("refresh", Teal, "Обновить прямо сейчас", "Обычно программа обновляется сама",
                "Настройки", "Проверить обновления"),
            new Hint("folder", Orange, "Найти файлы программы", "Там лежат конфиги и журнал",
                "Настройки", "Открыть папку"),
            new Hint("trash", Red, "Удалить программу", "Сначала спросит. Твои сайты и игры тоже удалятся",
                "Настройки", "Удалить программу")
        };

        static readonly Hint[] Auto =
        {
            new Hint("shield", Green, "Сама чинит", "Проверяет YouTube и Discord. Сломалось: сменит конфиг или включит защищённый DNS"),
            new Hint("list", Teal, "Сама запоминает", "Помнит рабочие конфиги и проверяет их первыми, поэтому включается быстро"),
            new Hint("refresh", Blue, "Сама обновляется", "Раз в день проверяет обновления. Твои настройки остаются на месте"),
            new Hint("bell", Orange, "Когда придёт уведомление", "Когда сама не справилась, например не нашла рабочий конфиг. Остальное делает молча"),
            new Hint("sites", Slate, "Наведи мышку на Z", "Покажет текущий конфиг, скорость и пинг. Открывать окно не нужно"),
            new Hint("exit", Red, "Закрыть программу", "Кнопка внизу слева, zapret тоже выключится", "Выход")
        };

        static readonly string[] Tabs = { "Если сломалось", "Настроить", "Что значат значки" };

        const TextFormatFlags Line = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
        const TextFormatFlags Center = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

        readonly Font cardFont = new Font("Segoe UI Semibold", 11f);
        readonly Font titleFont = new Font("Segoe UI Semibold", 13f);
        readonly List<Rectangle> tabRects = new List<Rectangle>();
        public int Page;

        static Palette P { get { return Theme.Current; } }

        public int Extra { get { return 0; } }
        public void Activate(Control host, bool on) { }

        public void DrawHeader(Graphics g, Rectangle r, Point mouse)
        {
            tabRects.Clear();
            ControlPanel.PageTitle(g, "Подсказки", titleFont, r.Left, r.Top - 2, 34, 130);
            int x = r.Left + 140;
            for (int i = 0; i < Tabs.Length; i++)
            {
                var tab = new Rectangle(x, r.Top, TextRenderer.MeasureText(Tabs[i], MenuKit.Bold).Width + 28, 34);
                tabRects.Add(tab);
                if (i == Page) ControlPanel.Accent(g, tab, 9);
                else ControlPanel.Card(g, tab, tab.Contains(mouse) ? P.Hover : P.Card, 9);
                TextRenderer.DrawText(g, Tabs[i], MenuKit.Bold, tab, i == Page ? Color.White : P.Text, Center);
                x = tab.Right + 6;
            }
        }

        public int Draw(Graphics g, int x, int top, Point mouse)
        {
            int y = top;
            if (Page == 2)
            {
                DrawLegend(g, x, y);
                y += LegendHeight + RowGap;
            }
            var hints = Page == 0 ? Broken : Page == 1 ? Setup : Auto;
            for (int i = 0; i < hints.Length; i++)
                DrawCard(g, hints[i], x + (i % 2) * (CardWidth + Gap), y + (i / 2) * (CardHeight + RowGap));
            return Available;
        }

        public bool HeaderHot(Point p)
        {
            foreach (var tab in tabRects) if (tab.Contains(p)) return true;
            return false;
        }

        public bool Hot(Point p) { return false; }

        public bool Click(Point p)
        {
            for (int i = 0; i < tabRects.Count; i++)
            {
                if (!tabRects[i].Contains(p) || i == Page) continue;
                Page = i;
                return true;
            }
            return false;
        }

        // Что значат цвета Z и значки: две карточки, в каждой столбик "значок - подпись"
        void DrawLegend(Graphics g, int x, int y)
        {
            int rx = x + CardWidth + Gap;

            LegendCard(g, x, y, "Значок Z у часов", "Два клика по Z: включить или выключить");
            ControlPanel.Box(g, new Rectangle(x + 12, y + 10, 28, 28), Theme.On, 14);
            TextRenderer.DrawText(g, "Z", cardFont, new Rectangle(x + 12, y + 10, 28, 28), Color.White, Center);
            LegendDot(g, x, y + 46, Theme.On, "работает");
            LegendDot(g, x, y + 68, Theme.Off, "выключен");
            LegendDot(g, x, y + 90, Theme.Busy, "подбирает настройки, подожди");

            LegendCard(g, rx, y, "Значки на главной", "Нажми на плитку на главной, и она проверится заново");
            ControlPanel.Badge(g, new Rectangle(rx + 11, y + 9, 30, 30), P.Check, "list");
            LegendMark(g, rx, y + 46, "✓", "открывается");
            LegendMark(g, rx, y + 68, "✗", "не открывается");
            LegendMark(g, rx, y + 90, "3/4", "открылись не все");
        }

        void LegendCard(Graphics g, int x, int y, string title, string note)
        {
            ControlPanel.Card(g, new Rectangle(x, y, CardWidth, LegendHeight), P.Card, 10);
            TextRenderer.DrawText(g, title, cardFont, new Rectangle(x + 50, y + 10, CardWidth - 62, 28), P.Text, Line);
            TextRenderer.DrawText(g, note, MenuKit.Small, new Rectangle(x + 14, y + 120, CardWidth - 28, 20), P.SubText, Line);
        }

        // Значки стоят в колонке шириной с большой значок сверху, подписи начинаются на линии заголовка
        void LegendDot(Graphics g, int x, int y, Color color, string text)
        {
            using (var b = new SolidBrush(color)) g.FillEllipse(b, x + 20, y + 5, 12, 12);
            TextRenderer.DrawText(g, text, MenuKit.Font, new Rectangle(x + 50, y, CardWidth - 62, 22), P.Text, Line);
        }

        void LegendMark(Graphics g, int x, int y, string mark, string text)
        {
            bool count = mark.Length > 1;
            ThemedRenderer.DrawResult(g, mark, count ? x + 14 : x + 21, y + 11, 30, new Rectangle(x + 14, y, 30, 22));
            TextRenderer.DrawText(g, text, MenuKit.Font, new Rectangle(x + 50, y, CardWidth - 62, 22), P.Text, Line);
        }

        void DrawCard(Graphics g, Hint h, int x, int y)
        {
            ControlPanel.Card(g, new Rectangle(x, y, CardWidth, CardHeight), P.Card, 10);
            ControlPanel.Badge(g, new Rectangle(x + 11, y + 9, 30, 30), h.Color, h.Icon);
            TextRenderer.DrawText(g, h.Title, cardFont, new Rectangle(x + 50, y + 10, CardWidth - 62, 28), P.Text, Line);

            // Карточка без кнопок: просто объяснение на две строки
            if (h.Steps.Length == 0)
            {
                TextRenderer.DrawText(g, h.Note, MenuKit.Font, new Rectangle(x + 14, y + 46, CardWidth - 28, 50), P.SubText,
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak);
                return;
            }

            int px = x + 14, py = y + 44;
            for (int i = 0; i < h.Steps.Length; i++)
            {
                bool last = i == h.Steps.Length - 1;
                int w = TextRenderer.MeasureText(h.Steps[i], MenuKit.Small).Width + 12;
                if (last) ControlPanel.Accent(g, new Rectangle(px, py, w, 24), 7);
                else ControlPanel.Box(g, new Rectangle(px, py, w, 24), P.Tile, 7);
                TextRenderer.DrawText(g, h.Steps[i], MenuKit.Small, new Rectangle(px, py, w, 24), last ? Color.White : P.Text, Center);
                px += w;
                if (!last)
                {
                    using (var p = new Pen(P.SubText, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    {
                        int cy = py + 12;
                        g.DrawLine(p, px + 6, cy, px + 16, cy);
                        g.DrawLine(p, px + 12, cy - 4, px + 16, cy);
                        g.DrawLine(p, px + 12, cy + 4, px + 16, cy);
                    }
                    px += 22;
                }
            }
            TextRenderer.DrawText(g, h.Note, MenuKit.Small, new Rectangle(x + 14, y + 75, CardWidth - 28, 20), P.SubText, Line);
        }
    }

    // Поле ввода со своей серой подсказкой: системную нельзя покрасить, и на тёмном фоне она почти не видна
    class CueBox : TextBox
    {
        public string Cue;
        public Color CueColor;
        public bool CueAtTop;   // многострочное поле: подсказка сверху, а не посередине
        public bool HideCueOnFocus;   // нажал в поле, чтобы писать, - подсказка уходит

        // Поле перерисовывает только строку с курсором - остальные строки подсказки оставались бы на экране
        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            if (HideCueOnFocus) Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            if (HideCueOnFocus) Invalidate();
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            if (Cue != null) Invalidate();
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg != 0xF || TextLength > 0 || Cue == null || HideCueOnFocus && Focused) return;   // WM_PAINT
            // Подсказка чуть правее начала строки: иначе мигающий курсор стоит прямо на первой букве
            var r = ClientRectangle;
            r.X += 4;
            r.Width -= 4;
            using (var g = CreateGraphics())
                TextRenderer.DrawText(g, Cue, Font, r, CueColor, BackColor, CueAtTop
                    ? TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding
                    : TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }
    }

    // Мои сайты: сверху поле ввода, ниже строки с переключателем и крестиком. Включён - обход, выключен - исключение
    class SitesView : IPanelView
    {
        const int RowHeight = 54, Gap = 8, ContentWidth = ControlPanel.CardWidth * 2 + 12;

        static readonly Font TitleFont = new Font("Segoe UI Semibold", 13f);

        readonly Func<List<Tuple<string, string, bool>>> list;
        readonly Action<string, bool> add;
        readonly Action<Tuple<string, string, bool>, bool> setExclude;
        readonly Action<Tuple<string, string, bool>> remove;

        readonly CueBox box = new CueBox { BorderStyle = BorderStyle.None, Font = MenuKit.Font, Cue = "Вставь любую ссылку, сайт или IP: https, www и остальное лишнее уберётся само" };
        readonly List<KeyValuePair<Rectangle, Action>> hots = new List<KeyValuePair<Rectangle, Action>>();
        Rectangle addButton;
        List<Tuple<string, string, bool>> entries;
        string error;
        Control host;

        static Palette P { get { return Theme.Current; } }

        const TextFormatFlags Line = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
        const TextFormatFlags Center = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

        public SitesView(Func<List<Tuple<string, string, bool>>> list, Action<string, bool> add,
            Action<Tuple<string, string, bool>, bool> setExclude, Action<Tuple<string, string, bool>> remove)
        {
            this.list = list;
            this.add = add;
            this.setExclude = setExclude;
            this.remove = remove;
            box.Enter += (s, e) => { if (host != null) host.Invalidate(); };
            box.Leave += (s, e) => { if (host != null) host.Invalidate(); };
            box.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true;
                Add();
            };
        }

        public int Extra { get { return 50; } }

        public void Activate(Control host, bool on)
        {
            this.host = host;
            if (on)
            {
                entries = list();
                error = null;
                if (box.Parent == null) host.Controls.Add(box);
                box.Show();
                box.Focus();
            }
            else box.Hide();
        }

        void Add()
        {
            string value;
            bool isIp;
            if (!Util.ParseEntry(box.Text, out value, out isIp))
            {
                error = "Не понял, что это. Впиши домен (youtube.com) или IP (1.2.3.4).";
                if (host != null) host.Invalidate();
                return;
            }
            error = null;
            add(value, isIp);
            box.Clear();
            box.Focus();
            entries = list();
            if (host != null) host.Invalidate();
        }

        public void DrawHeader(Graphics g, Rectangle r, Point mouse)
        {
            ControlPanel.PageTitle(g, "Мои сайты", TitleFont, r.Left, r.Top - 2, 34, 120);
            if (entries != null && entries.Count > 0)
            {
                var count = new Rectangle(r.Left + 124, r.Top + 3, 32, 24);
                ControlPanel.Box(g, count, P.Tile, 8);
                TextRenderer.DrawText(g, entries.Count.ToString(), MenuKit.Small, count, P.SubText, Center);
            }

            var field = new Rectangle(r.Left, r.Top + 44, ContentWidth - 130, 36);
            // Заливка ровная, без перелива: иначе прямоугольник самого поля ввода заметен на фоне
            var fill = ControlPanel.Mix(P.Back, P.Card, 0.5f);
            ControlPanel.Box(g, field, fill, 9);
            using (var path = ThemedRenderer.RoundRect(field, 9))
            using (var pen = new Pen(box.Focused ? P.Check : P.Separator)) g.DrawPath(pen, path);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            ThemedRenderer.DrawIcon(g, "sites", field.Left + 12, field.Top + 18, P.SubText);
            box.BackColor = fill;
            box.ForeColor = P.Text;
            box.CueColor = P.SubText;
            // Поле ввода - настоящее окно поверх рисунка: его место и шрифт переводим в масштаб окна
            var panel = host as ControlPanel;
            var place = new Rectangle(field.Left + 38, field.Top + 9, field.Width - 50, 18);
            if (panel != null)
            {
                place = panel.ToDevice(place);
                box.Font = TextRenderer.Scaled(MenuKit.Font, panel.FontScale);
            }
            box.SetBounds(place.X, place.Y, place.Width, place.Height);

            addButton = new Rectangle(field.Right + 10, field.Top, 120, 36);
            ControlPanel.Accent(g, addButton, 9);
            TextRenderer.DrawText(g, "Добавить", MenuKit.Bold, addButton, Color.White, Center);
        }

        public int Draw(Graphics g, int x, int top, Point mouse)
        {
            hots.Clear();
            int y = top;
            bool failed = error != null;
            TextRenderer.DrawText(g, failed ? error : "Включено: zapret помогает сайту открыться. Выключено: zapret его не трогает.", MenuKit.Small,
                new Rectangle(x, y, ContentWidth, 22), failed ? Theme.Bad : P.SubText, Line);
            y += 30;

            if (entries == null || entries.Count == 0)
            {
                TextRenderer.DrawText(g, "Пока пусто. Впиши сайт сверху и нажми «Добавить».", MenuKit.Font, new Rectangle(x, y, ContentWidth, 30), P.Disabled, Line);
                return y - top + 30;
            }
            foreach (var e in entries)
            {
                var entry = e;
                var row = new Rectangle(x, y, ContentWidth, RowHeight);
                ControlPanel.Card(g, row, P.Card, 10);
                var badge = new Rectangle(x + 11, y + 10, 34, 34);
                bool isIp = entry.Item2.IndexOf("ipset", StringComparison.OrdinalIgnoreCase) >= 0;
                ControlPanel.Badge(g, badge, entry.Item3 ? Color.FromArgb(230, 126, 34) : Color.FromArgb(14, 136, 232), isIp ? "dns" : "sites");
                TextRenderer.DrawText(g, entry.Item1, MenuKit.Bold, new Rectangle(x + 54, y, ContentWidth - 250, RowHeight), P.Text, Line);

                var del = new Rectangle(row.Right - 44, y + 11, 32, 32);
                var toggle = new Rectangle(del.Left - 52, y + 16, 40, 22);
                bool on = !entry.Item3;
                TextRenderer.DrawText(g, on ? "Обход" : "Исключён", MenuKit.Small, new Rectangle(toggle.Left - 92, y, 84, RowHeight), on ? P.Text : P.SubText,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                ControlPanel.DrawSwitch(g, toggle, on);

                if (del.Contains(mouse)) ControlPanel.Box(g, del, Theme.Bad, 8);
                using (var p = new Pen(del.Contains(mouse) ? Color.White : P.SubText, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawLine(p, del.Left + 11, del.Top + 11, del.Right - 11, del.Bottom - 11);
                    g.DrawLine(p, del.Right - 11, del.Top + 11, del.Left + 11, del.Bottom - 11);
                }

                hots.Add(new KeyValuePair<Rectangle, Action>(toggle, () => setExclude(entry, on)));
                hots.Add(new KeyValuePair<Rectangle, Action>(del, () => remove(entry)));
                y += RowHeight + Gap;
            }
            return y - top;
        }

        public bool HeaderHot(Point p) { return addButton.Contains(p); }

        public bool Hot(Point p)
        {
            foreach (var h in hots) if (h.Key.Contains(p)) return true;
            return false;
        }

        public bool Click(Point p)
        {
            if (addButton.Contains(p))
            {
                Add();
                return false;
            }
            foreach (var h in hots)
            {
                if (!h.Key.Contains(p)) continue;
                h.Value();
                entries = list();
                break;
            }
            return false;
        }
    }

    // Окно "Отправить баг" в стиле главного окна: без рамки Windows, своё поле и кнопки
    class BugDialog : Form
    {
        const int W = 460, H = 340, Pad = 20;
        static readonly Font TitleFont = new Font("Segoe UI Semibold", 13f);
        readonly CueBox box = new CueBox { Multiline = true, BorderStyle = BorderStyle.None, ScrollBars = ScrollBars.None, Font = MenuKit.Font, MaxLength = 1500, CueAtTop = true, HideCueOnFocus = true,
            Cue = "Что не так? Например: YouTube не грузит видео, Discord отваливается.\nМожно оставить пустым." };
        readonly Rectangle field = new Rectangle(Pad, 78, W - Pad * 2, 118);
        readonly Rectangle sendButton = new Rectangle(W - Pad - 130, H - Pad - 38, 130, 38);
        readonly Rectangle cancelButton = new Rectangle(W - Pad - 130 - 10 - 100, H - Pad - 38, 100, 38);
        readonly Rectangle closeButton = new Rectangle(W - 44, 10, 34, 28);
        string hover;

        static Palette P { get { return Theme.Current; } }

        public string Comment { get { return box.Text; } }

        public BugDialog()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            TopMost = true;
            DoubleBuffered = true;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(W, H);
            Text = "Отправить баг";
            Icon = Theme.AppIcon();
            BackColor = P.Back;
            var fill = ControlPanel.Mix(P.Back, P.Card, 0.6f);
            box.BackColor = fill;
            box.ForeColor = P.Text;
            box.CueColor = P.SubText;
            box.SetBounds(field.Left + 14, field.Top + 12, field.Width - 28, field.Height - 24);
            box.GotFocus += (s, e) => Invalidate();
            box.LostFocus += (s, e) => Invalidate();
            box.TextChanged += (s, e) => Invalidate(new Rectangle(0, field.Bottom, W, 30));
            Controls.Add(box);
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) Finish(DialogResult.Cancel);
                else if (e.KeyCode == Keys.Enter && e.Control) { e.SuppressKeyPress = true; Finish(DialogResult.OK); }
            };
            // Поле не в фокусе сразу: пусть подсказку видно, пока не нажал в поле
            Shown += (s, e) => { Theme.RoundCorners(this); ActiveControl = null; };
            // Кликнул мимо - окно закрывается, как и сама панель
            Deactivate += (s, e) => Finish(DialogResult.Cancel);
        }

        bool finished;

        void Finish(DialogResult result)
        {
            if (finished) return;
            finished = true;
            DialogResult = result;
            Close();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(P.Back);
            ControlPanel.Glow(g, new Rectangle(W - 260, -200, 440, 360), P.Check, 36);
            using (var pen = new Pen(P.Border)) g.DrawRectangle(pen, 0, 0, W - 1, H - 1);

            ControlPanel.Badge(g, new Rectangle(Pad, Pad, 40, 40), Color.FromArgb(14, 136, 232), "load");
            const TextFormatFlags Line = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
            TextRenderer.DrawText(g, "Отправить баг", TitleFont, new Rectangle(Pad + 54, Pad - 2, 300, 26), P.Text, Line);
            TextRenderer.DrawText(g, "Отчёт о соединении сам уйдёт разработчику", MenuKit.Small, new Rectangle(Pad + 54, Pad + 22, 320, 20), P.SubText, Line);

            if (hover == "close") ControlPanel.Box(g, closeButton, Theme.Bad, 7);
            using (var p = new Pen(hover == "close" ? Color.White : P.SubText, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                int cx = closeButton.Left + closeButton.Width / 2, cy = closeButton.Top + closeButton.Height / 2;
                g.DrawLine(p, cx - 5, cy - 5, cx + 5, cy + 5);
                g.DrawLine(p, cx + 5, cy - 5, cx - 5, cy + 5);
            }

            ControlPanel.Box(g, field, box.BackColor, 10);
            using (var path = ThemedRenderer.RoundRect(field, 10))
            using (var pen = new Pen(box.Focused ? P.Check : P.Separator)) g.DrawPath(pen, path);
            TextRenderer.DrawText(g, box.TextLength + " / " + box.MaxLength, MenuKit.Small, new Rectangle(field.Right - 120, field.Bottom + 4, 120, 20), P.Disabled,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            // Правило программы: никаких личных данных. Пишем прямо, что уйдёт и чего не будет
            TextRenderer.DrawText(g, "Твоих личных данных в отчёте нет", MenuKit.Bold, new Rectangle(Pad, field.Bottom + 6, 300, 20), Theme.On, Line);
            TextRenderer.DrawText(g, "В любом случае уйдёт технический отчёт: что открылось, настройки и журнал программы.", MenuKit.Small, new Rectangle(Pad, field.Bottom + 28, W - Pad * 2, 40), P.SubText,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak);

            const TextFormatFlags Center = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            ControlPanel.Card(g, cancelButton, hover == "cancel" ? P.Hover : P.Card, 10);
            TextRenderer.DrawText(g, "Отмена", MenuKit.Bold, cancelButton, P.Text, Center);
            ControlPanel.Accent(g, sendButton, 10);
            if (hover == "send") using (var b = new SolidBrush(Color.FromArgb(40, Color.White))) using (var path = ThemedRenderer.RoundRect(sendButton, 10)) g.FillPath(b, path);
            TextRenderer.DrawText(g, "Отправить", MenuKit.Bold, sendButton, Color.White, Center);
            TextRenderer.DrawText(g, "Ctrl+Enter", MenuKit.Small, new Rectangle(Pad, sendButton.Top, 160, sendButton.Height), P.Disabled, Line);
        }

        string HitTest(Point p)
        {
            if (sendButton.Contains(p)) return "send";
            if (cancelButton.Contains(p)) return "cancel";
            if (closeButton.Contains(p)) return "close";
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var hit = HitTest(e.Location);
            if (field.Contains(e.Location)) Cursor = Cursors.IBeam;
            else Cursor = hit != null ? Cursors.Hand : Cursors.Default;
            if (hit == hover) return;
            hover = hit;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hover = null;
            Invalidate();
        }

        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (field.Contains(e.Location)) { box.Focus(); return; }
            // Нажал мимо поля - поле теряет фокус, и пустому возвращается подсказка
            ActiveControl = null;
            Invalidate();
            var hit = HitTest(e.Location);
            if (hit == "send") Finish(DialogResult.OK);
            else if (hit == "cancel" || hit == "close") Finish(DialogResult.Cancel);
            else
            {
                // Тащим окно за любое пустое место
                ReleaseCapture();
                SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);
            }
        }
    }

    // "Что нового": один раз после обновления. Пункты для каждой версии пишем здесь; нет пунктов - окна нет.
    // Version = Welcome - то же окно для первой установки: коротко, что где
    class WhatsNewDialog : Form
    {
        class Item
        {
            public string Icon, Title, Note;
            public Color Color;
            public Item(string icon, Color color, string title, string note) { Icon = icon; Color = color; Title = title; Note = note; }
        }

        static readonly Dictionary<string, Item[]> Notes = new Dictionary<string, Item[]>
        {
            { "0.7", new[] {
                new Item("ping", Color.FromArgb(142, 84, 233), "Графики за час", "В каждой плитке. Наведи мышь - покажет число"),
                new Item("load", Color.FromArgb(14, 136, 232), "Отправить баг", "Кнопка на главной, без личных данных"),
                new Item("monitor", Color.FromArgb(96, 110, 140), "Размер окна", "Ползунок в настройках, от 75 до 200%"),
                new Item("dns", Color.FromArgb(0, 150, 160), "DNS чинится сам", "Если провайдер подменяет адрес YouTube"),
                new Item("shield", Color.FromArgb(46, 160, 67), "Спокойнее с VPN", "Конфиг больше не скачет при запуске"),
                new Item("report", Color.FromArgb(230, 126, 34), "Понятнее отчёт", "Сразу пишет, в чём дело и что нажать") } },
            { Welcome, new[] {
                new Item("power", Color.FromArgb(46, 160, 67), "Всё уже работает", "Программа сама подобрала настройки"),
                new Item("ping", Color.FromArgb(142, 84, 233), "Плитки на главной", "Галочка - открывается. Нажми плитку - проверит заново"),
                new Item("games", Color.FromArgb(230, 126, 34), "Мои игры", "Добавь игру - обход включится и для неё"),
                new Item("load", Color.FromArgb(14, 136, 232), "Что-то не так?", "«Отправить баг» - и разработчик посмотрит") } },
        };

        public const string Welcome = "welcome";

        public static bool Has(string version) { return Notes.ContainsKey(version); }

        const int W = 460, Pad = 20, ListTop = 84, Row = 62;
        static readonly Font TitleFont = new Font("Segoe UI Semibold", 13f), ItemFont = new Font("Segoe UI Semibold", 10.5f);
        readonly Item[] items;
        readonly string version;
        Rectangle okButton, closeButton;
        string hover;

        static Palette P { get { return Theme.Current; } }

        public WhatsNewDialog(string version)
        {
            this.version = version;
            items = Notes[version];
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            TopMost = true;
            DoubleBuffered = true;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.None;
            int h = ListTop + items.Length * Row + 70;
            ClientSize = new Size(W, h);
            okButton = new Rectangle(W - Pad - 130, h - Pad - 38, 130, 38);
            closeButton = new Rectangle(W - 44, 10, 34, 28);
            Text = version == Welcome ? "Zapret Tray" : "Что нового";
            Icon = Theme.AppIcon();
            BackColor = P.Back;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape || e.KeyCode == Keys.Enter) Close(); };
            Shown += (s, e) => Theme.RoundCorners(this);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(P.Back);
            ControlPanel.Glow(g, new Rectangle(W - 260, -200, 440, 360), P.Check, 36);
            using (var pen = new Pen(P.Border)) g.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);

            const TextFormatFlags Line = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
            // Логотип программы, как в колонке главного окна
            ControlPanel.Glow(g, new Rectangle(Pad - 12, Pad - 12, 64, 64), Theme.On, 80);
            ControlPanel.Box(g, new Rectangle(Pad, Pad, 40, 40), Theme.On, 20);
            TextRenderer.DrawText(g, "Z", TitleFont, new Rectangle(Pad, Pad, 40, 40), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            bool welcome = version == Welcome;
            TextRenderer.DrawText(g, welcome ? "Привет! Это Zapret Tray" : "Обновлено до " + version, TitleFont, new Rectangle(Pad + 54, Pad - 2, 330, 26), P.Text, Line);
            TextRenderer.DrawText(g, welcome ? "Коротко, что тут где" : "Вот что появилось", MenuKit.Small, new Rectangle(Pad + 54, Pad + 22, 300, 20), P.SubText, Line);

            if (hover == "close") ControlPanel.Box(g, closeButton, Theme.Bad, 7);
            using (var p = new Pen(hover == "close" ? Color.White : P.SubText, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                int cx = closeButton.Left + closeButton.Width / 2, cy = closeButton.Top + closeButton.Height / 2;
                g.DrawLine(p, cx - 5, cy - 5, cx + 5, cy + 5);
                g.DrawLine(p, cx + 5, cy - 5, cx - 5, cy + 5);
            }

            // Пункты одним спокойным списком
            var list = new Rectangle(Pad, ListTop, W - Pad * 2, items.Length * Row);
            ControlPanel.Card(g, list, P.Card, 12);
            for (int i = 0; i < items.Length; i++)
            {
                int y = ListTop + i * Row;
                if (i > 0) using (var pen = new Pen(P.Separator)) g.DrawLine(pen, list.Left + 62, y, list.Right - 14, y);
                ControlPanel.Badge(g, new Rectangle(list.Left + 14, y + 13, 36, 36), items[i].Color, items[i].Icon);
                TextRenderer.DrawText(g, items[i].Title, ItemFont, new Rectangle(list.Left + 62, y + 9, list.Width - 76, 22), P.Text, Line);
                TextRenderer.DrawText(g, items[i].Note, MenuKit.Small, new Rectangle(list.Left + 62, y + 31, list.Width - 76, 20), P.SubText, Line);
            }

            ControlPanel.Accent(g, okButton, 10);
            if (hover == "ok") using (var b = new SolidBrush(Color.FromArgb(40, Color.White))) using (var path = ThemedRenderer.RoundRect(okButton, 10)) g.FillPath(b, path);
            TextRenderer.DrawText(g, "Понятно", MenuKit.Bold, okButton, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            TextRenderer.DrawText(g, "Всё это есть в «Подсказках»", MenuKit.Small, new Rectangle(Pad, okButton.Top, 240, okButton.Height), P.Disabled, Line);
        }

        string HitTest(Point p)
        {
            if (okButton.Contains(p)) return "ok";
            if (closeButton.Contains(p)) return "close";
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var hit = HitTest(e.Location);
            Cursor = hit != null ? Cursors.Hand : Cursors.Default;
            if (hit == hover) return;
            hover = hit;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hover = null;
            Invalidate();
        }

        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (HitTest(e.Location) != null) { Close(); return; }
            ReleaseCapture();
            SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);
        }
    }
}