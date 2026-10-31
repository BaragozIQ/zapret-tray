// "Мои игры": запоминаем игру по имени процесса и собираем адреса её серверов
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace ZapretTray
{
    class Game
    {
        public string Exe;      // имя процесса без .exe
        public bool Enabled;
        public HashSet<string> Subnets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    // tray-games.txt: строки "имя|1"; адреса - lists\games\<имя>.txt
    class GameStore
    {
        readonly AppPaths paths;
        public readonly List<Game> Games = new List<Game>();
        readonly object sync = new object();

        public GameStore(AppPaths paths)
        {
            this.paths = paths;
            Directory.CreateDirectory(paths.GamesDir);
            foreach (var line in Util.ReadEntries(paths.GamesList))
            {
                var parts = line.Split('|');
                var g = new Game { Exe = parts[0], Enabled = parts.Length < 2 || parts[1] == "1" };
                foreach (var s in Util.ReadEntries(GameFile(g.Exe))) g.Subnets.Add(s);
                Games.Add(g);
            }
        }

        string GameFile(string exe) { return Path.Combine(paths.GamesDir, exe + ".txt"); }

        public Game Find(string exe)
        {
            lock (sync) return Games.FirstOrDefault(g => string.Equals(g.Exe, exe, StringComparison.OrdinalIgnoreCase));
        }

        public Game Add(string exe)
        {
            lock (sync)
            {
                var g = Find(exe);
                if (g == null)
                {
                    g = new Game { Exe = exe, Enabled = true };
                    Games.Add(g);
                }
                g.Enabled = true;
                SaveList();
                return g;
            }
        }

        public void Remove(Game g)
        {
            lock (sync)
            {
                Games.Remove(g);
                SaveList();
                try { File.Delete(GameFile(g.Exe)); } catch { }
            }
        }

        public void SetEnabled(Game g, bool enabled)
        {
            lock (sync) { g.Enabled = enabled; SaveList(); }
        }

        // true - адрес новый
        public bool AddSubnet(Game g, string subnet)
        {
            lock (sync) return g.Subnets.Add(subnet);
        }

        public void SaveSubnets(Game g)
        {
            lock (sync) Util.WriteLinesAtomic(GameFile(g.Exe), g.Subnets.OrderBy(s => s));
        }

        void SaveList()
        {
            Util.WriteLinesAtomic(paths.GamesList, Games.Select(g => g.Exe + "|" + (g.Enabled ? "1" : "0")));
        }

        public List<string> EnabledSubnets()
        {
            lock (sync) return Games.Where(g => g.Enabled).SelectMany(g => g.Subnets).ToList();
        }

        public HashSet<string> EnabledExes()
        {
            lock (sync) return new HashSet<string>(Games.Where(g => g.Enabled).Select(g => g.Exe), StringComparer.OrdinalIgnoreCase);
        }
    }

    // Слушает слой FLOW драйвера WinDivert (тот же, что у winws): видно, какой процесс
    // с каким сервером открыл соединение - и TCP, и UDP. Пакеты не трогаем, только смотрим
    class FlowWatcher
    {
        const int LayerFlow = 2;
        const ulong FlagSniff = 1, FlagRecvOnly = 4;
        const int EventFlowEstablished = 1;
        const int AddressSize = 80;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr LoadLibrary(string path);

        [DllImport("WinDivert.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        static extern IntPtr WinDivertOpen(string filter, int layer, short priority, ulong flags);

        [DllImport("WinDivert.dll", SetLastError = true)]
        static extern bool WinDivertRecv(IntPtr handle, IntPtr packet, uint packetLen, IntPtr recvLen, IntPtr addr);

        [DllImport("WinDivert.dll", SetLastError = true)]
        static extern bool WinDivertShutdown(IntPtr handle, int how);

        [DllImport("WinDivert.dll", SetLastError = true)]
        static extern bool WinDivertClose(IntPtr handle);

        static readonly IntPtr InvalidHandle = new IntPtr(-1);

        // pid и удалённый IPv4 (байты по порядку a.b.c.d)
        public event Action<int, byte[]> FlowSeen;

        IntPtr handle = InvalidHandle;
        Thread thread;
        public string Error;

        public bool Start(string binDir)
        {
            if (thread != null) return true;
            if (LoadLibrary(Path.Combine(binDir, "WinDivert.dll")) == IntPtr.Zero)
            {
                Error = "не удалось загрузить WinDivert.dll";
                return false;
            }
            handle = WinDivertOpen("true", LayerFlow, 0, FlagSniff | FlagRecvOnly);
            if (handle == InvalidHandle || handle == IntPtr.Zero)
            {
                Error = "WinDivert не открылся (код " + Marshal.GetLastWin32Error() + ")";
                handle = InvalidHandle;
                return false;
            }
            thread = new Thread(Loop) { IsBackground = true, Name = "FlowWatcher" };
            thread.Start();
            return true;
        }

        public void Stop()
        {
            if (handle == InvalidHandle) return;
            WinDivertShutdown(handle, 3);
            WinDivertClose(handle);
            handle = InvalidHandle;
            thread = null;
        }

        void Loop()
        {
            var addr = Marshal.AllocHGlobal(AddressSize);
            try
            {
                var h = handle;
                while (h != InvalidHandle)
                {
                    if (!WinDivertRecv(h, IntPtr.Zero, 0, IntPtr.Zero, addr)) break;
                    int ev = Marshal.ReadByte(addr, 9);
                    bool ipv6 = (Marshal.ReadByte(addr, 10) & 0x10) != 0;
                    if (ev != EventFlowEstablished || ipv6) continue;
                    int pid = Marshal.ReadInt32(addr, 32);
                    // IPv4 хранится как IPv4-mapped IPv6, первое слово - адрес в порядке хоста
                    uint a = (uint)Marshal.ReadInt32(addr, 52);
                    var bytes = new[] { (byte)(a >> 24), (byte)(a >> 16), (byte)(a >> 8), (byte)a };
                    var handler = FlowSeen;
                    if (handler != null) handler(pid, bytes);
                }
            }
            catch { }
            finally { Marshal.FreeHGlobal(addr); }
        }
    }

    // Уже открытые TCP-соединения процесса (то, что было до начала слежения)
    static class TcpTable
    {
        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

        public static List<byte[]> RemoteAddresses(HashSet<int> pids)
        {
            var result = new List<byte[]>();
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 5, 0);
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buf, ref size, false, 2, 5, 0) != 0) return result;
                int count = Marshal.ReadInt32(buf);
                for (int i = 0; i < count; i++)
                {
                    // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, pid
                    var row = buf + 4 + i * 24;
                    int pid = Marshal.ReadInt32(row, 20);
                    if (!pids.Contains(pid)) continue;
                    result.Add(BitConverter.GetBytes(Marshal.ReadInt32(row, 12)));
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return result;
        }
    }
}
