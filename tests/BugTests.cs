using System;using System.IO;using System.Linq;using System.Net;using System.Net.Sockets;using System.Text;using System.Threading;using System.Collections.Generic;
namespace ZapretTray{static class B{static int pass,fail;static List<string> log=new List<string>();static void Ok(bool c,string n){if(c)pass++;else fail++;log.Add((c?"OK   ":"FAIL ")+n);}
 static string Serve(TcpListener l,int code){var cl=l.AcceptTcpClient();var s=cl.GetStream();var buf=new MemoryStream();var b=new byte[65536];int head=-1,len=0;
  while(true){int n=s.Read(b,0,b.Length);if(n<=0)break;buf.Write(b,0,n);var txt=Encoding.UTF8.GetString(buf.ToArray());if(head<0){head=txt.IndexOf("\r\n\r\n");if(head>=0){var m=System.Text.RegularExpressions.Regex.Match(txt,"Content-Length: (\\d+)",System.Text.RegularExpressions.RegexOptions.IgnoreCase);len=m.Success?int.Parse(m.Groups[1].Value):0;}}
   if(head>=0&&buf.Length>=head+4+len)break;}
  var resp=Encoding.ASCII.GetBytes("HTTP/1.1 "+code+" X\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");s.Write(resp,0,resp.Length);cl.Close();return Encoding.UTF8.GetString(buf.ToArray());}
 static void Main(){try{
  var l=new TcpListener(IPAddress.Loopback,0);l.Start();int port=((IPEndPoint)l.LocalEndpoint).Port;string got=null;
  var th=new Thread(()=>{got=Serve(l,204);});th.Start();
  var report="=== ZAPRET TRAY: ОТЧЁТ ===\r\n\r\n=== ВЫВОД ===\r\n- DNS не находит адрес: \"youtube\"\r\n\r\n=== ПРОГРАММА ===\r\nПрограмма 0.6\r\n";
  string err;bool ok=BugReport.Send("http://127.0.0.1:"+port+"/hook","Не грузит @everyone \"видео\"\nвторая строка",report,out err);th.Join(5000);
  Ok(ok,"отправка дошла: "+err);Ok(got!=null&&got.Contains("name=payload_json"),"есть текст сообщения");Ok(got!=null&&got.Contains("files[0]")&&got.Contains("zapret-report.txt"),"есть файл отчёта");
  Ok(got!=null&&got.Contains("Программа 0.6"),"в файле весь отчёт");Ok(got!=null&&got.Contains("> Не грузит")&&got.Contains("\\n> вторая строка"),"комментарий в сообщении");
  Ok(got!=null&&got.Contains("DNS не находит адрес")&&!got.Contains("=== ПРОГРАММА ===\\n"),"в сообщении только вывод");Ok(got!=null&&got.Contains("\"allowed_mentions\":{\"parse\":[]}"),"упоминания выключены");
  var th2=new Thread(()=>Serve(l,429));th2.Start();ok=BugReport.Send("http://127.0.0.1:"+port+"/hook","",report,out err);th2.Join(5000);Ok(!ok&&err.Contains("429"),"ошибка сервера видна: "+err);l.Stop();
  ok=BugReport.Send("http://127.0.0.1:"+port+"/hook","",report,out err);Ok(!ok&&!string.IsNullOrEmpty(err),"нет связи - не падает: "+err);
  Ok(BugReport.Payload("a\"b\\c\nd\t").Contains("a\\\"b\\\\c\\nd\\u0009"),"кавычки и переносы экранируются");
  l=new TcpListener(IPAddress.Loopback,0);l.Start();port=((IPEndPoint)l.LocalEndpoint).Port;var big=new string('x',5000);var th3=new Thread(()=>{got=Serve(l,204);});th3.Start();BugReport.Send("http://127.0.0.1:"+port+"/hook",big,report,out err);th3.Join(5000);l.Stop();
  Ok(got!=null&&got.Contains("xxx…"),"длинный комментарий обрезается под предел Discord");
  Ok(BugReport.Available,"ссылка вшита в эту сборку");
  l=new TcpListener(IPAddress.Loopback,0);l.Start();port=((IPEndPoint)l.LocalEndpoint).Port;var th4=new Thread(()=>{got=Serve(l,204);});th4.Start();BugReport.Send("http://127.0.0.1:"+port+"/hook","x",report,out err,"ПК 4821-7735-0912");th4.Join(5000);l.Stop();
  Ok(got!=null&&got.Contains("Баг от ПК 4821-7735-0912, версия"),"в сообщении видно, от кого");
  var ini=new Settings(Path.Combine(Path.GetTempPath(),"zt-id-"+Guid.NewGuid().ToString("N")+".ini"));var id1=BugReport.MachineId(ini);var id2=BugReport.MachineId(ini);Ok(System.Text.RegularExpressions.Regex.IsMatch(id1,@"^\d{4}-\d{4}-\d{4}$")&&id1==id2,"номер компьютера постоянный: "+id1);var ini2=new Settings(Path.Combine(Path.GetTempPath(),"zt-id-"+Guid.NewGuid().ToString("N")+".ini"));Ok(BugReport.MachineId(ini2)==id1,"после переустановки номер тот же");
 }catch(Exception e){Ok(false,"ТЕСТ УПАЛ: "+e);}
 File.WriteAllLines("result.txt",log.Concat(new[]{"ИТОГО ok="+pass+" fail="+fail}));}}}