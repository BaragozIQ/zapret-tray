using System;using System.Drawing;using System.Drawing.Imaging;using System.IO;using System.Linq;using System.Threading;using System.Windows.Forms;using System.Collections.Generic;using System.Reflection;
namespace ZapretTray{
class TP:ControlPanel{public TP(InfoItem a,InfoItem b,InfoItem c,Func<Color> f,Func<string> pg,ToolStripMenuItem[] s,ToolStripMenuItem[] q,ToolStripMenuItem t,ToolStripMenuItem e,Action r,Dictionary<ToolStripMenuItem,IPanelView> v):base(a,b,c,f,pg,s,q,t,e,r,v){}
 public void Tap(Point p){OnMouseMove(new MouseEventArgs(MouseButtons.None,0,p.X,p.Y,0));OnMouseClick(new MouseEventArgs(MouseButtons.Left,1,p.X,p.Y,0));}
 public void PaintZero(){var old=ClientSize;ClientSize=new Size(0,0);using(var b=new Bitmap(10,10))using(var g=Graphics.FromImage(b))OnPaint(new PaintEventArgs(g,new Rectangle(0,0,10,10)));ClientSize=old;}
 public void Hov(Point p){OnMouseMove(new MouseEventArgs(MouseButtons.None,0,p.X,p.Y,0));}}
static class A{static int pass,fail;static List<string> log=new List<string>();static void Ok(bool c,string n){if(c)pass++;else fail++;log.Add((c?"OK   ":"FAIL ")+n);}
 static void Pump(int ms){var t=Environment.TickCount;while(Environment.TickCount-t<ms){Application.DoEvents();Thread.Sleep(10);}}
 static bool Same(Bitmap a,Bitmap b){for(int y=0;y<a.Height;y+=1)for(int x=0;x<a.Width;x+=1)if(a.GetPixel(x,y)!=b.GetPixel(x,y))return false;return true;}
 [STAThread]static void Main(string[] av){Application.EnableVisualStyles();try{foreach(var mode in new[]{Theme.Dark,Theme.Light}){Theme.Mode=mode;
  Func<string,string,ToolStripMenuItem> I=(x,ic)=>MenuKit.Icon(MenuKit.Item(x,null),ic);
  var st=MenuKit.Info("Работает\tALT",ItemKind.Status);var he=MenuKit.Info("✓\t✗\t1/2\t",ItemKind.Health);var sp=MenuKit.Info("↓ 8,4 МБ/с\t32 мс\t—",ItemKind.Stats);var m=new ContextMenuStrip();m.Items.AddRange(new ToolStripItem[]{st,he,sp});
  var g=I("Мои игры","games");g.DropDownItems.Add(I("Добавить","plus"));var f=I("A","fast");var f2=I("B","report");var tg=I("T","power");var ex=I("E","exit");m.Items.AddRange(new ToolStripItem[]{g,f,f2,tg,ex});
  var p=new TP(st,he,sp,()=>Theme.On,()=>"отклик 1 мс\tотклик 2 мс\t\t",new[]{g},new[]{f,f2},tg,ex,()=>{},new Dictionary<ToolStripMenuItem,IPanelView>());
  var calls=new List<int>();var checking=new bool[7];p.RecheckTile=i=>{calls.Add(i);checking[i]=true;};p.TileChecking=i=>checking[i];
  p.StartPosition=FormStartPosition.Manual;p.Location=new Point(-3000,-3000);p.Show();Pump(50);
  Func<Bitmap> Shot=()=>{var b=new Bitmap(p.Width,p.Height);p.DrawToBitmap(b,new Rectangle(0,0,p.Width,p.Height));return b;};
  Shot();int tile=(772-36)/4;Func<int,Point> T=i=>new Point(210+(tile+12)*i+tile/2,400);
  p.Hov(new Point(5,5));var idle=Shot();
  p.Hov(T(1));var hov=Shot();hov.Save(mode+"-hover.png");Ok(!Same(idle,hov),mode+" плитка подсвечивается");Ok(p.Cursor==Cursors.Hand,mode+" над плиткой курсор-рука");
  p.Tap(T(3));Ok(calls.Count==0,mode+" плитка без данных не нажимается");
  p.Tap(T(1));Ok(calls.SequenceEqual(new[]{1}),mode+" нажатие на Discord проверяет только Discord");
  p.Tap(T(1));Ok(calls.Count==1,mode+" повторное нажатие не запускает вторую проверку");
  Pump(120);var s1=Shot();s1.Save(mode+"-spin1.png");Pump(200);var s2=Shot();s2.Save(mode+"-spin2.png");Ok(!Same(s1,s2),mode+" кружок крутится");
  Pump(1000);p.Hov(new Point(5,5));var still=Shot();still.Save(mode+"-spin3.png");Ok(!Same(idle,still),mode+" пока проверка идёт, кружок не исчезает (долгий ответ)");
  p.Tap(T(0));Ok(calls.Count==2&&calls[1]==0,mode+" другую плитку можно проверять одновременно");
  checking[1]=false;he.Value="✓\t✓\t1/2\t";Pump(70);var pop=Shot();pop.Save(mode+"-pop.png");checking[0]=false;Pump(1300);p.Hov(new Point(5,5));var done=Shot();done.Save(mode+"-done.png");
  he.Value="✓\t✗\t1/2\t";var back=Shot();Ok(Same(idle,back),mode+" после проверки вид как прежде, анимация убрана");
  he.Value="⋯\t⋯\t⋯\t";calls.Clear();p.Tap(T(1));Ok(calls.Count==1,mode+" во время подбора плитка нажимается сразу");checking[1]=false;Pump(1000);he.Value="✓\t✗\t1/2\t";
  calls.Clear();p.Tap(T(2));checking[2]=false;var tl=(772-36)/4;p.Hov(new Point(210+(tl+12)*2+tl-19,286+19));calls.Clear();p.Tap(new Point(210+(tl+12)*2+tl-19,286+19));Ok(calls.Count==0,mode+" вопросик не запускает проверку");
  Pump(1000);calls.Clear();he.Value="✓\t✗\t1/2\t";var y0=170;Func<int,Point> N=i=>new Point(210+(tile+12)*i+tile/2,y0);
  p.Tap(N(1));p.Tap(N(2));p.Tap(N(3));Ok(calls.SequenceEqual(new[]{4,5,6}),mode+" скорость, пинг и время нажимаются");Pump(100);Shot().Save(mode+"-numbers.png");Pump(1000);
  calls.Clear();p.Tap(N(0));Ok(calls.SequenceEqual(new[]{0,1,2}),mode+" плитка статуса проверяет все сервисы с данными");Pump(100);Shot().Save(mode+"-status.png");Pump(1000);
  calls.Clear();p.Tap(T(3));Ok(calls.Count==0,mode+" сайтов нет: проверка не идёт");p.Hov(T(3));Ok(p.Cursor==Cursors.Hand,mode+" плитка с прочерком тоже нажимается");
  he.Value="✓\t✗\t\t";var path=(List<ToolStripMenuItem>)typeof(ControlPanel).GetField("path",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(p);p.Tap(T(2));Ok(path.Count==1&&path[0]==g,mode+" игры с прочерком открывают свой раздел");p.OpenSection(null);Shot();he.Value="✓\t✗\t1/2\t";
  p.ShowTileCheck(3);Pump(100);Shot().Save(mode+"-edited.png");Pump(1000);Ok(true,mode+" кружок после правки списка");
  int th=0;p.ToggleTheme=()=>th++;Pump(30);Shot();p.Hov(new Point(5,5));var ts=new Point(60,660-100+21);p.Tap(ts);Ok(th==1,mode+" переключатель темы нажимается");p.OpenSection(g);Shot();p.Tap(ts);Ok(th==2,mode+" переключатель темы работает и на другой странице");p.OpenSection(null);Shot();p.Hov(new Point(5,5));
  bool noCrash=true;try{p.PaintZero();}catch(Exception){noCrash=false;}Ok(noCrash,mode+" нулевой размер окна не роняет программу");Pump(30);
  // Уход на другую страницу и закрытие во время кружения
  checking[1]=true;p.Tap(T(1));p.OpenSection(g);Pump(1000);Ok(!p.IsDisposed,mode+" смена страницы во время кружения не ломает");p.OpenSection(null);checking[1]=false;Pump(900);
  checking[0]=true;p.OpenSection(null);p.Tap(T(0));p.Close();Pump(200);Ok(p.IsDisposed,mode+" закрытие во время кружения не ломает");
 }
 var tc=typeof(TrayContext);Func<string,object[],object> C=(n,a)=>tc.GetMethod(n,BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,a);
 var ps=new List<Tuple<string,string[]>>{Tuple.Create("A",new[]{"a.com","a2.com"}),Tuple.Create("B",new[]{"b.com"})};
 var okA=new SiteResult{Url="https://a2.com",Status=SiteStatus.Ok,TlsMs=50};var noB=new SiteResult{Url="https://b.com",Status=SiteStatus.NoConnect};
 Ok((string)C("GamesMark",new object[]{ps,new List<SiteResult>{okA,noB}})=="1/2","игры: 1 из 2");Ok((string)C("GamesMark",new object[]{new List<Tuple<string,string[]>>(),new List<SiteResult>()})=="","игр нет: пусто");Ok((string)C("GamesMark",new object[]{ps.Take(1).ToList(),new List<SiteResult>{okA}})=="✓","одна игра: галочка");
 Ok((string)C("GameTimeOf",new object[]{new List<SiteResult>{okA,noB}})=="отклик 50 мс","отклик игр");Ok((string)C("SitesTimeOf",new object[]{new List<SiteResult>{okA},new long[]{-1,10,20}})=="отклик 50 мс\nпинг 15 мс","отклик и пинг сайтов");Ok((string)C("SitesTimeOf",new object[]{new List<SiteResult>{noB},new long[]{-1}})=="","нет данных: пусто");
 }catch(Exception e){Ok(false,"ТЕСТ УПАЛ: "+e);}
 File.WriteAllLines("result.txt",log.Concat(new[]{"ИТОГО ok="+pass+" fail="+fail}));}}}