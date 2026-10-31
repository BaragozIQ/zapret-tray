// Setup.exe - установка и удаление Zapret Tray
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace ZapretTray
{
    static class SetupProgram
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Тихое обновление из трея: Setup.exe /update "<папка установки>"
            if (args.Length >= 2 && args[0].Equals("/update", StringComparison.OrdinalIgnoreCase))
            {
                Installer.SilentUpdate(args[1]);
                return;
            }

            var installed = Installer.FindInstalled();
            if (args.Any(a => a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase)))
            {
                if (installed == null)
                {
                    MessageBox.Show(AppInfo.Name + " не установлен.", AppInfo.Name);
                    return;
                }
                if (MessageBox.Show("Удалить " + AppInfo.Name + " из папки\n" + installed + " ?\n\nzapret будет выключен, свои списки и игры тоже удалятся.",
                        AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                Installer.Uninstall(installed);
                MessageBox.Show("Готово, " + AppInfo.Name + " удалён.", AppInfo.Name);
                return;
            }
            Application.Run(new SetupForm(installed));
        }
    }

    static class Installer
    {
        public static string SourceDir { get { return AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'); } }

        // В архиве для друзей всё, кроме Setup.exe, лежит в подпапке files
        public static string PayloadDir
        {
            get
            {
                var files = Path.Combine(SourceDir, "files");
                return File.Exists(Path.Combine(files, AppInfo.TrayExe)) ? files : SourceDir;
            }
        }

        // Путь установленной программы - из задачи планировщика
        public static string FindInstalled()
        {
            string xml;
            if (Util.Run("schtasks.exe", "/query /tn \"" + AppInfo.RunTask + "\" /xml", out xml) != 0) return null;
            var m = Regex.Match(xml, "<Command>(.*?)</Command>");
            if (!m.Success) return null;
            var exe = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim('"');
            var dir = Path.GetDirectoryName(exe);
            return Directory.Exists(dir) ? dir : null;
        }

        // Старые копии zapret: папки на корнях дисков, где есть bin\winws.exe (на 1-2 уровня вглубь)
        public static List<string> FindOldCopies(string exclude1, string exclude2)
        {
            var result = new List<string>();
            var skip = new[] { "Windows", "Program Files", "Program Files (x86)", "ProgramData", "$Recycle.Bin", "System Volume Information", "Users" };
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            {
                string[] tops;
                try { tops = Directory.GetDirectories(drive.RootDirectory.FullName); } catch { continue; }
                foreach (var top in tops)
                {
                    if (skip.Contains(Path.GetFileName(top), StringComparer.OrdinalIgnoreCase)) continue;
                    if (SameOrInside(exclude1, top) || SameOrInside(exclude2, top)) continue;
                    if (HasWinws(top)) { result.Add(top); continue; }
                    string[] subs;
                    try { subs = Directory.GetDirectories(top); } catch { continue; }
                    if (subs.Any(HasWinws)) result.Add(top);
                }
            }
            return result;
        }

        static bool HasWinws(string dir)
        {
            try { return File.Exists(Path.Combine(dir, @"bin\winws.exe")); } catch { return false; }
        }

        // path совпадает с dir или лежит внутри него
        static bool SameOrInside(string path, string dir)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var p = Path.GetFullPath(path).TrimEnd('\\') + "\\";
            var d = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
            return p.StartsWith(d, StringComparison.OrdinalIgnoreCase);
        }

        public static void StopEverything()
        {
            Tasks.End(AppInfo.RunTask);
            Tasks.End(AppInfo.AutoTask);
            int self = Process.GetCurrentProcess().Id;
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AppInfo.TrayExe)))
            {
                if (p.Id == self) continue;
                try { p.Kill(); p.WaitForExit(3000); } catch { }
            }
            Util.KillWinws();
        }

        // Без окон: остановить трей и winws, заменить файлы (свои списки и настройки сохраняются), запустить трей снова.
        // Задачу планировщика не трогаем через /end - трей запущен из неё, и вместе с ним мог бы погибнуть и этот процесс.
        public static void SilentUpdate(string target)
        {
            var log = Path.Combine(Path.GetTempPath(), "ZapretTrayUpdate.log");
            try
            {
                if (!File.Exists(Path.Combine(target, AppInfo.TrayExe))) throw new Exception("программа не найдена в " + target);
                Thread.Sleep(1500);   // даём трею спокойно запустить нас и выйти
                int self = Process.GetCurrentProcess().Id;
                foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AppInfo.TrayExe)))
                {
                    if (p.Id == self) continue;
                    try { p.Kill(); p.WaitForExit(5000); } catch { }
                }
                Util.KillWinws();
                Thread.Sleep(500);
                CopyFiles(target);
                File.WriteAllText(log, DateTime.Now + " OK -> " + AppInfo.Version);
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(log, DateTime.Now + " FAILED: " + ex); } catch { }
            }
            // В любом случае поднимаем трей обратно - новый или старый
            var trayName = Path.GetFileNameWithoutExtension(AppInfo.TrayExe);
            if (Tasks.RunNow(AppInfo.RunTask))
            {
                for (int i = 0; i < 10 && Process.GetProcessesByName(trayName).Length == 0; i++) Thread.Sleep(500);
            }
            if (Process.GetProcessesByName(trayName).Length == 0)
            {
                try { Process.Start(new ProcessStartInfo(Path.Combine(target, AppInfo.TrayExe)) { UseShellExecute = true }); } catch { }
            }
        }

        // Дописать в dst строки из src, которых там нет (вместе с комментарием-заголовком над ними)
        static void MergeLines(string src, string dst)
        {
            var have = new HashSet<string>(Util.ReadEntries(dst), StringComparer.OrdinalIgnoreCase);
            var add = new List<string>();
            string header = null;
            foreach (var raw in File.ReadAllLines(src))
            {
                var line = raw.Trim();
                if (line.StartsWith("#")) { header = line; continue; }
                if (line.Length == 0 || have.Contains(line)) continue;
                if (header != null) { add.Add(header); header = null; }
                add.Add(line);
                have.Add(line);
            }
            if (add.Count == 0) return;
            var lines = File.ReadAllLines(dst).ToList();
            while (lines.Count > 0 && lines[lines.Count - 1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
            lines.AddRange(add);
            Util.WriteLinesAtomic(dst, lines);
        }

        public static void RemoveZapretService()
        {
            foreach (var name in new[] { "zapret", "WinDivert", "WinDivert14" })
            {
                Util.Run("sc.exe", "stop \"" + name + "\"");
                Util.Run("sc.exe", "delete \"" + name + "\"");
            }
        }

        // Копирование с сохранением файлов пользователя при переустановке
        public static void CopyFiles(string target)
        {
            // Setup.exe запущен из самой установленной папки - копировать нечего
            if (string.Equals(SourceDir, target, StringComparison.OrdinalIgnoreCase)) return;
            Directory.CreateDirectory(target);
            foreach (var exe in new[] { AppInfo.TrayExe, AppInfo.SetupExe })
            {
                var src = exe == AppInfo.SetupExe ? Application.ExecutablePath : Path.Combine(PayloadDir, exe);
                var dst = Path.Combine(target, exe);
                if (!string.Equals(Path.GetFullPath(src), Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase))
                    File.Copy(src, dst, true);
            }

            var srcCore = Path.Combine(PayloadDir, "core");
            var dstCore = Path.Combine(target, "core");
            var keep = new HashSet<string>(AppPaths.UserFiles, StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.GetFiles(srcCore, "*", SearchOption.AllDirectories))
            {
                var rel = file.Substring(srcCore.Length + 1);
                var dst = Path.Combine(dstCore, rel);
                if (keep.Contains(rel) && File.Exists(dst))
                {
                    // Свой список сайтов не затираем, но новые сайты из сборки (например, для игр) дописываем
                    if (string.Equals(rel, @"lists\list-general-user.txt", StringComparison.OrdinalIgnoreCase)) MergeLines(file, dst);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                File.Copy(file, dst, true);
            }
            // Флаг проверки обновлений не нужен
            var upd = Path.Combine(dstCore, @"utils\check_updates.enabled");
            if (File.Exists(upd)) File.Delete(upd);
        }

        public static void Uninstall(string dir)
        {
            StopEverything();
            Tasks.Delete(AppInfo.RunTask);
            Tasks.Delete(AppInfo.AutoTask);
            foreach (var lnk in new[] { Shortcut.DesktopPath, Shortcut.StartMenuPath })
            {
                try { if (File.Exists(lnk)) File.Delete(lnk); } catch { }
            }
            // Setup.exe может лежать в этой же папке - удаляем после выхода
            var psi = new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"" + dir + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetTempPath()
            };
            Process.Start(psi);
        }
    }

    class SetupForm : Form
    {
        readonly string installed;
        readonly TextBox pathBox;
        readonly CheckBox autostartBox, desktopBox, watchdogBox, autoupdateBox, serviceBox;
        readonly CheckedListBox oldList;
        readonly Button installButton, uninstallButton, cancelButton;
        readonly Label statusLabel;

        public SetupForm(string installed)
        {
            this.installed = installed;
            Text = AppInfo.Name + " " + AppInfo.Version + " - установка";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9);
            ClientSize = new Size(520, 494);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            int y = 12;
            Controls.Add(new Label
            {
                Text = installed != null
                    ? "Программа уже установлена в " + installed + ".\nМожно переустановить (свои списки и игры сохранятся) или удалить."
                    : "zapret со значком в трее: сам подбирает рабочий конфиг, сам чинится, умеет добавлять сайты и игры.",
                Left = 12, Top = y, Width = 496, Height = 36
            });
            y += 44;

            Controls.Add(new Label { Text = "Папка установки:", Left = 12, Top = y, AutoSize = true });
            y += 20;
            pathBox = new TextBox { Left = 12, Top = y, Width = 400, Text = installed ?? @"C:\ZapretTray" };
            var browse = new Button { Text = "Обзор...", Left = 420, Top = y - 1, Width = 88 };
            browse.Click += (s, e) => Browse();
            Controls.Add(pathBox);
            Controls.Add(browse);
            y += 30;
            Controls.Add(new Label
            {
                Text = "Без русских букв и пробелов в пути, и не в OneDrive - иначе zapret может не работать.",
                Left = 12, Top = y, Width = 496, Height = 18, ForeColor = Color.DimGray
            });
            y += 28;

            autostartBox = new CheckBox { Text = "Запускать вместе с Windows", Left = 12, Top = y, Width = 496, Checked = true };
            y += 24;
            desktopBox = new CheckBox { Text = "Ярлык на рабочем столе", Left = 12, Top = y, Width = 496, Checked = true };
            y += 24;
            watchdogBox = new CheckBox { Text = "Сторож: сам подбирает другой конфиг, если всё перестало открываться", Left = 12, Top = y, Width = 496, Checked = true };
            y += 24;
            autoupdateBox = new CheckBox { Text = "Обновляться автоматически (программа и свежие конфиги zapret, настройки сохраняются)", Left = 12, Top = y, Width = 496, Checked = true };
            y += 32;
            Controls.AddRange(new Control[] { autostartBox, desktopBox, watchdogBox, autoupdateBox });

            Controls.Add(new Label { Text = "Удалить старое:", Left = 12, Top = y, AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
            y += 22;
            bool hasService = Util.ServiceExists("zapret");
            serviceBox = new CheckBox
            {
                Text = hasService ? "Удалить службу zapret (она мешает программе)" : "Службы zapret нет - удалять нечего",
                Left = 12, Top = y, Width = 496, Checked = hasService, Enabled = hasService
            };
            Controls.Add(serviceBox);
            y += 26;
            Controls.Add(new Label { Text = "Найденные старые папки zapret (отметь, какие удалить целиком):", Left = 12, Top = y, Width = 496 });
            y += 20;
            oldList = new CheckedListBox { Left = 12, Top = y, Width = 496, Height = 90, CheckOnClick = true, IntegralHeight = false };
            foreach (var dir in Installer.FindOldCopies(installed, Installer.SourceDir)) oldList.Items.Add(dir, false);
            if (oldList.Items.Count == 0) { oldList.Items.Add("(ничего не найдено)"); oldList.Enabled = false; }
            Controls.Add(oldList);
            y += 100;

            statusLabel = new Label { Left = 12, Top = y, Width = 496, Height = 20, ForeColor = Color.DarkGreen };
            Controls.Add(statusLabel);
            y += 28;

            installButton = new Button { Text = installed != null ? "Переустановить" : "Установить", Left = 12, Top = y, Width = 130, Height = 30 };
            installButton.Font = new Font(Font, FontStyle.Bold);
            installButton.Click += (s, e) => Install();
            uninstallButton = new Button { Text = "Удалить программу", Left = 150, Top = y, Width = 140, Height = 30, Visible = installed != null };
            uninstallButton.Click += (s, e) => DoUninstall();
            cancelButton = new Button { Text = "Закрыть", Left = 418, Top = y, Width = 90, Height = 30 };
            cancelButton.Click += (s, e) => Close();
            Controls.AddRange(new Control[] { installButton, uninstallButton, cancelButton });
            AcceptButton = installButton;
        }

        void Browse()
        {
            using (var d = new FolderBrowserDialog { Description = "Куда установить", SelectedPath = pathBox.Text })
            {
                if (d.ShowDialog(this) == DialogResult.OK) pathBox.Text = d.SelectedPath;
            }
        }

        void Status(string text)
        {
            statusLabel.Text = text;
            statusLabel.Refresh();
        }

        void Install()
        {
            string target;
            try { target = Path.GetFullPath(pathBox.Text.Trim()).TrimEnd('\\'); }
            catch { MessageBox.Show("Неправильная папка."); return; }

            if (Regex.IsMatch(target, @"[^\x00-\x7F]") || target.Contains(" "))
            {
                if (MessageBox.Show("В пути есть русские буквы или пробелы - zapret может не работать. Всё равно ставить?",
                        Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            }

            var toDelete = oldList.Enabled ? oldList.CheckedItems.Cast<string>().ToList() : new List<string>();
            if (toDelete.Any(d => target.StartsWith(d.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(d, target, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show("Папка установки находится внутри папки, отмеченной на удаление. Выбери другую.", Text);
                return;
            }
            if (toDelete.Count > 0 &&
                MessageBox.Show("Будут удалены целиком:\n\n" + string.Join("\n", toDelete) + "\n\nТочно?", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

            installButton.Enabled = uninstallButton.Enabled = cancelButton.Enabled = false;
            UseWaitCursor = true;
            try
            {
                Status("Останавливаю zapret...");
                Installer.StopEverything();
                Thread.Sleep(500);

                if (serviceBox.Checked)
                {
                    Status("Удаляю службу zapret...");
                    Installer.RemoveZapretService();
                }

                foreach (var dir in toDelete)
                {
                    Status("Удаляю " + dir + "...");
                    try { Directory.Delete(dir, true); }
                    catch (Exception ex) { MessageBox.Show("Не удалось удалить " + dir + ":\n" + ex.Message, Text); }
                }

                // Переустановка в другую папку - старую копию программы убираем
                if (installed != null && !string.Equals(installed, target, StringComparison.OrdinalIgnoreCase)
                    && !Installer.SourceDir.StartsWith(installed.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(Installer.SourceDir, installed, StringComparison.OrdinalIgnoreCase))
                {
                    try { Directory.Delete(installed, true); } catch { }
                }

                Status("Копирую файлы...");
                Installer.CopyFiles(target);
                var paths = new AppPaths(target);

                Status("Настраиваю запуск...");
                string err;
                if (!Tasks.Create(AppInfo.RunTask, paths.TrayExePath, false, out err))
                    throw new Exception("не удалось создать задачу планировщика:\n" + err);
                if (!Tasks.Create(AppInfo.AutoTask, paths.TrayExePath, true, out err))
                    throw new Exception("не удалось создать задачу автозапуска:\n" + err);
                if (!autostartBox.Checked) Tasks.SetEnabled(AppInfo.AutoTask, false);

                var st = new Settings(paths.Settings);
                st.SetBool("watchdog", watchdogBox.Checked);
                st.SetBool("autoupdate", autoupdateBox.Checked);

                Shortcut.Create(Shortcut.StartMenuPath, paths.TrayExePath, AppInfo.Name);
                if (desktopBox.Checked) Shortcut.Create(Shortcut.DesktopPath, paths.TrayExePath, AppInfo.Name);
                else if (File.Exists(Shortcut.DesktopPath)) File.Delete(Shortcut.DesktopPath);

                Status("Запускаю...");
                Tasks.RunNow(AppInfo.RunTask);

                UseWaitCursor = false;
                MessageBox.Show("Готово!\n\nЗначок zapret появится возле часов (может быть спрятан под стрелочкой ^).\n" +
                                "Первый запуск может занять пару минут - zapret подбирает рабочий конфиг.\n\n" +
                                "Правый клик по значку - меню, двойной клик - включить/выключить.", Text,
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                Close();
            }
            catch (Exception ex)
            {
                UseWaitCursor = false;
                Status("");
                MessageBox.Show("Ошибка установки: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                installButton.Enabled = uninstallButton.Enabled = cancelButton.Enabled = true;
            }
        }

        void DoUninstall()
        {
            if (MessageBox.Show("Удалить " + AppInfo.Name + " из папки\n" + installed + " ?\n\nzapret будет выключен, свои списки и игры тоже удалятся.",
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            Installer.Uninstall(installed);
            MessageBox.Show("Готово, программа удалена.", Text);
            Close();
        }
    }
}
