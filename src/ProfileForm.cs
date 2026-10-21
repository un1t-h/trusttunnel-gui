using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace TrustTunnelGui
{
    /// <summary>Базовое окно-диалог в стиле приложения: тема, тёмный заголовок, ручное масштабирование.</summary>
    public class ThemedDialog : Form
    {
        public ThemedDialog()
        {
            Font = Theme.UiFont(9.5f);
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            Icon = Icons.Shield(Icons.Accent, 32);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.ApplyTitleBar(this);
        }

        protected override void OnLoad(EventArgs e)
        {
            Theme.Apply(this);
            base.OnLoad(e);
        }

        /// <summary>Нижняя панель с кнопками, справа налево.</summary>
        protected FlowLayoutPanel ButtonBar()
        {
            var bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Height = Ui.S(62),
                Padding = new Padding(Ui.S(16), Ui.S(14), Ui.S(16), Ui.S(12)),
                Tag = "surface",
            };
            bar.Paint += (s, e) => { using (var pen = new Pen(Theme.Current.Border)) e.Graphics.DrawLine(pen, 0, 0, bar.Width, 0); };
            return bar;
        }

        protected static FlatButton DialogButton(string text, ButtonKind kind)
        {
            var b = new FlatButton { Text = text, Kind = kind, Margin = new Padding(Ui.S(8), 0, 0, 0) };
            b.Size = new Size(Math.Max(Ui.S(100), b.GetPreferredSize(Size.Empty).Width), Ui.S(34));
            return b;
        }
    }

    /// <summary>Редактор сервера (профиля подключения).</summary>
    public class ProfileForm : ThemedDialog
    {
        public Profile Result { get; private set; }
        readonly Profile src;

        TextBox txtName, txtHost, txtSni, txtAddresses, txtUser, txtPass, txtCert, txtClientRandom, txtAuthKey;
        ComboBox cmbProtocol, cmbTls, cmbMode, cmbListener, cmbLog;
        CheckBox chkAntiDpi, chkIpv6, chkSkipVerify, chkShowPass, chkKill, chkSysDns, chkPq;
        TextBox txtExclusions, txtKillPorts, txtDns, txtDevice, txtIncluded, txtExcluded, txtSocksAddr, txtSocksUser, txtSocksPass, txtExtra;
        NumericUpDown numMtu;
        Control[] tunControls, socksControls;

        readonly List<FlatButton> navButtons = new List<FlatButton>();
        readonly List<Panel> pages = new List<Panel>();

        public ProfileForm(Profile profile, string title) : this(profile, title, null) { }

        public ProfileForm(Profile profile, string title, string subtitle)
        {
            src = profile;
            Text = title;
            Size = new Size(Ui.S(820), Ui.S(640));
            MinimumSize = new Size(Ui.S(640), Ui.S(480));

            // заголовок
            var header = new Panel { Dock = DockStyle.Top, Height = Ui.S(subtitle == null ? 60 : 74), Tag = "surface" };
            var lblTitle = new Label { Text = title, AutoSize = true, Font = Theme.SemiboldFont(14f), Location = new Point(Ui.S(20), Ui.S(14)) };
            header.Controls.Add(lblTitle);
            if (subtitle != null)
                header.Controls.Add(new Label { Text = subtitle, AutoSize = true, Tag = "muted", Location = new Point(Ui.S(22), Ui.S(46)) });
            header.Paint += (s, e) => { using (var pen = new Pen(Theme.Current.Border)) e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1); };

            // навигация
            var nav = new Panel { Dock = DockStyle.Left, Width = Ui.S(200), Tag = "surface", Padding = new Padding(Ui.S(10), Ui.S(12), Ui.S(10), 0) };
            nav.Paint += (s, e) => { using (var pen = new Pen(Theme.Current.Border)) e.Graphics.DrawLine(pen, nav.Width - 1, 0, nav.Width - 1, nav.Height); };

            var host = new Panel { Dock = DockStyle.Fill, Tag = "bg" };
            AddPage(nav, host, "Сервер", Glyphs.Globe, BuildServerPage());
            AddPage(nav, host, "Маршруты и DNS", Glyphs.Link, BuildRoutingPage());
            AddPage(nav, host, "Тип подключения", Glyphs.Shield, BuildInterfacePage());
            AddPage(nav, host, "Дополнительно", Glyphs.Settings, BuildAdvancedPage());
            ShowPage(0);

            // кнопки
            var bar = ButtonBar();
            var ok = DialogButton("Сохранить", ButtonKind.Primary);
            var cancel = DialogButton("Отмена", ButtonKind.Secondary);
            cancel.DialogResult = DialogResult.Cancel;
            var preview = DialogButton("Показать конфиг", ButtonKind.Ghost);
            ok.Click += (s, e) => OnSave();
            preview.Click += (s, e) => ShowToml();
            bar.Controls.Add(ok);
            bar.Controls.Add(cancel);
            bar.Controls.Add(preview);
            preview.Margin = new Padding(Ui.S(8), 0, Ui.S(40), 0);
            AcceptButton = ok;
            CancelButton = cancel;

            Controls.Add(host);
            Controls.Add(nav);
            Controls.Add(bar);
            Controls.Add(header);
            LoadFrom(profile);
        }

        void AddPage(Panel nav, Panel host, string title, string glyph, TableLayoutPanel grid)
        {
            int index = pages.Count;
            var b = new FlatButton { Kind = ButtonKind.Nav, Text = title, Glyph = glyph, Dock = DockStyle.Top, Height = Ui.S(38) };
            b.Click += (s, e) => ShowPage(index);
            var spacer = new Panel { Dock = DockStyle.Top, Height = Ui.S(4) };
            nav.Controls.Add(spacer);
            nav.Controls.Add(b);
            spacer.BringToFront();
            b.BringToFront();
            navButtons.Add(b);

            var page = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Visible = false, Padding = new Padding(Ui.S(8), Ui.S(6), Ui.S(8), Ui.S(8)) };
            page.Controls.Add(grid);
            host.Controls.Add(page);
            pages.Add(page);
        }

        void ShowPage(int i)
        {
            for (int k = 0; k < pages.Count; k++)
            {
                pages[k].Visible = k == i;
                navButtons[k].Checked = k == i;
            }
        }

        // ---------- построение страниц ----------

        static TableLayoutPanel Grid(string title, string description)
        {
            var g = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 2,
                Padding = new Padding(Ui.S(12), Ui.S(8), Ui.S(18), Ui.S(12)),
            };
            g.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Ui.S(190)));
            g.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var t = new Label { Text = title, AutoSize = true, Font = Theme.SemiboldFont(13f), Margin = new Padding(Ui.S(2), Ui.S(4), 0, Ui.S(2)) };
            g.Controls.Add(t);
            g.SetColumnSpan(t, 2);
            if (description != null)
            {
                var d = new Label { Text = description, AutoSize = true, Tag = "muted", MaximumSize = new Size(Ui.S(520), 0), Margin = new Padding(Ui.S(3), 0, 0, Ui.S(10)) };
                g.Controls.Add(d);
                g.SetColumnSpan(d, 2);
            }
            return g;
        }

        static Label AddRow(TableLayoutPanel g, string label, Control c, string hint = null)
        {
            var l = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Margin = new Padding(Ui.S(3), Ui.S(9), Ui.S(3), Ui.S(3)) };
            if (!(c is NumericUpDown)) c.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            c.Margin = new Padding(Ui.S(3), Ui.S(5), Ui.S(3), hint == null ? Ui.S(5) : 0);
            if (c is TextBox && !((TextBox)c).Multiline) c.Height = Ui.S(26);
            g.Controls.Add(l);
            g.Controls.Add(c);
            if (hint != null)
            {
                g.Controls.Add(new Label());
                g.Controls.Add(new Label
                {
                    Text = hint,
                    AutoSize = true,
                    Tag = "muted",
                    Font = Theme.UiFont(8.5f),
                    Margin = new Padding(Ui.S(3), Ui.S(3), Ui.S(3), Ui.S(8)),
                    MaximumSize = new Size(Ui.S(400), 0),
                });
            }
            return l;
        }

        static void AddCheck(TableLayoutPanel g, CheckBox c, string hint = null)
        {
            c.AutoSize = true;
            c.Margin = new Padding(Ui.S(3), Ui.S(6), Ui.S(3), hint == null ? Ui.S(4) : 0);
            g.Controls.Add(new Label());
            g.Controls.Add(c);
            if (hint != null)
            {
                g.Controls.Add(new Label());
                g.Controls.Add(new Label { Text = hint, AutoSize = true, Tag = "muted", Font = Theme.UiFont(8.5f), Margin = new Padding(Ui.S(21), Ui.S(1), Ui.S(3), Ui.S(8)), MaximumSize = new Size(Ui.S(400), 0) });
            }
        }

        static void AddHeader(TableLayoutPanel g, string text)
        {
            var l = new Label { Text = text, AutoSize = true, Font = Theme.SemiboldFont(10f), Margin = new Padding(Ui.S(3), Ui.S(18), Ui.S(3), Ui.S(4)) };
            g.Controls.Add(l);
            g.SetColumnSpan(l, 2);
        }

        static TextBox Multi(int lines)
        {
            return new TextBox
            {
                AutoSize = false,
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                Height = Ui.S(10 + lines * 17),
                AcceptsReturn = true,
                WordWrap = false,
                Font = new Font("Consolas", 9.5f),
            };
        }

        static ComboBox Combo(params string[] items)
        {
            var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            c.Items.AddRange(items);
            return c;
        }

        TableLayoutPanel BuildServerPage()
        {
            var g = Grid("Сервер", "Эти данные выдаёт администратор VPN-сервера. При импорте ссылки tt:// они заполняются автоматически.");
            AddRow(g, "Название", txtName = new TextBox(), "Как сервер будет называться в списке");
            AddRow(g, "Адреса сервера", txtAddresses = Multi(2), "IP:порт или домен:порт, по одному на строку");
            AddRow(g, "Имя хоста (TLS)", txtHost = new TextBox());
            AddRow(g, "Пользователь", txtUser = new TextBox());
            AddRow(g, "Пароль", txtPass = new TextBox { UseSystemPasswordChar = true });
            chkShowPass = new CheckBox { Text = "Показать пароль" };
            chkShowPass.CheckedChanged += (s, e) => txtPass.UseSystemPasswordChar = !chkShowPass.Checked;
            AddCheck(g, chkShowPass);

            AddHeader(g, "Протокол и маскировка");
            AddRow(g, "Протокол", cmbProtocol = Combo("HTTP/2 — через TCP, работает везде", "HTTP/3 — через QUIC/UDP, быстрее"));
            AddCheck(g, chkAntiDpi = new CheckBox { Text = "Защита от блокировок (anti-DPI)" }, "Включите, если провайдер замедляет или блокирует соединение");
            AddRow(g, "TLS-отпечаток", cmbTls = Combo(new[] { "по умолчанию" }.Concat(ConfigBuilder.TlsProfiles).ToArray()),
                "Под какой браузер маскировать соединение. Нужна версия клиента 1.3+");
            AddRow(g, "Свой SNI", txtSni = new TextBox(), "Необязательно: имя сервера в TLS, отличное от имени хоста");
            AddCheck(g, chkIpv6 = new CheckBox { Text = "Пропускать IPv6 через VPN" });

            AddHeader(g, "Сертификат сервера");
            AddRow(g, "Сертификат (PEM)", txtCert = Multi(4), "Пусто — проверка через сертификаты Windows. Для самоподписанного сертификата он приходит в ссылке автоматически.");
            AddCheck(g, chkSkipVerify = new CheckBox { Text = "Не проверять сертификат (небезопасно)" });
            AddRow(g, "client_random", txtClientRandom = new TextBox(), "Необязательно: prefix[/mask] в hex");
            AddRow(g, "client_random_auth_key", txtAuthKey = new TextBox(), "Необязательно, нужна версия клиента 1.3+");
            return g;
        }

        TableLayoutPanel BuildRoutingPage()
        {
            var g = Grid("Маршруты и DNS", "Что пускать через VPN, а что — напрямую.");
            AddRow(g, "Режим", cmbMode = Combo("Всё через VPN, кроме исключений", "Через VPN только исключения"));
            AddRow(g, "Свои исключения", txtExclusions = Multi(7),
                "По одному на строку: example.com (вместе с поддоменами), *.example.com, 1.2.3.4, 10.0.0.0/8.\n" +
                "Готовые сервисы (Discord, YouTube…), приложения и локальные сети — в окне «Исключения» на главном экране.");
            AddHeader(g, "Защита от утечек");
            AddCheck(g, chkKill = new CheckBox { Text = "Kill switch — блокировать интернет, если VPN оборвался" });
            AddRow(g, "Разрешённые порты", txtKillPorts = new TextBox(), "Входящие локальные порты, которые остаются доступны при kill switch, через запятую");
            AddHeader(g, "DNS");
            AddRow(g, "DNS-серверы", txtDns = Multi(3),
                "По одному на строку: 8.8.8.8:53, tls://1.1.1.1, https://dns.adguard-dns.com/dns-query.\nПусто — AdGuard DNS без фильтрации.");
            return g;
        }

        TableLayoutPanel BuildInterfacePage()
        {
            var g = Grid("Тип подключения", "TUN защищает весь трафик компьютера. SOCKS5 — только программы, где вы сами укажете прокси.");
            AddRow(g, "Тип", cmbListener = Combo("TUN — весь трафик компьютера", "SOCKS5-прокси — только выбранные программы"));
            cmbListener.SelectedIndexChanged += (s, e) => UpdateListenerUi();

            AddHeader(g, "TUN");
            var l1 = AddRow(g, "Имя адаптера", txtDevice = new TextBox(), "Как VPN-адаптер будет называться в Windows");
            var l2 = AddRow(g, "MTU", numMtu = new NumericUpDown { Minimum = 0, Maximum = 9000, Width = Ui.S(110) }, "0 — по умолчанию (1350)");
            AddCheck(g, chkSysDns = new CheckBox { Text = "Менять системные DNS на время подключения" });
            var l3 = AddRow(g, "Через VPN", txtIncluded = Multi(2), "Сети в формате CIDR. Пусто — весь интернет (0.0.0.0/0 и 2000::/3)");
            var l4 = AddRow(g, "Мимо VPN", txtExcluded = Multi(2), "Пусто — локальная сеть и служебные адреса");

            AddHeader(g, "SOCKS5");
            var l5 = AddRow(g, "Адрес прокси", txtSocksAddr = new TextBox());
            var l6 = AddRow(g, "Пользователь", txtSocksUser = new TextBox(), "Необязательно");
            var l7 = AddRow(g, "Пароль", txtSocksPass = new TextBox { UseSystemPasswordChar = true });

            tunControls = new Control[] { l1, l2, l3, l4, txtDevice, numMtu, chkSysDns, txtIncluded, txtExcluded };
            socksControls = new Control[] { l5, l6, l7, txtSocksAddr, txtSocksUser, txtSocksPass };
            return g;
        }

        TableLayoutPanel BuildAdvancedPage()
        {
            var g = Grid("Дополнительно", "Обычно здесь ничего менять не нужно.");
            AddRow(g, "Подробность журнала", cmbLog = Combo("info — обычный", "debug — подробный", "trace — максимальный"));
            AddCheck(g, chkPq = new CheckBox { Text = "Постквантовый обмен ключами в TLS" });
            AddRow(g, "Свои параметры TOML", txtExtra = Multi(6),
                "Строки, которые будут вставлены в конфиг как есть — для параметров, которых нет в интерфейсе.");
            return g;
        }

        void UpdateListenerUi()
        {
            bool tun = cmbListener.SelectedIndex == 0;
            foreach (var c in tunControls) c.Enabled = tun;
            foreach (var c in socksControls) c.Enabled = !tun;
        }

        // ---------- данные ----------

        static readonly string[] LogLevels = { "info", "debug", "trace" };

        static string Join(List<string> l) { return string.Join("\r\n", l); }

        static List<string> Lines(TextBox t)
        {
            return t.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        }

        void LoadFrom(Profile p)
        {
            txtName.Text = p.Name;
            txtHost.Text = p.Hostname;
            txtSni.Text = p.CustomSni;
            txtAddresses.Text = Join(p.Addresses);
            txtUser.Text = p.Username;
            txtPass.Text = p.Password;
            cmbProtocol.SelectedIndex = p.UpstreamProtocol == "http3" ? 1 : 0;
            int tls = Array.IndexOf(ConfigBuilder.TlsProfiles, p.TlsProfile);
            cmbTls.SelectedIndex = tls < 0 ? 0 : tls + 1;
            chkAntiDpi.Checked = p.AntiDpi;
            chkIpv6.Checked = p.HasIpv6;
            chkSkipVerify.Checked = p.SkipVerification;
            txtCert.Text = (p.Certificate ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n");
            txtClientRandom.Text = p.ClientRandom;
            txtAuthKey.Text = p.ClientRandomAuthKey;

            cmbMode.SelectedIndex = p.VpnMode == "selective" ? 1 : 0;
            txtExclusions.Text = Join(p.Exclusions);
            chkKill.Checked = p.KillSwitch;
            txtKillPorts.Text = p.KillSwitchAllowPorts;
            txtDns.Text = Join(p.DnsUpstreams);

            cmbListener.SelectedIndex = p.IsTun ? 0 : 1;
            txtDevice.Text = p.DeviceName;
            numMtu.Value = Math.Max(0, Math.Min(9000, p.MtuSize));
            chkSysDns.Checked = p.ChangeSystemDns;
            txtIncluded.Text = Join(p.IncludedRoutes);
            txtExcluded.Text = Join(p.ExcludedRoutes);
            txtSocksAddr.Text = p.SocksAddress;
            txtSocksUser.Text = p.SocksUsername;
            txtSocksPass.Text = p.SocksPassword;

            int li = Array.IndexOf(LogLevels, p.LogLevel);
            cmbLog.SelectedIndex = li < 0 ? 0 : li;
            chkPq.Checked = p.PostQuantum;
            txtExtra.Text = (p.ExtraToml ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n");
            UpdateListenerUi();
        }

        Profile Collect()
        {
            var p = src.Clone();
            p.Name = txtName.Text.Trim();
            p.Hostname = txtHost.Text.Trim();
            p.CustomSni = txtSni.Text.Trim();
            p.Addresses = Lines(txtAddresses);
            p.Username = txtUser.Text.Trim();
            p.Password = txtPass.Text;
            p.UpstreamProtocol = cmbProtocol.SelectedIndex == 1 ? "http3" : "http2";
            p.TlsProfile = cmbTls.SelectedIndex <= 0 ? "" : ConfigBuilder.TlsProfiles[cmbTls.SelectedIndex - 1];
            p.AntiDpi = chkAntiDpi.Checked;
            p.HasIpv6 = chkIpv6.Checked;
            p.SkipVerification = chkSkipVerify.Checked;
            p.Certificate = txtCert.Text.Trim();
            p.ClientRandom = txtClientRandom.Text.Trim();
            p.ClientRandomAuthKey = txtAuthKey.Text.Trim();

            p.VpnMode = cmbMode.SelectedIndex == 1 ? "selective" : "general";
            p.Exclusions = Lines(txtExclusions);
            p.KillSwitch = chkKill.Checked;
            p.KillSwitchAllowPorts = txtKillPorts.Text.Trim();
            p.DnsUpstreams = Lines(txtDns);

            p.ListenerType = cmbListener.SelectedIndex == 1 ? "socks" : "tun";
            p.DeviceName = txtDevice.Text.Trim();
            p.MtuSize = (int)numMtu.Value;
            p.ChangeSystemDns = chkSysDns.Checked;
            p.IncludedRoutes = Lines(txtIncluded);
            p.ExcludedRoutes = Lines(txtExcluded);
            p.SocksAddress = txtSocksAddr.Text.Trim();
            p.SocksUsername = txtSocksUser.Text.Trim();
            p.SocksPassword = txtSocksPass.Text;

            p.LogLevel = LogLevels[Math.Max(0, cmbLog.SelectedIndex)];
            p.PostQuantum = chkPq.Checked;
            p.ExtraToml = txtExtra.Text.Trim();
            return p;
        }

        void OnSave()
        {
            var p = Collect();
            string err = null;
            int page = 0;
            if (p.Addresses.Count == 0) err = "Укажите хотя бы один адрес сервера, например 1.2.3.4:443.";
            else if (p.Addresses.Any(a => a.LastIndexOf(':') <= 0)) err = "В адресе сервера нужен порт, например 1.2.3.4:443.";
            else if (p.Hostname.Length == 0) err = "Укажите имя хоста сервера (TLS).";
            else if (p.Username.Length == 0 || p.Password.Length == 0) err = "Укажите имя пользователя и пароль.";
            else if (p.Certificate.Length > 0 && !p.Certificate.Contains("BEGIN CERTIFICATE")) err = "Сертификат должен быть в формате PEM (-----BEGIN CERTIFICATE-----).";
            else if (!p.IsTun && p.SocksAddress.LastIndexOf(':') <= 0) { err = "Укажите адрес SOCKS5-прокси, например 127.0.0.1:1080."; page = 2; }
            if (err != null)
            {
                ShowPage(page);
                MessageBox.Show(this, err, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (p.Name.Length == 0) p.Name = p.Hostname;
            Result = p;
            DialogResult = DialogResult.OK;
            Close();
        }

        void ShowToml()
        {
            TextViewer.Show(this, "Конфиг trusttunnel_client.toml", ConfigBuilder.Build(Collect()));
        }
    }

    /// <summary>Окно для просмотра и копирования текста.</summary>
    public static class TextViewer
    {
        public static void Show(IWin32Window owner, string title, string text)
        {
            using (var f = new ViewerForm(title, text)) f.ShowDialog(owner);
        }

        class ViewerForm : ThemedDialog
        {
            public ViewerForm(string title, string text)
            {
                Text = title;
                Size = new Size(Ui.S(680), Ui.S(540));
                var wrap = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Ui.S(14)), Tag = "bg" };
                var tb = new TextBox
                {
                    Multiline = true,
                    ReadOnly = true,
                    ScrollBars = ScrollBars.Both,
                    WordWrap = false,
                    Dock = DockStyle.Fill,
                    Font = new Font("Consolas", 9.5f),
                    Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n"),
                };
                wrap.Controls.Add(tb);
                var bar = ButtonBar();
                var close = DialogButton("Закрыть", ButtonKind.Primary);
                close.DialogResult = DialogResult.OK;
                var copy = DialogButton("Копировать", ButtonKind.Secondary);
                copy.Click += (s, e) => { Clipboard.SetText(text); copy.Text = "Скопировано"; };
                bar.Controls.Add(close);
                bar.Controls.Add(copy);
                Controls.Add(wrap);
                Controls.Add(bar);
                AcceptButton = close;
                CancelButton = close;
                Shown += (s, e) => tb.Select(0, 0);
            }
        }
    }

    /// <summary>Диалог ввода одной строки (для ссылки tt://).</summary>
    public static class InputBox
    {
        public static string Show(IWin32Window owner, string title, string prompt, string initial)
        {
            using (var f = new InputForm(title, prompt, initial))
                return f.ShowDialog(owner) == DialogResult.OK ? f.Value : null;
        }

        class InputForm : ThemedDialog
        {
            readonly TextBox tb;
            public string Value { get { return tb.Text.Trim(); } }

            public InputForm(string title, string prompt, string initial)
            {
                Text = title;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                ClientSize = new Size(Ui.S(560), Ui.S(250));
                var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Ui.S(20), Ui.S(16), Ui.S(20), Ui.S(12)), Tag = "bg" };
                var lblT = new Label { Text = title, AutoSize = true, Font = Theme.SemiboldFont(13f), Dock = DockStyle.Top };
                var lbl = new Label { Text = prompt, AutoSize = true, Tag = "muted", Dock = DockStyle.Top, Padding = new Padding(0, Ui.S(4), 0, Ui.S(10)) };
                tb = new TextBox
                {
                    Dock = DockStyle.Fill,
                    Multiline = true,
                    ScrollBars = ScrollBars.Vertical,
                    Font = new Font("Consolas", 9.5f),
                    Text = initial ?? "",
                };
                body.Controls.Add(tb);
                body.Controls.Add(lbl);
                body.Controls.Add(lblT);
                var bar = ButtonBar();
                var ok = DialogButton("Добавить", ButtonKind.Primary);
                ok.DialogResult = DialogResult.OK;
                var cancel = DialogButton("Отмена", ButtonKind.Secondary);
                cancel.DialogResult = DialogResult.Cancel;
                bar.Controls.Add(ok);
                bar.Controls.Add(cancel);
                Controls.Add(body);
                Controls.Add(bar);
                AcceptButton = ok;
                CancelButton = cancel;
            }
        }
    }
}
