using System;
using System.Collections.Generic;
using System.Linq;

namespace TrustTunnelGui
{
    /// <summary>Готовый набор исключений (сервис или приложение).</summary>
    public class ExclusionPreset
    {
        public string Id;
        public string Name;
        public string Description;
        public string[] Entries;
        public string[] Executables; // имена exe (без .exe), по которым приложение узнаётся
        public string[] Networks;    // все сети сервиса; если заданы, в режиме «только выбранное» его можно выделить маршрутами
    }

    /// <summary>Исключение-приложение, собранное по его сетевым подключениям.</summary>
    public class ExclusionApp
    {
        public string Name { get; set; }
        public string Exe { get; set; }
        public List<string> Entries { get; set; }
        public bool Enabled { get; set; }

        public ExclusionApp()
        {
            Name = "";
            Exe = "";
            Entries = new List<string>();
            Enabled = true;
        }
    }

    public static class ExclusionCatalog
    {
        public const string ClaudeId = "claude";

        public static readonly ExclusionPreset[] Presets =
        {
            new ExclusionPreset
            {
                Id = ClaudeId, Name = "Claude / Anthropic", Description = "claude.ai, приложение Claude, Claude Code, API Anthropic",
                Executables = new[] { "claude" },
                Entries = new[] { "claude.ai", "claude.com", "anthropic.com", "claudeusercontent.com" },
                // собственные сети Anthropic (AS399358): claude.ai, api.anthropic.com
                Networks = new[] { "160.79.104.0/23", "2607:6bc0::/48" },
            },
            new ExclusionPreset
            {
                Id = "discord", Name = "Discord", Description = "чат, голос, CDN",
                Executables = new[] { "discord", "discordptb", "discordcanary" },
                Entries = new[] { "discord.com", "discord.gg", "discord.media", "discordapp.com", "discordapp.net", "discord.co",
                    "discordcdn.com", "discord.dev", "discord.new", "discord.gift", "discordstatus.com", "dis.gd", "discord.tools" },
            },
            new ExclusionPreset
            {
                Id = "youtube", Name = "YouTube", Description = "сайт и видео",
                Entries = new[] { "youtube.com", "youtu.be", "googlevideo.com", "ytimg.com", "youtube-nocookie.com",
                    "youtubei.googleapis.com", "ggpht.com", "yt.be" },
            },
            new ExclusionPreset
            {
                Id = "telegram", Name = "Telegram", Description = "домены и IP-сети Telegram",
                Executables = new[] { "telegram", "ayugram", "64gram" },
                Entries = new[] { "telegram.org", "t.me", "telegram.me", "telesco.pe", "tdesktop.com", "telegra.ph",
                    "91.108.4.0/22", "91.108.8.0/22", "91.108.12.0/22", "91.108.16.0/22", "91.108.56.0/22",
                    "149.154.160.0/20", "95.161.64.0/20" },
                // https://core.telegram.org/resources/cidr.txt
                Networks = new[] { "91.105.192.0/23", "91.108.4.0/22", "91.108.8.0/22", "91.108.12.0/22", "91.108.16.0/22",
                    "91.108.20.0/22", "91.108.56.0/22", "95.161.64.0/20", "149.154.160.0/20", "185.76.151.0/24",
                    "2001:67c:4e8::/48", "2001:b28:f23c::/48", "2001:b28:f23d::/48", "2001:b28:f23f::/48", "2a0a:f280::/32" },
            },
            new ExclusionPreset
            {
                Id = "whatsapp", Name = "WhatsApp", Description = "мессенджер",
                Executables = new[] { "whatsapp" },
                Entries = new[] { "whatsapp.com", "whatsapp.net", "wa.me" },
            },
            new ExclusionPreset
            {
                Id = "steam", Name = "Steam", Description = "магазин, загрузки, игры",
                Executables = new[] { "steam", "steamwebhelper" },
                Entries = new[] { "steampowered.com", "steamcommunity.com", "steamstatic.com", "steamcontent.com",
                    "steamserver.net", "steamgames.com", "valvesoftware.com", "steamusercontent.com" },
            },
            new ExclusionPreset
            {
                Id = "twitch", Name = "Twitch", Description = "стримы",
                Entries = new[] { "twitch.tv", "ttvnw.net", "jtvnw.net", "twitchcdn.net", "twitchsvc.net" },
            },
            new ExclusionPreset
            {
                Id = "spotify", Name = "Spotify", Description = "музыка",
                Executables = new[] { "spotify" },
                Entries = new[] { "spotify.com", "scdn.co", "spotifycdn.com", "spotify.design" },
            },
            new ExclusionPreset
            {
                Id = "ru-services", Name = "Российские сервисы", Description = "банки, Госуслуги, Яндекс, VK, маркетплейсы",
                Entries = new[] { "gosuslugi.ru", "nalog.gov.ru", "mos.ru", "sberbank.ru", "tbank.ru", "tinkoff.ru", "vtb.ru",
                    "alfabank.ru", "yandex.ru", "ya.ru", "yandex.net", "yastatic.net", "vk.com", "vk.ru", "userapi.com", "vkuser.net",
                    "mail.ru", "ok.ru", "ozon.ru", "wildberries.ru", "wb.ru", "avito.ru", "kinopoisk.ru", "rutube.ru", "dzen.ru" },
            },
            new ExclusionPreset
            {
                Id = "ru-zones", Name = "Все сайты .ru / .рф / .su", Description = "все домены в российских зонах",
                Entries = new[] { "*.ru", "*.xn--p1ai", "*.su" },
            },
        };

        /// <summary>Адрес для проверки, что туннель реально работает: сайт, который точно идёт через VPN.</summary>
        public static string HealthUrl(Profile p)
        {
            if (p.VpnMode != "selective") return "https://www.gstatic.com/generate_204";
            if (p.ExclusionPresets.Contains(ClaudeId)) return "https://claude.ai/";
            foreach (var e in Effective(p))
            {
                // первый «обычный» домен: не IP, не подсеть, не маска
                if (e.Contains("*") || e.Contains("/") || e.Contains(":")) continue;
                System.Net.IPAddress ip;
                if (System.Net.IPAddress.TryParse(e, out ip)) continue;
                if (e.Contains(".")) return "https://" + e + "/";
            }
            return null;
        }

        public static ExclusionPreset Find(string id)
        {
            return Presets.FirstOrDefault(p => p.Id == id);
        }

        /// <summary>Готовый набор для приложения по имени его exe (например, Discord.exe → набор Discord).</summary>
        public static ExclusionPreset ForExecutable(string exeName)
        {
            var n = System.IO.Path.GetFileNameWithoutExtension(exeName ?? "").ToLowerInvariant();
            return Presets.FirstOrDefault(p => p.Executables != null && p.Executables.Contains(n));
        }

        /// <summary>Все исключения профиля: свои записи + включённые наборы + приложения, без повторов.</summary>
        public static List<string> Effective(Profile p)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var res = new List<string>();
            Action<IEnumerable<string>> add = list =>
            {
                foreach (var e in list)
                {
                    var t = (e ?? "").Trim();
                    if (t.Length > 0 && seen.Add(t)) res.Add(t);
                }
            };
            add(p.Exclusions);
            foreach (var id in p.ExclusionPresets)
            {
                var preset = Find(id);
                if (preset != null) add(preset.Entries);
            }
            foreach (var app in p.ExclusionApps)
                if (app.Enabled) add(app.Entries);
            return res;
        }

        /// <summary>Короткое описание для главного экрана: «Discord, YouTube и ещё 3».</summary>
        public static string Summary(Profile p)
        {
            var names = new List<string>();
            foreach (var id in p.ExclusionPresets)
            {
                var preset = Find(id);
                if (preset != null) names.Add(preset.Name);
            }
            foreach (var app in p.ExclusionApps) if (app.Enabled) names.Add(app.Name);
            int own = p.Exclusions.Count;
            if (names.Count == 0 && own == 0) return "нет";
            string s = string.Join(", ", names.Take(2));
            int rest = names.Count - Math.Min(2, names.Count) + own;
            if (names.Count == 0) return own + " " + Plural(own, "адрес", "адреса", "адресов");
            return rest > 0 ? s + " и ещё " + rest : s;
        }

        public static string Plural(int n, string one, string few, string many)
        {
            int m10 = n % 10, m100 = n % 100;
            return m10 == 1 && m100 != 11 ? one : m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14) ? few : many;
        }
    }

    /// <summary>
    /// Разделение маршрутами для режима «через VPN только выбранное». Если всё выбранное известно по IP,
    /// в туннель заводятся только эти сети, а остальной трафик в него не попадает вовсе — как у WireGuard/AmneziaVPN.
    /// Тогда клиенту не нужно самому открывать прямые соединения, и zapret/GoodbyeDPI можно не останавливать.
    /// </summary>
    public static class SplitRouting
    {
        public static List<string> Networks(Profile p)
        {
            string blocker;
            return Networks(p, out blocker);
        }

        /// <summary>Сети для туннеля или null, если нужен режим по доменам; blocker — что мешает (для подсказки).</summary>
        public static List<string> Networks(Profile p, out string blocker)
        {
            blocker = null;
            if (!p.IsTun || p.VpnMode != "selective") return null;
            if (p.IncludedRoutes.Count > 0) { blocker = "в профиле заданы свои маршруты в туннель"; return null; }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var res = new List<string>();
            // IPv6-сети — только если IPv6 пропускается через VPN: иначе соединения к ним уходят в туннель и висят,
            // пока сервер не ответит таймаутом (на сервере без IPv6-выхода), вместо мгновенного перехода на IPv4.
            Action<IEnumerable<string>> add = list =>
            {
                foreach (var n in list)
                    if ((p.HasIpv6 || !n.Contains(":")) && seen.Add(n)) res.Add(n);
            };
            foreach (var id in p.ExclusionPresets)
            {
                var preset = ExclusionCatalog.Find(id);
                if (preset == null) continue;
                if (preset.Networks == null) { blocker = preset.Name; return null; }
                add(preset.Networks);
            }
            foreach (var e in p.Exclusions)
            {
                var cidr = ToCidr(e);
                if (cidr == null) { blocker = e; return null; }
                add(new[] { cidr });
            }
            foreach (var app in p.ExclusionApps)
            {
                if (!app.Enabled) continue;
                var preset = ExclusionCatalog.ForExecutable(app.Exe);
                if (preset != null && preset.Networks != null) { add(preset.Networks); continue; }
                var nets = app.Entries.Select(ToCidr).ToList();
                if (nets.Count == 0 || nets.Contains(null)) { blocker = app.Name; return null; }
                add(nets);
            }
            if (res.Count == 0) { blocker = "ничего не выбрано"; return null; }
            return res;
        }

        /// <summary>IP-адрес или подсеть в виде CIDR (1.2.3.4 → 1.2.3.4/32); для доменов, масок и портов — null.</summary>
        public static string ToCidr(string entry)
        {
            var s = (entry ?? "").Trim();
            string addr = s, bits = null;
            int slash = s.IndexOf('/');
            if (slash > 0) { addr = s.Substring(0, slash); bits = s.Substring(slash + 1); }
            System.Net.IPAddress ip;
            if (!System.Net.IPAddress.TryParse(addr, out ip)) return null;
            // IPAddress.TryParse принимает и «10» или «1.2» — требуем полную запись адреса
            bool v6 = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
            if (!v6 && addr.Count(c => c == '.') != 3) return null;
            int max = v6 ? 128 : 32, n = max;
            if (bits != null && (!int.TryParse(bits, out n) || n < 0 || n > max)) return null;
            return addr + "/" + n;
        }
    }

    /// <summary>Маршрутизация локальных (частных) сетей: напрямую или через VPN.</summary>
    public static class LanRouting
    {
        /// <summary>Частные сети RFC 1918.</summary>
        public static readonly string[] PrivateRanges = { "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16" };

        /// <summary>
        /// Маршруты мимо VPN. Пользовательский список имеет приоритет; иначе — значения клиента по умолчанию,
        /// из которых в режиме «локальные сети через VPN» убираются частные сети (кроме, при желании, своей домашней).
        /// </summary>
        public static List<string> ExcludedRoutes(Profile p, IEnumerable<string> homeSubnets)
        {
            if (p.ExcludedRoutes.Count > 0) return new List<string>(p.ExcludedRoutes);
            var list = new List<string>(ConfigBuilder.DefaultExcludedRoutes);
            if (p.LanViaVpn)
            {
                list.RemoveAll(r => PrivateRanges.Contains(r));
                if (p.LanKeepHome && homeSubnets != null)
                    foreach (var s in homeSubnets) if (!list.Contains(s)) list.Add(s);
            }
            return list;
        }

        public static string Describe(Profile p, IEnumerable<string> homeSubnets)
        {
            if (!p.LanViaVpn) return "локальная сеть напрямую";
            var home = homeSubnets == null ? new List<string>() : homeSubnets.ToList();
            return p.LanKeepHome && home.Count > 0 ? "локальные сети через VPN (кроме " + string.Join(", ", home) + ")" : "локальные сети через VPN";
        }
    }
}
