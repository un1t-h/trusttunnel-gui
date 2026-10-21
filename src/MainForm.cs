using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TrustTunnelGui
{
    public class MainForm : Form
    {
        internal readonly AppData data;
        readonly VpnController vpn = new VpnController();
        readonly Timer timer = new Timer { Interval = 500 };
        readonly ToolTip tips = new ToolTip();
        readonly bool startHidden;
        readonly bool isAdmin;
        bool exiting, trayHintShown, busyInstall;
        int installPct = -1;
        VpnState shownState = (VpnState)(-1);
        bool shownWarning;
        string publicIp;
        bool? publicIpMatches;
        bool ipChecking;
        int ipCheckSeq;
        DateTime? reconnectAt;
        int reconnectAttempt;
        Profile reconnectProfile;
        static readonly string LogFile = Path.Combine(AppData.DataDir, "logs", "trusttunnel_gui.log");

        // UI
        Panel topBar, sidebar, content, mainArea, emptyPanel, logPanel;
        ServerList lstProfiles;
        PowerButton power;
        Label lblState, lblSub;
        InfoTile tileIp, tileTime, tileDown, tileUp;
        Banner bnClient, bnAdmin, bnRoute, bnInstall;
        FlatButton btnLog, btnEdit, btnExcl;
        Banner bnDpi;
        CheckBox chkAutoStart;
        bool syncingAutoStart;
        // сторож соединения: периодическая проверка, что сайт за туннелем открывается
        DateTime nextHealthCheck = DateTime.MinValue;
        int healthFails;
        bool healthBusy;
        bool switching; // идёт переключение/переподключение профиля — не возвращать обход DPI в паузе
        Label lblNoServers;
        TextBox txtLog;
        NotifyIcon tray;
        ContextMenuStrip addMenu, moreMenu, listMenu, trayMenu;

        internal string ClientDir
        {
            get
            {
                return string.IsNullOrEmpty(data.Settings.ClientDir)
                    ? Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "client")
                    : data.Settings.ClientDir;
            }
        }

        internal bool ClientInstalled { get { return File.Exists(Path.Combine(ClientDir, VpnController.ClientExe)); } }

        Profile Selected { get { return lstProfiles.SelectedItem as Profile; } }

        bool RouteWarn { get { return vpn.State == VpnState.Connected && vpn.RouteWarning != null; } }

        public MainForm(string[] args)
        {
            data = AppData.Load();
            startHidden = args.Contains("--minimized") || data.Settings.StartMinimized;
            isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
            Theme.Select(data.Settings.ThemeMode);

            Text = "TrustTunnel";
            Font = Theme.UiFont(9.5f);
            AutoScaleMode = AutoScaleMode.None;
            Size = new Size(Ui.S(980), Ui.S(640));
            MinimumSize = new Size(Ui.S(760), Ui.S(520));
            StartPosition = FormStartPosition.CenterScreen;
            Icon = Icons.Shield(Icons.Grey, 32);
            KeyPreview = true;
            DoubleBuffered = true;

            BuildUi();
            BuildTray();
            ApplyTheme();
            RefreshProfiles(data.Settings.LastProfileId);

            Theme.Changed += ApplyTheme;
            SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
            System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
            netChangeTimer.Tick += (s, e) => { netChangeTimer.Stop(); CheckPhysicalInterfaceChanged(); };
            vpn.UnexpectedExit += OnUnexpectedExit;
            timer.Tick += (s, e) => OnTick();
            timer.Start();

            if (startHidden)
            {
                WindowState = FormWindowState.Minimized;
                ShowInTaskbar = false;
            }
            Load += (s, e) => OnStartup(args);
        }

        // =====================================================================
        // Построение интерфейса
        // =====================================================================

        void BuildUi()
        {
            SuspendLayout();

            // ---------- верхняя панель ----------
            topBar = new Panel { Dock = DockStyle.Top, Height = Ui.S(56), Tag = "surface" };
            topBar.Paint += (s, e) =>
            {
                var t = Theme.Current;
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                int sz = Ui.S(28);
                using (var img = Icons.DrawShield(t.ForState(vpn.State == VpnState.Disconnected ? VpnState.Connected : vpn.State), sz))
                    g.DrawImage(img, Ui.S(18), (topBar.Height - sz) / 2);
                using (var f = Theme.SemiboldFont(12.5f))
                    TextRenderer.DrawText(g, "TrustTunnel", f, new Point(Ui.S(54), (topBar.Height - f.Height) / 2), t.Text);
                using (var pen = new Pen(t.Border)) g.DrawLine(pen, 0, topBar.Height - 1, topBar.Width, topBar.Height - 1);
            };
            var topButtons = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                AutoSize = true,
                WrapContents = false,
                Padding = new Padding(0, Ui.S(11), Ui.S(12), 0),
            };
            btnLog = TopButton("Журнал", Glyphs.Log, (s, e) => ToggleLog());
            var btnSettings = TopButton("Настройки", Glyphs.Settings, (s, e) => OpenSettings());
            var btnMore = TopButton("Ещё", Glyphs.More, null);
            btnMore.IconOnly = true;
            btnMore.Click += (s, e) => moreMenu.Show(btnMore, new Point(btnMore.Width - moreMenu.Width, btnMore.Height));
            tips.SetToolTip(btnMore, "Импорт, экспорт и справка");
            topButtons.Controls.AddRange(new Control[] { btnLog, btnSettings, btnMore });
            topBar.Controls.Add(topButtons);

            // ---------- меню ----------
            addMenu = NewMenu();
            addMenu.Items.Add("Вставить ссылку tt://…", null, (s, e) => ImportLinkDialog());
            addMenu.Items.Add("Из буфера обмена\tCtrl+V", null, (s, e) => ImportClipboard());
            addMenu.Items.Add("Открыть файл .toml…", null, (s, e) => ImportFileDialog());
            addMenu.Items.Add(new ToolStripSeparator());
            addMenu.Items.Add("Ввести параметры вручную…", null, (s, e) => NewProfile());

            moreMenu = NewMenu();
            moreMenu.Items.Add("Добавить сервер по ссылке…", null, (s, e) => ImportLinkDialog());
            moreMenu.Items.Add("Добавить из файла .toml…", null, (s, e) => ImportFileDialog());
            moreMenu.Items.Add("Поделиться выбранным сервером (ссылка tt://)", null, (s, e) => ExportLink(Selected));
            moreMenu.Items.Add("Показать конфиг выбранного сервера", null, (s, e) => ShowToml(Selected));
            moreMenu.Items.Add("Исключения выбранного сервера…", null, (s, e) => OpenExclusions(Selected));
            moreMenu.Items.Add(new ToolStripSeparator());
            moreMenu.Items.Add("Проверить внешний IP", null, (s, e) => CheckPublicIp(0));
            moreMenu.Items.Add("Открыть файл журнала", null, (s, e) => OpenLogFile());
            moreMenu.Items.Add(new ToolStripSeparator());
            moreMenu.Items.Add("Сайт TrustTunnel", null, (s, e) => OpenUrl("https://trusttunnel.org/"));
            moreMenu.Items.Add("Как поднять свой сервер", null, (s, e) => OpenUrl("https://github.com/TrustTunnel/TrustTunnel#quick-start"));
            moreMenu.Items.Add(new ToolStripSeparator());
            moreMenu.Items.Add("Выход", null, (s, e) => ExitApp());

            listMenu = NewMenu();
            listMenu.Items.Add("Подключиться", null, (s, e) => { if (vpn.IsRunning) SwitchTo(Selected); else Connect(Selected); });
            listMenu.Items.Add("Изменить…", null, (s, e) => EditProfile(Selected));
            listMenu.Items.Add("Исключения…", null, (s, e) => OpenExclusions(Selected));
            listMenu.Items.Add("Дублировать", null, (s, e) => DuplicateProfile(Selected));
            listMenu.Items.Add("Поделиться (ссылка tt://)", null, (s, e) => ExportLink(Selected));
            listMenu.Items.Add("Показать конфиг", null, (s, e) => ShowToml(Selected));
            listMenu.Items.Add(new ToolStripSeparator());
            listMenu.Items.Add("Удалить", null, (s, e) => DeleteProfile(Selected));
            listMenu.Opening += (s, e) => { if (Selected == null) e.Cancel = true; };

            // ---------- боковая панель ----------
            sidebar = new Panel { Dock = DockStyle.Left, Width = Ui.S(290), Tag = "surface" };
            sidebar.Paint += (s, e) => { using (var pen = new Pen(Theme.Current.Border)) e.Graphics.DrawLine(pen, sidebar.Width - 1, 0, sidebar.Width - 1, sidebar.Height); };

            var sideHead = new Panel { Dock = DockStyle.Top, Height = Ui.S(52) };
            var lblServers = new Label { Text = "Серверы", AutoSize = true, Font = Theme.SemiboldFont(11f), Location = new Point(Ui.S(18), Ui.S(16)) };
            var btnAddSmall = new FlatButton { Kind = ButtonKind.Ghost, Glyph = Glyphs.Add, IconOnly = true, Text = "Добавить" };
            btnAddSmall.Size = new Size(Ui.S(34), Ui.S(34));
            btnAddSmall.Click += (s, e) => addMenu.Show(btnAddSmall, new Point(0, btnAddSmall.Height));
            tips.SetToolTip(btnAddSmall, "Добавить сервер");
            sideHead.Controls.Add(lblServers);
            sideHead.Controls.Add(btnAddSmall);
            sideHead.Resize += (s, e) => btnAddSmall.Location = new Point(sideHead.Width - Ui.S(48), Ui.S(10));

            lblNoServers = new Label
            {
                Text = "Здесь появятся ваши серверы.\r\nДобавьте первый — кнопкой «Добавить» ниже.",
                AutoSize = false,
                Dock = DockStyle.Top,
                Height = Ui.S(60),
                Padding = new Padding(Ui.S(18), Ui.S(6), Ui.S(12), 0),
                Tag = "muted",
                Visible = false,
            };

            lstProfiles = new ServerList { Dock = DockStyle.Fill };
            lstProfiles.StatusOf = p => vpn.IsRunning && vpn.ActiveProfile != null && vpn.ActiveProfile.Id == p.Id ? vpn.State : (VpnState?)null;
            lstProfiles.WarningOf = p => RouteWarn && vpn.ActiveProfile != null && vpn.ActiveProfile.Id == p.Id;
            lstProfiles.ContextMenuStrip = listMenu;
            lstProfiles.DoubleClick += (s, e) => { if (Selected != null && !vpn.IsRunning) Connect(Selected); };
            lstProfiles.SelectedIndexChanged += (s, e) =>
            {
                if (Selected != null && data.Settings.LastProfileId != Selected.Id)
                {
                    data.Settings.LastProfileId = Selected.Id;
                    SaveData();
                }
                UpdateStatusUi();
            };
            lstProfiles.MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                int idx = lstProfiles.IndexFromPoint(e.Location);
                if (idx >= 0) lstProfiles.SelectedIndex = idx;
            };
            lstProfiles.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Delete) DeleteProfile(Selected);
                else if (e.KeyCode == Keys.Enter) ToggleConnect();
                else if (e.KeyCode == Keys.F2) EditProfile(Selected);
            };

            var sideFoot = new Panel { Dock = DockStyle.Bottom, Height = Ui.S(62), Padding = new Padding(Ui.S(14), Ui.S(12), Ui.S(14), Ui.S(14)) };
            btnEdit = new FlatButton { Kind = ButtonKind.Secondary, Glyph = Glyphs.Edit, Text = "Изменить", Dock = DockStyle.Left, Width = Ui.S(126) };
            btnEdit.Click += (s, e) => EditProfile(Selected);
            var btnAdd = new FlatButton { Kind = ButtonKind.Secondary, Glyph = Glyphs.Add, Text = "Добавить", Dock = DockStyle.Right, Width = Ui.S(126) };
            btnAdd.Click += (s, e) => addMenu.Show(btnAdd, new Point(0, -addMenu.PreferredSize.Height));
            sideFoot.Controls.Add(btnEdit);
            sideFoot.Controls.Add(btnAdd);

            sidebar.Controls.Add(lstProfiles);
            sidebar.Controls.Add(lblNoServers);
            sidebar.Controls.Add(sideFoot);
            sidebar.Controls.Add(sideHead);

            // ---------- центральная область ----------
            content = new Panel { Dock = DockStyle.Fill, Tag = "bg" };

            mainArea = new Panel { Dock = DockStyle.Fill, Tag = "bg" };
            power = new PowerButton { Size = new Size(Ui.S(200), Ui.S(200)) };
            power.Click += (s, e) => ToggleConnect();
            lblState = new Label { AutoSize = false, Font = Theme.SemiboldFont(19f), TextAlign = ContentAlignment.MiddleCenter, Height = Ui.S(42) };
            lblSub = new Label { AutoSize = false, Font = Theme.UiFont(10f), TextAlign = ContentAlignment.TopCenter, Height = Ui.S(44), Tag = "muted" };
            tileIp = new InfoTile { Glyph = Glyphs.Globe, Caption = "Внешний IP" };
            tileTime = new InfoTile { Glyph = Glyphs.Timer, Caption = "Время в сети" };
            tileDown = new InfoTile { Glyph = Glyphs.Download, Caption = "Загрузка" };
            tileUp = new InfoTile { Glyph = Glyphs.Upload, Caption = "Отдача" };
            btnExcl = new FlatButton { Kind = ButtonKind.Ghost, Glyph = Glyphs.Filter, Font = Theme.UiFont(9.5f) };
            btnExcl.Click += (s, e) => OpenExclusions(vpn.IsRunning ? vpn.ActiveProfile : Selected);
            tips.SetToolTip(btnExcl, "Что идёт мимо VPN: сервисы, приложения, сайты, локальные сети");
            chkAutoStart = new CheckBox { AutoSize = true, Text = "Запускать с Windows и сразу подключаться к этому серверу", Cursor = Cursors.Hand };
            chkAutoStart.CheckedChanged += (s, e) => { if (!syncingAutoStart) OnAutoStartToggled(); };
            tips.SetToolTip(chkAutoStart, "Программа стартует при входе в Windows, сама подключится и будет держать соединение");
            mainArea.Controls.AddRange(new Control[] { power, lblState, lblSub, btnExcl, chkAutoStart, tileIp, tileTime, tileDown, tileUp });
            mainArea.Layout += (s, e) => LayoutMain();

            emptyPanel = BuildEmptyState();
            mainArea.Controls.Add(emptyPanel);

            logPanel = BuildLogPanel();

            bnInstall = new Banner();
            bnClient = new Banner();
            bnClient.ActionClick += (s, e) => InstallClient();
            bnAdmin = new Banner();
            bnAdmin.ActionClick += (s, e) => RelaunchAsAdmin();
            bnRoute = new Banner();
            bnRoute.ActionClick += (s, e) => { if (!logPanel.Visible) ToggleLog(); };
            bnDpi = new Banner();
            bnDpi.ActionClick += (s, e) => DpiBannerAction();
            foreach (var b in new[] { bnInstall, bnClient, bnAdmin, bnRoute, bnDpi }) b.Visible = false;

            content.Controls.Add(mainArea);
            content.Controls.Add(logPanel);
            content.Controls.Add(bnDpi);
            content.Controls.Add(bnRoute);
            content.Controls.Add(bnAdmin);
            content.Controls.Add(bnClient);
            content.Controls.Add(bnInstall);

            Controls.Add(content);
            Controls.Add(sidebar);
            Controls.Add(topBar);

            AllowDrop = true;
            DragEnter += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Text)) e.Effect = DragDropEffects.Copy; };
            DragDrop += (s, e) => OnDrop(e.Data);
            ResumeLayout(true);
        }

        FlatButton TopButton(string text, string glyph, EventHandler click)
        {
            var b = new FlatButton { Kind = ButtonKind.Ghost, Text = text, Glyph = glyph, Margin = new Padding(Ui.S(4), 0, 0, 0) };
            b.Size = b.GetPreferredSize(Size.Empty);
            if (click != null) b.Click += click;
            return b;
        }

        static ContextMenuStrip NewMenu()
        {
            return new ContextMenuStrip { Renderer = new ThemedMenuRenderer(), Font = Theme.UiFont(9.5f), ShowImageMargin = false };
        }

        Panel BuildEmptyState()
        {
            var p = new Panel { Dock = DockStyle.Fill, Tag = "bg", Visible = false };
            var title = new Label { Text = "Добавьте свой первый сервер", AutoSize = false, Font = Theme.SemiboldFont(17f), TextAlign = ContentAlignment.MiddleCenter, Height = Ui.S(40) };
            var text = new Label
            {
                Text = "Попросите у администратора VPN-сервера ссылку вида tt://… или файл конфигурации.\r\n" +
                       "Скопируйте ссылку и нажмите «Вставить ссылку» — программа сама всё настроит.",
                AutoSize = false,
                Font = Theme.UiFont(10f),
                TextAlign = ContentAlignment.TopCenter,
                Height = Ui.S(48),
                Tag = "muted",
            };
            var bPaste = new FlatButton { Kind = ButtonKind.Primary, Glyph = Glyphs.Link, Text = "Вставить ссылку" };
            bPaste.Click += (s, e) => ImportLinkDialog();
            var bFile = new FlatButton { Kind = ButtonKind.Secondary, Glyph = Glyphs.File, Text = "Открыть файл" };
            bFile.Click += (s, e) => ImportFileDialog();
            var bManual = new FlatButton { Kind = ButtonKind.Ghost, Glyph = Glyphs.Keyboard, Text = "Ввести вручную" };
            bManual.Click += (s, e) => NewProfile();
            var hint = new Label
            {
                Text = "Своего сервера нет? Его можно поднять на любом VPS за несколько минут.",
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Height = Ui.S(24),
                Tag = "muted",
                Cursor = Cursors.Hand,
                Font = Theme.UiFont(9f, FontStyle.Underline),
            };
            hint.Click += (s, e) => OpenUrl("https://github.com/TrustTunnel/TrustTunnel#quick-start");
            p.Controls.AddRange(new Control[] { title, text, bPaste, bFile, bManual, hint });

            int art = Ui.S(96);
            Rectangle artRect = Rectangle.Empty;
            p.Paint += (s, e) =>
            {
                var t = Theme.Current;
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(Color.FromArgb(t.IsDark ? 45 : 24, t.Accent))) g.FillEllipse(b, artRect);
                using (var img = Icons.DrawShield(t.Accent, Ui.S(52)))
                    g.DrawImage(img, artRect.X + (art - Ui.S(52)) / 2, artRect.Y + (art - Ui.S(52)) / 2);
            };
            p.Layout += (s, e) =>
            {
                foreach (var b in new[] { bPaste, bFile, bManual }) b.Size = new Size(b.GetPreferredSize(Size.Empty).Width + Ui.S(8), Ui.S(38));
                int w = p.ClientSize.Width;
                int blockH = art + Ui.S(20) + title.Height + Ui.S(6) + text.Height + Ui.S(20) + Ui.S(38) + Ui.S(26) + hint.Height;
                int y = Math.Max(Ui.S(20), (p.ClientSize.Height - blockH) / 2);
                artRect = new Rectangle((w - art) / 2, y, art, art);
                y += art + Ui.S(20);
                title.SetBounds(Ui.S(20), y, w - Ui.S(40), title.Height); y += title.Height + Ui.S(6);
                text.SetBounds(Ui.S(20), y, w - Ui.S(40), text.Height); y += text.Height + Ui.S(20);
                int gap = Ui.S(10);
                int bw = bPaste.Width + bFile.Width + bManual.Width + gap * 2;
                int x = (w - bw) / 2;
                bPaste.Location = new Point(x, y); x += bPaste.Width + gap;
                bFile.Location = new Point(x, y); x += bFile.Width + gap;
                bManual.Location = new Point(x, y);
                y += Ui.S(38) + Ui.S(26);
                hint.SetBounds(Ui.S(20), y, w - Ui.S(40), hint.Height);
                p.Invalidate();
            };
            return p;
        }

        Panel BuildLogPanel()
        {
            var p = new Panel { Dock = DockStyle.Bottom, Height = Ui.S(230), Tag = "surface", Visible = data.Settings.ShowLog };
            p.Paint += (s, e) => { using (var pen = new Pen(Theme.Current.Border)) e.Graphics.DrawLine(pen, 0, 0, p.Width, 0); };
            var head = new Panel { Dock = DockStyle.Top, Height = Ui.S(44), Padding = new Padding(Ui.S(16), Ui.S(6), Ui.S(10), Ui.S(4)) };
            var title = new Label { Text = "Журнал подключения", AutoSize = true, Font = Theme.SemiboldFont(10f), Location = new Point(Ui.S(16), Ui.S(13)) };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false };
            var bCopy = SmallGhost("Копировать", Glyphs.Copy, (s, e) => { if (txtLog.TextLength > 0) Clipboard.SetText(txtLog.Text); });
            var bClear = SmallGhost("Очистить", Glyphs.Delete, (s, e) => txtLog.Clear());
            var bFile = SmallGhost("Файл", Glyphs.Folder, (s, e) => OpenLogFile());
            var bClose = SmallGhost("Скрыть", Glyphs.Close, (s, e) => ToggleLog());
            bClose.IconOnly = true;
            bClose.Size = new Size(Ui.S(32), Ui.S(32));
            tips.SetToolTip(bClose, "Скрыть журнал");
            buttons.Controls.AddRange(new Control[] { bCopy, bClear, bFile, bClose });
            head.Controls.Add(title);
            head.Controls.Add(buttons);

            txtLog = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                WordWrap = true,
                Font = new Font("Consolas", 9f),
                MaxLength = 0,
                Tag = "log",
            };
            var wrap = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Ui.S(16), 0, Ui.S(4), Ui.S(8)) };
            wrap.Controls.Add(txtLog);
            p.Controls.Add(wrap);
            p.Controls.Add(head);
            return p;
        }

        FlatButton SmallGhost(string text, string glyph, EventHandler click)
        {
            var b = new FlatButton { Kind = ButtonKind.Ghost, Text = text, Glyph = glyph, Font = Theme.UiFont(9f), Margin = new Padding(Ui.S(2), 0, 0, 0) };
            b.Size = new Size(b.GetPreferredSize(Size.Empty).Width, Ui.S(32));
            b.Click += click;
            return b;
        }

        void LayoutMain()
        {
            int w = mainArea.ClientSize.Width, h = mainArea.ClientSize.Height;
            int tileH = Ui.S(70), gap = Ui.S(10);
            int fourW = (w - Ui.S(48) - 3 * gap) / 4;
            int cols = fourW >= Ui.S(138) ? 4 : 2;
            int tileW = cols == 4 ? Math.Min(Ui.S(185), fourW) : Math.Min(Ui.S(220), (w - gap - Ui.S(48)) / 2);
            int rows = 4 / cols;
            int tilesH = rows * tileH + (rows - 1) * gap;
            int textH = Ui.S(6) + lblState.Height + lblSub.Height + (btnExcl.Visible ? Ui.S(36) + Ui.S(28) : 0);
            // Кнопка подстраивается под высоту; если места совсем мало (открыт журнал) — прячем плитки.
            int pb = Math.Min(Ui.S(200), h - textH - Ui.S(18) - tilesH - Ui.S(24));
            bool showTiles = pb >= Ui.S(110);
            if (!showTiles) { tilesH = 0; pb = Math.Max(Ui.S(90), Math.Min(Ui.S(200), h - textH - Ui.S(24))); }
            foreach (var tile in new[] { tileIp, tileTime, tileDown, tileUp }) tile.Visible = showTiles;
            int blockH = pb + textH + (showTiles ? Ui.S(18) + tilesH : 0);
            int y = Math.Max(Ui.S(8), (h - blockH) / 2);

            power.SetBounds((w - pb) / 2, y, pb, pb);
            y += pb + Ui.S(6);
            lblState.SetBounds(Ui.S(24), y, w - Ui.S(48), lblState.Height);
            y += lblState.Height;
            lblSub.SetBounds(Ui.S(24), y, w - Ui.S(48), lblSub.Height);
            y += lblSub.Height;
            if (btnExcl.Visible)
            {
                int bw = Math.Min(w - Ui.S(48), btnExcl.GetPreferredSize(Size.Empty).Width);
                btnExcl.SetBounds((w - bw) / 2, y, bw, Ui.S(32));
                y += Ui.S(36);
                var cs = chkAutoStart.GetPreferredSize(Size.Empty);
                chkAutoStart.Location = new Point((w - cs.Width) / 2, y);
                y += Ui.S(28);
            }
            chkAutoStart.Visible = btnExcl.Visible;
            y += Ui.S(18);

            var tiles = new[] { tileIp, tileTime, tileDown, tileUp };
            int rowW = cols * tileW + (cols - 1) * gap;
            for (int i = 0; i < 4; i++)
            {
                int c = i % cols, r = i / cols;
                tiles[i].SetBounds((w - rowW) / 2 + c * (tileW + gap), y + r * (tileH + gap), tileW, tileH);
            }
        }

        void BuildTray()
        {
            trayMenu = NewMenu();
            trayMenu.Opening += (s, e) => RebuildTrayMenu();
            tray = new NotifyIcon
            {
                Icon = Icons.Shield(Icons.Grey, 16),
                Text = "TrustTunnel",
                Visible = true,
                ContextMenuStrip = trayMenu,
            };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowFromTray(); };
            RebuildTrayMenu();
        }

        void RebuildTrayMenu()
        {
            trayMenu.Items.Clear();
            trayMenu.Items.Add(new ToolStripMenuItem(StateTitle()) { Enabled = false });
            trayMenu.Items.Add(new ToolStripSeparator());
            if (vpn.IsRunning)
                trayMenu.Items.Add("Отключиться", null, (s, e) => { CancelReconnect(); vpn.Disconnect(); UpdateStatusUi(); });
            else
            {
                var prof = Selected;
                var mi = trayMenu.Items.Add(prof == null ? "Подключиться" : "Подключиться к «" + prof.DisplayName + "»", null, (s, e) => Connect(Selected));
                mi.Enabled = prof != null;
            }
            if (data.Profiles.Count > 1)
            {
                var sub = new ToolStripMenuItem("Сервер");
                sub.DropDown.Renderer = new ThemedMenuRenderer();
                foreach (var p in data.Profiles)
                {
                    var pp = p;
                    var item = new ToolStripMenuItem(p.DisplayName) { Checked = vpn.IsRunning ? vpn.ActiveProfile == p : Selected == p };
                    item.Click += (s, e) =>
                    {
                        lstProfiles.SelectedItem = pp;
                        if (vpn.IsRunning) SwitchTo(pp); else Connect(pp);
                    };
                    sub.DropDownItems.Add(item);
                }
                trayMenu.Items.Add(sub);
            }
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("Открыть TrustTunnel", null, (s, e) => ShowFromTray());
            trayMenu.Items.Add("Выход", null, (s, e) => ExitApp());
        }

        // =====================================================================
        // Тема
        // =====================================================================

        void ApplyTheme()
        {
            Theme.Apply(this);
            var t = Theme.Current;
            topBar.BackColor = t.Surface;
            sidebar.BackColor = t.Surface;
            logPanel.BackColor = t.Surface;
            content.BackColor = t.Bg;
            mainArea.BackColor = t.Bg;
            emptyPanel.BackColor = t.Bg;
            lstProfiles.BackColor = t.Surface;
            shownState = (VpnState)(-1);
            UpdateStatusUi();
            Invalidate(true);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.ApplyTitleBar(this);
        }

        void OnSystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category == UserPreferenceCategory.General && data.Settings.ThemeMode == "system")
                BeginInvoke(new Action(() => Theme.Select("system")));
        }

        internal void SetThemeMode(string mode)
        {
            data.Settings.ThemeMode = mode;
            SaveData();
            Theme.Select(mode);
        }

        // =====================================================================
        // Жизненный цикл
        // =====================================================================

        void OnStartup(string[] args)
        {
            if (startHidden) BeginInvoke(new Action(() => { Hide(); ShowInTaskbar = true; WindowState = FormWindowState.Normal; }));
            vpn.Log(Program.AppName + " " + Program.AppVersion + " · клиент: " + ClientDir);
            if (!isAdmin) vpn.Log("⚠ Программа запущена без прав администратора — TUN-режим работать не будет.");
            if (!ClientInstalled) vpn.Log("Компонент trusttunnel_client не найден.");
            KillOrphans(false);
            // если программа в прошлый раз закрылась аварийно, вернём приостановленный обход DPI
            if (!vpn.IsRunning) ResumeDpiBypass();
            HandleArgs(args, false);
            var autoProfile = data.Profiles.FirstOrDefault(x => x.AutoStart);
            if (autoProfile != null && ClientInstalled && !vpn.IsRunning)
            {
                lstProfiles.SelectedItem = autoProfile;
                vpn.Log("Автоподключение к «" + autoProfile.DisplayName + "»");
                Connect(autoProfile, true); // без диалогов: при входе в Windows спрашивать некого
            }
            else if (data.Settings.AutoConnect && Selected != null && ClientInstalled && !vpn.IsRunning) Connect(Selected);
            UpdateBanners();
            UpdateStatusUi();
        }

        public void HandleArgs(string[] args, bool fromOtherInstance)
        {
            foreach (var a in args)
            {
                if (DeepLink.LooksLikeLink(a)) { ShowFromTray(); ImportLink(a); }
                else if (a.EndsWith(".toml", StringComparison.OrdinalIgnoreCase) && File.Exists(a)) { ShowFromTray(); ImportFile(a); }
                else if (a == "--show" && fromOtherInstance) ShowFromTray();
            }
        }

        void ShowFromTray()
        {
            Show();
            ShowInTaskbar = true;
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.V) && !(ActiveControl is TextBox)) { ImportClipboard(); return true; }
            if (keyData == (Keys.Control | Keys.L)) { ToggleLog(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing && data.Settings.CloseToTray)
            {
                e.Cancel = true;
                Hide();
                if (!trayHintShown)
                {
                    trayHintShown = true;
                    tray.ShowBalloonTip(3000, "TrustTunnel", "Программа продолжает работать в трее. Выход — через меню значка.", ToolTipIcon.Info);
                }
                return;
            }
            exiting = true;
            timer.Stop();
            SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
            System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
            if (vpn.IsRunning)
            {
                Cursor = Cursors.WaitCursor;
                vpn.Shutdown(8000);
            }
            ResumeDpiBypass();
            tray.Visible = false;
            tray.Dispose();
            base.OnFormClosing(e);
        }

        void ExitApp()
        {
            exiting = true;
            Close();
        }

        void OnTick()
        {
            vpn.Poll();
            DrainLog();
            if (reconnectAt.HasValue && DateTime.Now >= reconnectAt.Value)
            {
                reconnectAt = null;
                if (!vpn.IsRunning && reconnectProfile != null && data.Profiles.Contains(reconnectProfile))
                {
                    vpn.Log("↻ Переподключение (попытка " + reconnectAttempt + ")");
                    Connect(reconnectProfile, true);
                }
            }
            // VPN выключен и переподключение не ожидается — возвращаем zapret / GoodbyeDPI
            if (!vpn.IsRunning && !reconnectAt.HasValue && !switching && HasPausedDpi) ResumeDpiBypass();
            HealthCheckTick();
            UpdateStatusUi();
        }

        // =====================================================================
        // Совместимость с zapret / GoodbyeDPI
        // =====================================================================

        bool HasPausedDpi { get { return data.Settings.PausedServices.Count > 0 || data.Settings.PausedProcesses.Count > 0; } }

        /// <summary>Перед подключением в режиме TUN: приостановить обход DPI (или предупредить, если это выключено).</summary>
        void PauseDpiBypassIfNeeded(Profile p)
        {
            if (!p.IsTun || HasPausedDpi) return;
            // При разделении маршрутами туннель не видит остальной трафик — обход DPI ему не мешает.
            if (SplitRouting.Networks(p) != null) return;
            // Приостанавливаем и в режиме «через VPN только выбранное»: WinDivert отправляет перехваченные
            // пакеты заново по таблице маршрутов, и прямые соединения клиента на 80/443 уходят обратно в туннель.
            bool selective = p.VpnMode == "selective";
            DpiBypass.Detected found;
            try { found = DpiBypass.Detect(); } catch (Exception ex) { vpn.Log("Проверка zapret/GoodbyeDPI: " + ex.Message); return; }
            if (!found.Any) return;
            if (!data.Settings.PauseDpiBypass)
            {
                vpn.Log(selective
                    ? "⚠ Работает " + found.Describe() + " — в режиме «только выбранное» он ломает прямые соединения на портах 80/443 (сайты не открываются)."
                    : "⚠ Работает " + found.Describe() + " — внутри VPN он искажает трафик (Discord, YouTube могут не работать).");
                return;
            }
            if (selective)
                vpn.Log("Обход DPI приостановлен: он ломает прямые соединения клиента. Сайты, которым нужен zapret (YouTube, Discord), добавьте в «через VPN».");
            var paused = DpiBypass.Pause(found, s => vpn.Log(s));
            data.Settings.PausedServices = paused.Services;
            data.Settings.PausedProcesses = paused.Processes.Select(DpiBypass.Pack).ToList();
            SaveData();
        }

        void ResumeDpiBypass()
        {
            if (!HasPausedDpi) return;
            var services = data.Settings.PausedServices.ToList();
            var procs = data.Settings.PausedProcesses.Select(DpiBypass.Unpack).Where(x => x != null).ToList();
            data.Settings.PausedServices = new List<string>();
            data.Settings.PausedProcesses = new List<string>();
            SaveData();
            try { DpiBypass.Resume(services, procs, s => vpn.Log(s)); }
            catch (Exception ex) { vpn.Log("Не удалось вернуть обход DPI: " + ex.Message); }
            UpdateBanners();
        }

        void DpiBannerAction()
        {
            if (HasPausedDpi)
            {
                MessageBox.Show(this,
                    "zapret и GoodbyeDPI перехватывают пакеты на всех сетевых адаптерах, включая VPN, и подмешивают в них «фальшивые» пакеты.\n\n" +
                    "В интернете такие пакеты отбрасывает сервер, а внутри туннеля их принимает TrustTunnel — соединения Discord, YouTube и других сайтов из списков zapret ломаются.\n\n" +
                    "Пока VPN включён, обход DPI не нужен: провайдер не видит, что идёт внутри туннеля. Поэтому на время подключения TrustTunnel приостанавливает его и запускает снова после отключения.\n\n" +
                    "В режиме «через VPN только выбранное» zapret мешает и прямым соединениям: перехваченные пакеты на портах 80/443 он отправляет заново, и Windows возвращает их в туннель — сайты не открываются.\n\n" +
                    "Учтите: сайты, идущие мимо VPN, в это время работают без обхода DPI. Если YouTube или Discord напрямую не открываются, добавьте их в «через VPN» (окно «Исключения»). Отключить приостановку можно в настройках.",
                    "zapret и VPN", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            data.Settings.PauseDpiBypass = true;
            SaveData();
            if (vpn.IsRunning && vpn.ActiveProfile != null)
            {
                vpn.Log("Приостановка обхода DPI включена — переподключаюсь, чтобы применить");
                Reconnect(vpn.ActiveProfile);
            }
            UpdateBanners();
        }

        void DrainLog()
        {
            string line;
            StringBuilder sb = null;
            int n = 0;
            while (n < 500 && vpn.TryDequeueLog(out line))
            {
                if (sb == null) sb = new StringBuilder();
                sb.Append(DateTime.Now.ToString("HH:mm:ss")).Append("  ").Append(line).Append("\r\n");
                n++;
            }
            if (sb == null) return;
            AppendLogFile(sb.ToString());
            if (txtLog.TextLength > 600000)
            {
                var t = txtLog.Text;
                txtLog.Text = t.Substring(t.Length - 300000);
            }
            txtLog.AppendText(sb.ToString());
        }

        static void AppendLogFile(string text)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFile));
                var fi = new FileInfo(LogFile);
                if (fi.Exists && fi.Length > 2 * 1024 * 1024)
                {
                    File.Copy(LogFile, LogFile + ".1", true);
                    File.Delete(LogFile);
                }
                File.AppendAllText(LogFile, text, Encoding.UTF8);
            }
            catch { }
        }

        void OpenLogFile()
        {
            if (File.Exists(LogFile)) OpenUrl(LogFile);
            else OpenFolder(Path.GetDirectoryName(LogFile));
        }

        void ToggleLog()
        {
            logPanel.Visible = !logPanel.Visible;
            btnLog.Checked = logPanel.Visible;
            data.Settings.ShowLog = logPanel.Visible;
            SaveData();
            if (logPanel.Visible) { txtLog.SelectionStart = txtLog.TextLength; txtLog.ScrollToCaret(); }
        }

        void OnUnexpectedExit()
        {
            var p = vpn.ActiveProfile;
            bool wasUp = vpn.ConnectedSince != default(DateTime);
            if (reconnectAttempt == 0) tray.ShowBalloonTip(3000, "TrustTunnel", vpn.LastError ?? "Соединение разорвано", ToolTipIcon.Warning);
            if ((data.Settings.AutoReconnect || p != null && p.AutoStart) && p != null && !exiting) ScheduleReconnect(p, wasUp);
        }

        /// <summary>
        /// Переподключение без ограничения числа попыток: пауза растёт 3 → 6 → 12 → 24 → 48 → 60 с и дальше
        /// не увеличивается. Появление сети или выход из сна сокращают ожидание до нуля.
        /// </summary>
        void ScheduleReconnect(Profile p, bool wasUp)
        {
            reconnectAttempt++;
            int delay = Math.Min(60, 3 * (1 << Math.Min(reconnectAttempt - 1, 5)));
            reconnectProfile = p;
            reconnectAt = DateTime.Now.AddSeconds(delay);
            vpn.Log("Повторное подключение через " + delay + " с (попытка " + reconnectAttempt + ")" +
                    (wasUp || reconnectAttempt > 1 ? "" : " — если повторяется, проверьте журнал выше"));
        }

        // ---------- сторож соединения ----------

        /// <summary>
        /// Раз в 30 секунд проверяет, что сайт за туннелем открывается (для «только Claude» — claude.ai).
        /// Процесс клиента может быть жив, а соединение с сервером — нет (смена сети, сон, сбой у провайдера):
        /// после трёх неудач подряд переподключаемся.
        /// </summary>
        void HealthCheckTick()
        {
            if (vpn.State != VpnState.Connected || healthBusy || switching || RouteWarn) return;
            if (DateTime.Now < nextHealthCheck) return;
            var p = vpn.ActiveProfile;
            if (p == null || !p.IsTun) return;
            string url = ExclusionCatalog.HealthUrl(p);
            if (url == null) return;
            nextHealthCheck = DateTime.Now.AddSeconds(30);
            healthBusy = true;
            System.Threading.Tasks.Task.Run(() => Probe(url)).ContinueWith(t =>
            {
                healthBusy = false;
                if (vpn.State != VpnState.Connected || vpn.ActiveProfile != p) return;
                if (t.Result)
                {
                    if (healthFails > 0) vpn.Log("✔ Связь через туннель восстановилась");
                    healthFails = 0;
                    return;
                }
                healthFails++;
                vpn.Log("⚠ " + url + " не открывается через туннель (" + healthFails + "/3)");
                nextHealthCheck = DateTime.Now.AddSeconds(10);
                if (healthFails >= 3)
                {
                    healthFails = 0;
                    vpn.Log("↻ Связь через туннель пропала — переподключаюсь");
                    Reconnect(p);
                }
            }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>Любой HTTP-ответ (даже 403) означает, что связь есть; ошибка соединения или таймаут — нет.</summary>
        static bool Probe(string url)
        {
            try
            {
                System.Net.ServicePointManager.SecurityProtocol |= (System.Net.SecurityProtocolType)3072;
                var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
                req.Method = "HEAD";
                req.Timeout = 10000;
                req.AllowAutoRedirect = false;
                req.UserAgent = "TrustTunnelGUI/" + Program.AppVersion;
                using (req.GetResponse()) return true;
            }
            catch (System.Net.WebException ex)
            {
                return ex.Response != null; // сервер ответил ошибкой HTTP — значит, сеть работает
            }
            catch { return false; }
        }

        void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode != PowerModes.Resume) return;
            BeginInvoke(new Action(() =>
            {
                var p = vpn.IsRunning ? vpn.ActiveProfile : reconnectProfile ?? data.Profiles.FirstOrDefault(x => x.AutoStart);
                if (p == null || exiting) return;
                vpn.Log("Компьютер вышел из сна — переподключаюсь через 5 с");
                var t = new Timer { Interval = 5000 };
                t.Tick += (s2, e2) => { t.Stop(); t.Dispose(); if (vpn.IsRunning) Reconnect(vpn.ActiveProfile); else Connect(p, true); };
                t.Start();
            }));
        }

        void OnNetworkAvailabilityChanged(object sender, System.Net.NetworkInformation.NetworkAvailabilityEventArgs e)
        {
            if (!e.IsAvailable) return;
            BeginInvoke(new Action(() =>
            {
                if (reconnectAt.HasValue && !vpn.IsRunning)
                {
                    vpn.Log("Сеть появилась — переподключаюсь сразу");
                    reconnectAt = DateTime.Now;
                }
            }));
        }

        readonly Timer netChangeTimer = new Timer { Interval = 3000 };

        /// <summary>Смена адресов сети (Wi-Fi ↔ кабель, другой роутер) — проверим через 3 с, когда всё устоится.</summary>
        void OnNetworkAddressChanged(object sender, EventArgs e)
        {
            try { BeginInvoke(new Action(() => { netChangeTimer.Stop(); netChangeTimer.Start(); })); } catch { }
        }

        /// <summary>
        /// bound_if задаётся явно, поэтому клиент сам не следит за сменой сети. Если физический адаптер
        /// сменился (например, отключили кабель и работает Wi-Fi) — переподключаемся с новым.
        /// </summary>
        void CheckPhysicalInterfaceChanged()
        {
            if (!vpn.IsRunning || switching || exiting) return;
            var p = vpn.ActiveProfile;
            if (p == null || !p.IsTun || !string.IsNullOrEmpty(p.BoundIf)) return;
            var now = NetInfo.PhysicalInterfaceIndex();
            if (now == null || now == vpn.BoundIf) return;
            var phys = NetInfo.PhysicalInterface();
            vpn.Log("Сменилась сеть — теперь «" + (phys != null ? phys.Name : now) + "». Переподключаюсь с новым адаптером");
            Reconnect(p);
        }

        // ---------- автозапуск с подключением ----------

        void OnAutoStartToggled()
        {
            var p = vpn.IsRunning ? vpn.ActiveProfile : Selected;
            if (p == null) return;
            bool on = chkAutoStart.Checked;
            foreach (var x in data.Profiles) x.AutoStart = on && x.Id == p.Id;
            p.AutoStart = on;
            if (on)
            {
                if (!SetAutostart(true))
                {
                    p.AutoStart = false;
                    foreach (var x in data.Profiles) x.AutoStart = false;
                    syncingAutoStart = true; chkAutoStart.Checked = false; syncingAutoStart = false;
                    return;
                }
                data.Settings.AutoReconnect = true;
                vpn.Log("«" + p.DisplayName + "»: запуск с Windows и автоподключение включены, соединение будет восстанавливаться само");
                if (p.VpnMode != "selective")
                    vpn.Log("Подсказка: чтобы через VPN шёл только Claude, откройте «Исключения» → «Быстрая настройка: через VPN только Claude и Anthropic».");
                if (!vpn.IsRunning) Connect(p);
            }
            else
            {
                vpn.Log("«" + p.DisplayName + "»: автоподключение при входе в Windows выключено");
                if (AutostartEnabled() && MessageBox.Show(this, "Убрать TrustTunnel из автозапуска Windows?", "TrustTunnel",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    SetAutostart(false);
            }
            SaveData();
            lstProfiles.Invalidate();
        }

        // =====================================================================
        // Подключение
        // =====================================================================

        void ToggleConnect()
        {
            if (reconnectAt.HasValue && !vpn.IsRunning)
            {
                CancelReconnect();
                vpn.Log("Автопереподключение отменено");
                UpdateStatusUi();
                return;
            }
            if (vpn.IsRunning) { CancelReconnect(); vpn.Disconnect(); }
            else Connect(Selected);
            UpdateStatusUi();
        }

        void CancelReconnect()
        {
            reconnectAt = null;
            reconnectAttempt = 0;
            reconnectProfile = null;
        }

        void Connect(Profile p, bool isRetry = false)
        {
            if (p == null)
            {
                if (data.Profiles.Count == 0) addMenu.Show(power, new Point(power.Width / 2, power.Height / 2));
                return;
            }
            if (vpn.IsRunning) return;
            if (!isRetry) CancelReconnect();
            if (!ClientInstalled)
            {
                if (MessageBox.Show(this, "Для подключения нужен компонент trusttunnel_client (официальный клиент TrustTunnel).\n\nСкачать его сейчас с GitHub?",
                        "TrustTunnel", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    InstallClient();
                return;
            }
            if (p.IsTun && !File.Exists(Path.Combine(ClientDir, "wintun.dll")))
                vpn.Log("⚠ Рядом с клиентом нет wintun.dll — TUN-режим, скорее всего, не запустится. Переустановите компонент в настройках.");
            if (p.IsTun)
            {
                var others = VpnController.OtherActiveVpns(string.IsNullOrEmpty(p.DeviceName) ? "TrustTunnel" : p.DeviceName);
                if (others.Count > 0)
                {
                    vpn.Log("⚠ Активен другой VPN: " + string.Join(", ", others));
                    if (!isRetry && MessageBox.Show(this,
                            "Сейчас включён другой VPN:\n\n" + string.Join("\n", others) +
                            "\n\nДва VPN одновременно спорят за маршрут, и трафик может пойти мимо TrustTunnel. " +
                            "Лучше сначала отключить другой VPN.\n\nВсё равно подключиться?",
                            "TrustTunnel", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                        return;
                }
            }
            PauseDpiBypassIfNeeded(p);
            try
            {
                vpn.Connect(p, ClientDir);
                lstProfiles.Invalidate();
            }
            catch (Exception ex)
            {
                vpn.Log("Ошибка запуска: " + ex.Message);
                if (!isRetry) MessageBox.Show(this, ex.Message, "TrustTunnel", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            UpdateStatusUi();
        }

        void SwitchTo(Profile p)
        {
            if (p == null || vpn.ActiveProfile == p) return;
            Reconnect(p);
        }

        /// <summary>Отключиться и подключиться к профилю заново (другому или изменённому).</summary>
        void Reconnect(Profile p)
        {
            if (p == null) return;
            switching = true;
            CancelReconnect();
            vpn.Disconnect();
            var t = new Timer { Interval = 300 };
            DateTime started = DateTime.Now;
            t.Tick += (s, e) =>
            {
                if (!vpn.IsRunning) { t.Stop(); t.Dispose(); Connect(p); switching = false; }
                else if ((DateTime.Now - started).TotalSeconds > 15) { t.Stop(); t.Dispose(); switching = false; }
            };
            t.Start();
        }

        /// <summary>Окно «Исключения» для профиля; при подключении к нему предлагает сразу применить изменения.</summary>
        void OpenExclusions(Profile p)
        {
            if (p == null) { ImportLinkDialog(); return; }
            using (var f = new ExclusionsForm(p, NetInfo.HomeSubnets()))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                int idx = data.Profiles.FindIndex(x => x.Id == p.Id);
                if (idx < 0) return;
                data.Profiles[idx] = f.Result;
                SaveData();
                RefreshProfiles(f.Result.Id);
                vpn.Log("Исключения «" + f.Result.DisplayName + "»: " + ExclusionCatalog.Summary(f.Result) +
                        ", " + LanRouting.Describe(f.Result, NetInfo.HomeSubnets()));
                if (vpn.IsRunning && vpn.ActiveProfile != null && vpn.ActiveProfile.Id == p.Id &&
                    MessageBox.Show(this, "Применить изменения сейчас? VPN переподключится на пару секунд.", "Исключения",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    Reconnect(f.Result);
            }
        }

        string StateTitle()
        {
            if (RouteWarn) return "Трафик идёт мимо VPN";
            switch (vpn.State)
            {
                case VpnState.Connecting: return "Подключение…";
                case VpnState.Connected: return "Подключено";
                case VpnState.Disconnecting: return "Отключение…";
                case VpnState.Error: return "Не удалось подключиться";
                default: return "Не подключено";
            }
        }

        static string FormatBytes(double b)
        {
            string[] u = { "Б", "КБ", "МБ", "ГБ", "ТБ" };
            int i = 0;
            while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
            return (i == 0 ? b.ToString("0") : b.ToString("0.0")) + " " + u[i];
        }

        void UpdateStatusUi()
        {
            if (lstProfiles == null) return;
            var t = Theme.Current;
            var st = vpn.State;
            bool warn = RouteWarn;
            bool noProfiles = data.Profiles.Count == 0;

            emptyPanel.Visible = noProfiles && !vpn.IsRunning;
            if (emptyPanel.Visible) emptyPanel.BringToFront();
            lblNoServers.Visible = noProfiles;
            btnEdit.Enabled = Selected != null;

            if (st != shownState || warn != shownWarning)
            {
                bool becameConnected = st == VpnState.Connected && shownState != VpnState.Connected;
                shownState = st;
                shownWarning = warn;
                power.SetState(st, warn);
                var c = warn ? t.Orange : t.ForState(st);
                Icon = Icons.Shield(warn ? Icons.Orange : Icons.ForState(st), 32);
                tray.Icon = Icons.Shield(warn ? Icons.Orange : Icons.ForState(st), 16);
                lblState.ForeColor = st == VpnState.Disconnected ? t.Text : c;
                lstProfiles.Invalidate();
                topBar.Invalidate();
                if (st != VpnState.Connected) { publicIp = null; publicIpMatches = null; }
                if (becameConnected)
                {
                    reconnectAttempt = 0;
                    healthFails = 0;
                    nextHealthCheck = DateTime.Now.AddSeconds(20); // первая проверка — когда всё устоится
                    if (!Visible) tray.ShowBalloonTip(2000, "TrustTunnel", "Подключено: " + vpn.ActiveProfile.DisplayName, ToolTipIcon.Info);
                    CheckPublicIp(2000);
                }
                if (warn)
                {
                    tray.ShowBalloonTip(4000, "TrustTunnel", vpn.RouteWarning + ". Отключите другой VPN.", ToolTipIcon.Warning);
                    CheckPublicIp(0);
                }
                UpdateBanners();
            }

            lblState.Text = reconnectAt.HasValue && !vpn.IsRunning ? "Соединение потеряно" : StateTitle();

            var p = vpn.IsRunning ? vpn.ActiveProfile : Selected;
            string name = p == null ? "" : "«" + p.DisplayName + "»";
            string sub;
            if (reconnectAt.HasValue && !vpn.IsRunning)
                sub = "Повторная попытка через " + Math.Max(0, (int)Math.Ceiling((reconnectAt.Value - DateTime.Now).TotalSeconds)) +
                      " с. Нажмите кнопку, чтобы отменить.";
            else if (warn) sub = vpn.RouteWarning + ".\r\nОтключите другой VPN и переподключитесь.";
            else switch (st)
            {
                case VpnState.Connected: sub = "Соединение защищено · " + p.DisplayName + (p.IsTun ? "" : "\r\nSOCKS5-прокси: " + p.SocksAddress); break;
                case VpnState.Connecting: sub = "Устанавливаем защищённое соединение с " + name; break;
                case VpnState.Disconnecting: sub = "Восстанавливаем обычное подключение к интернету"; break;
                case VpnState.Error: sub = (vpn.LastError ?? "Ошибка") + ".\r\nПодробности — в журнале."; break;
                default: sub = p == null ? "Выберите сервер слева" : "Нажмите кнопку, чтобы подключиться к " + name; break;
            }
            if (lblSub.Text != sub) lblSub.Text = sub;

            // кнопка «Исключения: …» под статусом
            bool showExcl = p != null;
            string exclText = p == null ? "" : "Исключения: " + ExclusionCatalog.Summary(p) + (p.LanViaVpn ? " · локальные сети через VPN" : "");
            if (p != null && p.VpnMode == "selective") exclText = "Через VPN только: " + ExclusionCatalog.Summary(p);
            if (btnExcl.Visible != showExcl || btnExcl.Text != exclText)
            {
                btnExcl.Visible = showExcl;
                btnExcl.Text = exclText;
                LayoutMain();
            }
            bool auto = p != null && p.AutoStart;
            if (chkAutoStart.Checked != auto)
            {
                syncingAutoStart = true;
                chkAutoStart.Checked = auto;
                syncingAutoStart = false;
            }

            if (st == VpnState.Connected)
            {
                if (publicIp != null)
                    tileIp.SetValue(publicIp,
                        publicIpMatches == true ? "✔ VPN-сервер" : publicIpMatches == false ? "не IP сервера" :
                        vpn.ActiveProfile != null && vpn.ActiveProfile.VpnMode == "selective" ? "для сайтов вне VPN" : "",
                        publicIpMatches == false ? t.Orange : (Color?)null);
                else tileIp.SetValue(ipChecking ? "проверка…" : "—", "");
                var up = DateTime.Now - vpn.ConnectedSince;
                tileTime.SetValue(((int)up.TotalHours).ToString("00") + ":" + up.Minutes.ToString("00") + ":" + up.Seconds.ToString("00"),
                    "с " + vpn.ConnectedSince.ToString("HH:mm"));
                if (vpn.ActiveProfile.IsTun)
                {
                    tileDown.SetValue(FormatBytes(vpn.RxRate) + "/с", "всего " + FormatBytes(vpn.RxBytes));
                    tileUp.SetValue(FormatBytes(vpn.TxRate) + "/с", "всего " + FormatBytes(vpn.TxBytes));
                }
                else
                {
                    tileDown.SetValue("—", "SOCKS5");
                    tileUp.SetValue("—", "SOCKS5");
                }
            }
            else
            {
                tileIp.SetValue(publicIp ?? "—", "");
                tileTime.SetValue("—", "");
                tileDown.SetValue("—", "");
                tileUp.SetValue("—", "");
            }

            btnLog.Checked = logPanel.Visible;
            string tip = "TrustTunnel — " + StateTitle();
            if (tip.Length > 63) tip = tip.Substring(0, 63);
            if (tray.Text != tip) tray.Text = tip;
        }

        void UpdateBanners()
        {
            SuspendLayout();
            bnInstall.Visible = installPct >= 0;
            if (installPct >= 0) bnInstall.Set(BannerKind.Info, "Загрузка компонента TrustTunnel… " + installPct + "%", null);

            bnClient.Visible = !ClientInstalled && installPct < 0;
            if (bnClient.Visible) bnClient.Set(BannerKind.Warning, "Не установлен клиент TrustTunnel — без него подключиться нельзя.", "Установить");

            bnAdmin.Visible = !isAdmin;
            if (bnAdmin.Visible) bnAdmin.Set(BannerKind.Error, "Нужны права администратора, чтобы создать VPN-адаптер.", "Перезапустить");

            bnRoute.Visible = RouteWarn;
            if (bnRoute.Visible) bnRoute.Set(BannerKind.Warning, "Трафик идёт мимо VPN — отключите другие VPN.", "Подробнее");

            bnDpi.Visible = false;
            if (HasPausedDpi)
            {
                bnDpi.Visible = true;
                bnDpi.Set(BannerKind.Info, "zapret приостановлен на время VPN — внутри туннеля он ломает Discord и YouTube.", "Почему?");
            }
            else if (vpn.IsRunning && vpn.ActiveProfile != null && vpn.ActiveProfile.IsTun && !data.Settings.PauseDpiBypass &&
                     SplitRouting.Networks(vpn.ActiveProfile) == null)
            {
                DpiBypass.Detected d = null;
                try { d = DpiBypass.Detect(); } catch { }
                if (d != null && d.Any)
                {
                    bnDpi.Visible = true;
                    bnDpi.Set(BannerKind.Warning, vpn.ActiveProfile.VpnMode == "selective"
                        ? "Работает " + d.Describe() + " — он ломает прямые соединения, сайты вне VPN не открываются."
                        : "Работает " + d.Describe() + " — внутри VPN он ломает Discord и YouTube.", "Приостанавливать");
                }
            }
            ResumeLayout(true);
        }

        void RelaunchAsAdmin()
        {
            try
            {
                Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--relaunch") { UseShellExecute = true, Verb = "runas" });
                ExitApp();
            }
            catch (Exception ex) { vpn.Log("Перезапуск отменён: " + ex.Message); }
        }

        // =====================================================================
        // Внешний IP
        // =====================================================================

        async void CheckPublicIp(int delayMs)
        {
            int seq = ++ipCheckSeq;
            ipChecking = true;
            UpdateStatusUi();
            try
            {
                if (delayMs > 0) await System.Threading.Tasks.Task.Delay(delayMs);
                if (seq != ipCheckSeq) return;
                string ip;
                using (var wc = ClientInstaller.NewClient())
                    ip = (await wc.DownloadStringTaskAsync("https://api.ipify.org")).Trim();
                if (seq != ipCheckSeq) return;
                var p = vpn.ActiveProfile;
                publicIp = ip;
                // в выборочном режиме сайт проверки IP идёт мимо VPN — сравнивать с сервером бессмысленно
                bool selective = p != null && p.VpnMode == "selective";
                publicIpMatches = vpn.State == VpnState.Connected && p != null && !selective ? ServerIps(p).Contains(ip) : (bool?)null;
                vpn.Log("Внешний IP: " + ip + (selective ? "  (для сайтов вне VPN)" :
                        publicIpMatches == true ? "  ✔ это адрес VPN-сервера" : publicIpMatches == false ? "  ⚠ не совпадает с адресом VPN-сервера" : ""));
            }
            catch (Exception ex)
            {
                if (seq == ipCheckSeq) vpn.Log("Не удалось проверить внешний IP: " + ex.Message);
            }
            finally
            {
                if (seq == ipCheckSeq) ipChecking = false;
            }
            UpdateStatusUi();
        }

        static HashSet<string> ServerIps(Profile p)
        {
            var r = new HashSet<string>();
            foreach (var a in p.Addresses)
            {
                string host = a;
                int colon = a.LastIndexOf(':');
                if (colon > 0) host = a.Substring(0, colon);
                host = host.Trim('[', ']');
                System.Net.IPAddress ip;
                if (System.Net.IPAddress.TryParse(host, out ip)) r.Add(ip.ToString());
                else
                {
                    try { foreach (var x in System.Net.Dns.GetHostAddresses(host)) r.Add(x.ToString()); } catch { }
                }
            }
            return r;
        }

        // =====================================================================
        // Профили
        // =====================================================================

        void RefreshProfiles(string selectId)
        {
            lstProfiles.BeginUpdate();
            lstProfiles.Items.Clear();
            foreach (var p in data.Profiles) lstProfiles.Items.Add(p);
            lstProfiles.EndUpdate();
            var sel = data.Profiles.FirstOrDefault(p => p.Id == selectId) ?? data.Profiles.FirstOrDefault();
            if (sel != null) lstProfiles.SelectedItem = sel;
            UpdateStatusUi();
        }

        internal void SaveData()
        {
            try { data.Save(); }
            catch (Exception ex) { vpn.Log("Не удалось сохранить настройки: " + ex.Message); }
        }

        void NewProfile()
        {
            using (var f = new ProfileForm(new Profile(), "Новый сервер"))
                if (f.ShowDialog(this) == DialogResult.OK) AddProfile(f.Result);
        }

        void AddProfile(Profile p)
        {
            data.Profiles.Add(p);
            SaveData();
            RefreshProfiles(p.Id);
            vpn.Log("Сервер «" + p.DisplayName + "» добавлен");
        }

        void EditProfile(Profile p)
        {
            if (p == null) return;
            using (var f = new ProfileForm(p, p.DisplayName))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                int idx = data.Profiles.IndexOf(p);
                if (idx < 0) return;
                data.Profiles[idx] = f.Result;
                SaveData();
                RefreshProfiles(f.Result.Id);
                if (vpn.IsRunning && vpn.ActiveProfile != null && vpn.ActiveProfile.Id == p.Id)
                    vpn.Log("Изменения применятся после переподключения.");
            }
        }

        void DuplicateProfile(Profile p)
        {
            if (p == null) return;
            var c = p.Clone();
            c.Id = Guid.NewGuid().ToString("N");
            c.Name = p.DisplayName + " (копия)";
            AddProfile(c);
        }

        void DeleteProfile(Profile p)
        {
            if (p == null) return;
            if (vpn.IsRunning && vpn.ActiveProfile != null && vpn.ActiveProfile.Id == p.Id)
            {
                MessageBox.Show(this, "Нельзя удалить сервер, к которому вы сейчас подключены. Сначала отключитесь.", "TrustTunnel", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show(this, "Удалить сервер «" + p.DisplayName + "»?", "TrustTunnel", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            int idx = data.Profiles.IndexOf(p);
            data.Profiles.Remove(p);
            SaveData();
            var next = data.Profiles.Count == 0 ? null : data.Profiles[Math.Min(idx, data.Profiles.Count - 1)];
            RefreshProfiles(next == null ? null : next.Id);
        }

        void ImportLinkDialog()
        {
            string initial = "";
            try { if (Clipboard.ContainsText() && DeepLink.LooksLikeLink(Clipboard.GetText())) initial = Clipboard.GetText().Trim(); } catch { }
            var link = InputBox.Show(this, "Добавить сервер по ссылке",
                "Вставьте ссылку tt://… которую вам дал администратор сервера.", initial);
            if (!string.IsNullOrEmpty(link)) ImportLink(link);
        }

        void ImportClipboard()
        {
            string text = "";
            try { text = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : ""; } catch { }
            if (DeepLink.LooksLikeLink(text)) ImportLink(text);
            else if (text.Contains("hostname")) ImportTomlText(text, "буфера обмена");
            else MessageBox.Show(this, "В буфере обмена нет ссылки tt:// или конфигурации TrustTunnel.", "TrustTunnel", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        void ImportLink(string link)
        {
            Profile p;
            try { p = DeepLink.Decode(link); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось разобрать ссылку:\n" + ex.Message, "TrustTunnel", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            ConfirmImported(p);
        }

        void ImportFileDialog()
        {
            using (var d = new OpenFileDialog { Filter = "Конфигурация TrustTunnel (*.toml)|*.toml|Все файлы (*.*)|*.*", Title = "Открыть конфигурацию TrustTunnel" })
                if (d.ShowDialog(this) == DialogResult.OK) ImportFile(d.FileName);
        }

        void ImportFile(string path)
        {
            string text;
            try { text = File.ReadAllText(path); }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "TrustTunnel", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            ImportTomlText(text, Path.GetFileName(path));
        }

        void ImportTomlText(string text, string source)
        {
            Profile p;
            try { p = ConfigBuilder.FromToml(text); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось прочитать конфигурацию из " + source + ":\n" + ex.Message, "TrustTunnel", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            ConfirmImported(p);
        }

        /// <summary>Перед сохранением показываем импортированные параметры пользователю (как требует спецификация deep link).</summary>
        void ConfirmImported(Profile p)
        {
            if (string.IsNullOrEmpty(p.Name)) p.Name = p.Hostname;
            using (var f = new ProfileForm(p, "Новый сервер", "Проверьте параметры и нажмите «Сохранить»."))
                if (f.ShowDialog(this) == DialogResult.OK) AddProfile(f.Result);
        }

        void OnDrop(IDataObject d)
        {
            if (d.GetDataPresent(DataFormats.FileDrop))
            {
                foreach (var f in (string[])d.GetData(DataFormats.FileDrop)) ImportFile(f);
            }
            else if (d.GetDataPresent(DataFormats.Text))
            {
                var t = ((string)d.GetData(DataFormats.Text)).Trim();
                if (DeepLink.LooksLikeLink(t)) ImportLink(t); else ImportTomlText(t, "перетаскивания");
            }
        }

        void ExportLink(Profile p)
        {
            if (p == null) return;
            try
            {
                Clipboard.SetText(DeepLink.Encode(p));
                MessageBox.Show(this, "Ссылка tt:// скопирована в буфер обмена — её можно открыть в TrustTunnel на телефоне или другом компьютере.\n\n" +
                                      "Внимание: в ссылке есть логин и пароль. Передавайте её только тем, кому доверяете.",
                    "TrustTunnel", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "TrustTunnel", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        void ShowToml(Profile p)
        {
            if (p == null) return;
            TextViewer.Show(this, "Конфиг — " + p.DisplayName, ConfigBuilder.Build(p));
        }

        // =====================================================================
        // Клиент и настройки
        // =====================================================================

        void OpenSettings()
        {
            using (var f = new SettingsForm(this)) f.ShowDialog(this);
            UpdateBanners();
        }

        internal async void InstallClient()
        {
            if (busyInstall) return;
            if (vpn.IsRunning)
            {
                MessageBox.Show(this, "Сначала отключитесь — файлы клиента сейчас заняты.", "TrustTunnel", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            busyInstall = true;
            try
            {
                vpn.Log("Проверка последней версии на GitHub…");
                var rel = await ClientInstaller.GetLatestAsync();
                string msg = "Скачать официальный клиент TrustTunnel?\n\n" +
                             "Версия: " + rel.Tag + "\nФайл: " + rel.AssetName + " (" + FormatBytes(rel.Size) + ")\n" +
                             "Источник: github.com/TrustTunnel/TrustTunnelClient\nПапка: " + ClientDir;
                if (!string.IsNullOrEmpty(data.Settings.ClientVersion))
                    msg += "\n\nСейчас установлен: " + data.Settings.ClientVersion;
                if (MessageBox.Show(this, msg, "TrustTunnel", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                {
                    vpn.Log("Загрузка отменена");
                    return;
                }
                installPct = 0;
                UpdateBanners();
                var notes = await ClientInstaller.InstallAsync(rel, ClientDir,
                    s => BeginInvoke(new Action(() => vpn.Log(s))),
                    pct => BeginInvoke(new Action(() =>
                    {
                        if (installPct >= 0 && pct != installPct) { installPct = pct; UpdateBanners(); }
                    })));
                data.Settings.ClientVersion = rel.Tag;
                SaveData();
                if (notes.Count > 0)
                    MessageBox.Show(this, string.Join("\n\n", notes), "TrustTunnel", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                else
                    vpn.Log("Готово. Клиент " + rel.Tag + " установлен.");
            }
            catch (Exception ex)
            {
                vpn.Log("Ошибка загрузки клиента: " + ex.Message);
                MessageBox.Show(this, "Не удалось скачать клиент:\n" + ex.Message +
                                      "\n\nМожно скачать архив вручную со страницы релизов и указать папку в настройках.",
                    "TrustTunnel", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                busyInstall = false;
                installPct = -1;
                UpdateBanners();
            }
        }

        internal void ChooseClientDir()
        {
            using (var d = new FolderBrowserDialog { Description = "Папка, где лежат trusttunnel_client.exe и wintun.dll", SelectedPath = ClientDir })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                if (!File.Exists(Path.Combine(d.SelectedPath, VpnController.ClientExe)) &&
                    MessageBox.Show(this, "В этой папке нет " + VpnController.ClientExe + ". Всё равно использовать её?", "TrustTunnel",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    return;
                data.Settings.ClientDir = d.SelectedPath;
                data.Settings.ClientVersion = "";
                SaveData();
                UpdateBanners();
            }
        }

        internal void KillOrphans(bool interactive)
        {
            if (vpn.IsRunning)
            {
                if (interactive) MessageBox.Show(this, "Сначала отключитесь.", "TrustTunnel", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var list = VpnController.FindOrphans();
            if (list.Length == 0)
            {
                if (interactive) MessageBox.Show(this, "Посторонних процессов trusttunnel_client не найдено.", "TrustTunnel", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show(this, "Найдено запущенных процессов trusttunnel_client: " + list.Length +
                                      " (например, после сбоя или запущенных вручную).\n\nЗавершить их?",
                    "TrustTunnel", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            foreach (var p in list)
            {
                try { p.Kill(); p.WaitForExit(3000); vpn.Log("Завершён процесс trusttunnel_client (PID " + p.Id + ")"); }
                catch (Exception ex) { vpn.Log("Не удалось завершить PID " + p.Id + ": " + ex.Message); }
            }
        }

        // =====================================================================
        // Интеграция с Windows
        // =====================================================================

        const string TaskName = "TrustTunnelGUI";

        static int RunHidden(string exe, string args)
        {
            var psi = new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false };
            using (var p = Process.Start(psi))
            {
                p.WaitForExit(10000);
                return p.HasExited ? p.ExitCode : -1;
            }
        }

        internal static bool AutostartEnabled()
        {
            try { return RunHidden("schtasks.exe", "/Query /TN " + TaskName) == 0; } catch { return false; }
        }

        internal bool SetAutostart(bool on)
        {
            // Программе нужны права администратора, поэтому автозапуск — через планировщик с наивысшими правами (без запроса UAC).
            int code;
            if (on)
            {
                // Описание задачи в XML: у задачи по умолчанию Планировщик через 72 часа завершает процесс
                // и не запускает её от батареи — для VPN, который должен работать постоянно, это не годится.
                string user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
                string xml = "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n" +
                    "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\n" +
                    "  <RegistrationInfo><Description>TrustTunnel GUI: запуск и подключение к VPN при входе в Windows</Description></RegistrationInfo>\n" +
                    "  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + user + "</UserId><Delay>PT15S</Delay></LogonTrigger></Triggers>\n" +
                    "  <Principals><Principal id=\"Author\"><UserId>" + user + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\n" +
                    "  <Settings>\n" +
                    "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\n" +
                    "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\n" +
                    "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\n" +
                    "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\n" +
                    "    <AllowStartOnDemand>true</AllowStartOnDemand>\n" +
                    "    <StartWhenAvailable>true</StartWhenAvailable>\n" +
                    "    <Enabled>true</Enabled>\n" +
                    "    <Priority>7</Priority>\n" +
                    "  </Settings>\n" +
                    "  <Actions Context=\"Author\"><Exec><Command>" + SecurityElement.Escape(Application.ExecutablePath) + "</Command><Arguments>--minimized</Arguments></Exec></Actions>\n" +
                    "</Task>\n";
                string tmp = Path.Combine(Path.GetTempPath(), "ttgui_task_" + Guid.NewGuid().ToString("N") + ".xml");
                File.WriteAllText(tmp, xml, Encoding.Unicode);
                try { code = RunHidden("schtasks.exe", "/Create /F /TN " + TaskName + " /XML \"" + tmp + "\""); }
                finally { try { File.Delete(tmp); } catch { } }
            }
            else
                code = RunHidden("schtasks.exe", "/Delete /F /TN " + TaskName);
            if (code != 0)
            {
                MessageBox.Show(this, "Не удалось изменить автозапуск (schtasks, код " + code + "). Нужны права администратора.", "TrustTunnel", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            vpn.Log(on ? "Автозапуск при входе в Windows включён" : "Автозапуск выключен");
            return true;
        }

        internal static bool UrlHandlerEnabled()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Classes\tt\shell\open\command"))
            {
                var v = k == null ? null : k.GetValue("") as string;
                return v != null && v.IndexOf(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        internal void SetUrlHandler(bool on)
        {
            try
            {
                if (on)
                {
                    using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\tt"))
                    {
                        k.SetValue("", "URL:TrustTunnel");
                        k.SetValue("URL Protocol", "");
                        using (var ic = k.CreateSubKey("DefaultIcon")) ic.SetValue("", "\"" + Application.ExecutablePath + "\",0");
                        using (var c = k.CreateSubKey(@"shell\open\command")) c.SetValue("", "\"" + Application.ExecutablePath + "\" \"%1\"");
                    }
                    vpn.Log("Ссылки tt:// теперь открываются в TrustTunnel");
                }
                else
                {
                    Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\tt", false);
                    vpn.Log("Обработчик ссылок tt:// удалён");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "TrustTunnel", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        internal static void OpenFolder(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                Process.Start("explorer.exe", "\"" + path + "\"");
            }
            catch { }
        }

        internal static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }
    }
}
