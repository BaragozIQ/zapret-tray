// Живые окна: главное окно (свернуть, развернуть, закрыть), ползунок размера, подписи графиков, окно "Отправить баг".
// Мышь не двигается: нажатия передаются окнам напрямую. Окно бага на пару секунд появляется на экране - так проверяется,
// видна ли серая подсказка (по точкам на снимке экрана)
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace ZapretTray
{
    class TestPanel : ControlPanel
    {
        public TestPanel(InfoItem a, InfoItem b, InfoItem c, Func<Color> f, Func<string> pings, ToolStripMenuItem[] s, ToolStripMenuItem[] q,
            ToolStripMenuItem t, ToolStripMenuItem e, Action r, Dictionary<ToolStripMenuItem, IPanelView> v) : base(a, b, c, f, pings, s, q, t, e, r, v) { }

        MouseEventArgs At(Point p, MouseButtons b) { return new MouseEventArgs(b, 1, p.X, p.Y, 0); }
        public void Hover(Point p) { OnMouseMove(At(p, MouseButtons.None)); }
        public void Tap(Point p) { Hover(p); OnMouseClick(At(p, MouseButtons.Left)); }
        public void Press(Point p) { Hover(p); OnMouseDown(At(p, MouseButtons.Left)); OnMouseUp(At(p, MouseButtons.Left)); OnMouseClick(At(p, MouseButtons.Left)); }
    }

    class TestBugDialog : BugDialog
    {
        public void Down(Point p) { OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, p.X, p.Y, 0)); }
    }

    static class W
    {
        static int pass, fail;
        static readonly List<string> log = new List<string>();
        const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;

        static void Ok(bool c, string n) { if (c) pass++; else fail++; log.Add((c ? "OK   " : "FAIL ") + n); }

        static void Pump(int ms)
        {
            var end = DateTime.Now.AddMilliseconds(ms);
            do { Application.DoEvents(); Thread.Sleep(15); } while (DateTime.Now < end);
        }

        static T Field<T>(object o, string name) { return (T)o.GetType().BaseType.GetField(name, Hidden).GetValue(o); }

        // Рисуем окно в картинку: так заполняются места карточек и графиков, даже если окно за краем экрана
        static void Paint(Form f) { using (var b = new Bitmap(f.Width, f.Height)) f.DrawToBitmap(b, new Rectangle(0, 0, f.Width, f.Height)); }

        static Point Device(TestPanel p, float x, float y) { return new Point((int)(x * p.UiScale), (int)(y * p.UiScale)); }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main()
        {
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            try { Panel(); } catch (Exception ex) { Ok(false, "главное окно: " + ex.GetType().Name + ": " + ex.Message); }
            try { Bug(); } catch (Exception ex) { Ok(false, "окно бага: " + ex.GetType().Name + ": " + ex.Message); }
            try { Hints(); } catch (Exception ex) { Ok(false, "подсказки: " + ex.GetType().Name + ": " + ex.Message); }
            try { TextFits(); } catch (Exception ex) { Ok(false, "текст в рамке: " + ex.GetType().Name + ": " + ex.Message); }
            foreach (var scale in new[] { 100, 150, 200 })
                try { Help(scale); } catch (Exception ex) { Ok(false, "вопросик " + scale + "%: " + ex.GetType().Name + ": " + ex.Message); }
            File.WriteAllLines("result.txt", log.Concat(new[] { "ИТОГО ok=" + pass + " fail=" + fail }));
        }

        static void Panel()
        {
            Theme.Mode = Theme.Dark;
            ControlPanel.UserScale = 1f;
            Func<string, string, ToolStripMenuItem> I = (t, ic) => MenuKit.Icon(MenuKit.Item(t, null), ic);
            var st = MenuKit.Info("Работает\tALT", ItemKind.Status);
            var he = MenuKit.Info("✓\t✓\t\t", ItemKind.Health);
            var sp = MenuKit.Info("↓ 1 КБ/с\t30 мс\t5 мин", ItemKind.Stats);
            var menu = new ContextMenuStrip();
            menu.Items.AddRange(new ToolStripItem[] { st, he, sp });
            var set = I("Настройки", "settings");
            var theme = I("Тема", "theme");
            theme.DropDownItems.Add(I("Тёмная", "moon"));
            var scale = I("Размер окна", "monitor");
            foreach (var v in ControlPanel.ScaleStops) scale.DropDownItems.Add(I(v + "%", "monitor"));
            set.DropDownItems.AddRange(new ToolStripItem[] { MenuKit.Sep("Вид"), theme, scale });
            var f1 = I("F", "fast"); var f2 = I("G", "load"); var tg = I("T", "power"); var ex = I("E", "exit");
            menu.Items.AddRange(new ToolStripItem[] { set, f1, f2, tg, ex });

            var p = new TestPanel(st, he, sp, () => Theme.On, () => "", new[] { set }, new[] { f1, f2 }, tg, ex, () => { }, new Dictionary<ToolStripMenuItem, IPanelView>());
            int chosen = 0;
            p.ScaleItem = scale;
            p.ScaleChanged = v => chosen = v;
            // Пинг: ровно 30 мс, точка 20 - 90 мс
            var ping = Enumerable.Range(0, 36).Select(k => k == 20 ? 90L : 30L).ToArray();
            var times = Enumerable.Range(0, 36).Select(k => DateTime.UtcNow.AddSeconds(-10 * (35 - k))).ToArray();
            p.TileHistory = t => t == 5 ? ping : new long[0];
            p.TileTimes = t => t == 5 ? times : new DateTime[0];
            p.RecheckTile = t => { };
            p.TileChecking = t => false;
            p.StartPosition = FormStartPosition.Manual;
            p.Location = new Point(-3000, -3000);
            p.Show();
            Pump(200);

            // Графики: мышь над точкой - подпись этой точки, над другой - другой, мимо графика - подписи нет
            Paint(p);
            var area = Field<Rectangle[]>(p, "graphArea")[5];
            int points = Field<int[]>(p, "graphPoints")[5];
            Ok(points == 36 && area.Width > 0, "график пинга нарисован: точек " + points);
            float step = (float)area.Width / (points - 1);
            p.Hover(Device(p, area.Left + 20 * step, area.Top + area.Height / 2));
            Ok(Field<int>(p, "graphHot") == 5020, "мышь над точкой 20 - подпись этой точки");
            p.Hover(Device(p, area.Left + 3 * step, area.Top + area.Height / 2));
            Ok(Field<int>(p, "graphHot") == 5003, "мышь сдвинулась - подпись другой точки");
            p.Hover(Device(p, 5, 5));
            Ok(Field<int>(p, "graphHot") == -1, "мышь ушла с графика - подписи нет");

            // Ползунок размера: нажатие на четвёртое деление - 115%
            p.OpenSection(set);
            Paint(p);
            var cards = Field<List<KeyValuePair<Rectangle, ToolStripMenuItem>>>(p, "cards");
            var card = cards.Find(c => c.Value == scale).Key;
            Ok(card.Width > 0, "карточка «Размер окна» на месте");
            var track = (Rectangle)typeof(ControlPanel).GetMethod("SliderTrack", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { card });
            int stops = ControlPanel.ScaleStops.Length;
            p.Press(Device(p, track.Left + track.Width * 3f / (stops - 1), track.Top + 2));
            Ok(chosen == 115, "ползунок: нажал на 115 - выбрано " + chosen);
            p.Press(Device(p, track.Right + 3, track.Top + 2));
            Ok(chosen == 200, "ползунок: нажал у правого края - выбрано " + chosen);
            Ok(p.Visible && path(p) == 1, "нажатие на ползунок не открывает подменю");

            // Свернуть, развернуть, закрыть
            var other = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-3000, -2000), ShowInTaskbar = false };
            other.Show();
            other.Activate();
            Pump(300);
            Ok(!p.IsDisposed && p.Visible, "окно не пропадает, когда ушёл в другое окно");
            p.Tap(new Point(p.ClientSize.Width - 62, 20));
            Pump(200);
            Ok(!p.Visible && !p.IsDisposed, "кнопка «свернуть» прячет окно");
            p.Restore();
            Pump(200);
            Ok(p.Visible && p.WindowState == FormWindowState.Normal, "окно возвращается");
            Ok(p.Width > 300 && p.Height > 200, "после возврата размер нормальный: " + p.Width + "x" + p.Height);
            p.WindowState = FormWindowState.Minimized;
            Pump(200);
            Ok(!p.Visible, "свёрнутое Windows окно тоже прячется");
            p.Restore();
            Pump(200);
            Ok(p.Visible && p.Width > 300, "и снова возвращается: " + p.Width + "x" + p.Height);
            p.Tap(new Point(p.ClientSize.Width - 24, 20));
            Pump(100);
            Ok(p.IsDisposed, "крестик закрывает окно");
            other.Close();
        }

        // Подсказки: нажатие на строку списка выбирает подсказку, кнопка внизу открывает раздел, о котором она
        static void Hints()
        {
            Theme.Mode = Theme.Dark;
            ControlPanel.UserScale = 1f;
            Func<string, string, ToolStripMenuItem> I = (t, ic) => MenuKit.Icon(MenuKit.Item(t, null), ic);
            var st = MenuKit.Info("Работает\tALT", ItemKind.Status);
            var he = MenuKit.Info("✓\t✓\t\t", ItemKind.Health);
            var sp = MenuKit.Info("↓ 1 КБ/с\t30 мс\t5 мин", ItemKind.Stats);
            var menu = new ContextMenuStrip();
            var sites = I("Мои сайты...", "sites"); var hints = I("Подсказки...", "hints");
            var f1 = I("F", "fast"); var f2 = I("G", "load"); var tg = I("T", "power"); var ex = I("E", "exit");
            menu.Items.AddRange(new ToolStripItem[] { st, he, sp, sites, hints, f1, f2, tg, ex });
            var hv = new HintsView();
            var p = new TestPanel(st, he, sp, () => Theme.On, () => "", new[] { sites, hints }, new[] { f1, f2 }, tg, ex, () => { },
                new Dictionary<ToolStripMenuItem, IPanelView> { { hints, hv } });
            p.StartPosition = FormStartPosition.Manual;
            p.Location = new Point(-3000, -3000);
            p.Show();
            p.OpenSection(hints);
            Pump(600);
            Paint(p);
            var rows = (List<Rectangle>)typeof(HintsView).GetField("rowRects", Hidden).GetValue(hv);
            var selected = (int[])typeof(HintsView).GetField("selected", Hidden).GetValue(hv);
            Ok(rows.Count == 8, "в списке «Если сломалось» 8 строк");
            p.Tap(Device(p, rows[3].Left + 40, rows[3].Top + rows[3].Height / 2));
            Ok(selected[0] == 3, "нажал на четвёртую строку - выбрана она");
            p.Tap(Device(p, rows[0].Left + 40, rows[0].Top + rows[0].Height / 2));
            Pump(600);
            Paint(p);
            var open = (Rectangle)typeof(HintsView).GetField("openButton", Hidden).GetValue(hv);
            Ok(open.Width > 0, "у подсказки «Не открывается сайт» есть кнопка «Открыть»");
            p.Tap(Device(p, open.Left + open.Width / 2, open.Top + open.Height / 2));
            var path = (List<ToolStripMenuItem>)typeof(ControlPanel).GetField("path", Hidden).GetValue(p);
            Ok(path.Count == 1 && path[0] == sites, "кнопка открыла «Мои сайты»");
            p.Close();
        }

        // Рамки строятся по мерке текста. При увеличенном окне мерка должна совпадать с тем, что реально нарисуется,
        // иначе текст переносится лишний раз и обрезается (так было с подсказкой у вопросика при 150 и 200%)
        static void TextFits()
        {
            var texts = new[] {
                "Пинг появится, когда запустишь игру из «Моих игр».",
                "В любом случае уйдёт технический отчёт: что открылось, настройки и журнал программы.",
                "Твоих личных данных в отчёте нет", "Отклик 183 мс", "100 мс · 38 мин назад" };
            var fonts = new[] { MenuKit.Font, MenuKit.Small, MenuKit.Bold };
            var flags = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak;
            int bad = 0, total = 0;
            string first = null;
            using (var bmp = new Bitmap(10, 10))
            using (var g = Graphics.FromImage(bmp))
                foreach (var scale in new[] { 0.75f, 0.9f, 1f, 1.15f, 1.25f, 1.5f, 1.75f, 2f })
                    foreach (var font in fonts)
                        foreach (var text in texts)
                            foreach (var width in new[] { 0, 230, 300 })
                            {
                                total++;
                                g.ResetTransform();
                                g.ScaleTransform(scale, scale);
                                var logical = TextRenderer.MeasureText(g, text, font, new Size(width, 0), flags);
                                // Как нарисует программа: прямоугольник в точках экрана, шрифт увеличен
                                var device = new Size((int)Math.Round(logical.Width * scale), (int)Math.Round(logical.Height * scale));
                                g.ResetTransform();
                                var real = System.Windows.Forms.TextRenderer.MeasureText(g, text, TextRenderer.Scaled(font, scale * 96f / g.DpiX), new Size(device.Width, 0), flags);
                                if (real.Width > device.Width + 1 || real.Height > device.Height + 1)
                                {
                                    bad++;
                                    if (first == null) first = scale * 100 + "%, " + font.Size + "pt, ширина " + width + ": нужно " + real + ", рамка " + device;
                                }
                            }
            Ok(bad == 0, "текст влезает в свою рамку при всех размерах окна: " + (total - bad) + " из " + total + (first != null ? " (" + first + ")" : ""));
        }

        // Вопросик в плитке игр: знак по центру кружка, подсказка влезает в окно, текст в ней не обрезан
        static void Help(int percent)
        {
            Theme.Mode = Theme.Dark;
            ControlPanel.UserScale = percent / 100f;
            Func<string, string, ToolStripMenuItem> I = (t, ic) => MenuKit.Icon(MenuKit.Item(t, null), ic);
            var st = MenuKit.Info("Работает\tALT", ItemKind.Status);
            var he = MenuKit.Info("✓\t✓\t1/2\t✓", ItemKind.Health);
            var sp = MenuKit.Info("↓ 1 КБ/с\t30 мс\t5 мин", ItemKind.Stats);
            var menu = new ContextMenuStrip();
            var a = I("A", "games"); var tg = I("T", "power"); var ex = I("E", "exit");
            var f1 = MenuKit.Icon(MenuKit.Item("Подобрать конфиг заново", null), "fast", "Если сайт или игра перестали открываться");
            var f2 = MenuKit.Icon(MenuKit.Item("Отправить баг", null), "load", "Отчёт сам уйдёт разработчику");
            menu.Items.AddRange(new ToolStripItem[] { st, he, sp, a, f1, f2, tg, ex });
            var p = new TestPanel(st, he, sp, () => Theme.On, () => "отклик 1 мс\tотклик 1 мс\tотклик 1 мс\tотклик 1 мс", new[] { a }, new[] { f1, f2 }, tg, ex, () => { }, new Dictionary<ToolStripMenuItem, IPanelView>());
            p.RecheckTile = t => { };
            p.TileChecking = t => false;
            p.StartPosition = FormStartPosition.Manual;
            p.Location = new Point(-4000, -4000);
            p.Show();
            Pump(100);
            Paint(p);
            var help = Field<Rectangle>(p, "gameHelp");
            p.Hover(Device(p, help.Left + help.Width / 2, help.Top + help.Height / 2));
            Ok(Equals(Field<object>(p, "hover"), "help"), percent + "%: мышь на вопросике");
            using (var b = new Bitmap(p.Width, p.Height))
            {
                p.DrawToBitmap(b, new Rectangle(0, 0, p.Width, p.Height));
                float u = p.UiScale;
                // Кружок: все светлые точки в клетке вопросика; знак: светлые точки внутри кружка
                var cell = new Rectangle((int)(help.Left * u), (int)(help.Top * u), (int)(help.Width * u), (int)(help.Height * u));
                var bg = b.GetPixel(cell.Left + 1, cell.Top + 1);
                Func<Color, bool> lit = c => Math.Abs(c.R - bg.R) + Math.Abs(c.G - bg.G) + Math.Abs(c.B - bg.B) > 60;
                float rx0 = 1e9f, rx1 = -1, ry0 = 1e9f, ry1 = -1;
                for (int y = cell.Top; y < cell.Bottom; y++)
                    for (int x = cell.Left; x < cell.Right; x++)
                        if (lit(b.GetPixel(x, y))) { rx0 = Math.Min(rx0, x); rx1 = Math.Max(rx1, x); ry0 = Math.Min(ry0, y); ry1 = Math.Max(ry1, y); }
                float cx = (rx0 + rx1) / 2, cy = (ry0 + ry1) / 2, inner = (rx1 - rx0) / 2 - 2.5f * u;
                float mx0 = 1e9f, mx1 = -1, my0 = 1e9f, my1 = -1;
                for (int y = cell.Top; y < cell.Bottom; y++)
                    for (int x = cell.Left; x < cell.Right; x++)
                        if ((x - cx) * (x - cx) + (y - cy) * (y - cy) < inner * inner && lit(b.GetPixel(x, y)))
                        { mx0 = Math.Min(mx0, x); mx1 = Math.Max(mx1, x); my0 = Math.Min(my0, y); my1 = Math.Max(my1, y); }
                float dx = (mx0 + mx1) / 2 - cx, dy = (my0 + my1) / 2 - cy;
                Ok(mx1 > 0 && Math.Abs(dx) <= 1f * u && Math.Abs(dy) <= 1.5f * u, percent + "%: знак вопроса по центру кружка (сдвиг " + dx.ToString("0.0") + ", " + dy.ToString("0.0") + ")");

                // Карточки внизу главной: две строки текста по центру, на одной высоте со значком
                var cards = Field<List<KeyValuePair<Rectangle, ToolStripMenuItem>>>(p, "cards");
                foreach (var c in cards.Where(k => k.Value == f1 || k.Value == f2))
                {
                    var r = new Rectangle((int)(c.Key.Left * u), (int)(c.Key.Top * u), (int)(c.Key.Width * u), (int)(c.Key.Height * u));
                    var back = b.GetPixel(r.Right - (int)(4 * u), r.Top + r.Height / 2);
                    Func<int, int, bool> ink = (x0, x1) => false;
                    float badgeMid = Middle(b, r.Left + (int)(11 * u), r.Left + (int)(51 * u), r, back, 90);
                    float textMid = Middle(b, r.Left + (int)(64 * u), r.Right - (int)(20 * u), r, back, 90);
                    Ok(Math.Abs(badgeMid - textMid) <= 3 * u, percent + "%: «" + c.Value.Text + "»: текст на высоте значка (разница " + (textMid - badgeMid).ToString("0.0") + ")");
                }
            }
            p.Close();
            ControlPanel.UserScale = 1f;
        }

        // Середина по высоте всего, что заметно отличается от фона карточки, в полосе x0..x1
        static float Middle(Bitmap b, int x0, int x1, Rectangle r, Color back, int threshold)
        {
            int top = -1, bottom = -1;
            for (int y = r.Top + 3; y < r.Bottom - 3; y++)
                for (int x = x0; x < x1; x++)
                {
                    var c = b.GetPixel(x, y);
                    if (Math.Abs(c.R - back.R) + Math.Abs(c.G - back.G) + Math.Abs(c.B - back.B) <= threshold) continue;
                    if (top < 0) top = y;
                    bottom = y;
                    break;
                }
            return (top + bottom) / 2f;
        }

        static int path(ControlPanel p) { return ((System.Collections.ICollection)typeof(ControlPanel).GetField("path", Hidden).GetValue(p)).Count; }

        // Сколько точек в поле не совпадает с его фоном: серая подсказка - сотни, пусто - почти ноль (курсор)
        static int Ink(Form d, TextBox box, Rectangle part)
        {
            var screen = box.RectangleToScreen(part);
            using (var b = new Bitmap(screen.Width, screen.Height))
            {
                using (var g = Graphics.FromImage(b)) g.CopyFromScreen(screen.Location, Point.Empty, screen.Size);
                var back = box.BackColor;
                int n = 0;
                for (int y = 0; y < b.Height; y++)
                    for (int x = 0; x < b.Width; x++)
                    {
                        var c = b.GetPixel(x, y);
                        if (Math.Abs(c.R - back.R) + Math.Abs(c.G - back.G) + Math.Abs(c.B - back.B) > 40) n++;
                    }
                return n;
            }
        }

        static void Bug()
        {
            Theme.Mode = Theme.Dark;
            var d = new TestBugDialog { StartPosition = FormStartPosition.Manual, Location = new Point(80, 80) };
            d.Show();
            d.Activate();
            Pump(500);
            var box = (TextBox)typeof(BugDialog).GetField("box", Hidden).GetValue(d);
            var field = (Rectangle)typeof(BugDialog).GetField("field", Hidden).GetValue(d);
            var all = new Rectangle(0, 0, box.Width, box.Height);
            var lower = new Rectangle(0, 24, box.Width, box.Height - 24);   // вторая строка и ниже

            Ok(!box.Focused, "при открытии поле не в фокусе");
            int shown = Ink(d, box, all);
            Ok(shown > 150, "подсказка видна: точек " + shown);

            d.Down(new Point(field.Left + field.Width / 2, field.Top + field.Height / 2));
            Pump(300);
            int clicked = Ink(d, box, all);
            Ok(box.Focused && clicked < 60, "нажал в поле - подсказка исчезла: точек " + clicked);

            d.Down(new Point(30, field.Bottom + 70));
            Pump(300);
            int away = Ink(d, box, all);
            Ok(!box.Focused && away > 150, "нажал мимо поля - подсказка вернулась: точек " + away);

            box.Focus();
            box.Text = "abc";
            Pump(300);
            int typed = Ink(d, box, lower);
            Ok(typed < 5, "напечатал - строки подсказки ниже первой стёрты: точек " + typed);
            box.Text = "";

            var other = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(700, 80), Size = new Size(200, 100), ShowInTaskbar = false };
            other.Show();
            other.Activate();
            Pump(500);
            Ok(d.IsDisposed || !d.Visible, "нажал мимо окна - окно бага закрылось");
            other.Close();
        }
    }
}
