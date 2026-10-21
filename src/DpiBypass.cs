using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.ServiceProcess;

namespace TrustTunnelGui
{
    /// <summary>
    /// Совместимость с программами обхода DPI (zapret / zapret-discord-youtube, GoodbyeDPI).
    ///
    /// Они перехватывают пакеты через WinDivert на ВСЕХ интерфейсах, включая VPN-адаптер, и подмешивают
    /// «фальшивые» пакеты (fake, multisplit, fooling ts/badseq/md5sig). В интернете их отбрасывает
    /// сервер назначения, а внутри туннеля они попадают в TCP-стек TrustTunnel и портят соединения —
    /// так ломаются Discord и YouTube. Пока VPN включён, обход DPI не нужен (провайдер не видит
    /// содержимое туннеля), поэтому на время подключения его приостанавливаем, а после — возвращаем.
    /// </summary>
    public static class DpiBypass
    {
        static readonly string[] ServiceNames = { "zapret", "winws1", "winws2", "GoodbyeDPI" };
        static readonly string[] ProcessNames = { "winws", "goodbyedpi" };

        public class Detected
        {
            public List<string> Services = new List<string>();   // запущенные службы
            public List<ProcInfo> Processes = new List<ProcInfo>(); // процессы вне служб
            public bool Any { get { return Services.Count > 0 || Processes.Count > 0; } }

            public string Describe()
            {
                var parts = new List<string>();
                foreach (var s in Services) parts.Add("служба «" + s + "»");
                foreach (var p in Processes) parts.Add(Path.GetFileName(p.Exe));
                return string.Join(", ", parts.Distinct());
            }
        }

        public class ProcInfo
        {
            public int Pid;
            public string Exe = "";
            public string Args = "";
            public string WorkDir = "";
        }

        public static Detected Detect()
        {
            var d = new Detected();
            var servicePids = new HashSet<int>();
            foreach (var name in ServiceNames)
            {
                try
                {
                    using (var sc = new ServiceController(name))
                    {
                        if (sc.Status == ServiceControllerStatus.Running || sc.Status == ServiceControllerStatus.StartPending)
                        {
                            d.Services.Add(sc.ServiceName);
                            int pid = ServicePid(sc.ServiceName);
                            if (pid > 0) servicePids.Add(pid);
                        }
                    }
                }
                catch (InvalidOperationException) { } // службы нет
                catch { }
            }
            foreach (var pname in ProcessNames)
            {
                Process[] procs;
                try { procs = Process.GetProcessesByName(pname); } catch { continue; }
                foreach (var p in procs)
                {
                    if (servicePids.Contains(p.Id)) continue;
                    var info = ReadProcess(p.Id);
                    if (info != null) d.Processes.Add(info);
                }
            }
            return d;
        }

        static int ServicePid(string service)
        {
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT ProcessId FROM Win32_Service WHERE Name='" + service.Replace("'", "") + "'"))
                    foreach (ManagementObject o in s.Get()) return Convert.ToInt32(o["ProcessId"]);
            }
            catch { }
            return 0;
        }

        static ProcInfo ReadProcess(int pid)
        {
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT ExecutablePath, CommandLine FROM Win32_Process WHERE ProcessId=" + pid))
                {
                    foreach (ManagementObject o in s.Get())
                    {
                        var exe = o["ExecutablePath"] as string ?? "";
                        var cmd = o["CommandLine"] as string ?? "";
                        if (exe.Length == 0) return null;
                        return new ProcInfo { Pid = pid, Exe = exe, Args = StripExe(cmd), WorkDir = GuessWorkDir(exe) };
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>Отрезает путь к exe от командной строки, оставляя аргументы.</summary>
        static string StripExe(string cmd)
        {
            cmd = cmd.TrimStart();
            if (cmd.StartsWith("\""))
            {
                int end = cmd.IndexOf('"', 1);
                return end > 0 ? cmd.Substring(end + 1).Trim() : "";
            }
            int sp = cmd.IndexOf(' ');
            return sp > 0 ? cmd.Substring(sp + 1).Trim() : "";
        }

        /// <summary>zapret-discord-youtube запускает bin\winws.exe из корня сборки — его и берём рабочим каталогом.</summary>
        static string GuessWorkDir(string exe)
        {
            var dir = Path.GetDirectoryName(exe) ?? "";
            if (string.Equals(Path.GetFileName(dir), "bin", StringComparison.OrdinalIgnoreCase))
                return Path.GetDirectoryName(dir) ?? dir;
            return dir;
        }

        /// <summary>Сериализация процесса для сохранения в настройках (на случай сбоя программы).</summary>
        public static string Pack(ProcInfo p) { return p.Exe + "\t" + p.WorkDir + "\t" + p.Args; }

        public static ProcInfo Unpack(string s)
        {
            var parts = s.Split(new[] { '\t' }, 3);
            if (parts.Length < 3) return null;
            return new ProcInfo { Exe = parts[0], WorkDir = parts[1], Args = parts[2] };
        }

        /// <summary>Останавливает найденное. Возвращает, что именно остановлено.</summary>
        public static Detected Pause(Detected d, Action<string> log)
        {
            var paused = new Detected();
            foreach (var name in d.Services)
            {
                try
                {
                    using (var sc = new ServiceController(name))
                    {
                        if (sc.Status != ServiceControllerStatus.Stopped)
                        {
                            sc.Stop();
                            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                        }
                    }
                    paused.Services.Add(name);
                    log("⏸ Служба «" + name + "» приостановлена на время VPN");
                }
                catch (Exception ex) { log("Не удалось остановить службу «" + name + "»: " + ex.Message); }
            }
            foreach (var p in d.Processes)
            {
                try
                {
                    using (var proc = Process.GetProcessById(p.Pid))
                    {
                        proc.Kill();
                        proc.WaitForExit(5000);
                    }
                    paused.Processes.Add(p);
                    log("⏸ " + Path.GetFileName(p.Exe) + " приостановлен на время VPN");
                }
                catch (Exception ex) { log("Не удалось остановить " + Path.GetFileName(p.Exe) + ": " + ex.Message); }
            }
            return paused;
        }

        public static void Resume(IEnumerable<string> services, IEnumerable<ProcInfo> processes, Action<string> log)
        {
            foreach (var name in services)
            {
                try
                {
                    using (var sc = new ServiceController(name))
                    {
                        if (sc.Status == ServiceControllerStatus.Stopped)
                        {
                            sc.Start();
                            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
                        }
                    }
                    log("▶ Служба «" + name + "» снова запущена");
                }
                catch (Exception ex) { log("Не удалось запустить службу «" + name + "»: " + ex.Message); }
            }
            foreach (var p in processes)
            {
                try
                {
                    if (!File.Exists(p.Exe)) { log("Не найден " + p.Exe + " — запустите обход DPI вручную"); continue; }
                    Process.Start(new ProcessStartInfo(p.Exe, p.Args)
                    {
                        WorkingDirectory = Directory.Exists(p.WorkDir) ? p.WorkDir : Path.GetDirectoryName(p.Exe),
                        UseShellExecute = true,
                        WindowStyle = ProcessWindowStyle.Minimized,
                    });
                    log("▶ " + Path.GetFileName(p.Exe) + " снова запущен");
                }
                catch (Exception ex) { log("Не удалось запустить " + Path.GetFileName(p.Exe) + ": " + ex.Message); }
            }
        }
    }
}
