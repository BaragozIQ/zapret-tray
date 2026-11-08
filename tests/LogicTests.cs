using System;using System.Drawing;using System.IO;using System.Linq;using System.Reflection;using System.Windows.Forms;using System.Collections.Generic;using System.Diagnostics;using System.Runtime.InteropServices;using System.IO.Compression;
namespace ZapretTray{
static class U{
 static int pass,fail;static List<string> log=new List<string>();
 static void Ok(bool c,string n){if(c)pass++;else fail++;log.Add((c?"OK   ":"FAIL ")+n);}
 [DllImport("user32.dll")]static extern int GetGuiResources(IntPtr h,int k);
 static object Call(Type t,string m,params object[] a){return t.GetMethod(m,BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Public).Invoke(null,a);}
 static void Entry(string s,string want,bool ip){string v;bool i;bool r=Util.ParseEntry(s,out v,out i);Ok(want==null?!r:(r&&v==want&&i==ip),"ввод сайта '"+s+"' -> "+(r?v:"отказ"));}
 [STAThread]static void Main(string[] a){string zip=a[0];Application.EnableVisualStyles();
  try{Logic();Sign(zip);Package(zip);Settings();Checks();Direct(zip);Vpn(zip);Hints();Stress();}catch(Exception e){Ok(false,"ТЕСТ УПАЛ: "+e);}
  File.WriteAllLines("result.txt",log.Concat(new[]{"ИТОГО ok="+pass+" fail="+fail}));}
 static void Logic(){
  Entry("youtube.com","youtube.com",false);Entry("https://YouTube.com/watch?v=1","youtube.com",false);Entry("*.site.org","site.org",false);Entry("site.org:8443","site.org",false);
  Entry("1.2.3.4","1.2.3.4",true);Entry("1.2.3.0/24","1.2.3.0/24",true);Entry("1.2.3.0/4",null,false);Entry("",null,false);Entry("   ",null,false);Entry("не сайт",null,false);Entry("../../windows",null,false);Entry("a b c",null,false);Entry("www.youtube.com","youtube.com",false);Entry("HTTPS://WWW.YouTube.com/","youtube.com",false);Entry("http://www.youtube.com/watch?v=abc&t=12#x","youtube.com",false);Entry("youtube.com?x=1","youtube.com",false);Entry("youtube.com#top","youtube.com",false);Entry("//youtube.com","youtube.com",false);Entry("\"https://youtube.com/\"","youtube.com",false);Entry("<youtube.com>","youtube.com",false);Entry("вот сайт youtube.com, спасибо","youtube.com",false);Entry("youtube.com.","youtube.com",false);Entry("user:pw@site.org:8080/a","site.org",false);Entry("music.youtube.com","music.youtube.com",false);Entry("www.com","www.com",false);Entry("https://1.2.3.4:443/a","1.2.3.4",true);Entry("http://1.2.3.0/24","1.2.3.0/24",true);Entry("президент.рф","xn--d1abbgf6aiiy.xn--p1ai",false);Entry("# см. example.com",null,false);Entry("foo",null,false);Entry("1.2.3",null,false);Entry("--flag=evil",null,false);
  foreach(var c in new[]{new object[]{"0.5","0.4",true},new object[]{"0.4","0.4",false},new object[]{"0.3","0.4",false},new object[]{"0.10","0.9",true},new object[]{"abc","0.4",false},new object[]{"","0.4",false}})Ok(Updater.IsNewer((string)c[0],(string)c[1])==(bool)c[2],"версия "+c[0]+" новее "+c[1]+": "+c[2]);
  var tc=typeof(TrayContext);var cp=typeof(ControlPanel);
  foreach(var c in new[]{new object[]{2,2,"✓"},new object[]{1,2,"1/2"},new object[]{0,3,"0/3"}})Ok((string)Call(tc,"CountMark",c[0],c[1])==(string)c[2],"отметка "+c[0]+" из "+c[1]+" = "+c[2]);
  Ok((int)Call(cp,"PingBars","32 мс")==4&&(int)Call(cp,"PingBars","60 мс")==3&&(int)Call(cp,"PingBars","180 мс")==2&&(int)Call(cp,"PingBars","12000 мс")==1&&(int)Call(cp,"PingBars","—")==0,"полоски пинга");
  Func<string,double> L=s=>(double)Call(cp,"SpeedLevel",s);
  Ok(L("↓ —")==0&&L("↓ 0 КБ/с")==0,"спидометр: нет скорости - стрелка в нуле");Ok(Math.Abs(L("↓ 8,4 МБ/с")-L("↓ 8.4 МБ/с"))<1e-9,"спидометр: запятая и точка одинаковы");Ok(L("↓ 12 КБ/с")<L("↓ 900 КБ/с")&&L("↓ 900 КБ/с")<L("↓ 8,4 МБ/с"),"спидометр: растёт со скоростью");Ok(L("↓ 99999 МБ/с")==1,"спидометр: не зашкаливает");
 }
 static void Sign(string zip){var d=File.ReadAllBytes(zip);var s=File.ReadAllBytes(zip+".sig");Ok(Updater.SignatureValid(d,s),"подпись настоящего архива принята");var b=(byte[])d.Clone();b[b.Length/2]^=1;Ok(!Updater.SignatureValid(b,s),"испорченный архив отвергнут");var s2=(byte[])s.Clone();s2[0]^=1;Ok(!Updater.SignatureValid(d,s2),"испорченная подпись отвергнута");Ok(!Updater.SignatureValid(d,new byte[0]),"пустая подпись отвергнута");
  using(var rsa=new System.Security.Cryptography.RSACryptoServiceProvider(2048))Ok(!Updater.SignatureValid(d,rsa.SignData(d,"SHA256")),"подпись чужим ключом отвергнута");}
 static void Package(string zip){using(var z=ZipFile.OpenRead(zip)){var n=z.Entries.Select(e=>e.FullName.Replace('\\','/')).ToList();foreach(var w in new[]{"Setup.exe","README.txt","files/ZapretTray.exe","files/core/bin/winws.exe"})Ok(n.Contains(w),"в архиве есть "+w);Ok(n.Any(x=>x.StartsWith("files/core/lists/")),"в архиве есть списки");Ok(!n.Any(x=>x.IndexOf("signing",StringComparison.OrdinalIgnoreCase)>=0||x.EndsWith(".xml",StringComparison.OrdinalIgnoreCase)&&x.IndexOf("key",StringComparison.OrdinalIgnoreCase)>=0),"в архиве нет ключа подписи");Ok(!n.Any(x=>x.Contains("..")),"в архиве нет выхода из папки");}}
 static void Settings(){var dir=Path.Combine(Path.GetTempPath(),"zt-test-"+Guid.NewGuid().ToString("N"));var p=new AppPaths(dir);Directory.CreateDirectory(p.Lists);
  try{File.WriteAllLines(p.ListGeneralUser,new[]{"mysite.com","#off embark.games","# коммент"});File.WriteAllLines(p.ListExcludeUser,new[]{"bad.com"});File.WriteAllLines(p.IpsetUser,new[]{"1.2.3.4"});
   var g=new Game{Exe="cs2",Enabled=false};g.Subnets.Add("5.5.5.0/24");var file=Path.Combine(dir,"s.txt");
   SettingsFile.Save(file,p,new[]{g},new Dictionary<string,string>{{"theme","dark"},{"watchdog","1"}});var r=SettingsFile.Read(file,p);
   Ok(r.Lists["list-general-user.txt"].SequenceEqual(new[]{"mysite.com","#off embark.games"}),"перенос: сайты и выключенные игры сохранились");Ok(r.Lists["list-exclude-user.txt"].Single()=="bad.com","перенос: исключения");Ok(r.Lists["tray-ipset-user.txt"].Single()=="1.2.3.4","перенос: IP");Ok(r.Games.Single().Exe=="cs2"&&!r.Games[0].Enabled&&r.Games[0].Subnets.Contains("5.5.5.0/24"),"перенос: игра с адресами");Ok(r.Values["theme"]=="dark","перенос: тема");
   var evil=Path.Combine(dir,"e.txt");File.WriteAllLines(evil,new[]{"ZapretTray settings 1","[settings]","evil=1","theme=dark","[list ..\\..\\Windows\\hosts]","1.2.3.4","[list other.txt]","x.com","[list list-general-user.txt]","good.com","--dpi-desync=evil","a b c","1.2.3.4","[list tray-ipset-user.txt]","notip.com","9.9.9.9","[game ..\\..\\evil|1]","8.8.8.8","[game ok|1]","127.0.0.1","192.168.0.1","8.8.4.0/24","5.5.4.0/24","garbage"});
   r=SettingsFile.Read(evil,p);Ok(!r.Values.ContainsKey("evil")&&r.Values.ContainsKey("theme"),"чужой файл: лишние параметры отброшены");Ok(r.Lists.Keys.All(k=>k=="list-general-user.txt"||k=="tray-ipset-user.txt"),"чужой файл: нельзя записать в посторонний путь");Ok(r.Lists["list-general-user.txt"].SequenceEqual(new[]{"good.com"}),"чужой файл: мусор в сайтах отброшен");Ok(r.Lists["tray-ipset-user.txt"].SequenceEqual(new[]{"9.9.9.9"}),"чужой файл: домен в IP-списке отброшен");Ok(r.Games.Count==1&&r.Games[0].Exe=="ok","чужой файл: опасное имя игры отброшено");Ok(r.Games[0].Subnets.SetEquals(new[]{"5.5.4.0/24"}),"чужой файл: мусор и DNS-адреса в игре отброшены");
   var notOurs=Path.Combine(dir,"n.txt");File.WriteAllText(notOurs,"hello");bool thrown=false;try{SettingsFile.Read(notOurs,p);}catch(InvalidDataException){thrown=true;}Ok(thrown,"не наш файл не принимается");
   var empty=Path.Combine(dir,"z.txt");File.WriteAllText(empty,"");thrown=false;try{SettingsFile.Read(empty,p);}catch(InvalidDataException){thrown=true;}Ok(thrown,"пустой файл не принимается");
   var big=Path.Combine(dir,"b.txt");using(var fs=File.Create(big))fs.SetLength(6*1024*1024);thrown=false;try{SettingsFile.Read(big,p);}catch(InvalidDataException){thrown=true;}Ok(thrown,"огромный файл не принимается");
  }finally{try{Directory.Delete(dir,true);}catch{}}}
 // Подсказки: название влезает в строку списка, каждый шаг - в свою кнопку, пояснение - в несколько строк плашки
 static void Checks(){
  Ok(CheckStore.Normalize("youtube.com")=="https://youtube.com","проверка: домен -> https://");
  Ok(CheckStore.Normalize("https://WWW.YouTube.com/watch?v=1")=="https://www.youtube.com","проверка: ссылка -> только хост");
  Ok(CheckStore.Normalize("  discord.com/app  ")=="https://discord.com","проверка: путь и пробелы убираются");
  Ok(CheckStore.Normalize("не сайт")==null&&CheckStore.Normalize("")==null&&CheckStore.Normalize("foo")==null,"проверка: мусор отвергнут");
  var dir=Path.Combine(Path.GetTempPath(),"zt-chk-"+Guid.NewGuid().ToString("N"));var p=new AppPaths(dir);Directory.CreateDirectory(p.Lists);
  try{
   var s=new CheckStore(p);
   Ok(s.All().Count==CheckStore.Defaults.Length,"проверки: засеялись по умолчанию");
   Ok(s.All().Count(c=>c.Required)==8,"проверки: 8 обязательных по умолчанию");
   Ok(s.All().Any(c=>c.Url=="https://arcraiders.com"&&!c.Required),"проверки: игровой сайт необязательный");
   s.Add("example.com");s.SetRequired("https://arcraiders.com",true);s.Remove("https://id.embark.games");
   Ok(!s.Add("example.com"),"проверки: дубликат не добавляется");
   var s2=new CheckStore(p);
   Ok(s2.All().Any(c=>c.Url=="https://example.com"&&c.Required),"проверки: добавленный сохранился обязательным");
   Ok(s2.All().Any(c=>c.Url=="https://arcraiders.com"&&c.Required),"проверки: переключение сохранилось");
   Ok(!s2.All().Any(c=>c.Url=="https://id.embark.games"),"проверки: удалённый не вернулся");
  }finally{try{Directory.Delete(dir,true);}catch{}}
  // Кнопка «Проверить» в «Моих сайтах»: проверка идёт в фоне, «Подобрать» отдаёт адрес сайта
  string picked=null;
  Func<string,SiteResult> fake=u=>u.Contains("bad")?new SiteResult{Url=u,Status=SiteStatus.NoConnect,Measured=true,Connected=true,TlsDone=true}:new SiteResult{Url=u,Status=SiteStatus.Ok,Measured=true,Connected=true,TlsDone=true,TlsMs=50};
  var sv=new SitesView(()=>new List<Tuple<string,string,bool>>(),(x,y)=>{},(x,y)=>{},x=>{},fake,u=>picked=u);
  var hid=BindingFlags.NonPublic|BindingFlags.Instance;
  typeof(SitesView).GetMethod("StartCheck",hid).Invoke(sv,new object[]{"good.com"});typeof(SitesView).GetMethod("StartCheck",hid).Invoke(sv,new object[]{"bad.com"});
  var states=(System.Collections.IDictionary)typeof(SitesView).GetField("states",hid).GetValue(sv);
  var t0=Environment.TickCount;Func<string,string,object> F=(k,f)=>{var o=states[k];return o==null?null:o.GetType().GetField(f).GetValue(o);};
  while(Environment.TickCount-t0<3000&&((bool)F("good.com","Busy")||(bool)F("bad.com","Busy")))System.Threading.Thread.Sleep(20);
  Ok(!(bool)F("good.com","Busy")&&(bool)F("good.com","Opened"),"проверка сайта: открылся");
  Ok(!(bool)F("bad.com","Busy")&&!(bool)F("bad.com","Opened")&&(string)F("bad.com","Stage")=="соединился, сервер не ответил","проверка сайта: не открылся, видно почему");
  typeof(SitesView).GetMethod("Pick",hid).Invoke(sv,new object[]{"bad.com"});
  Ok(picked=="https://bad.com"&&!states.Contains("bad.com"),"«Подобрать» отдаёт адрес сайта и сбрасывает результат");
 }
 // Туннель VPN (Clash в режиме TUN): место для --wf-iface в конфигах zapret, чтобы подделки не попадали в туннель
 static void Vpn(string zip){
  var raw=System.Text.Encoding.GetEncoding(28591);
  const string bat="start \"zapret: %~n0\" /b \"%BIN%winws.exe\" --wf-tcp=80,443,%GameFilterTCP% --wf-udp=443,%GameFilterUDP% ^\r\n--filter-udp=443 --wf-udp=1 --dpi-desync=fake\r\n";
  var p1=ZapretUpdater.PatchIface(bat);
  Ok(p1.Contains("--wf-udp=443,%GameFilterUDP% %TrayWinws% ^")&&System.Text.RegularExpressions.Regex.Matches(p1,"%TrayWinws%").Count==1,"туннель: место для параметров - сразу после --wf-udp, один раз");
  Ok(ZapretUpdater.PatchIface(p1)==p1,"туннель: повторная правка ничего не меняет");
  Ok(ZapretUpdater.UnsafeLine(p1)==null,"туннель: поправленный конфиг проходит проверку безопасности");
  using(var z=ZipFile.OpenRead(zip)){
   var bats=z.Entries.Where(e=>e.FullName.Replace('\\','/').StartsWith("files/core/general")&&e.FullName.EndsWith(".bat")).ToList();
   Ok(bats.Count>=20&&bats.All(e=>{string t;using(var r=new StreamReader(e.Open(),raw))t=r.ReadToEnd();return System.Text.RegularExpressions.Regex.Matches(t,"%TrayWinws%").Count==1&&ZapretUpdater.UnsafeLine(t)==null;}),"туннель: все конфиги в архиве с местом для параметров ("+bats.Count+")");}
  // Батник: переменная не задана - пусто; задана - подставляется; мусор с символами cmd не проходит
  var dir=Path.Combine(Path.GetTempPath(),"zt-vpn-"+Guid.NewGuid().ToString("N"));var p=new AppPaths(dir);Directory.CreateDirectory(p.Core);
  try{
   var b=Path.Combine(p.Core,"t.bat");File.WriteAllText(b,"@echo off\r\necho [%TrayWinws%]> out.txt\r\n");var outf=Path.Combine(p.Core,"out.txt");
   Func<string,string> Run=x=>{try{File.Delete(outf);}catch{}Selector.WinwsExtra=()=>x;Selector.StartConfig(p,b);var t0=Environment.TickCount;while(!File.Exists(outf)&&Environment.TickCount-t0<5000)System.Threading.Thread.Sleep(50);System.Threading.Thread.Sleep(100);return File.Exists(outf)?File.ReadAllText(outf).Trim():null;};
   Ok(Run("")=="[]","туннель: без параметров в батнике пусто");
   Ok(Run("--wf-iface=10")=="[--wf-iface=10]"&&Selector.LastExtra=="--wf-iface=10","туннель: --wf-iface доходит до winws");
   Ok(Run("--wf-iface=10 & calc")=="[]"&&Selector.LastExtra=="","туннель: команда cmd в параметрах не проходит");
   Selector.WinwsExtra=()=>"";
  }finally{Selector.WinwsExtra=()=>"";try{Directory.Delete(dir,true);}catch{}}
 }
 // Игры «напрямую» (WARDOGS: от подделок хрипел голос): их сервера исключены из игровых правил, а 80/443 по-прежнему через обход
 static void Direct(string zip){
  using(var d=new GameModeDialog("WardogsClient-Win64-Shipping")){Ok(d.Direct&&!d.DontAsk,"окно новой игры: по умолчанию голос и матч напрямую");Ok(d.Text=="Wardogs добавлена"&&typeof(GameModeDialog).GetField("icon",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(d)!=null,"окно новой игры: значок и имя самой игры");}
  using(var d=new GameModeDialog("unknown-app")){Ok(d.Text=="unknown-app добавлена","окно новой игры: незнакомое приложение - по имени процесса");}
  var raw=System.Text.Encoding.GetEncoding(28591);
  const string bat="start \"zapret: %~n0\" /b \"%BIN%winws.exe\" --wf-tcp=80,443,%GameFilterTCP% ^\r\n--filter-tcp=80,443 --ipset=\"%LISTS%ipset-all.txt\" --new ^\r\n--filter-tcp=%GameFilterTCP% --ipset=\"%LISTS%ipset-all.txt\" --dpi-desync=fake --new ^\r\n--filter-udp=%GameFilterUDP% --ipset=\"%LISTS%ipset-all.txt\" --dpi-desync=fake\r\n";
  var p1=ZapretUpdater.PatchDirect(bat);
  Func<string,int> N=s=>System.Text.RegularExpressions.Regex.Matches(s,"tray-ipset-direct\\.txt").Count;
  Ok(N(p1)==2,"напрямую: исключение вписано в оба игровых правила");
  Ok(p1.Contains("--filter-tcp=80,443 --ipset")&&!p1.Contains("--filter-tcp=80,443 --ipset-exclude=\"%LISTS%tray-ipset-direct"),"напрямую: правило 80/443 (вход в игру) не тронуто");
  Ok(ZapretUpdater.PatchDirect(p1)==p1,"напрямую: повторная правка ничего не меняет");
  Ok(ZapretUpdater.UnsafeLine(p1)==null,"напрямую: поправленный конфиг проходит проверку безопасности");
  using(var z=ZipFile.OpenRead(zip)){
   var bats=z.Entries.Where(e=>e.FullName.Replace('\\','/').StartsWith("files/core/general")&&e.FullName.EndsWith(".bat")).ToList();
   Ok(bats.Count>=20,"напрямую: в архиве все конфиги ("+bats.Count+")");
   foreach(var e in bats){string t;using(var r=new StreamReader(e.Open(),raw))t=r.ReadToEnd();Ok(N(t)==2&&ZapretUpdater.UnsafeLine(t)==null,"напрямую: конфиг из архива поправлен: "+e.Name);}
   var ph=z.Entries.FirstOrDefault(e=>e.FullName.Replace('\\','/')=="files/core/lists/tray-ipset-direct.txt");
   string phText=null;if(ph!=null)using(var r=new StreamReader(ph.Open()))phText=r.ReadToEnd().Trim();
   Ok(phText==Ipset.Placeholder,"напрямую: в архиве есть непустой список (иначе winws не запустится)");}
  var dir=Path.Combine(Path.GetTempPath(),"zt-dir-"+Guid.NewGuid().ToString("N"));var p=new AppPaths(dir);Directory.CreateDirectory(p.GamesDir);
  try{
   File.WriteAllLines(p.GamesList,new[]{"Wardogs|1|1","Arc|1","Off|0|1"});
   File.WriteAllLines(Path.Combine(p.GamesDir,"Wardogs.txt"),new[]{"3.3.3.0/24"});File.WriteAllLines(Path.Combine(p.GamesDir,"Arc.txt"),new[]{"4.4.4.0/24"});File.WriteAllLines(Path.Combine(p.GamesDir,"Off.txt"),new[]{"5.5.5.0/24"});
   var gs=new GameStore(p);
   Ok(gs.Find("Wardogs").Direct&&!gs.Find("Arc").Direct,"напрямую: режим читается из списка игр");
   Ok(gs.EnabledSubnets().SequenceEqual(new[]{"4.4.4.0/24"}),"напрямую: адреса такой игры не добавляются в обход");
   Ok(gs.DirectSubnets().SequenceEqual(new[]{"3.3.3.0/24"}),"напрямую: в исключение - только включённая игра напрямую");
   gs.SetDirect(gs.Find("Arc"),true);gs.SetDirect(gs.Find("Wardogs"),false);var gs2=new GameStore(p);
   Ok(gs2.Find("Arc").Direct&&!gs2.Find("Wardogs").Direct&&gs2.Find("Arc").Enabled,"напрямую: переключение сохраняется");
   Ipset.WriteDirect(p,new string[0]);Ok(Util.ReadEntries(p.IpsetDirect).SequenceEqual(new[]{Ipset.Placeholder}),"напрямую: пустой список - заглушка, а не пустой файл");
   Ipset.WriteDirect(p,gs2.DirectSubnets());Ok(Util.ReadEntries(p.IpsetDirect).SequenceEqual(new[]{"4.4.4.0/24"}),"напрямую: список пишется");
   var f=Path.Combine(dir,"s.txt");SettingsFile.Save(f,p,gs2.Games,new Dictionary<string,string>());var rd=SettingsFile.Read(f,p);
   Ok(rd.Games.First(x=>x.Exe=="Arc").Direct&&!rd.Games.First(x=>x.Exe=="Wardogs").Direct,"напрямую: режим переносится файлом настроек");
  }finally{try{Directory.Delete(dir,true);}catch{}}
  // Подбор: обязательный сайт без адреса (cdninstagram.com) не в счёт, иначе подбор каждый раз гоняет все конфиги
  var cnt=typeof(Selector).GetMethod("Countable",BindingFlags.NonPublic|BindingFlags.Static);
  var mixed=new List<SiteResult>{new SiteResult{Status=SiteStatus.Ok},new SiteResult{Status=SiteStatus.NoDns}};
  Ok((bool)cnt.Invoke(null,new object[]{mixed,0})&&!(bool)cnt.Invoke(null,new object[]{mixed,1}),"подбор: сайт без адреса не в счёт");
  var none=new List<SiteResult>{new SiteResult{Status=SiteStatus.NoDns},new SiteResult{Status=SiteStatus.NoDns}};
  Ok((bool)cnt.Invoke(null,new object[]{none,0}),"подбор: нет адреса ни у кого - это нет интернета, считаем как обычно");
  // «Проверить» у имени без адреса: «Подобрать» не предлагаем
  var sv=new SitesView(()=>new List<Tuple<string,string,bool>>(),(x,y)=>{},(x,y)=>{},x=>{},u=>new SiteResult{Url=u,Status=SiteStatus.NoDns},u=>{});
  var hid=BindingFlags.NonPublic|BindingFlags.Instance;typeof(SitesView).GetMethod("StartCheck",hid).Invoke(sv,new object[]{"cdninstagram.com"});
  var states=(System.Collections.IDictionary)typeof(SitesView).GetField("states",hid).GetValue(sv);
  Func<string,object> F=k=>{var o=states["cdninstagram.com"];return o.GetType().GetField(k).GetValue(o);};
  var t0=Environment.TickCount;while(Environment.TickCount-t0<3000&&(bool)F("Busy"))System.Threading.Thread.Sleep(20);
  Ok(!(bool)F("Busy")&&(bool)F("NoName"),"проверка сайта: имя без адреса - «Подобрать» не предлагается");
 }
 static void Hints(){var t=typeof(HintsView);int listW=(int)t.GetField("ListWidth",BindingFlags.NonPublic|BindingFlags.Static).GetValue(null),detailW=(int)t.GetField("DetailWidth",BindingFlags.NonPublic|BindingFlags.Static).GetValue(null);
  var rowFont=(Font)t.GetField("RowFont",BindingFlags.NonPublic|BindingFlags.Static).GetValue(null);var stepFont=(Font)t.GetField("StepFont",BindingFlags.NonPublic|BindingFlags.Static).GetValue(null);var noteFont=(Font)t.GetField("NoteFont",BindingFlags.NonPublic|BindingFlags.Static).GetValue(null);
  foreach(var page in new[]{"Broken","Setup","Auto"}){var list=(Array)t.GetField(page,BindingFlags.NonPublic|BindingFlags.Static).GetValue(null);foreach(var h in list){var ty=h.GetType();string title=(string)ty.GetField("Title").GetValue(h),note=(string)ty.GetField("Note").GetValue(h);var st=(string[])ty.GetField("Steps").GetValue(h);
    Ok(TextRenderer.MeasureText(title,rowFont).Width<=listW-64,"подсказка влезает в список: "+title);
    foreach(var s in st)Ok(TextRenderer.MeasureText(s,stepFont).Width+32<=detailW-92,"шаг влезает в кнопку: "+title+" / "+s);
    var sz=TextRenderer.MeasureText(note,noteFont,new Size(detailW-104,0),TextFormatFlags.WordBreak);Ok(sz.Height<=80,"пояснение не длиннее четырёх строк: "+title);}}
 }
 static void Stress(){foreach(var mode in new[]{Theme.Dark,Theme.Light}){Theme.Mode=mode;
  var st=MenuKit.Info("Работает\tALT",ItemKind.Status);var he=MenuKit.Info("✓\t✓\t1/2\t✓",ItemKind.Health);var sp=MenuKit.Info("↓ 8,4 МБ/с\t32 мс\t2 ч",ItemKind.Stats);var m=new ContextMenuStrip();m.Items.AddRange(new ToolStripItem[]{st,he,sp});
  Func<string,string,ToolStripMenuItem> I=(x,ic)=>MenuKit.Icon(MenuKit.Item(x,null),ic);
  var g=I("Мои игры","games");var w=I("Wardogs","games");w.Tag=ItemKind.Toggle;g.DropDownItems.Add(w);g.DropDownItems.Add(I("ARC Raiders","games"));var sites=I("Мои сайты","sites");var hin=I("Подсказки","hints");var f=I("A","fast");var tg=I("T","power");var ex=I("E","exit");m.Items.AddRange(new ToolStripItem[]{g,sites,hin,f,tg,ex});
  var sl=new List<Tuple<string,string,bool>>{Tuple.Create("a.com","l",false)};var hv=new HintsView();
  var p=new ControlPanel(st,he,sp,()=>Theme.On,()=>"отклик 1 мс\tотклик 2 мс\tотклик 3 мс\nпинг 4 мс\tотклик 5 мс",new[]{g,sites,hin},new[]{f,f},tg,ex,()=>{},new Dictionary<ToolStripMenuItem,IPanelView>{{sites,new SitesView(()=>sl,(x,y)=>{},(x,y)=>{},x=>{})},{hin,hv}});
  p.StartPosition=FormStartPosition.Manual;p.Location=new Point(-3000,-3000);p.Show();Application.DoEvents();
  var proc=Process.GetCurrentProcess();var me=proc.Handle;var frame=new Bitmap(p.Width,p.Height);var pages=new ToolStripMenuItem[]{null,g,sites,hin};
  Action<int> paint=n=>{for(int i=0;i<n;i++){p.OpenSection(pages[i%pages.Length]);p.DrawToBitmap(frame,new Rectangle(0,0,p.Width,p.Height));}};
  paint(40);GC.Collect();GC.WaitForPendingFinalizers();int gdi0=GetGuiResources(me,0),usr0=GetGuiResources(me,1);long mem0=GC.GetTotalMemory(true);
  var sw=Stopwatch.StartNew();paint(800);sw.Stop();GC.Collect();GC.WaitForPendingFinalizers();int gdi1=GetGuiResources(me,0),usr1=GetGuiResources(me,1);long mem1=GC.GetTotalMemory(true);
  double ms=sw.ElapsedMilliseconds/800.0;Ok(gdi0>0,mode+" счётчик GDI работает");Ok(ms>1,mode+" кадры правда рисуются");GC.KeepAlive(proc);Ok(ms<50,mode+" скорость: один кадр "+ms.ToString("0.0")+" мс (норма до 50)");Ok(gdi1-gdi0<20,mode+" утечка графики: GDI "+gdi0+" -> "+gdi1);Ok(usr1-usr0<20,mode+" утечка окон: USER "+usr0+" -> "+usr1);Ok(mem1-mem0<5*1024*1024,mode+" утечка памяти: "+(mem0/1024)+" -> "+(mem1/1024)+" КБ");
  p.Close();Application.DoEvents();Ok(p.IsDisposed,mode+" окно закрывается");}}
}}
