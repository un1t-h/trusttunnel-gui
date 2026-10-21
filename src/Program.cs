using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace TrustTunnelGui
{
    static class Program
    {
        public const string AppName = "TrustTunnel GUI";
        public const string AppVersion = "1.3.1";
        const string MutexName = "TrustTunnelGui_SingleInstance";
        const string PipeName = "TrustTunnelGui_Pipe";

        [STAThread]
        static void Main(string[] args)
        {
            bool created;
            using (var mutex = new Mutex(true, MutexName, out created))
            {
                // После «Перезапустить с правами администратора» ждём, пока старый экземпляр закроется.
                if (!created && Array.IndexOf(args, "--relaunch") >= 0)
                {
                    try { created = mutex.WaitOne(10000); }
                    catch (AbandonedMutexException) { created = true; }
                }
                if (!created)
                {
                    // Уже запущено — передаём аргументы (например, ссылку tt://) первому экземпляру.
                    SendToRunningInstance(args);
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Ui.Init();
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) =>
                    MessageBox.Show(e.Exception.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);

                var form = new MainForm(args);
                StartPipeServer(form);
                Application.Run(form);
                GC.KeepAlive(mutex);
            }
        }

        static void SendToRunningInstance(string[] args)
        {
            try
            {
                using (var c = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                {
                    c.Connect(3000);
                    var data = Encoding.UTF8.GetBytes(string.Join("\n", args.Length == 0 ? new[] { "--show" } : args));
                    c.Write(data, 0, data.Length);
                }
            }
            catch { }
        }

        static void StartPipeServer(MainForm form)
        {
            var t = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        using (var s = new NamedPipeServerStream(PipeName, PipeDirection.In))
                        {
                            s.WaitForConnection();
                            var ms = new MemoryStream();
                            s.CopyTo(ms);
                            var a = Encoding.UTF8.GetString(ms.ToArray()).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                            form.BeginInvoke(new Action(() => form.HandleArgs(a, true)));
                        }
                    }
                    catch { Thread.Sleep(500); }
                }
            });
            t.IsBackground = true;
            t.Start();
        }
    }
}
