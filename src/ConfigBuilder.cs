using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TrustTunnelGui
{
    /// <summary>Преобразование профиля в trusttunnel_client.toml и обратно.</summary>
    public static class ConfigBuilder
    {
        public static readonly string[] TlsProfiles = { "chrome", "chrome_canary", "safari", "firefox", "okhttp", "openssl", "default" };

        // Значения, которые записывает официальный setup_wizard
        public static readonly string[] DefaultIncludedRoutes = { "0.0.0.0/0", "2000::/3" };
        public static readonly string[] DefaultExcludedRoutes =
            { "0.0.0.0/8", "10.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12", "192.168.0.0/16", "224.0.0.0/3" };

        /// <summary>
        /// Собирает конфиг для trusttunnel_client. Необязательные поля пишутся только если
        /// отличаются от значений по умолчанию — так конфиг совместим и со старыми версиями клиента.
        /// </summary>
        public static string Build(Profile p)
        {
            var sb = new StringBuilder();
            sb.Append("# Сгенерировано TrustTunnel GUI. Профиль: ").Append(p.DisplayName.Replace('\n', ' ')).Append('\n');
            Line(sb, "loglevel", TomlWriter.Str(string.IsNullOrEmpty(p.LogLevel) ? "info" : p.LogLevel));
            Line(sb, "vpn_mode", TomlWriter.Str(p.VpnMode == "selective" ? "selective" : "general"));
            Line(sb, "killswitch_enabled", TomlWriter.Bool(p.KillSwitch));
            var ports = ParsePorts(p.KillSwitchAllowPorts);
            if (ports.Count > 0) Line(sb, "killswitch_allow_ports", "[" + string.Join(", ", ports) + "]");
            if (!p.PostQuantum) Line(sb, "post_quantum_group_enabled", "false");
            Line(sb, "exclusions", TomlWriter.Arr(p.Exclusions));
            if (!string.IsNullOrWhiteSpace(p.ExtraToml))
                sb.Append(p.ExtraToml.Replace("\r\n", "\n").TrimEnd()).Append('\n');

            sb.Append("\n[endpoint]\n");
            Line(sb, "hostname", TomlWriter.Str(p.Hostname));
            if (!string.IsNullOrEmpty(p.CustomSni)) Line(sb, "custom_sni", TomlWriter.Str(p.CustomSni));
            Line(sb, "addresses", TomlWriter.Arr(p.Addresses));
            Line(sb, "has_ipv6", TomlWriter.Bool(p.HasIpv6));
            Line(sb, "username", TomlWriter.Str(p.Username));
            Line(sb, "password", TomlWriter.Str(p.Password));
            Line(sb, "skip_verification", TomlWriter.Bool(p.SkipVerification));
            if (!string.IsNullOrWhiteSpace(p.Certificate))
                Line(sb, "certificate", TomlWriter.Str(p.Certificate.Replace("\r\n", "\n").Trim() + "\n"));
            Line(sb, "upstream_protocol", TomlWriter.Str(p.UpstreamProtocol == "http3" ? "http3" : "http2"));
            Line(sb, "anti_dpi", TomlWriter.Bool(p.AntiDpi));
            if (!string.IsNullOrEmpty(p.TlsProfile)) Line(sb, "tls_profile", TomlWriter.Str(p.TlsProfile));
            if (!string.IsNullOrEmpty(p.ClientRandom)) Line(sb, "client_random", TomlWriter.Str(p.ClientRandom));
            if (!string.IsNullOrEmpty(p.ClientRandomAuthKey)) Line(sb, "client_random_auth_key", TomlWriter.Str(p.ClientRandomAuthKey));
            if (!string.IsNullOrEmpty(p.Name)) Line(sb, "name", TomlWriter.Str(p.Name));
            if (p.DnsUpstreams.Count > 0) Line(sb, "dns_upstreams", TomlWriter.Arr(p.DnsUpstreams));

            if (p.IsTun)
            {
                // Маршруты и MTU пишем всегда явно, как официальный setup_wizard: если ключа included_routes
                // нет, клиент (по крайней мере v1.1.x) не ставит маршруты, и трафик идёт мимо туннеля.
                sb.Append("\n[listener.tun]\n");
                if (!string.IsNullOrEmpty(p.DeviceName)) Line(sb, "device_name", TomlWriter.Str(p.DeviceName));
                Line(sb, "included_routes", TomlWriter.Arr(p.IncludedRoutes.Count > 0 ? (IEnumerable<string>)p.IncludedRoutes : DefaultIncludedRoutes));
                Line(sb, "excluded_routes", TomlWriter.Arr(p.ExcludedRoutes.Count > 0 ? (IEnumerable<string>)p.ExcludedRoutes : DefaultExcludedRoutes));
                Line(sb, "mtu_size", (p.MtuSize > 0 ? p.MtuSize : 1350).ToString(CultureInfo.InvariantCulture));
                Line(sb, "change_system_dns", TomlWriter.Bool(p.ChangeSystemDns));
            }
            else
            {
                sb.Append("\n[listener.socks]\n");
                Line(sb, "address", TomlWriter.Str(string.IsNullOrEmpty(p.SocksAddress) ? "127.0.0.1:1080" : p.SocksAddress));
                if (!string.IsNullOrEmpty(p.SocksUsername)) Line(sb, "username", TomlWriter.Str(p.SocksUsername));
                if (!string.IsNullOrEmpty(p.SocksPassword)) Line(sb, "password", TomlWriter.Str(p.SocksPassword));
            }
            return sb.ToString();
        }

        static void Line(StringBuilder sb, string key, string value)
        {
            sb.Append(key).Append(" = ").Append(value).Append('\n');
        }

        public static List<int> ParsePorts(string s)
        {
            var r = new List<int>();
            foreach (var part in (s ?? "").Split(new[] { ',', ' ', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int port;
                if (int.TryParse(part.Trim(), out port) && port > 0 && port < 65536) r.Add(port);
            }
            return r;
        }

        /// <summary>
        /// Импорт из TOML: понимает как конфиг клиента (trusttunnel_client.toml с [endpoint]),
        /// так и файл, экспортированный сервером (trusttunnel_endpoint ... --format toml).
        /// </summary>
        public static Profile FromToml(string text)
        {
            var t = TomlParser.Parse(text);
            var p = new Profile();
            Func<string, object> ep = k =>
            {
                object v;
                if (t.TryGetValue("endpoint." + k, out v)) return v;
                if (t.TryGetValue(k, out v)) return v;
                return null;
            };

            p.Hostname = S(ep("hostname"), "");
            if (string.IsNullOrEmpty(p.Hostname))
                throw new FormatException("В файле не найден hostname — это не похоже на конфиг TrustTunnel");
            p.CustomSni = S(ep("custom_sni"), "");
            // старый синтаксис "hostname|sni"
            int bar = p.Hostname.IndexOf('|');
            if (bar > 0 && string.IsNullOrEmpty(p.CustomSni))
            {
                p.CustomSni = p.Hostname.Substring(bar + 1);
                p.Hostname = p.Hostname.Substring(0, bar);
            }
            p.Addresses = L(ep("addresses"));
            p.Username = S(ep("username"), "");
            p.Password = S(ep("password"), "");
            p.HasIpv6 = B(ep("has_ipv6"), true);
            p.SkipVerification = B(ep("skip_verification"), false);
            p.Certificate = S(ep("certificate"), "").Replace("\r\n", "\n").Replace("\n", "\r\n");
            p.UpstreamProtocol = S(ep("upstream_protocol"), "http2") == "http3" ? "http3" : "http2";
            p.AntiDpi = B(ep("anti_dpi"), false);
            p.TlsProfile = S(ep("tls_profile"), "");
            p.ClientRandom = S(ep("client_random"), S(ep("client_random_prefix"), ""));
            p.ClientRandomAuthKey = S(ep("client_random_auth_key"), "");
            p.Name = S(ep("name"), "");
            p.SubscriptionUrl = S(ep("subscription_url"), "");
            p.DnsUpstreams = L(ep("dns_upstreams"));

            object v2;
            if (t.TryGetValue("loglevel", out v2)) p.LogLevel = S(v2, "info");
            if (t.TryGetValue("vpn_mode", out v2)) p.VpnMode = S(v2, "general") == "selective" ? "selective" : "general";
            if (t.TryGetValue("killswitch_enabled", out v2)) p.KillSwitch = B(v2, true);
            if (t.TryGetValue("killswitch_allow_ports", out v2)) p.KillSwitchAllowPorts = string.Join(", ", L(v2));
            if (t.TryGetValue("post_quantum_group_enabled", out v2)) p.PostQuantum = B(v2, true);
            if (t.TryGetValue("exclusions", out v2)) p.Exclusions = L(v2);

            bool socks = false;
            foreach (var k in t.Keys) if (k.StartsWith("listener.socks", StringComparison.Ordinal)) socks = true;
            if (socks)
            {
                p.ListenerType = "socks";
                if (t.TryGetValue("listener.socks.address", out v2)) p.SocksAddress = S(v2, "127.0.0.1:1080");
                if (t.TryGetValue("listener.socks.username", out v2)) p.SocksUsername = S(v2, "");
                if (t.TryGetValue("listener.socks.password", out v2)) p.SocksPassword = S(v2, "");
            }
            else
            {
                p.ListenerType = "tun";
                if (t.TryGetValue("listener.tun.device_name", out v2) && S(v2, "") != "") p.DeviceName = S(v2, "");
                if (t.TryGetValue("listener.tun.mtu_size", out v2) && v2 is long) p.MtuSize = (int)(long)v2;
                if (t.TryGetValue("listener.tun.change_system_dns", out v2)) p.ChangeSystemDns = B(v2, true);
                if (t.TryGetValue("listener.tun.included_routes", out v2)) p.IncludedRoutes = L(v2);
                if (t.TryGetValue("listener.tun.excluded_routes", out v2)) p.ExcludedRoutes = L(v2);
            }
            return p;
        }

        static string S(object v, string def)
        {
            if (v == null) return def;
            if (v is string) return (string)v;
            if (v is bool) return (bool)v ? "true" : "false";
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        static bool B(object v, bool def) { return v is bool ? (bool)v : def; }

        static List<string> L(object v)
        {
            var r = new List<string>();
            var list = v as List<object>;
            if (list != null) foreach (var x in list) r.Add(S(x, ""));
            else if (v is string && (string)v != "") r.Add((string)v);
            return r;
        }
    }
}
