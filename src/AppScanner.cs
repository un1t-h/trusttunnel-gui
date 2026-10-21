using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace TrustTunnelGui
{
    /// <summary>Сведения о локальной сети компьютера.</summary>
    public static class NetInfo
    {
        static readonly Regex VpnLike = new Regex(@"WireGuard|Wintun|TAP-|OpenVPN|Amnezia|WARP|Outline|Tunnel|VPN|TrustTunnel|tun\d",
            RegexOptions.IgnoreCase);

        /// <summary>Подсети физических адаптеров с выходом в интернет (например, 192.168.0.0/24).</summary>
        public static List<string> HomeSubnets()
        {
            var res = new List<string>();
            try
            {
                foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (n.OperationalStatus != OperationalStatus.Up) continue;
                    if (n.NetworkInterfaceType == NetworkInterfaceType.Loopback || n.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    if (VpnLike.IsMatch(n.Name + " " + n.Description)) continue;
                    var props = n.GetIPProperties();
                    if (!props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))) continue;
                    foreach (var ua in props.UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                        if (!IsPrivate(ua.Address)) continue;
                        var cidr = Network(ua.Address, ua.IPv4Mask);
                        if (cidr != null && !res.Contains(cidr)) res.Add(cidr);
                    }
                }
            }
            catch { }
            return res;
        }

        /// <summary>
        /// Физический адаптер с выходом в интернет — для прямых соединений (bound_if).
        /// Клиент TrustTunnel на Windows не всегда определяет его сам, и тогда в режиме
        /// «через VPN только выбранное» весь остальной интернет перестаёт работать
        /// (см. TrustTunnel/TrustTunnelClient#56). VPN-адаптеры пропускаются; при нескольких
        /// подходящих предпочитаем проводной.
        /// </summary>
        public static NetworkInterface PhysicalInterface()
        {
            NetworkInterface best = null;
            int bestScore = int.MinValue;
            try
            {
                foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (n.OperationalStatus != OperationalStatus.Up) continue;
                    if (n.NetworkInterfaceType == NetworkInterfaceType.Loopback || n.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    if (VpnLike.IsMatch(n.Name + " " + n.Description)) continue;
                    IPInterfaceProperties props;
                    try { props = n.GetIPProperties(); } catch { continue; }
                    if (!props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))) continue;
                    try { props.GetIPv4Properties(); } catch { continue; }
                    int score = n.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 3 :
                                n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 2 : 1;
                    if (n.Speed > 0) score = score * 1000 + (int)Math.Min(999, n.Speed / 10000000); // при равенстве — быстрее
                    if (score > bestScore) { best = n; bestScore = score; }
                }
            }
            catch { }
            return best;
        }

        /// <summary>Индекс физического адаптера (IPv4) строкой — в формате, который понимает bound_if на Windows.</summary>
        public static string PhysicalInterfaceIndex()
        {
            var n = PhysicalInterface();
            if (n == null) return null;
            try { return n.GetIPProperties().GetIPv4Properties().Index.ToString(); } catch { return null; }
        }

        static string Network(IPAddress ip, IPAddress mask)
        {
            var a = ip.GetAddressBytes();
            var m = mask.GetAddressBytes();
            int bits = 0;
            for (int i = 0; i < 4; i++) { a[i] &= m[i]; for (int b = 7; b >= 0; b--) if ((m[i] & (1 << b)) != 0) bits++; }
            if (bits == 0 || bits == 32) return null;
            return new IPAddress(a) + "/" + bits;
        }

        public static bool IsPrivate(IPAddress ip)
        {
            var b = ip.GetAddressBytes();
            if (b.Length != 4) return false;
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
        }

        /// <summary>Публичный ли адрес (не локальный, не служебный).</summary>
        public static bool IsPublic(IPAddress ip)
        {
            var b = ip.GetAddressBytes();
            if (b.Length != 4) return false;
            if (IsPrivate(ip) || b[0] == 0 || b[0] == 127 || b[0] >= 224) return false;
            if (b[0] == 169 && b[1] == 254) return false;
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return false; // CGNAT
            return true;
        }
    }

    /// <summary>
    /// Собирает адреса, к которым подключается приложение: TCP-соединения его процессов + имена из DNS-кэша Windows.
    /// Официальный клиент TrustTunnel не умеет исключать процессы напрямую, поэтому приложение превращается
    /// в список доменов и IP — так оно идёт мимо VPN (или только через VPN в выборочном режиме).
    /// </summary>
    public class AppScanner
    {
        public readonly HashSet<string> Ips = new HashSet<string>();

        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

        const int AF_INET = 2;
        const int TCP_TABLE_OWNER_PID_ALL = 5;

        /// <summary>Один замер: удалённые публичные IP всех TCP-соединений процессов с этим именем.</summary>
        public int Sample(string processName)
        {
            var pids = new HashSet<int>();
            try { foreach (var p in Process.GetProcessesByName(processName)) pids.Add(p.Id); } catch { }
            if (pids.Count == 0) return 0;

            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
            IntPtr buf = Marshal.AllocHGlobal(size + 4096);
            try
            {
                size += 4096;
                if (GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0) != 0) return pids.Count;
                int count = Marshal.ReadInt32(buf);
                // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, owningPid — по 4 байта
                for (int i = 0; i < count; i++)
                {
                    IntPtr row = buf + 4 + i * 24;
                    uint remote = (uint)Marshal.ReadInt32(row, 12);
                    int pid = Marshal.ReadInt32(row, 20);
                    if (!pids.Contains(pid) || remote == 0) continue;
                    var ip = new IPAddress(remote);
                    if (NetInfo.IsPublic(ip)) Ips.Add(ip.ToString());
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return pids.Count;
        }

        /// <summary>Сопоставляет найденные IP с именами из DNS-кэша Windows.</summary>
        public Dictionary<string, string> HostsFromDnsCache()
        {
            var map = new Dictionary<string, string>(); // ip -> host
            try
            {
                var scope = new ManagementScope(@"\\.\root\StandardCimv2");
                using (var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT Entry, Data, Type FROM MSFT_DNSClientCache")))
                {
                    foreach (ManagementObject o in s.Get())
                    {
                        var data = o["Data"] as string;
                        var entry = o["Entry"] as string;
                        if (string.IsNullOrEmpty(data) || string.IsNullOrEmpty(entry)) continue;
                        if (Ips.Contains(data) && !map.ContainsKey(data)) map[data] = entry.TrimEnd('.').ToLowerInvariant();
                    }
                }
            }
            catch { }
            return map;
        }

        /// <summary>
        /// Итоговые записи для исключений: домены второго уровня (покрывают поддомены),
        /// а IP без имени — как адреса /32.
        /// </summary>
        public List<string> Suggest()
        {
            var hosts = HostsFromDnsCache();
            var domains = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var ips = new SortedSet<string>();
            foreach (var ip in Ips)
            {
                string host;
                if (hosts.TryGetValue(ip, out host)) domains.Add(BaseDomain(host));
                else ips.Add(ip);
            }
            var res = new List<string>(domains);
            res.AddRange(ips);
            return res;
        }

        static readonly HashSet<string> SecondLevelSuffixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "co.uk", "com.au", "com.br", "co.jp", "com.ru", "net.ru", "org.ru", "msk.ru", "spb.ru", "com.ua", "co.kr", "com.cn" };

        // Общие CDN и облака: исключать их целиком нельзя (уйдут чужие сайты) — берём полное имя хоста.
        static readonly HashSet<string> SharedInfra = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "cloudfront.net", "akamai.net", "akamaiedge.net", "akamaized.net", "edgekey.net", "edgesuite.net", "fastly.net",
          "fastlylb.net", "cloudflare.com", "cloudflare.net", "amazonaws.com", "azureedge.net", "azurefd.net", "trafficmanager.net",
          "googleusercontent.com", "googleapis.com", "gstatic.com", "1e100.net", "cdn77.org", "llnwd.net" };

        public static string BaseDomain(string host)
        {
            var parts = host.Split('.');
            if (parts.Length <= 2) return host;
            string last2 = parts[parts.Length - 2] + "." + parts[parts.Length - 1];
            if (SharedInfra.Contains(last2)) return host;
            if (SecondLevelSuffixes.Contains(last2) && parts.Length >= 3) return parts[parts.Length - 3] + "." + last2;
            return last2;
        }
    }
}
