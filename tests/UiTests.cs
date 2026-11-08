using System;using System.Drawing;using System.Drawing.Imaging;using System.IO;using System.Linq;using System.Windows.Forms;using System.Collections.Generic;
namespace ZapretTray{
class TP:ControlPanel{public TP(InfoItem a,InfoItem b,InfoItem c,Func<Color> f,Func<string> pg,ToolStripMenuItem[] s,ToolStripMenuItem[] q,ToolStripMenuItem t,ToolStripMenuItem e,Action r,Dictionary<ToolStripMenuItem,IPanelView> v):base(a,b,c,f,pg,s,q,t,e,r,v){}
 public void Tap(Point p){Shot();OnMouseClick(new MouseEventArgs(MouseButtons.Left,1,p.X,p.Y,0));Shot();}
 public void Hover(Point p){OnMouseMove(new MouseEventArgs(MouseButtons.None,0,p.X,p.Y,0));}
 public void Wheel(int d){OnMouseWheel(new MouseEventArgs(MouseButtons.None,0,0,0,d));}
 public Bitmap Shot(){var b=new Bitmap(Width,Height);DrawToBitmap(b,new Rectangle(0,0,Width,Height));return b;}}
static class T{
 static int pass,fail;static List<string> log=new List<string>();
 static void Ok(bool c,string n){if(c)pass++;else fail++;log.Add((c?"OK   ":"FAIL ")+n);}
 static ToolStripMenuItem I(string t,string ic,EventHandler h=null,string note=null){return MenuKit.Icon(MenuKit.Item(t,h),ic,note);}
 static Rectangle Diff(Bitmap a,Bitmap b){int x0=9999,y0=9999,x1=-1,y1=-1;for(int y=0;y<a.Height;y++)for(int x=0;x<a.Width;x++)if(a.GetPixel(x,y)!=b.GetPixel(x,y)){x0=Math.Min(x0,x);y0=Math.Min(y0,y);x1=Math.Max(x1,x);y1=Math.Max(y1,y);}return x1<0?Rectangle.Empty:Rectangle.FromLTRB(x0,y0,x1+1,y1+1);}
 [STAThread]static void Main(){ControlPanel.NoAnimation=true;Application.EnableVisualStyles();
 foreach(var mode in new[]{Theme.Dark,Theme.Light}){Theme.Mode=mode;Run(mode);}
 File.WriteAllLines("result.txt",log.Concat(new[]{"ИТОГО ok="+pass+" fail="+fail}));}
 static void Run(string mode){
 var clicks=new List<string>();
 var st=MenuKit.Info("Работает\tALT10",ItemKind.Status);var he=MenuKit.Info("✓\t✓\t✓\t3/4",ItemKind.Health);var sp=MenuKit.Info("↓ 2.1 МБ/с\t24 мс\t—",ItemKind.Stats);
 var menu=new ContextMenuStrip();menu.Items.AddRange(new ToolStripItem[]{st,he,sp});
 var games=I("Мои игры","games");var add=I("Добавить игру...","plus",(a,b)=>clicks.Add("addgame"),"Выбери запущенную игру из списка");games.DropDownItems.Add(add);games.DropDownItems.Add(MenuKit.Sep("Встроенные игры"));var ar=I("ARC Raiders","games");ar.Checked=true;ar.Tag=ItemKind.Toggle;ar.Click+=(a,b)=>ar.Checked=!ar.Checked;games.DropDownItems.Add(ar);games.DropDownItems.Add(MenuKit.Sep("Твои игры"));var ug=I("VeryVeryLongGameNameShippingClient-Win64-Shipping-Edition   (адресов: 12345, выкл)","games");ug.DropDownItems.Add(I("Включена","power"));games.DropDownItems.Add(ug);
 var sites=I("Мои сайты...","sites");
 var fix=I("Подключение","connection");var fast=I("Подобрать: быстро","fast",(a,b)=>clicks.Add("fast"),"Найдёт первый рабочий конфиг");fix.DropDownItems.Add(fast);var man=I("Выбрать конфиг вручную","list");for(int i=0;i<40;i++){var c=I("general (FAKE TLS AUTO ALT"+i+")","config");c.Checked=i==10;man.DropDownItems.Add(c);}fix.DropDownItems.Add(man);var undo=I("Вернуть DNS как было","undo",(a,b)=>clicks.Add("undo"));undo.Enabled=false;fix.DropDownItems.Add(undo);var rep=I("Что-то не работает","report",(a,b)=>clicks.Add("report"));fix.DropDownItems.Add(rep);
 var set=I("Настройки","settings");var be=I("Поведение","config");var w=I("Сторож (сам чинит, если сломалось)","shield");w.CheckOnClick=true;w.Checked=true;be.DropDownItems.Add(w);set.DropDownItems.Add(be);
 var hint=I("Подсказки...","hints");var tg=I("Выключить","power",(a,b)=>clicks.Add("toggle"));tg.Tag=ItemKind.PowerOff;var ex=I("Выход","exit",(a,b)=>clicks.Add("exit"));
 menu.Items.AddRange(new ToolStripItem[]{games,sites,fix,set,hint,tg,ex});
 var sl=new List<Tuple<string,string,bool>>{Tuple.Create("mysite.com","f",false),Tuple.Create("очень-очень-очень-длинный-поддомен.another-very-long-domain-name-for-testing.example.com","f",false),Tuple.Create("1.2.3.4","ipset",true)};
 var hv=new HintsView();var sv=new SitesView(()=>sl,(v,ip)=>sl.Add(Tuple.Create(v,"f",false)),(e,x)=>{int k=sl.IndexOf(e);sl[k]=Tuple.Create(e.Item1,e.Item2,x);},e=>sl.Remove(e));
 var p=new TP(st,he,sp,()=>Theme.On,()=>"отклик 183 мс\nпинг 24 мс\tотклик 240 мс\nпинг 31 мс\tотклик 112 мс\nпинг 44 мс\tотклик 95 мс\nпинг 40 мс",new[]{games,sites,fix,set,hint},new[]{fast,rep},tg,ex,()=>{},new Dictionary<ToolStripMenuItem,IPanelView>{{sites,sv},{hint,hv}});
 p.CreateControl();var size=p.Size;
 Func<string,Bitmap> snap=n=>{var b=p.Shot();b.Save(mode+"_"+n+".png",ImageFormat.Png);return b;};
 Func<int,Point> side=i=>p.SideCenter(i);
 var P_=new Point(0,0);
 // Размеры не меняются от данных, главная
 var vals=new[]{new[]{"Работает\tALT10","✓\t✓\t✓\t3/4","↓ 2.1 МБ/с\t24 мс\t—"},new[]{"Работает через VPN\tgeneral (FAKE TLS AUTO ALT3)","✗\t✗\t✗\t10/10","↓ 999.9 МБ/с\t12000 мс\t123 ч 59 мин"},new[]{"Ищу конфиг с минимальным пингом...","⋯\t⋯\t⋯\t⋯","↓ —\t—\t—"},new[]{"Есть проблема\tgeneral","✓\t✓\t\t","↓ 0 КБ/с\t—\t<1 мин"},new[]{"Работает\tALT","✓\t✗\t2/2\t","↓ 12 КБ/с\t1 мс\t1 мин"}};
 var names=new[]{"normal","extreme","busy","twotiles","nosites"};
 for(int i=0;i<vals.Length;i++){st.Value=vals[i][0];he.Value=vals[i][1];sp.Value=vals[i][2];snap("home_"+names[i]);Ok(p.Size==size,mode+" главная "+names[i]+": размер окна не изменился");}
 // Текст влезает в плитку
 int tile=182;using(var hf=new Font("Bahnschrift",16f,FontStyle.Bold)){foreach(var v in new[]{"↓ 999.9 МБ/с","12000 мс","123 ч 59 мин"})Ok(TextRenderer.MeasureText(v,hf).Width<=tile-12,mode+" влезает в плитку: "+v+" ("+TextRenderer.MeasureText(v,hf).Width+" из "+(tile-12)+")");}
 // Меняем только пинг: изменения только внутри плитки пинга
 st.Value="Работает\tALT10";he.Value="✓\t✓\t✓\t3/4";sp.Value="↓ 2.1 МБ/с\t24 мс\t—";var a1=p.Shot();sp.Value="↓ 2.1 МБ/с\t12000 мс\t—";var a2=p.Shot();var d=Diff(a1,a2);var pr=p.TileRect(5);Ok(pr.Contains(new Rectangle(d.Left,d.Top,Math.Max(1,d.Width),Math.Max(1,d.Height))),mode+" смена пинга трогает только свою плитку: "+d);
 sp.Value="↓ 2.1 МБ/с\t24 мс\t—";
 // Наведение ничего не сдвигает: повтор снимка даёт то же самое
 var h1=p.Shot();p.Hover(new Point(600,600));var h2=p.Shot();p.Hover(new Point(5,5));var h3=p.Shot();Ok(Diff(h1,h3).IsEmpty,mode+" наведение и уход мыши возвращают тот же вид");
 { p.Shot();var q=p.HelpCenter;var u0=p.Shot();p.Hover(q);var u1=p.Shot();u1.Save(mode+"_help.png");Ok(!Diff(u0,u1).IsEmpty,mode+" вопросик показывает пояснение");p.Tap(q);Ok(p.Size==size,mode+" клик по вопросику ничего не ломает");p.Hover(new Point(5,5));var u2=p.Shot();Ok(Diff(u0,u2).IsEmpty,mode+" пояснение исчезает, вид прежний"); }
 foreach(var ge in new[]{"WardogsClient-Win64-Shipping","PioneerGame","SomeUnknownGame"}){var gn=ge;p.RunningGame=()=>gn;var gb=p.Shot();gb.Save(mode+"_game_"+gn+".png");Ok(p.Size==size,mode+" плитка с игрой "+gn);}p.RunningGame=null;
 { var b0=p.Shot();p.Hover(new Point(p.ClientSize.Width-24,20));var b1=p.Shot();b1.Save(mode+"_btn.png");p.Hover(new Point(p.ClientSize.Width-24,20));Ok(!Diff(b0,b1).IsEmpty,mode+" кнопка закрыть подсвечивается");p.Hover(new Point(p.ClientSize.Width-62,20));var b2=p.Shot();Ok(!Diff(b1,b2).IsEmpty,mode+" кнопка свернуть подсвечивается");p.Hover(new Point(5,5));p.Tap(new Point(p.ClientSize.Width-62,20));Ok(!p.Visible,mode+" кнопка свернуть прячет окно"); }
 // Клики по разделам
 var title=new[]{"games","sites","fix","set","hint"};
 for(int i=0;i<5;i++){p.Tap(side(i+1));var b=snap("page_"+title[i]);Ok(p.Size==size,mode+" раздел "+title[i]+" открылся");}
 // Мои сайты: переключатель и крестик
 p.Tap(side(2));var before=sl[0].Item3;
 var row=new Point(108+772-44-52+20,62+50+30+27);p.Tap(row);Ok(sl[0].Item3!=before,mode+" переключатель сайта работает");
 snap("sites_off");p.Tap(row);Ok(sl[0].Item3==before,mode+" переключатель сайта обратно");
 int cnt=sl.Count;var extra=Tuple.Create("zz.example.com","f",false);sl.Add(extra);p.Tap(side(0));p.Tap(side(2));p.Shot();
 var delY=62+50+30+(cnt)*(54+8)+27;p.Tap(new Point(108+772-44+16,delY));Ok(!sl.Contains(extra),mode+" крестик удаляет строку");
 // Много сайтов: прокрутка и шапка
 for(int i=0;i<40;i++)sl.Add(Tuple.Create("site"+i+".example.com","f",false));p.Tap(side(0));p.Tap(side(2));p.Shot();var hs1=p.Shot();p.Wheel(-2400);var hs2=p.Shot();var ds=Diff(hs1,hs2);hs2.Save(mode+"_sites_scrolled.png");Ok(!ds.IsEmpty&&ds.Top>=62+50-1,mode+" прокрутка сайтов не трогает шапку: "+ds);
 p.Wheel(-99999);p.Shot();p.Wheel(99999);var hs3=p.Shot();Ok(Diff(hs1,hs3).IsEmpty,mode+" прокрутка туда и обратно возвращает вид");
 sl.RemoveAll(e=>e.Item1.StartsWith("site"));
 // Игры: переключатель
 p.Tap(side(1));var g0=ar.Checked;p.Shot();var at=new Point(108+40,62+62+12+6+28+30);p.Tap(at);Ok(ar.Checked!=g0,mode+" переключатель игры работает");p.Tap(at);
 // Длинный список конфигов
 p.Tap(side(3));p.Tap(new Point(658,62+30));var c1=p.Shot();c1.Save(mode+"_cfg0.png");p.Wheel(-1500);var c2=p.Shot();c2.Save(mode+"_cfg1.png");var dc=Diff(c1,c2);Ok(dc.Top>=62-1,mode+" список конфигов: шапка на месте "+dc);
 // Подсказки: прокрутки нет
 p.Tap(side(5));for(int i=0;i<3;i++){hv.Page=i;var until=DateTime.Now.AddMilliseconds(600);while(DateTime.Now<until){Application.DoEvents();System.Threading.Thread.Sleep(15);}/* шаги подсказки выезжают с анимацией: ждём, пока встанут */var s1=p.Shot();p.Wheel(-1000);var s2=p.Shot();Ok(Diff(s1,s2).IsEmpty,mode+" подсказки вкладка "+i+": колесо ничего не двигает");s2.Save(mode+"_hint"+i+".png");}
 // Отключённое и обычные действия
 p.Tap(side(3));clicks.Clear();p.Shot();p.Tap(new Point(108+40,62+62+12+30));
 Ok(clicks.Count==0||!clicks.Contains("undo"),mode+" отключённый пункт не нажимается");
 he.Available=false;sp.Available=false;st.Value="Выключен";p.Tap(side(0));snap("off");he.Available=true;sp.Available=true;
 clicks.Clear();p.Shot();var pc=p.PowerCenter;p.Tap(pc);p.Tap(p.ExitCenter);Ok(clicks.Contains("toggle")&&clicks.Contains("exit"),mode+" Выключить и Выход нажимаются");
 p.Dispose();}
}}