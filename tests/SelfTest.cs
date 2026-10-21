using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TrustTunnelGui;

// Проверки без внешних зависимостей: varint, tt:// (кодирование/декодирование по DEEP_LINK.md), TOML.
static class SelfTest
{
    static int failed, passed;

    static void Check(bool cond, string name)
    {
        if (cond) { passed++; }
        else { failed++; Console.WriteLine("FAIL: " + name); }
    }

    static void Eq<T>(T actual, T expected, string name)
    {
        Check(Equals(actual, expected), name + " (ожидалось «" + expected + "», получено «" + actual + "»)");
    }

    static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

        // --- varint (RFC 9000 §16, примеры из RFC) ---
        foreach (var v in new ulong[] { 0, 37, 63, 64, 15293, 16383, 16384, 494878333, 1UL << 30, 151288809941952652UL })
        {
            var b = DeepLink.Varint(v);
            int pos = 0;
            Eq(DeepLink.ReadVarint(b, ref pos), v, "varint roundtrip " + v);
            Eq(pos, b.Length, "varint length " + v);
        }
        {
            int pos = 0;
            Eq(DeepLink.ReadVarint(new byte[] { 0x7b, 0xbd }, ref pos), 15293UL, "varint RFC 2-byte");
            pos = 0;
            Eq(DeepLink.ReadVarint(new byte[] { 0x9d, 0x7f, 0x3e, 0x7d }, ref pos), 494878333UL, "varint RFC 4-byte");
        }

        // --- tt:// пример из спецификации (ручная сборка TLV) ---
        {
            var tlv = new List<byte>();
            Action<int, byte[]> add = (tag, val) => { tlv.Add((byte)tag); tlv.Add((byte)val.Length); tlv.AddRange(val); };
            Func<string, byte[]> u = s => Encoding.UTF8.GetBytes(s);
            add(0x00, new byte[] { 2 });
            add(0x01, u("vpn.example.com"));
            add(0x02, u("1.2.3.4:443"));
            add(0x03, u("example.org"));
            add(0x04, new byte[] { 1 });
            add(0x05, u("premium"));
            add(0x06, u("s3cretPass"));
            add(0x09, new byte[] { 1 });
            add(0x0D, new byte[] { 7, 0x31, 0x2E, 0x31, 0x2E, 0x31, 0x2E, 0x31 });
            add(0x3F, u("unknown tag is ignored"));
            var link = "tt://?" + DeepLink.Base64UrlEncode(tlv.ToArray());
            var p = DeepLink.Decode(link);
            Eq(p.Hostname, "vpn.example.com", "link hostname");
            Eq(p.Addresses.Count, 1, "link addresses count");
            Eq(p.Addresses[0], "1.2.3.4:443", "link address");
            Eq(p.CustomSni, "example.org", "link sni");
            Eq(p.HasIpv6, true, "link ipv6");
            Eq(p.Username, "premium", "link user");
            Eq(p.Password, "s3cretPass", "link pass");
            Eq(p.UpstreamProtocol, "http2", "link proto");
            Eq(p.SkipVerification, false, "link skip default");
            Eq(p.AntiDpi, false, "link antidpi default");
            Eq(p.DnsUpstreams.Count, 1, "link dns count");
            Eq(p.DnsUpstreams[0], "1.1.1.1", "link dns");
        }

        // --- roundtrip encode → decode с сертификатом и http3 ---
        {
            var p = new Profile
            {
                Name = "Мой сервер",
                Hostname = "vpn.test",
                Username = "user",
                Password = "p@ss \"quoted\"",
                UpstreamProtocol = "http3",
                AntiDpi = true,
                HasIpv6 = false,
                ClientRandom = "abcd/ff00",
            };
            p.Addresses.Add("10.0.0.1:443");
            p.Addresses.Add("vpn.test:8443");
            p.DnsUpstreams.Add("tls://1.1.1.1");
            p.DnsUpstreams.Add("https://dns.adguard-dns.com/dns-query");
            // два «сертификата» — минимальные DER SEQUENCE (короткая и длинная форма длины)
            var der1 = new byte[] { 0x30, 0x03, 0x02, 0x01, 0x05 };
            var der2 = new byte[3 + 200]; der2[0] = 0x30; der2[1] = 0x81; der2[2] = 200;
            for (int i = 3; i < der2.Length; i++) der2[i] = (byte)i;
            var allDer = new byte[der1.Length + der2.Length];
            Buffer.BlockCopy(der1, 0, allDer, 0, der1.Length);
            Buffer.BlockCopy(der2, 0, allDer, der1.Length, der2.Length);
            p.Certificate = DeepLink.DerToPem(allDer);
            Check(p.Certificate.Split(new[] { "BEGIN CERTIFICATE" }, StringSplitOptions.None).Length == 3, "pem: два сертификата");

            var link = DeepLink.Encode(p);
            var payload = link.Substring(6);
            Check(link.StartsWith("tt://?") && payload.IndexOfAny(new[] { '=', '+', '/' }) < 0, "encode: base64url без паддинга");
            var raw = DeepLink.Base64UrlDecode(link.Substring(6));
            Eq(raw[0], (byte)0x00, "encode: первым идёт version");
            Eq(raw[2], (byte)2, "encode: version = 2");

            var q = DeepLink.Decode(link);
            Eq(q.Name, p.Name, "rt name");
            Eq(q.Hostname, p.Hostname, "rt host");
            Eq(string.Join("|", q.Addresses), string.Join("|", p.Addresses), "rt addresses");
            Eq(q.Password, p.Password, "rt pass");
            Eq(q.UpstreamProtocol, "http3", "rt http3");
            Eq(q.AntiDpi, true, "rt antidpi");
            Eq(q.HasIpv6, false, "rt ipv6");
            Eq(q.ClientRandom, "abcd/ff00", "rt client_random");
            Eq(string.Join("|", q.DnsUpstreams), string.Join("|", p.DnsUpstreams), "rt dns");
            Eq(Convert.ToBase64String(DeepLink.PemToDer(q.Certificate)), Convert.ToBase64String(allDer), "rt certificate DER");

            // --- профиль → TOML → профиль ---
            p.Exclusions.Add("example.com");
            p.Exclusions.Add("*.local");
            p.KillSwitchAllowPorts = "22, 3389";
            p.MtuSize = 1280;
            p.TlsProfile = "firefox";
            p.ExtraToml = "exclusions_preresolve_enabled = false";
            var toml = ConfigBuilder.Build(p);
            var t = TomlParser.Parse(toml);
            Eq((string)t["endpoint.hostname"], "vpn.test", "toml endpoint.hostname");
            Eq((string)t["endpoint.password"], p.Password, "toml экранирование пароля");
            Eq((string)t["endpoint.tls_profile"], "firefox", "toml tls_profile");
            Eq((long)t["listener.tun.mtu_size"], 1280L, "toml mtu");
            Eq((bool)t["exclusions_preresolve_enabled"], false, "toml extra");
            Eq(((List<object>)t["killswitch_allow_ports"]).Count, 2, "toml ports");
            Check(!t.ContainsKey("endpoint.client_random_auth_key"), "toml: пустые поля не пишутся");
            // маршруты обязаны быть явно — иначе клиент 1.1.x не ставит маршрут по умолчанию
            var inc = (List<object>)t["listener.tun.included_routes"];
            Check(inc.Count == 2 && (string)inc[0] == "0.0.0.0/0", "toml: included_routes по умолчанию записан явно");
            Check(((List<object>)t["listener.tun.excluded_routes"]).Contains("192.168.0.0/16"), "toml: excluded_routes по умолчанию записан явно");
            Check(!t.ContainsKey("post_quantum_group_enabled"), "toml: значения по умолчанию не пишутся");

            var r = ConfigBuilder.FromToml(toml);
            Eq(r.Hostname, p.Hostname, "fromToml host");
            Eq(r.Password, p.Password, "fromToml pass");
            Eq(string.Join("|", r.Exclusions), "example.com|*.local", "fromToml exclusions");
            Eq(r.UpstreamProtocol, "http3", "fromToml proto");
            Eq(r.MtuSize, 1280, "fromToml mtu");
            Eq(r.KillSwitchAllowPorts, "22, 3389", "fromToml ports");
            Eq(Convert.ToBase64String(DeepLink.PemToDer(r.Certificate)), Convert.ToBase64String(allDer), "fromToml certificate");

            // SOCKS
            p.ListenerType = "socks";
            p.SocksAddress = "127.0.0.1:2080";
            var r2 = ConfigBuilder.FromToml(ConfigBuilder.Build(p));
            Eq(r2.ListenerType, "socks", "socks listener");
            Eq(r2.SocksAddress, "127.0.0.1:2080", "socks address");
        }

        // --- импорт конфига «как из документации» (многострочные массивы, комментарии, литеральные строки) ---
        {
            var doc = @"# client config
loglevel = ""debug""
vpn_mode = ""selective""
killswitch_enabled = false
exclusions = [
  ""example.org"", # comment
  '*.corp.lan',
]

[endpoint]
hostname = ""vpn.example.com|front.example.net""
addresses = [""192.168.1.100:443""]
username = ""myuser""
password = 'my\pass'
certificate = """"""
-----BEGIN CERTIFICATE-----
AAAA
-----END CERTIFICATE-----
""""""
upstream_protocol = ""http2""

[listener.tun]
included_routes = [""0.0.0.0/0"", ""2000::/3""]
mtu_size = 1_350
";
            var p = ConfigBuilder.FromToml(doc);
            Eq(p.LogLevel, "debug", "doc loglevel");
            Eq(p.VpnMode, "selective", "doc vpn_mode");
            Eq(p.KillSwitch, false, "doc killswitch");
            Eq(string.Join("|", p.Exclusions), "example.org|*.corp.lan", "doc exclusions");
            Eq(p.Hostname, "vpn.example.com", "doc hostname|sni → hostname");
            Eq(p.CustomSni, "front.example.net", "doc hostname|sni → sni");
            Eq(p.Password, "my\\pass", "doc literal string");
            Check(p.Certificate.Contains("AAAA") && p.Certificate.StartsWith("-----BEGIN"), "doc multiline cert");
            Eq(p.MtuSize, 1350, "doc mtu с подчёркиванием");
            Eq(p.IncludedRoutes.Count, 2, "doc included_routes");
        }

        // --- экспорт сервера в плоском формате ---
        {
            var flat = "hostname = \"vpn.example.com\"\naddresses = [\"1.2.3.4:443\"]\nusername = \"u\"\npassword = \"p\"\nclient_random_prefix = \"aabb\"\nanti_dpi = true\n";
            var p = ConfigBuilder.FromToml(flat);
            Eq(p.Hostname, "vpn.example.com", "flat hostname");
            Eq(p.ClientRandom, "aabb", "flat client_random_prefix");
            Eq(p.AntiDpi, true, "flat anti_dpi");
        }

        // --- разделение маршрутами в режиме «только выбранное» ---
        {
            var p = new Profile { Hostname = "h", Username = "u", Password = "p", VpnMode = "selective", KillSwitch = true, ChangeSystemDns = true };
            p.Addresses.Add("1.2.3.4:443");
            p.ExclusionPresets.Add(ExclusionCatalog.ClaudeId);
            p.Exclusions.Add("8.8.8.8");
            p.Exclusions.Add("10.20.0.0/16");
            var nets = SplitRouting.Networks(p);
            Check(nets != null && nets.Contains("160.79.104.0/23") && nets.Contains("2607:6bc0::/48"), "split: сети Claude");
            Check(nets != null && nets.Contains("8.8.8.8/32") && nets.Contains("10.20.0.0/16"), "split: свои IP → CIDR");
            var t = TomlParser.Parse(ConfigBuilder.Build(p));
            Eq(t["vpn_mode"], "general", "split: vpn_mode general");
            Eq(t["killswitch_enabled"], false, "split: kill switch выключен");
            Eq(((List<object>)t["exclusions"]).Count, 0, "split: exclusions пусты");
            Eq(t["listener.tun.change_system_dns"], true, "split: DNS клиента (иначе restrict_dns_to блокирует роутер)");
            p.ChangeSystemDns = false;
            Eq(TomlParser.Parse(ConfigBuilder.Build(p))["listener.tun.change_system_dns"], true, "split: DNS клиента даже если в профиле выключен");
            p.ChangeSystemDns = true;
            p.HasIpv6 = false;
            Check(!SplitRouting.Networks(p).Contains("2607:6bc0::/48") && SplitRouting.Networks(p).Contains("160.79.104.0/23"), "split: без IPv6 через VPN — только IPv4-сети");
            p.HasIpv6 = true;
            Check(((List<object>)t["listener.tun.included_routes"]).Contains("160.79.104.0/23")
                  && !((List<object>)t["listener.tun.included_routes"]).Contains("0.0.0.0/0"), "split: included_routes только сети");

            p.ExclusionPresets.Add("spotify");
            string blocker;
            Check(SplitRouting.Networks(p, out blocker) == null && blocker == "Spotify", "split: Spotify → режим по доменам");
            var t2 = TomlParser.Parse(ConfigBuilder.Build(p));
            Eq(t2["vpn_mode"], "selective", "domains: vpn_mode selective");
            Eq(t2["killswitch_enabled"], true, "domains: kill switch как в профиле");
            p.ExclusionPresets.Remove("spotify");
            p.Exclusions.Add("example.com");
            Check(SplitRouting.Networks(p) == null, "split: свой домен → режим по доменам");
            p.Exclusions.Remove("example.com");
            p.VpnMode = "general";
            Check(SplitRouting.Networks(p) == null, "split: только в режиме «только выбранное»");
            Eq(SplitRouting.ToCidr("2001:db8::1"), "2001:db8::1/128", "cidr: IPv6");
            Check(SplitRouting.ToCidr("10") == null && SplitRouting.ToCidr("*:443") == null && SplitRouting.ToCidr("1.2.3.4/33") == null, "cidr: мусор отбрасывается");
        }

        // --- исключения: наборы, приложения, свои записи ---
        {
            var p = new Profile { Hostname = "h", Username = "u", Password = "p" };
            p.Addresses.Add("1.2.3.4:443");
            p.Exclusions.Add("example.com");
            p.Exclusions.Add("discord.com"); // дубль с набором
            p.ExclusionPresets.Add("discord");
            p.ExclusionPresets.Add("нет-такого");
            p.ExclusionApps.Add(new ExclusionApp { Name = "Game", Exe = "game.exe", Entries = new List<string> { "game.net", "5.6.7.8" } });
            p.ExclusionApps.Add(new ExclusionApp { Name = "Off", Exe = "off.exe", Entries = new List<string> { "off.net" }, Enabled = false });
            var eff = ExclusionCatalog.Effective(p);
            Check(eff.Contains("example.com") && eff.Contains("discord.media") && eff.Contains("game.net") && eff.Contains("5.6.7.8"), "effective: все источники");
            Check(!eff.Contains("off.net"), "effective: выключенное приложение не попадает");
            Eq(eff.Count(x => x == "discord.com"), 1, "effective: без дублей");
            Eq(ExclusionCatalog.Summary(p), "Discord, Game и ещё 2", "summary");
            Eq(ExclusionCatalog.ForExecutable("Discord.exe").Id, "discord", "приложение → набор");

            var t = TomlParser.Parse(ConfigBuilder.Build(p));
            Check(((List<object>)t["exclusions"]).Contains("discord.gg"), "toml: набор раскрыт в exclusions");

            // локальные сети
            var home = new List<string> { "192.168.0.0/24" };
            var direct = LanRouting.ExcludedRoutes(p, home);
            Check(direct.Contains("192.168.0.0/16") && direct.Contains("10.0.0.0/8"), "LAN напрямую: частные сети мимо VPN");
            p.LanViaVpn = true;
            var via = LanRouting.ExcludedRoutes(p, home);
            Check(!via.Contains("10.0.0.0/8") && !via.Contains("192.168.0.0/16") && !via.Contains("172.16.0.0/12"), "LAN через VPN: частные сети убраны");
            Check(via.Contains("192.168.0.0/24") && via.Contains("169.254.0.0/16"), "LAN через VPN: домашняя сеть и link-local остаются");
            p.LanKeepHome = false;
            Check(!LanRouting.ExcludedRoutes(p, home).Contains("192.168.0.0/24"), "LAN через VPN без домашней сети");
            var t2 = TomlParser.Parse(ConfigBuilder.Build(p, home));
            Check(!((List<object>)t2["listener.tun.excluded_routes"]).Contains("10.0.0.0/8"), "toml: excluded_routes по переключателю");
            p.ExcludedRoutes.Add("1.1.1.1/32");
            Eq(LanRouting.ExcludedRoutes(p, home).Count, 1, "свои маршруты важнее переключателя");
        }

        // --- «только Claude» и адрес проверки связи ---
        {
            var p = new Profile();
            Eq(ExclusionCatalog.HealthUrl(p), "https://www.gstatic.com/generate_204", "health: обычный режим");
            p.VpnMode = "selective";
            Eq(ExclusionCatalog.HealthUrl(p), null, "health: выборочный режим без адресов");
            p.Exclusions.Add("10.0.0.0/8");
            p.Exclusions.Add("*.corp.lan");
            p.Exclusions.Add("intranet.example.org");
            Eq(ExclusionCatalog.HealthUrl(p), "https://intranet.example.org/", "health: первый обычный домен");
            p.ExclusionPresets.Add(ExclusionCatalog.ClaudeId);
            Eq(ExclusionCatalog.HealthUrl(p), "https://claude.ai/", "health: набор Claude");
            var eff = ExclusionCatalog.Effective(p);
            Check(eff.Contains("claude.ai") && eff.Contains("anthropic.com") && eff.Contains("claude.com"), "Claude: домены в списке");
            Eq(ExclusionCatalog.ForExecutable("claude.exe").Id, ExclusionCatalog.ClaudeId, "приложение Claude → набор");
            var t = TomlParser.Parse(ConfigBuilder.Build(p));
            Eq((string)t["vpn_mode"], "selective", "toml: выборочный режим");
        }

        // --- bound_if: адаптер для прямых соединений ---
        {
            var p = new Profile { Hostname = "h", Username = "u", Password = "p" };
            p.Addresses.Add("1.2.3.4:443");
            var t0 = TomlParser.Parse(ConfigBuilder.Build(p));
            Check(!t0.ContainsKey("listener.tun.bound_if"), "bound_if: не пишется, если неизвестен");
            var t1 = TomlParser.Parse(ConfigBuilder.Build(p, null, "16"));
            Eq((string)t1["listener.tun.bound_if"], "16", "bound_if: автоопределённый адаптер");
            p.BoundIf = "Wi-Fi";
            var t2 = TomlParser.Parse(ConfigBuilder.Build(p, null, "16"));
            Eq((string)t2["listener.tun.bound_if"], "Wi-Fi", "bound_if: ручная настройка важнее");
            Eq(ConfigBuilder.FromToml(ConfigBuilder.Build(p)).BoundIf, "Wi-Fi", "bound_if: импорт из TOML");
            p.ListenerType = "socks";
            Check(!TomlParser.Parse(ConfigBuilder.Build(p, null, "16")).ContainsKey("listener.tun.bound_if"), "bound_if: не для SOCKS5");
        }

        // --- ошибки ---
        {
            Throws(() => DeepLink.Decode("https://example.com"), "не tt://");
            Throws(() => DeepLink.Decode("tt://?A"), "кривой base64");
            Throws(() => DeepLink.Decode("tt://?" + DeepLink.Base64UrlEncode(new byte[] { 0, 1, 9 })), "слишком новая версия");
            Throws(() => DeepLink.Decode("tt://?" + DeepLink.Base64UrlEncode(new byte[] { 1, 3, 0x61, 0x62, 0x63 })), "нет обязательных полей");
            Throws(() => DeepLink.Decode("tt://?" + DeepLink.Base64UrlEncode(new byte[] { 1, 30, 0x61 })), "длина за границами");
            Throws(() => ConfigBuilder.FromToml("a = \"unterminated"), "toml: незакрытая строка");
            Throws(() => ConfigBuilder.FromToml("foo = 1"), "toml: нет hostname");
        }

        Console.WriteLine("Self-test: " + passed + " ok, " + failed + " failed");
        return failed == 0 ? 0 : 1;
    }

    static void Throws(Action a, string name)
    {
        try { a(); Check(false, "ожидалось исключение: " + name); }
        catch (FormatException) { passed++; }
        catch (Exception ex) { Check(false, name + ": неожиданное исключение " + ex.GetType().Name); }
    }
}
