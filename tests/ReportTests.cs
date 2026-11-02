using System;using System.IO;using System.Linq;using System.Reflection;using System.Net;using System.Net.Sockets;using System.Threading;using System.Collections.Generic;
namespace ZapretTray{ static class R{ static int pass,fail;static List<string> log=new List<string>();static void Ok(bool c,string n){if(c)pass++;else fail++;log.Add((c?"OK   ":"FAIL ")+n);}
 static SiteResult Parse(string s,SiteStatus st){var r=new SiteResult{Url="https://x",Status=st};SiteCheck.ReadOutput(s,r);return r;}
 static List<string> C(bool winws,bool net,List<SiteResult> sites,ProblemReport.LocalState l,List<SiteResult> ctl){var m=typeof(ProblemReport).GetMethod("Conclusions",BindingFlags.NonPublic|BindingFlags.Static);return (List<string>)m.Invoke(null,new object[]{winws,net,null,new List<string>(),false,sites,false,l,ctl,false,false});}
 static void Main(){try{
  var ok=Parse("200 0.010 0.150 5000",SiteStatus.Ok);Ok(ok.Details.Contains("HTTP 200")&&ok.Details.Contains("140 мс"),"открылся: "+ok.Details);
  Func<string,double> Um=x=>(double)typeof(ControlPanel).GetMethod("UptimeMinutes",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{x});Ok(Um("2 ч 14 мин")==134&&Um("19 мин")==19&&Um("<1 мин")==0&&Um("—")==0&&Um("0 ч 0 мин")==0,"часы: время разбирается");var a=Parse("000 0.000000 0.000000 0",SiteStatus.NoConnect);Ok(a.Stage=="не подключился к адресу","этап: "+a.Stage);
  var b=Parse("000 0.020 0.000000 0",SiteStatus.NoConnect);Ok(b.Stage.Contains("шифрование"),"этап: "+b.Stage);
  var c=Parse("200 0.020 0.100 18432",SiteStatus.NoConnect);Ok(c.Stage.Contains("завис на 18 КБ"),"этап: "+c.Stage);
  var d=Parse("000 0.020 0.100 0",SiteStatus.NoConnect);Ok(d.Stage.Contains("сервер не ответил"),"этап: "+d.Stage);
  var ipr=Parse("200 0.01 0.1 10 142.251.155.4",SiteStatus.Ok);Ok(ipr.RemoteIp=="142.251.155.4"&&ipr.Measured,"адрес подключения читается");Ok(Parse("200 0.01 0.1 10",SiteStatus.Ok).RemoteIp==null&&Parse("000 0 0 0 ",SiteStatus.NoConnect).RemoteIp==null,"нет адреса - пусто");var e=Parse("",SiteStatus.NoConnect);Ok(!e.Measured&&e.HttpCode=="000"&&e.Stage=="","пустой ответ curl не ломает");
  var f=Parse("мусор",SiteStatus.NoConnect);Ok(!f.Measured,"мусор от curl не ломает");
  // настоящий curl, только адреса самого компьютера: сеть не трогаем
  var free=new TcpListener(IPAddress.Loopback,0);free.Start();int closed=((IPEndPoint)free.LocalEndpoint).Port;free.Stop();
  var r1=SiteCheck.Run("https://127.0.0.1:"+closed,3);Ok(r1.Status==SiteStatus.NoConnect&&r1.Measured&&!r1.Connected,"настоящий curl, порт закрыт: "+r1.Details);
  var srv=new TcpListener(IPAddress.Loopback,0);srv.Start();int port=((IPEndPoint)srv.LocalEndpoint).Port;var th=new Thread(()=>{try{while(true){var cl=srv.AcceptTcpClient();Thread.Sleep(100);cl.Close();}}catch{}}){IsBackground=true};th.Start();
  var r2=SiteCheck.Run("https://127.0.0.1:"+port,3);Ok(r2.Status==SiteStatus.NoConnect&&r2.Connected&&!r2.TlsDone,"настоящий curl, обрыв на шифровании: "+r2.Details);srv.Stop();
  var good=new ProblemReport.LocalState{Bfe="Running",Driver="Running",WinwsCount=1};var okc=new List<SiteResult>{Parse("200 0.01 0.1 10",SiteStatus.Ok)};
  Func<List<string>,string,bool> Has=(l,s)=>l.Any(x=>x.Contains(s));
  Ok(Has(C(true,true,new List<SiteResult>{ok},good,okc),"Явных проблем"),"вывод: всё хорошо");
  Ok(Has(C(true,true,new List<SiteResult>{a},good,okc),"блокировку по IP"),"вывод: адрес недоступен");
  Ok(Has(C(true,true,new List<SiteResult>{b},good,okc),"нужен другой"),"вывод: обрыв - другой конфиг");
  Ok(Has(C(true,true,new List<SiteResult>{c},good,okc),"нужен другой"),"вывод: заморозка - другой конфиг");
  var bad=new ProblemReport.LocalState{Bfe="STOPPED",Driver="нет",WinwsCount=2,MissingFiles=new List<string>{"winws.exe"},Conflicts=new List<string>{"Killer Network Service"},Antivirus=new List<string>{"Kaspersky"}};
  var all=C(false,true,new List<SiteResult>(),bad,new List<SiteResult>{a});Ok(Has(all,"Нет файлов")&&Has(all,"BFE")&&Has(all,"несколько winws")&&Has(all,"Killer")&&Has(all,"Kaspersky")&&Has(all,"незаблокированные"),"вывод: все поломки компьютера названы");
  {var m2=typeof(ProblemReport).GetMethod("Conclusions",BindingFlags.NonPublic|BindingFlags.Static);var dnsR=new List<SiteResult>{new SiteResult{Url="https://www.youtube.com",Status=SiteStatus.OkViaSecureDns}};
   var w10=(List<string>)m2.Invoke(null,new object[]{true,true,null,new List<string>(),false,dnsR,false,good,okc,true,true});Ok(Has(w10,"подменяет")&&Has(w10,"Chrome, Edge и Firefox")&&Has(w10,"Исправить DNS"),"вывод: Windows 10, провайдер подменяет DNS");
   var w10f=(List<string>)m2.Invoke(null,new object[]{true,true,null,new List<string>(),false,dnsR,true,good,okc,false,true});Ok(Has(w10f,"только в Chrome")&&!Has(w10f,"подменяет"),"вывод: Windows 10, исправление уже включено");
   var w11=(List<string>)m2.Invoke(null,new object[]{true,true,null,new List<string>(),false,dnsR,false,good,okc,false,false});Ok(Has(w11,"Поможет «Исправить DNS».")&&!Has(w11,"Chrome"),"вывод: Windows 11");}
  Ok(!Has(C(true,false,new List<SiteResult>(),good,new List<SiteResult>{a}),"незаблокированные"),"вывод: без интернета не винит сайты");
  var st=ProblemReport.ReadLocalState(new AppPaths(@"C:\ZapretTray"));Ok(st.MissingFiles.Count==0,"настоящий компьютер: файлы на месте");Ok(st.Bfe=="Running","настоящий компьютер: BFE "+st.Bfe);Ok(st.Driver=="Running","настоящий компьютер: WinDivert "+st.Driver);Ok(st.WinwsCount==1,"настоящий компьютер: winws "+st.WinwsCount+" с "+st.WinwsSince);Ok(st.Timestamps!="не удалось узнать","метки времени: "+st.Timestamps);Ok(ProblemReport.FindConflicts(new[]{"Killer Network Service","KILLER network service","Обычная служба","网络服务","AdGuardSvc"}).SequenceEqual(new[]{"Killer Network Service","AdGuardSvc"}),"мешающая служба находится, остальные нет");Ok(System.ServiceProcess.ServiceController.GetServices().Count(x=>x.Status==System.ServiceProcess.ServiceControllerStatus.Running)>20,"список служб читается от системы");Ok(st.Antivirus.Count>0,"антивирус определён");log.Add("INFO антивирус: "+string.Join(", ",st.Antivirus));log.Add("INFO мешающие службы: "+string.Join(", ",st.Conflicts));
 }catch(Exception e){Ok(false,"ТЕСТ УПАЛ: "+e);}
 File.WriteAllLines("result.txt",log.Concat(new[]{"ИТОГО ok="+pass+" fail="+fail}));}}}