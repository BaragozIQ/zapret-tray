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
            new Hint("games", Green, "Лагает игра", "Сервера игры добавятся сами",
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
            new Hint("theme", Slate, "Тёмная или светлая тема", "Или как в Windows",
                "Настройки", "Тема", "Выбери"),
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
            new Hint("shield", Green, "Сама чинит", "Раз в 10 минут и после сна проверяет YouTube и Discord. Сломалось: подберёт другой конфиг"),
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
        readonly List<Rectangle> tabRects = new List<Rectangle>();
        public int Page;

        static Palette P { get { return Theme.Current; } }

        public int Extra { get { return 0; } }
        public void Activate(Control host, bool on) { }

        public void DrawHeader(Graphics g, Rectangle r, Point mouse)
        {
            tabRects.Clear();
            int x = r.Left;
            for (int i = 0; i < Tabs.Length; i++)
            {
                var tab = new Rectangle(x, r.Top, TextRenderer.MeasureText(Tabs[i], MenuKit.Bold).Width + 28, 34);
                tabRects.Add(tab);
                ControlPanel.Box(g, tab, i == Page ? P.Check : tab.Contains(mouse) ? P.Hover : P.Card, 9);
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

            LegendCard(g, rx, y, "Значки на главной", "Правая кнопка по Z: открыть это окно");
            ControlPanel.Box(g, new Rectangle(rx + 12, y + 10, 28, 28), P.Check, 8);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            ThemedRenderer.DrawIcon(g, "list", rx + 18, y + 24, Color.White);
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
            ControlPanel.Box(g, new Rectangle(x + 12, y + 10, 28, 28), h.Color, 8);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            ThemedRenderer.DrawIcon(g, h.Icon, x + 18, y + 24, Color.White);
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
                ControlPanel.Box(g, new Rectangle(px, py, w, 24), last ? P.Check : P.Tile, 7);
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

    // Мои сайты: сверху поле ввода, ниже строки с переключателем и крестиком. Включён - обход, выключен - исключение
    class SitesView : IPanelView
    {
        const int RowHeight = 54, Gap = 8, ContentWidth = ControlPanel.CardWidth * 2 + 12;

        static readonly Font TitleFont = new Font("Segoe UI Semibold", 13f);

        readonly Func<List<Tuple<string, string, bool>>> list;
        readonly Action<string, bool> add;
        readonly Action<Tuple<string, string, bool>, bool> setExclude;
        readonly Action<Tuple<string, string, bool>> remove;

        readonly TextBox box = new TextBox { BorderStyle = BorderStyle.None, Font = MenuKit.Font };
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
            // Серая подсказка в пустом поле
            box.HandleCreated += (s, e) => SendMessage(box.Handle, 0x1501, (IntPtr)1, "Впиши сайт или IP: youtube.com или 1.2.3.4");
            box.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true;
                Add();
            };
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

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
            TextRenderer.DrawText(g, "Мои сайты", TitleFont, new Rectangle(r.Left, r.Top - 2, 120, 34), P.Text, Line);
            if (entries != null && entries.Count > 0)
            {
                var count = new Rectangle(r.Left + 112, r.Top + 3, 32, 24);
                ControlPanel.Box(g, count, P.Tile, 8);
                TextRenderer.DrawText(g, entries.Count.ToString(), MenuKit.Small, count, P.SubText, Center);
            }

            var field = new Rectangle(r.Left, r.Top + 44, ContentWidth - 130, 36);
            ControlPanel.Box(g, field, P.Tile, 9);
            box.BackColor = P.Tile;
            box.ForeColor = P.Text;
            box.SetBounds(field.Left + 12, field.Top + 9, field.Width - 24, 20);

            addButton = new Rectangle(field.Right + 10, field.Top, 120, 36);
            ControlPanel.Box(g, addButton, P.Check, 9);
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
                var badge = new Rectangle(x + 12, y + 12, 30, 30);
                bool isIp = entry.Item2.IndexOf("ipset", StringComparison.OrdinalIgnoreCase) >= 0;
                ControlPanel.Box(g, badge, entry.Item3 ? Color.FromArgb(230, 126, 34) : Color.FromArgb(14, 136, 232), 8);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                ThemedRenderer.DrawIcon(g, isIp ? "dns" : "sites", badge.Left + 7, badge.Top + 15, Color.White);
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
}
