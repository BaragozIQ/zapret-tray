// Подсказки: три вкладки, слева список "что случилось", справа шаги "куда нажимать". Страница окна ControlPanel.
// Новая функция в меню = новая подсказка здесь
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
        // Слева список "что случилось", справа выбранная подсказка: шаги по номерам, пояснение и кнопка, которая сразу открывает нужный раздел.
        // Всё влезает без прокрутки: самая длинная вкладка "Настроить", 10 строк. Под эту высоту подобрано окно ControlPanel
        public const int Available = 578;
        const int Width = ControlPanel.CardWidth * 2 + 12, ListWidth = 292, Gap = 12, RowGap = 4, MaxRow = 56;
        const int DetailWidth = Width - ListWidth - Gap, StepHeight = 54, AnimMs = 420;

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
            new Hint("sites", Blue, "Не открывается сайт", "Не помогло - в «Подключении» нажми «Подобрать конфиг заново».",
                "Мои сайты", "Впиши сайт или IP", "Добавить"),
            new Hint("undo", Orange, "Сайт сломался от программы", "Сайт останется в списке, но обходить его программа больше не будет.",
                "Мои сайты", "Выключи переключатель у сайта"),
            new Hint("ping", Teal, "Всё лагает", "Проверка всех конфигов занимает пару минут. Жди, пока Z снова станет зелёным.",
                "Подключение", "Подобрать конфиг с лучшим пингом"),
            new Hint("games", Green, "Лагает игра", "Пока игра запущена, на главной в плитке «Приложения» виден её пинг.",
                "Мои приложения", "Добавить из установленных", "Запусти игру"),
            new Hint("dns", Purple, "Крестик у YouTube", "Не помогло - там же нажми «Исправить DNS». Обычно программа делает это сама.",
                "Подключение", "Подобрать конфиг заново"),
            new Hint("undo", Purple, "Стало хуже после DNS", "Настройки DNS станут ровно такими, какими были до исправления.",
                "Подключение", "Вернуть DNS как было"),
            new Hint("shield", Slate, "Включён VPN", "С VPN плитки на главной показывают сеть VPN, а не работу zapret.",
                "Выключи VPN"),
            new Hint("load", Red, "Ничего не понятно", "Отчёт сам уйдёт разработчику. Твоих личных данных в нём нет.",
                "Главная", "Отправить баг")
        };

        static readonly Hint[] Setup =
        {
            new Hint("list", Teal, "Выбрать конфиг самому", "В списке галочка стоит у конфига, который работает сейчас.",
                "Подключение", "Выбрать конфиг вручную"),
            new Hint("ping", Teal, "Под какие сайты подбирать", "Обязательные (галочка) — под них ищется конфиг. Остальные просто проверяются. Можно добавить свой сайт.",
                "Проверки", "Отметь обязательные"),
            new Hint("plus", Green, "Добавить своё приложение", "Три способа: из установленных, запущенное или указать файл. Значок приложения подтянется сам.",
                "Мои приложения", "Добавить из установленных"),
            new Hint("games", Green, "Встроенные игры", "Клик по игре включает или выключает для неё обход.",
                "Мои приложения", "ARC Raiders или Wardogs"),
            new Hint("games", Green, "Выключить или убрать", "«Удалить» стирает и адреса, которые программа собрала для приложения.",
                "Мои приложения", "Нажми на приложение", "Включена или Удалить"),
            new Hint("sites", Blue, "Убрать сайт из списка", "Крестик справа в строке сайта.",
                "Мои сайты", "Нажми ✕"),
            new Hint("theme", Slate, "Тёмная или светлая тема", "Чтобы тема была как в Windows: «Настройки» → «Тема».",
                "День или Ночь внизу слева"),
            new Hint("config", Slate, "Сторож и автозапуск", "И уведомления, и автообновление: поставь или убери нужную галочку.",
                "Настройки", "Поведение", "Нужная галочка"),
            new Hint("monitor", Slate, "Окно мелкое или большое", "Ползунок от 75 до 200%. Больше экрана окно не станет.",
                "Настройки", "Размер окна"),
            new Hint("save", Blue, "Перенести на другой компьютер", "На другом компьютере: «Настройки» → «Подгрузить настройки».",
                "Настройки", "Сохранить настройки"),
            new Hint("refresh", Teal, "Обновить прямо сейчас", "Обычно программа обновляется сама, раз в день.",
                "Настройки", "Проверить обновления"),
            new Hint("trash", Red, "Удалить программу", "Сначала спросит. Твои сайты и игры тоже удалятся.",
                "Настройки", "Удалить программу")
        };

        // Первые две - таблички со значками, их рисует DrawLegend
        static readonly Hint[] Auto =
        {
            new Hint("power", Green, "Значок Z у часов", "Два клика по Z включают или выключают программу. Наведи мышь - покажет конфиг, скорость и пинг."),
            new Hint("list", Teal, "Значки на главной", "Нажми на плитку - она проверится заново. Наведи мышь на график - покажет число."),
            new Hint("shield", Green, "Сама чинит", "Проверяет YouTube и Discord. Сломалось - сменит конфиг или сама исправит DNS."),
            new Hint("list", Teal, "Сама запоминает", "Помнит рабочие конфиги и проверяет их первыми, поэтому включается быстро."),
            new Hint("refresh", Blue, "Сама обновляется", "Раз в полчаса проверяет обновления. Новую версию ставишь сам кнопкой «Обновить» в «Настройках». Сайты, игры и настройки остаются на месте."),
            new Hint("bell", Orange, "Когда придёт уведомление", "Только когда сама не справилась, например не нашла рабочий конфиг. Остальное делает молча."),
            new Hint("exit", Red, "Закрыть программу", "Кнопка «Выход» внизу слева. zapret при этом тоже выключится.")
        };

        static readonly string[] Tabs = { "Если сломалось", "Настроить", "Что значат значки" };
        // Разделы, которые кнопка внизу подсказки умеет открыть
        static readonly string[] Sections = { "Главная", "Мои приложения", "Мои сайты", "Подключение", "Проверки", "Настройки" };

        const TextFormatFlags Line = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
        const TextFormatFlags Center = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        const TextFormatFlags Wrap = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak;

        static readonly Font RowFont = new Font("Segoe UI Semibold", 10f);
        static readonly Font TitleFont = new Font("Segoe UI Semibold", 13f);
        static readonly Font BigFont = new Font("Segoe UI Semibold", 15f);
        static readonly Font StepFont = new Font("Segoe UI Semibold", 10f);
        static readonly Font NoteFont = new Font("Segoe UI", 10f);

        readonly List<Rectangle> tabRects = new List<Rectangle>();
        readonly List<Rectangle> rowRects = new List<Rectangle>();
        readonly int[] selected = new int[3];
        Rectangle openButton;
        int page;
        int animStart = -1;
        Timer anim;
        Control host;

        public int Page
        {
            get { return page; }
            set { page = value; Animate(); }
        }

        static Palette P { get { return Theme.Current; } }

        public int Extra { get { return 0; } }

        public void Activate(Control host, bool on)
        {
            this.host = host;
            if (on) Animate();
        }

        Hint[] Current { get { return page == 0 ? Broken : page == 1 ? Setup : Auto; } }
        Hint Chosen { get { return Current[Math.Min(selected[page], Current.Length - 1)]; } }

        static string Section(Hint h)
        {
            return h.Steps.Length > 0 && Array.IndexOf(Sections, h.Steps[0]) >= 0 ? h.Steps[0] : null;
        }

        // Шаги выезжают по очереди, пока таймер не дойдёт до конца
        void Animate()
        {
            animStart = Environment.TickCount;
            if (host == null) return;
            if (anim == null)
            {
                anim = new Timer { Interval = 15 };
                anim.Tick += (s, e) =>
                {
                    if (host == null || host.IsDisposed || Environment.TickCount - animStart > AnimMs + 60) { anim.Stop(); animStart = -1; }
                    if (host != null && !host.IsDisposed) host.Invalidate();
                };
            }
            anim.Start();
        }

        // 0 - ещё не появился, 1 - на месте. delay - когда начинает выезжать, доля от общего времени
        float Appear(float delay)
        {
            if (animStart < 0) return 1f;
            float t = (Environment.TickCount - animStart) / (float)AnimMs;
            float k = Math.Max(0f, Math.Min(1f, (t - delay) / 0.45f));
            return 1f - (1f - k) * (1f - k) * (1f - k);
        }

        public void DrawHeader(Graphics g, Rectangle r, Point mouse)
        {
            tabRects.Clear();
            ControlPanel.PageTitle(g, "Подсказки", TitleFont, r.Left, r.Top - 2, 34, 130);
            int x = r.Left + 140;
            for (int i = 0; i < Tabs.Length; i++)
            {
                var tab = new Rectangle(x, r.Top, TextRenderer.MeasureText(g, Tabs[i], MenuKit.Bold).Width + 28, 34);
                tabRects.Add(tab);
                if (i == page) ControlPanel.Accent(g, tab, 9);
                else ControlPanel.Card(g, tab, tab.Contains(mouse) ? P.Hover : P.Card, 9);
                TextRenderer.DrawText(g, Tabs[i], MenuKit.Bold, tab, i == page ? Color.White : P.Text, Center);
                x = tab.Right + 6;
            }
        }

        public int Draw(Graphics g, int x, int top, Point mouse)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // Отступ сверху: верхний край карточек не должен прятаться под шапкой
            top += 2;
            DrawList(g, x, top, mouse);
            DrawDetail(g, new Rectangle(x + ListWidth + Gap, top, DetailWidth, Available - 2), mouse);
            return Available;
        }

        // Список слева: строка как пункт меню, выбранная подсвечена цветом своей подсказки и полоской слева
        void DrawList(Graphics g, int x, int top, Point mouse)
        {
            rowRects.Clear();
            var hints = Current;
            int row = Math.Min(MaxRow, (Available - 2 - RowGap * (hints.Length - 1)) / hints.Length);
            for (int i = 0; i < hints.Length; i++)
            {
                var r = new Rectangle(x, top + i * (row + RowGap), ListWidth, row);
                rowRects.Add(r);
                bool chosen = i == selected[page], hot = !chosen && r.Contains(mouse);
                var h = hints[i];
                if (chosen)
                {
                    ControlPanel.Card(g, r, ControlPanel.Mix(P.Card, h.Color, 0.18f), 10, ControlPanel.Mix(h.Color, P.Card, 0.35f));
                    using (var b = new SolidBrush(h.Color)) using (var path = ThemedRenderer.RoundRect(new Rectangle(r.Left + 1, r.Top + 12, 3, r.Height - 24), 1)) g.FillPath(b, path);
                }
                else ControlPanel.Card(g, r, hot ? P.Hover : P.Card, 10);
                ControlPanel.Badge(g, new Rectangle(r.Left + 12, r.Top + (r.Height - 30) / 2, 30, 30), h.Color, h.Icon);
                TextRenderer.DrawText(g, h.Title, RowFont, new Rectangle(r.Left + 52, r.Top, r.Width - 64, r.Height), P.Text, Line);
            }
        }

        void DrawDetail(Graphics g, Rectangle r, Point mouse)
        {
            var h = Chosen;
            ControlPanel.Card(g, r, P.Card, 12);
            var state = g.Save();
            using (var clip = ThemedRenderer.RoundRect(new Rectangle(r.Left + 1, r.Top + 1, r.Width - 2, r.Height - 2), 11)) g.SetClip(clip, CombineMode.Intersect);
            ControlPanel.Glow(g, new Rectangle(r.Right - 300, r.Top - 220, 520, 400), h.Color, 70);
            g.Restore(state);

            // Шапка: большой значок, заголовок, вкладка
            float a = Appear(0f);
            int shift = (int)((1 - a) * 14);
            ControlPanel.Badge(g, new Rectangle(r.Left + 24, r.Top + 24 + shift, 56, 56), h.Color, h.Icon);
            TextRenderer.DrawText(g, h.Title, BigFont, new Rectangle(r.Left + 96, r.Top + 24 + shift, r.Width - 120, 32), P.Text, Line);
            TextRenderer.DrawText(g, Tabs[page], MenuKit.Small, new Rectangle(r.Left + 97, r.Top + 56 + shift, r.Width - 120, 20), h.Color, Line);
            using (var pen = new Pen(P.Separator)) g.DrawLine(pen, r.Left + 24, r.Top + 100, r.Right - 24, r.Top + 100);

            int y = r.Top + 118;
            if (page == 2 && selected[2] < 2)
                y = DrawLegend(g, r, y, selected[2] == 0);
            else if (h.Steps.Length > 0)
                y = DrawSteps(g, r, y, h);

            // Пояснение в цветной плашке
            float na = Appear(0.15f + 0.12f * h.Steps.Length);
            if (na > 0)
            {
                int ny = y + 8 + (int)((1 - na) * 12);
                var size = TextRenderer.MeasureText(g, h.Note, NoteFont, new Size(r.Width - 48 - 56, 0), Wrap);
                var box = new Rectangle(r.Left + 24, ny, r.Width - 48, Math.Max(52, size.Height + 28));
                ControlPanel.Box(g, box, ControlPanel.Mix(P.Card, h.Color, 0.12f), 10);
                using (var path = ThemedRenderer.RoundRect(box, 10))
                using (var pen = new Pen(ControlPanel.Mix(h.Color, P.Card, 0.55f))) g.DrawPath(pen, path);
                DrawInfo(g, box.Left + 14, box.Top + 14, h.Color);
                TextRenderer.DrawText(g, h.Note, NoteFont, new Rectangle(box.Left + 46, box.Top + 14, size.Width + 4, size.Height + 4), P.Text, Wrap);
            }

            // Кнопка внизу: сразу открыть раздел, о котором подсказка
            openButton = Rectangle.Empty;
            var section = Section(h);
            if (section != null && Appear(0.35f) > 0)
            {
                string text = section == "Главная" ? "Открыть главную" : "Открыть «" + section + "»";
                int w = TextRenderer.MeasureText(g, text, MenuKit.Bold).Width + 64;
                openButton = new Rectangle(r.Left + 24, r.Bottom - 24 - 44, w, 44);
                ControlPanel.Accent(g, openButton, 10);
                if (openButton.Contains(mouse))
                    using (var b = new SolidBrush(Color.FromArgb(40, Color.White))) using (var path = ThemedRenderer.RoundRect(openButton, 10)) g.FillPath(b, path);
                TextRenderer.DrawText(g, text, MenuKit.Bold, new Rectangle(openButton.Left + 18, openButton.Top, w - 50, 44), Color.White, Line);
                using (var p = new Pen(Color.White, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    int cx = openButton.Right - 24, cy = openButton.Top + 22;
                    g.DrawLine(p, cx - 7, cy, cx + 5, cy);
                    g.DrawLine(p, cx, cy - 5, cx + 5, cy);
                    g.DrawLine(p, cx, cy + 5, cx + 5, cy);
                }
            }
        }

        // Шаги столбиком: номер в кружке, линия к следующему, сам шаг - как кнопка в программе. Последний - главный, синий
        int DrawSteps(Graphics g, Rectangle r, int y, Hint h)
        {
            TextRenderer.DrawText(g, "ЧТО НАЖАТЬ", MenuKit.Small, new Rectangle(r.Left + 24, y - 6, 200, 20), P.SubText, Line);
            y += 22;
            int n = h.Steps.Length;
            for (int i = 0; i < n; i++)
            {
                float a = Appear(0.1f + 0.12f * i);
                if (a <= 0) continue;
                int sy = y + i * StepHeight + (int)((1 - a) * 16);
                int cx = r.Left + 40, cy = sy + 19;
                bool last = i == n - 1;
                if (i < n - 1)
                    using (var pen = new Pen(ControlPanel.Mix(h.Color, P.Card, 0.45f), 2f)) g.DrawLine(pen, cx, cy + 15, cx, cy + StepHeight - 15);
                var circle = new Rectangle(cx - 14, cy - 14, 28, 28);
                if (last) ControlPanel.Glow(g, new Rectangle(cx - 26, cy - 26, 52, 52), h.Color, 90);
                using (var b = new SolidBrush(last ? h.Color : ControlPanel.Mix(P.Card, h.Color, 0.25f))) g.FillEllipse(b, circle);
                TextRenderer.DrawText(g, (i + 1).ToString(), MenuKit.Bold, circle, last ? Color.White : P.Text, Center);

                int w = Math.Min(r.Right - 24 - (r.Left + 68), TextRenderer.MeasureText(g, h.Steps[i], StepFont).Width + 32);
                var pill = new Rectangle(r.Left + 68, cy - 19, w, 38);
                if (last) ControlPanel.Accent(g, pill, 9);
                else ControlPanel.Card(g, pill, P.Tile, 9);
                TextRenderer.DrawText(g, h.Steps[i], StepFont, new Rectangle(pill.Left + 16, pill.Top, pill.Width - 24, pill.Height), last ? Color.White : P.Text, Line);
            }
            return y + n * StepHeight;
        }

        // Таблички на вкладке "Что значат значки": крупные значки и подписи столбиком
        int DrawLegend(Graphics g, Rectangle r, int y, bool z)
        {
            TextRenderer.DrawText(g, z ? "ЦВЕТ ЗНАЧКА" : "ЧТО В ПЛИТКЕ", MenuKit.Small, new Rectangle(r.Left + 24, y - 6, 200, 20), P.SubText, Line);
            y += 22;
            var rows = z
                ? new[] { Tuple.Create("on", "Работает"), Tuple.Create("off", "Выключен"), Tuple.Create("busy", "Подбирает настройки, подожди") }
                : new[] { Tuple.Create("✓", "Открывается"), Tuple.Create("✗", "Не открывается"), Tuple.Create("3/4", "Открылись не все") };
            for (int i = 0; i < rows.Length; i++)
            {
                float a = Appear(0.1f + 0.12f * i);
                if (a <= 0) continue;
                int sy = y + i * StepHeight + (int)((1 - a) * 16);
                var tile = new Rectangle(r.Left + 24, sy, 38, 38);
                if (z)
                {
                    var color = rows[i].Item1 == "on" ? Theme.On : rows[i].Item1 == "off" ? Theme.Off : Theme.Busy;
                    ControlPanel.Glow(g, new Rectangle(tile.Left - 10, tile.Top - 10, 58, 58), color, 70);
                    ControlPanel.Box(g, tile, color, 19);
                    TextRenderer.DrawText(g, "Z", RowFont, tile, Color.White, Center);
                }
                else
                {
                    ControlPanel.Card(g, tile, P.Tile, 9);
                    bool count = rows[i].Item1.Length > 1;
                    ThemedRenderer.DrawResult(g, rows[i].Item1, count ? tile.Left + 4 : tile.Left + 13, tile.Top + 19, 30, tile);
                }
                TextRenderer.DrawText(g, rows[i].Item2, StepFont, new Rectangle(r.Left + 78, sy, r.Width - 102, 38), P.Text, Line);
            }
            return y + rows.Length * StepHeight;
        }

        // Значок "i" в кружке для пояснения
        static void DrawInfo(Graphics g, int x, int y, Color color)
        {
            using (var b = new SolidBrush(color)) g.FillEllipse(b, x, y, 20, 20);
            using (var p = new Pen(Color.White, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(p, x + 10, y + 9, x + 10, y + 14.5f);
            g.FillEllipse(Brushes.White, x + 9, y + 4.5f, 2.2f, 2.2f);
        }

        public bool HeaderHot(Point p)
        {
            foreach (var tab in tabRects) if (tab.Contains(p)) return true;
            return false;
        }

        public bool Hot(Point p)
        {
            if (openButton.Contains(p)) return true;
            for (int i = 0; i < rowRects.Count; i++) if (rowRects[i].Contains(p) && i != selected[page]) return true;
            return false;
        }

        public bool Click(Point p)
        {
            for (int i = 0; i < tabRects.Count; i++)
            {
                if (!tabRects[i].Contains(p) || i == page) continue;
                Page = i;
                return true;
            }
            for (int i = 0; i < rowRects.Count; i++)
            {
                if (!rowRects[i].Contains(p) || i == selected[page]) continue;
                selected[page] = i;
                Animate();
                return false;
            }
            var panel = host as ControlPanel;
            var section = Section(Chosen);
            if (openButton.Contains(p) && panel != null && section != null) panel.OpenByName(section);
            return false;
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

    // Раздел "Обязательные проверки": список сайтов, под которые подбирается конфиг. Переключатель "обязательная/нет"
    class ChecksView : IPanelView
    {
        const int RowHeight = 54, Gap = 8, ContentWidth = ControlPanel.CardWidth * 2 + 12;
        static readonly Font TitleFont = new Font("Segoe UI Semibold", 13f);

        readonly Func<List<Check>> list;
        readonly Action<string> add;
        readonly Action<Check, bool> setRequired;
        readonly Action<Check> remove;

        readonly CueBox box = new CueBox { BorderStyle = BorderStyle.None, Font = MenuKit.Font, Cue = "Добавить сайт для проверки: youtube.com, discord.com, свой сайт..." };
        readonly List<KeyValuePair<Rectangle, Action>> hots = new List<KeyValuePair<Rectangle, Action>>();
        Rectangle addButton;
        List<Check> entries;
        string error;
        Control host;

        static Palette P { get { return Theme.Current; } }

        const TextFormatFlags Line = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
        const TextFormatFlags Center = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

        public ChecksView(Func<List<Check>> list, Action<string> add, Action<Check, bool> setRequired, Action<Check> remove)
        {
            this.list = list;
            this.add = add;
            this.setRequired = setRequired;
            this.remove = remove;
            box.Enter += (s, e) => { if (host != null) host.Invalidate(); };
            box.Leave += (s, e) => { if (host != null) host.Invalidate(); };
            box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Add(); } };
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
            if (CheckStore.Normalize(box.Text) == null)
            {
                error = "Не понял, что это. Впиши сайт, например youtube.com.";
                if (host != null) host.Invalidate();
                return;
            }
            error = null;
            add(box.Text);
            box.Clear();
            box.Focus();
            entries = list();
            if (host != null) host.Invalidate();
        }

        public void DrawHeader(Graphics g, Rectangle r, Point mouse)
        {
            ControlPanel.PageTitle(g, "Обязательные проверки", TitleFont, r.Left, r.Top - 2, 34, 300);
            if (entries != null && entries.Count > 0)
            {
                var count = new Rectangle(r.Left + 304, r.Top + 3, 34, 24);
                ControlPanel.Box(g, count, P.Tile, 8);
                TextRenderer.DrawText(g, System.Linq.Enumerable.Count(entries, c => c.Required) + "/" + entries.Count, MenuKit.Small, count, P.SubText, Center);
            }

            var field = new Rectangle(r.Left, r.Top + 44, ContentWidth - 130, 36);
            var fill = ControlPanel.Mix(P.Back, P.Card, 0.5f);
            ControlPanel.Box(g, field, fill, 9);
            using (var path = ThemedRenderer.RoundRect(field, 9))
            using (var pen = new Pen(box.Focused ? P.Check : P.Separator)) g.DrawPath(pen, path);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            ThemedRenderer.DrawIcon(g, "ping", field.Left + 12, field.Top + 18, P.SubText);
            box.BackColor = fill;
            box.ForeColor = P.Text;
            box.CueColor = P.SubText;
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
            TextRenderer.DrawText(g, failed ? error : "Обязательные: под них программа подбирает конфиг. Необязательные просто проверяются для сведения.", MenuKit.Small,
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
                ControlPanel.Badge(g, badge, entry.Required ? Color.FromArgb(32, 170, 92) : Color.FromArgb(96, 110, 135), "ping");
                TextRenderer.DrawText(g, entry.Url.Replace("https://", ""), MenuKit.Bold, new Rectangle(x + 54, y, ContentWidth - 270, RowHeight), P.Text, Line);

                var del = new Rectangle(row.Right - 44, y + 11, 32, 32);
                var toggle = new Rectangle(del.Left - 52, y + 16, 40, 22);
                bool on = entry.Required;
                TextRenderer.DrawText(g, on ? "Обязательно" : "Не обязательно", MenuKit.Small, new Rectangle(toggle.Left - 112, y, 104, RowHeight), on ? P.Text : P.SubText,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                ControlPanel.DrawSwitch(g, toggle, on);

                if (del.Contains(mouse)) ControlPanel.Box(g, del, Theme.Bad, 8);
                using (var p = new Pen(del.Contains(mouse) ? Color.White : P.SubText, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawLine(p, del.Left + 11, del.Top + 11, del.Right - 11, del.Bottom - 11);
                    g.DrawLine(p, del.Right - 11, del.Top + 11, del.Left + 11, del.Bottom - 11);
                }

                hots.Add(new KeyValuePair<Rectangle, Action>(toggle, () => setRequired(entry, !on)));
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
            if (addButton.Contains(p)) { Add(); return false; }
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
            { "0.9.1", new[] {
                new Item("hints", Color.FromArgb(142, 84, 233), "Подсказки обо всём", "Раздел «Подсказки» теперь описывает весь функционал программы"),
                new Item("ping", Color.FromArgb(32, 170, 92), "Обязательные проверки", "Выбери, под какие сайты подбирать обход (раздел «Проверки»)") } },
            { "0.9", new[] {
                new Item("ping", Color.FromArgb(32, 170, 92), "Обязательные проверки", "Новый раздел: выбери, под какие сайты подбирать конфиг"),
                new Item("fast", Color.FromArgb(14, 136, 232), "Подбор стал быстрее", "Останавливается на первом рабочем, а не гоняет все") } },
            { "0.8", new[] {
                new Item("games", Color.FromArgb(230, 126, 34), "Мои приложения", "Бывшие «Мои игры»: теперь не только игры"),
                new Item("plus", Color.FromArgb(46, 160, 67), "Добавить из установленных", "Список твоих программ со значками, или выбери файл"),
                new Item("games", Color.FromArgb(14, 136, 232), "Настоящие иконки", "У добавленного приложения виден его значок"),
                new Item("refresh", Color.FromArgb(142, 84, 233), "Кнопка «Обновить»", "Новая версия ставится по твоему нажатию в «Настройках»"),
                new Item("sites", Color.FromArgb(0, 150, 160), "Instagram", "Добавлен в список по умолчанию") } },
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
                new Item("games", Color.FromArgb(230, 126, 34), "Мои приложения", "Добавь приложение - обход включится и для него"),
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