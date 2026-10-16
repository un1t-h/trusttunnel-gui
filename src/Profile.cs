using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace TrustTunnelGui
{
    /// <summary>Профиль подключения: всё, из чего собирается trusttunnel_client.toml.</summary>
    public class Profile
    {
        public string Id { get; set; }
        public string Name { get; set; }

        // [endpoint]
        public string Hostname { get; set; }
        public string CustomSni { get; set; }
        public List<string> Addresses { get; set; }
        public string Username { get; set; }
        [ScriptIgnore] public string Password { get; set; }
        public bool HasIpv6 { get; set; }
        public bool SkipVerification { get; set; }
        public string Certificate { get; set; }
        public string UpstreamProtocol { get; set; }
        public bool AntiDpi { get; set; }
        public string TlsProfile { get; set; }
        public string ClientRandom { get; set; }
        public string ClientRandomAuthKey { get; set; }
        public List<string> DnsUpstreams { get; set; }
        public string SubscriptionUrl { get; set; }

        // Маршрутизация
        public string VpnMode { get; set; }
        public List<string> Exclusions { get; set; }
        public bool KillSwitch { get; set; }
        public string KillSwitchAllowPorts { get; set; }

        // Listener
        public string ListenerType { get; set; }
        public string DeviceName { get; set; }
        public int MtuSize { get; set; }
        public bool ChangeSystemDns { get; set; }
        public List<string> IncludedRoutes { get; set; }
        public List<string> ExcludedRoutes { get; set; }
        public string SocksAddress { get; set; }
        public string SocksUsername { get; set; }
        [ScriptIgnore] public string SocksPassword { get; set; }

        // Дополнительно
        public string LogLevel { get; set; }
        public bool PostQuantum { get; set; }
        public string ExtraToml { get; set; }

        // Хранение секретов (DPAPI, привязано к пользователю Windows)
        public string PasswordProtected
        {
            get { return Secret.Protect(Password); }
            set { Password = Secret.Unprotect(value); }
        }
        public string SocksPasswordProtected
        {
            get { return Secret.Protect(SocksPassword); }
            set { SocksPassword = Secret.Unprotect(value); }
        }

        public Profile()
        {
            Id = Guid.NewGuid().ToString("N");
            Name = "";
            Hostname = "";
            CustomSni = "";
            Addresses = new List<string>();
            Username = "";
            Password = "";
            HasIpv6 = true;
            Certificate = "";
            UpstreamProtocol = "http2";
            TlsProfile = "";
            ClientRandom = "";
            ClientRandomAuthKey = "";
            DnsUpstreams = new List<string>();
            SubscriptionUrl = "";
            VpnMode = "general";
            Exclusions = new List<string>();
            KillSwitch = true;
            KillSwitchAllowPorts = "";
            ListenerType = "tun";
            DeviceName = "TrustTunnel";
            ChangeSystemDns = true;
            IncludedRoutes = new List<string>();
            ExcludedRoutes = new List<string>();
            SocksAddress = "127.0.0.1:1080";
            SocksUsername = "";
            SocksPassword = "";
            LogLevel = "info";
            PostQuantum = true;
            ExtraToml = "";
        }

        public string DisplayName
        {
            get { return string.IsNullOrEmpty(Name) ? (string.IsNullOrEmpty(Hostname) ? "Без имени" : Hostname) : Name; }
        }

        public bool IsTun { get { return ListenerType != "socks"; } }

        public Profile Clone()
        {
            var js = new JavaScriptSerializer();
            var copy = js.Deserialize<Profile>(js.Serialize(this));
            copy.Password = Password;
            copy.SocksPassword = SocksPassword;
            return copy;
        }
    }

    public class AppSettings
    {
        public string ClientDir { get; set; }
        public string LastProfileId { get; set; }
        public bool CloseToTray { get; set; }
        public bool StartMinimized { get; set; }
        public bool AutoConnect { get; set; }
        public bool AutoReconnect { get; set; }
        public string ClientVersion { get; set; }
        /// <summary>"system" | "light" | "dark"</summary>
        public string ThemeMode { get; set; }
        public bool ShowLog { get; set; }

        public AppSettings()
        {
            ThemeMode = "system";
            ClientDir = "";
            LastProfileId = "";
            CloseToTray = true;
            AutoReconnect = true;
            ClientVersion = "";
        }
    }

    public class AppData
    {
        public List<Profile> Profiles { get; set; }
        public AppSettings Settings { get; set; }

        public AppData()
        {
            Profiles = new List<Profile>();
            Settings = new AppSettings();
        }

        public static string DataDir
        {
            get
            {
                // Переменная окружения позволяет запускать отдельный экземпляр с другими данными (для разработки).
                var custom = Environment.GetEnvironmentVariable("TRUSTTUNNELGUI_DATA");
                if (!string.IsNullOrEmpty(custom)) return custom;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TrustTunnelGUI");
            }
        }

        static string DataFile { get { return Path.Combine(DataDir, "data.json"); } }

        public static AppData Load()
        {
            try
            {
                if (File.Exists(DataFile))
                {
                    var js = new JavaScriptSerializer();
                    js.MaxJsonLength = int.MaxValue;
                    var d = js.Deserialize<AppData>(File.ReadAllText(DataFile, Encoding.UTF8));
                    if (d != null)
                    {
                        if (d.Profiles == null) d.Profiles = new List<Profile>();
                        if (d.Settings == null) d.Settings = new AppSettings();
                        return d;
                    }
                }
            }
            catch (Exception ex)
            {
                try { File.Copy(DataFile, DataFile + ".broken", true); } catch { }
                System.Diagnostics.Debug.WriteLine(ex);
            }
            return new AppData();
        }

        public void Save()
        {
            Directory.CreateDirectory(DataDir);
            var js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            var tmp = DataFile + ".tmp";
            File.WriteAllText(tmp, js.Serialize(this), new UTF8Encoding(false));
            if (File.Exists(DataFile)) File.Replace(tmp, DataFile, null);
            else File.Move(tmp, DataFile);
        }
    }

    static class Secret
    {
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TrustTunnelGui.v1");

        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(data);
        }

        public static string Unprotect(string enc)
        {
            if (string.IsNullOrEmpty(enc)) return "";
            try
            {
                var data = ProtectedData.Unprotect(Convert.FromBase64String(enc), Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(data);
            }
            catch { return ""; }
        }
    }
}
