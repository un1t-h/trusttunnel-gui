using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace TrustTunnelGui
{
    /// <summary>Настройки приложения: поведение, оформление, компонент VPN, о программе.</summary>
    public class SettingsForm : ThemedDialog
    {
        readonly MainForm main;
        Label lblClient;

        public SettingsForm(MainForm owner)
        {
            main = owner;
            var s = owner.data.Settings;
            Text = "Настройки";
            Size = new Size(Ui.S(620), Ui.S(680));
            MinimumSize = new Size(Ui.S(520), Ui.S(480));

            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Tag = "bg" };
            var g = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 1,
                Padding = new Padding(Ui.S(22), Ui.S(14), Ui.S(22), Ui.S(16)),
            };
            g.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            Header(g, "Поведение");
            Check(g, "Подключаться к последнему серверу при запуске", s.AutoConnect, v => { s.AutoConnect = v; main.SaveData(); });
            Check(g, "Переподключаться автоматически, если соединение оборвалось", s.AutoReconnect, v => { s.AutoReconnect = v; main.SaveData(); });
            Check(g, "При закрытии окна сворачивать в трей", s.CloseToTray, v => { s.CloseToTray = v; main.SaveData(); });
            Check(g, "Запускать свёрнутым в трей", s.StartMinimized, v => { s.StartMinimized = v; main.SaveData(); });
            CheckBox chkAuto = null;
            chkAuto = Check(g, "Запускать при входе в Windows", MainForm.AutostartEnabled(), v =>
            {
                if (!main.SetAutostart(v)) { chkAuto.Checked = !v; }
            });
            Check(g, "Открывать ссылки tt:// в этой программе", MainForm.UrlHandlerEnabled(), v => main.SetUrlHandler(v));

            Header(g, "Оформление");
            var theme = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = Ui.S(240), Margin = new Padding(Ui.S(3), Ui.S(4), 0, Ui.S(4)) };
            theme.Items.AddRange(new object[] { "Как в Windows", "Светлая", "Тёмная" });
            theme.SelectedIndex = s.ThemeMode == "light" ? 1 : s.ThemeMode == "dark" ? 2 : 0;
            theme.SelectedIndexChanged += (o, e) =>
            {
                main.SetThemeMode(theme.SelectedIndex == 1 ? "light" : theme.SelectedIndex == 2 ? "dark" : "system");
                Theme.Apply(this);
                Invalidate(true);
            };
            g.Controls.Add(theme);

            Header(g, "Компонент VPN");
            g.Controls.Add(Muted("Официальный клиент TrustTunnel (trusttunnel_client), который устанавливает само соединение."));
            lblClient = new Label { AutoSize = true, MaximumSize = new Size(Ui.S(540), 0), Margin = new Padding(Ui.S(3), Ui.S(8), 0, Ui.S(8)) };
            g.Controls.Add(lblClient);
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0) };
            row.Controls.Add(Btn("Скачать / обновить", Glyphs.Download, ButtonKind.Primary, () => { main.InstallClient(); Close(); }));
            row.Controls.Add(Btn("Выбрать папку…", Glyphs.Folder, ButtonKind.Secondary, () => { main.ChooseClientDir(); UpdateClientInfo(); }));
            row.Controls.Add(Btn("Открыть папку", null, ButtonKind.Ghost, () => MainForm.OpenFolder(main.ClientDir)));
            g.Controls.Add(row);
            var row2 = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, Ui.S(4), 0, 0) };
            row2.Controls.Add(Btn("Завершить зависшие процессы клиента", null, ButtonKind.Ghost, () => main.KillOrphans(true)));
            g.Controls.Add(row2);

            Header(g, "О программе");
            g.Controls.Add(new Label { Text = Program.AppName + " " + Program.AppVersion, AutoSize = true, Margin = new Padding(Ui.S(3), Ui.S(2), 0, Ui.S(2)) });
            g.Controls.Add(Muted("Графический клиент для VPN-протокола TrustTunnel. Пароли хранятся зашифрованными средствами Windows."));
            var links = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, Ui.S(6), 0, 0) };
            links.Controls.Add(Btn("trusttunnel.org", Glyphs.Globe, ButtonKind.Ghost, () => MainForm.OpenUrl("https://trusttunnel.org/")));
            links.Controls.Add(Btn("Папка данных", Glyphs.Folder, ButtonKind.Ghost, () => MainForm.OpenFolder(AppData.DataDir)));
            g.Controls.Add(links);

            scroll.Controls.Add(g);
            var bar = ButtonBar();
            var close = DialogButton("Готово", ButtonKind.Primary);
            close.DialogResult = DialogResult.OK;
            bar.Controls.Add(close);
            AcceptButton = close;
            CancelButton = close;
            Controls.Add(scroll);
            Controls.Add(bar);
            UpdateClientInfo();
        }

        void UpdateClientInfo()
        {
            var s = main.data.Settings;
            lblClient.Text = main.ClientInstalled
                ? "✔ Установлен" + (string.IsNullOrEmpty(s.ClientVersion) ? "" : " " + s.ClientVersion) + " (" + ClientInstaller.Arch + ")\r\n" + main.ClientDir
                : "Не установлен. Нажмите «Скачать / обновить».";
            lblClient.ForeColor = main.ClientInstalled ? Theme.Current.Text : Theme.Current.Orange;
        }

        static void Header(TableLayoutPanel g, string text)
        {
            g.Controls.Add(new Label { Text = text, AutoSize = true, Font = Theme.SemiboldFont(11.5f), Margin = new Padding(Ui.S(2), Ui.S(g.Controls.Count == 0 ? 4 : 20), 0, Ui.S(6)) });
        }

        static Label Muted(string text)
        {
            return new Label { Text = text, AutoSize = true, Tag = "muted", MaximumSize = new Size(Ui.S(540), 0), Margin = new Padding(Ui.S(3), 0, 0, Ui.S(2)) };
        }

        static CheckBox Check(TableLayoutPanel g, string text, bool value, Action<bool> set)
        {
            var c = new CheckBox { Text = text, AutoSize = true, Checked = value, Margin = new Padding(Ui.S(3), Ui.S(4), 0, Ui.S(4)) };
            c.CheckedChanged += (s, e) => set(c.Checked);
            g.Controls.Add(c);
            return c;
        }

        static FlatButton Btn(string text, string glyph, ButtonKind kind, Action click)
        {
            var b = new FlatButton { Text = text, Glyph = glyph, Kind = kind, Margin = new Padding(Ui.S(2), 0, Ui.S(8), Ui.S(6)) };
            b.Size = b.GetPreferredSize(Size.Empty);
            b.Click += (s, e) => click();
            return b;
        }
    }
}
