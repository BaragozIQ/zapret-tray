// Список установленных приложений - как "добавить стороннюю игру" в Steam:
// проходим по ярлыкам в меню Пуск, достаём настоящий exe и его иконку.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace ZapretTray
{
    class InstalledApp
    {
        public string Name;      // как подписан ярлык
        public string Exe;       // имя процесса без .exe - именно его запоминаем
        public string ExePath;   // полный путь к exe, нужен для иконки
    }

    static class AppScan
    {
        // Папки меню Пуск: общая для всех и личная
        static IEnumerable<string> StartMenus()
        {
            yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
            yield return Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
        }

        // Ярлыки не про запуск самой программы: удаление, помощь, сайты и прочее
        static readonly string[] Skip =
            { "удал", "uninstall", "remove", "readme", "прочти", "website", "сайт", "support", "поддержк",
              "help", "справк", "manual", "docs", "документац", "crash", "report", "repair", "активац",
              "activate", "обновлен", "updater", "установ", "install" };

        // Свой обход: Directory.GetFiles(..., AllDirectories) падает целиком на первой папке без доступа,
        // а в меню Пуск такие попадаются. Здесь недоступную папку просто пропускаем
        static IEnumerable<string> LnkFiles(string root)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                string[] files = null, subs = null;
                try { files = Directory.GetFiles(dir, "*.lnk"); } catch { }
                try { subs = Directory.GetDirectories(dir); } catch { }
                if (files != null) foreach (var f in files) yield return f;
                if (subs != null) foreach (var s in subs) stack.Push(s);
            }
        }

        public static List<InstalledApp> Installed()
        {
            var byExe = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);
            foreach (var menu in StartMenus())
            {
                if (string.IsNullOrEmpty(menu) || !Directory.Exists(menu)) continue;
                foreach (var lnk in LnkFiles(menu))
                {
                    try
                    {
                        var name = Path.GetFileNameWithoutExtension(lnk);
                        if (Skip.Any(w => name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                        var target = ResolveTarget(lnk);
                        if (target == null || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(target)) continue;
                        if (InWindows(target)) continue;
                        var exe = Path.GetFileNameWithoutExtension(target);
                        if (Skip.Any(w => exe.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                        if (!byExe.ContainsKey(exe)) byExe[exe] = new InstalledApp { Name = name, Exe = exe, ExePath = target };
                    }
                    catch { }
                }
            }
            return byExe.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        static bool InWindows(string path)
        {
            var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            return !string.IsNullOrEmpty(win) && path.StartsWith(win, StringComparison.OrdinalIgnoreCase);
        }

        // TargetPath ярлыка через WScript.Shell, без лишних сборок (dynamic тянет Microsoft.CSharp)
        static string ResolveTarget(string lnk)
        {
            object shell = null;
            try
            {
                var t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return null;
                shell = Activator.CreateInstance(t);
                var sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                var target = sc.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, sc, null) as string;
                return string.IsNullOrEmpty(target) ? null : target;
            }
            catch { return null; }
            finally { if (shell != null) try { Marshal.ReleaseComObject(shell); } catch { } }
        }

        // Крупную иконку из exe берём через PrivateExtractIcons, иначе обычную ассоциированную
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] phicon, int[] piconid, int nIcons, int flags);
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

        // Иконка exe -> квадратный PNG рядом с играми. Возвращает путь или null
        public static string SaveIcon(string exePath, string pngPath)
        {
            try
            {
                if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return null;
                Directory.CreateDirectory(Path.GetDirectoryName(pngPath));
                Bitmap bmp = null;
                var handles = new IntPtr[1];
                if (PrivateExtractIcons(exePath, 0, 128, 128, handles, new int[1], 1, 0) > 0 && handles[0] != IntPtr.Zero)
                {
                    try { using (var ico = Icon.FromHandle(handles[0])) bmp = ico.ToBitmap(); }
                    finally { DestroyIcon(handles[0]); }
                }
                if (bmp == null)
                {
                    using (var ico = Icon.ExtractAssociatedIcon(exePath)) bmp = ico != null ? ico.ToBitmap() : null;
                }
                if (bmp == null) return null;
                using (bmp)
                using (var square = new Bitmap(128, 128))
                {
                    using (var g = Graphics.FromImage(square))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(bmp, new Rectangle(0, 0, 128, 128));
                    }
                    square.Save(pngPath, ImageFormat.Png);
                }
                return pngPath;
            }
            catch { return null; }
        }

        // Готовую картинку (например, значок окна) сохранить квадратным PNG
        public static bool SaveImage(Image img, string pngPath)
        {
            try
            {
                if (img == null) return false;
                Directory.CreateDirectory(Path.GetDirectoryName(pngPath));
                using (var square = new Bitmap(128, 128))
                {
                    using (var g = Graphics.FromImage(square))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(img, new Rectangle(0, 0, 128, 128));
                    }
                    square.Save(pngPath, ImageFormat.Png);
                }
                return true;
            }
            catch { return false; }
        }
    }
}
