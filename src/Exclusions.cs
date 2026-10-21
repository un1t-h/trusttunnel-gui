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
        public static readonly ExclusionPreset[] Presets =
        {
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
