using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace TrustTunnelGui
{
    /// <summary>Скачивание официального trusttunnel_client для Windows с GitHub Releases (и wintun.dll при необходимости).</summary>
    public static class ClientInstaller
    {
        const string ReleasesApi = "https://api.github.com/repos/TrustTunnel/TrustTunnelClient/releases/latest";
        const string WintunUrl = "https://www.wintun.net/builds/wintun-0.14.1.zip";

        public static string Arch
        {
            get
            {
                string a = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432");
                if (string.IsNullOrEmpty(a)) a = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE");
                a = (a ?? "").ToUpperInvariant();
                if (a == "ARM64") return "aarch64";
                if (a == "X86") return "i686";
                return "x86_64";
            }
        }

        static string WintunArch
        {
            get
            {
                switch (Arch) { case "aarch64": return "arm64"; case "i686": return "x86"; default: return "amd64"; }
            }
        }

        internal static WebClient NewClient()
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
            var wc = new WebClient();
            wc.Headers[HttpRequestHeader.UserAgent] = "TrustTunnelGUI/" + Program.AppVersion;
            return wc;
        }

        public class ReleaseInfo
        {
            public string Tag;
            public string AssetName;
            public string Url;
            public long Size;
        }

        public static async Task<ReleaseInfo> GetLatestAsync()
        {
            string json;
            using (var wc = NewClient())
            {
                wc.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
                json = await wc.DownloadStringTaskAsync(ReleasesApi);
            }
            var js = new JavaScriptSerializer();
            var root = (Dictionary<string, object>)js.DeserializeObject(json);
            string suffix = "-windows-" + Arch + ".zip";
            foreach (var o in (IEnumerable)root["assets"])
            {
                var a = (Dictionary<string, object>)o;
                string name = (string)a["name"];
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return new ReleaseInfo
                    {
                        Tag = (string)root["tag_name"],
                        AssetName = name,
                        Url = (string)a["browser_download_url"],
                        Size = Convert.ToInt64(a["size"]),
                    };
                }
            }
            throw new Exception("В последнем релизе нет сборки для Windows " + Arch);
        }

        /// <summary>Скачивает и распаковывает клиент в targetDir. Возвращает предупреждения (например, про wintun).</summary>
        public static async Task<List<string>> InstallAsync(ReleaseInfo rel, string targetDir, Action<string> log, Action<int> progress)
        {
            var notes = new List<string>();
            string tmp = Path.Combine(Path.GetTempPath(), "ttgui_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                string zip = Path.Combine(tmp, rel.AssetName);
                log("Скачивание " + rel.Url);
                using (var wc = NewClient())
                {
                    wc.DownloadProgressChanged += (s, e) => progress(e.ProgressPercentage);
                    await wc.DownloadFileTaskAsync(new Uri(rel.Url), zip);
                }
                string ex = Path.Combine(tmp, "x");
                await Task.Run(() => ZipFile.ExtractToDirectory(zip, ex));

                string exe = Directory.GetFiles(ex, VpnController.ClientExe, SearchOption.AllDirectories).FirstOrDefault();
                if (exe == null) throw new Exception("В архиве нет " + VpnController.ClientExe);
                string srcDir = Path.GetDirectoryName(exe);

                Directory.CreateDirectory(targetDir);
                foreach (var f in Directory.GetFiles(srcDir, "*", SearchOption.AllDirectories))
                {
                    string rel2 = f.Substring(srcDir.Length).TrimStart('\\');
                    string dst = Path.Combine(targetDir, rel2);
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    File.Copy(f, dst, true);
                }
                log("Клиент " + rel.Tag + " установлен в " + targetDir);

                if (!File.Exists(Path.Combine(targetDir, "wintun.dll")))
                {
                    log("wintun.dll в архиве нет — скачиваю драйвер с wintun.net");
                    try
                    {
                        string wz = Path.Combine(tmp, "wintun.zip");
                        using (var wc = NewClient()) await wc.DownloadFileTaskAsync(new Uri(WintunUrl), wz);
                        string wx = Path.Combine(tmp, "w");
                        await Task.Run(() => ZipFile.ExtractToDirectory(wz, wx));
                        string dll = Path.Combine(wx, "wintun", "bin", WintunArch, "wintun.dll");
                        File.Copy(dll, Path.Combine(targetDir, "wintun.dll"), true);
                        log("wintun.dll (" + WintunArch + ") установлен");
                    }
                    catch (Exception e)
                    {
                        notes.Add("Не удалось скачать wintun.dll: " + e.Message +
                                  "\nСкачайте его вручную с https://www.wintun.net и положите рядом с trusttunnel_client.exe.");
                    }
                }
                return notes;
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
            }
        }
    }
}
