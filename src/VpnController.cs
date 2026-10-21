using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace TrustTunnelGui
{
    public enum VpnState { Disconnected, Connecting, Connected, Disconnecting, Error }

    /// <summary>
    /// Запуск trusttunnel_client.exe, чтение логов, определение состояния и корректная остановка
    /// (Ctrl+C, чтобы клиент сам убрал маршруты, DNS и kill switch; принудительно — только по таймауту).
    /// Все методы, кроме чтения вывода процесса, вызываются из UI-потока.
    /// </summary>
    public class VpnController
    {
        public const string ClientExe = "trusttunnel_client.exe";
        static readonly Regex Ansi = new Regex(@"\x1B\[[0-9;?]*[A-Za-z]", RegexOptions.Compiled);

        readonly ConcurrentQueue<string> logQueue = new ConcurrentQueue<string>();
        Process proc;
        string configPath;
        DateTime stopRequestedAt;
        bool userStopped;

        public VpnState State { get; private set; }
        public Profile ActiveProfile { get; private set; }
        public DateTime ConnectedSince { get; private set; }
        public string LastError { get; private set; }
        public int? LastExitCode { get; private set; }

        /// <summary>Процесс завершился сам (не по кнопке «Отключить»).</summary>
        public event Action UnexpectedExit;

        // Статистика трафика по TUN-адаптеру
        long lastRx = -1, lastTx = -1;
        DateTime lastStatAt;
        public long RxBytes { get; private set; }
        public long TxBytes { get; private set; }
        public double RxRate { get; private set; }
        public double TxRate { get; private set; }
        long baseRx, baseTx;

        public bool IsRunning { get { return proc != null && !proc.HasExited; } }

        public void Log(string line) { logQueue.Enqueue(line); }

        public bool TryDequeueLog(out string line) { return logQueue.TryDequeue(out line); }

        public void Connect(Profile profile, string clientDir)
        {
            if (IsRunning) throw new InvalidOperationException("Клиент уже запущен");
            string exe = Path.Combine(clientDir, ClientExe);
            if (!File.Exists(exe))
                throw new FileNotFoundException("Не найден " + ClientExe + " в папке «" + clientDir + "». Скачайте клиент через меню «Клиент».");

            string runDir = Path.Combine(AppData.DataDir, "run");
            Directory.CreateDirectory(runDir);
            configPath = Path.Combine(runDir, "trusttunnel_client.toml");
            File.WriteAllText(configPath, ConfigBuilder.Build(profile, NetInfo.HomeSubnets()), new UTF8Encoding(false));

            var psi = new ProcessStartInfo(exe, "-c \"" + configPath + "\"")
            {
                WorkingDirectory = clientDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (s, e) => { if (e.Data != null) logQueue.Enqueue(Ansi.Replace(e.Data, "")); };
            p.ErrorDataReceived += (s, e) => { if (e.Data != null) logQueue.Enqueue(Ansi.Replace(e.Data, "")); };

            ActiveProfile = profile;
            LastError = null;
            LastExitCode = null;
            userStopped = false;
            ConnectedSince = default(DateTime);
            ResetStats();
            Log("▶ Запуск: " + exe + " -c " + configPath);
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            proc = p;
            State = VpnState.Connecting;
        }

        public void Disconnect()
        {
            if (!IsRunning) return;
            userStopped = true;
            if (State == VpnState.Disconnecting) { Kill(); return; } // повторное нажатие — принудительно
            State = VpnState.Disconnecting;
            stopRequestedAt = DateTime.UtcNow;
            Log("■ Остановка клиента…");
            if (!SendCtrlC(proc.Id))
            {
                Log("Не удалось отправить Ctrl+C, завершаю процесс принудительно");
                Kill();
            }
        }

        /// <summary>Синхронная остановка при выходе из программы.</summary>
        public void Shutdown(int timeoutMs)
        {
            if (!IsRunning) return;
            userStopped = true;
            if (SendCtrlC(proc.Id) && proc.WaitForExit(timeoutMs)) { Cleanup(); return; }
            Kill();
            try { proc.WaitForExit(2000); } catch { }
            Cleanup();
        }

        void Kill()
        {
            try { if (IsRunning) proc.Kill(); } catch (Exception ex) { Log("Ошибка завершения: " + ex.Message); }
        }

        /// <summary>Вызывается таймером раз в ~0.5 с из UI-потока.</summary>
        public void Poll()
        {
            if (proc == null) return;
            if (proc.HasExited)
            {
                // дочитать хвост вывода
                try { proc.WaitForExit(); } catch { }
                int code = 0;
                try { code = proc.ExitCode; } catch { }
                LastExitCode = code;
                bool unexpected = !userStopped;
                Log("■ Клиент завершён, код выхода " + code);
                if (unexpected)
                {
                    State = VpnState.Error;
                    LastError = "Клиент неожиданно завершился (код " + code + ")";
                }
                else State = VpnState.Disconnected;
                proc.Dispose();
                proc = null;
                Cleanup();
                if (unexpected && UnexpectedExit != null) UnexpectedExit();
                return;
            }

            if (State == VpnState.Disconnecting)
            {
                if ((DateTime.UtcNow - stopRequestedAt).TotalSeconds > 10)
                {
                    Log("Клиент не остановился за 10 с — завершаю принудительно");
                    Kill();
                }
                return;
            }

            bool up = ActiveProfile.IsTun ? TunAdapterUp() : SocksListening();
            if (up && State != VpnState.Connected)
            {
                State = VpnState.Connected;
                ConnectedSince = DateTime.Now;
                Log("✔ Туннель поднят");
            }
            else if (!up && State == VpnState.Connected)
            {
                State = VpnState.Connecting;
                Log("… Туннель пропал, клиент переподключается");
            }
            if (State == VpnState.Connected) CheckRoute();
            else RouteWarning = null;
            UpdateStats();
        }

        void Cleanup()
        {
            // В конфиге пароль в открытом виде — не оставляем его на диске дольше нужного.
            try { if (configPath != null && File.Exists(configPath)) File.Delete(configPath); } catch { }
            RouteWarning = null;
            ResetStats();
        }

        /// <summary>
        /// Ищем только адаптер с именем из профиля: другие VPN (WireGuard/AmneziaVPN и т.п.) тоже используют Wintun,
        /// и поиск «по любому Wintun» давал ложное «Подключено».
        /// </summary>
        NetworkInterface FindTunAdapter()
        {
            string name = ActiveProfile == null || string.IsNullOrEmpty(ActiveProfile.DeviceName) ? "TrustTunnel" : ActiveProfile.DeviceName;
            try
            {
                return NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase));
            }
            catch { return null; }
        }

        // ---- проверка, что интернет-трафик действительно идёт через туннель ----

        [DllImport("iphlpapi.dll")] static extern int GetBestInterface(uint destAddr, out uint bestIfIndex);

        /// <summary>Не null, если туннель поднят, но маршрут в интернет ведёт через другой интерфейс.</summary>
        public string RouteWarning { get; private set; }

        void CheckRoute()
        {
            string warn = null;
            // В выборочном режиме и с собственными маршрутами весь трафик и не должен идти в туннель.
            if (ActiveProfile.IsTun && ActiveProfile.VpnMode != "selective" &&
                (ActiveProfile.IncludedRoutes.Count == 0 || ActiveProfile.IncludedRoutes.Contains("0.0.0.0/0")))
            {
                var tun = FindTunAdapter();
                int tunIdx = -1;
                try { if (tun != null) tunIdx = tun.GetIPProperties().GetIPv4Properties().Index; } catch { }
                uint best;
                uint probe = BitConverter.ToUInt32(System.Net.IPAddress.Parse("8.8.4.4").GetAddressBytes(), 0);
                if (tunIdx >= 0 && GetBestInterface(probe, out best) == 0 && best != (uint)tunIdx)
                    warn = "Трафик в интернет идёт через «" + InterfaceName((int)best) + "», а не через туннель TrustTunnel";
            }
            if (warn != RouteWarning)
            {
                RouteWarning = warn;
                Log(warn != null ? "⚠ " + warn + ". Отключите другие VPN (AmneziaVPN, WireGuard и т.п.) и переподключитесь."
                                 : "✔ Маршрут по умолчанию идёт через туннель");
            }
        }

        static string InterfaceName(int index)
        {
            try
            {
                foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
                {
                    try { if (n.GetIPProperties().GetIPv4Properties().Index == index) return n.Name; } catch { }
                }
            }
            catch { }
            return "интерфейс #" + index;
        }

        static readonly Regex VpnLike = new Regex(@"WireGuard|Wintun|TAP-|OpenVPN|Amnezia|WARP|Outline|Tunnel|VPN|tun\d",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex NotVpn = new Regex(@"Teredo|IP-HTTPS|6to4|WAN Miniport|Kernel Debug|Loopback|isatap",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Активные сторонние VPN-адаптеры (конфликтуют с TrustTunnel за маршрут по умолчанию).</summary>
        public static List<string> OtherActiveVpns(string ourName)
        {
            var r = new List<string>();
            try
            {
                foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (n.OperationalStatus != OperationalStatus.Up) continue;
                    if (string.Equals(n.Name, ourName, StringComparison.OrdinalIgnoreCase)) continue;
                    string s = n.Name + " " + n.Description;
                    if (VpnLike.IsMatch(s) && !NotVpn.IsMatch(s)) r.Add(n.Name + " (" + n.Description + ")");
                }
            }
            catch { }
            return r;
        }

        bool TunAdapterUp()
        {
            var n = FindTunAdapter();
            return n != null && n.OperationalStatus == OperationalStatus.Up;
        }

        bool SocksListening()
        {
            int port = 1080;
            var addr = ActiveProfile.SocksAddress ?? "";
            int colon = addr.LastIndexOf(':');
            if (colon >= 0) int.TryParse(addr.Substring(colon + 1), out port);
            try
            {
                return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(ep => ep.Port == port);
            }
            catch { return false; }
        }

        void ResetStats()
        {
            lastRx = lastTx = -1;
            RxBytes = TxBytes = 0;
            RxRate = TxRate = 0;
        }

        void UpdateStats()
        {
            if (State != VpnState.Connected || !ActiveProfile.IsTun) return;
            var n = FindTunAdapter();
            if (n == null) return;
            IPInterfaceStatistics st;
            try { st = n.GetIPStatistics(); } catch { return; }
            long rx = st.BytesReceived, tx = st.BytesSent;
            var now = DateTime.UtcNow;
            if (lastRx < 0)
            {
                baseRx = rx; baseTx = tx;
            }
            else
            {
                double dt = (now - lastStatAt).TotalSeconds;
                if (dt > 0.2)
                {
                    RxRate = Math.Max(0, (rx - lastRx) / dt);
                    TxRate = Math.Max(0, (tx - lastTx) / dt);
                }
            }
            lastRx = rx; lastTx = tx; lastStatAt = now;
            RxBytes = rx - baseRx;
            TxBytes = tx - baseTx;
        }

        /// <summary>Находит trusttunnel_client, оставшиеся от прошлого запуска (например, после сбоя).</summary>
        public static Process[] FindOrphans()
        {
            try { return Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ClientExe)); }
            catch { return new Process[0]; }
        }

        // ---- Ctrl+C для консольного процесса без окна ----

        [DllImport("kernel32.dll", SetLastError = true)] static extern bool AttachConsole(uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool FreeConsole();
        [DllImport("kernel32.dll")] static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
        [DllImport("kernel32.dll")] static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);

        static readonly object consoleLock = new object();

        static bool SendCtrlC(int pid)
        {
            lock (consoleLock)
            {
                FreeConsole();
                if (!AttachConsole((uint)pid)) return false;
                // Наш процесс тоже подключён к консоли — игнорируем событие у себя.
                SetConsoleCtrlHandler(IntPtr.Zero, true);
                bool ok = GenerateConsoleCtrlEvent(0 /* CTRL_C_EVENT */, 0);
                Thread.Sleep(50);
                FreeConsole();
                return ok;
            }
        }
    }
}
