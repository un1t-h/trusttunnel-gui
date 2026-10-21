using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace TrustTunnelGui
{
    /// <summary>Удобный редактор исключений: режим, готовые сервисы, приложения, свои адреса, локальные сети.</summary>
    public class ExclusionsForm : ThemedDialog
    {
        public Profile Result { get; private set; }
        readonly Profile p;
        readonly List<string> home;

        FlatButton modeExcept, modeOnly, lanDirect, lanVpn;
        Label lblModeHint, lblLanHint;
        CheckBox chkKeepHome;
        readonly Dictionary<string, FlatButton> chips = new Dictionary<string, FlatButton>();
        FlowLayoutPanel appsBox;
        ListBox lstOwn;
        TextBox txtAdd;

        public ExclusionsForm(Profile profile, List<string> homeSubnets)
        {
            p = profile.Clone();
            home = homeSubnets ?? new List<string>();
            Text = "Исключения — " + profile.DisplayName;
            Size = new Size(Ui.S(780), Ui.S(720));
            MinimumSize = new Size(Ui.S(600), Ui.S(500));

            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Tag = "bg" };
            var g = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(Ui.S(22), Ui.S(10), Ui.S(22), Ui.S(16)) };
            g.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            // ---- режим ----
            Header(g, "Что идёт мимо VPN", null);
            modeExcept = Chip("Всё через VPN, кроме выбранного", null);
            modeOnly = Chip("Через VPN — только выбранное", null);
            modeExcept.Click += (s, e) => SetMode(false);
            modeOnly.Click += (s, e) => SetMode(true);
            g.Controls.Add(Row(modeExcept, modeOnly));
            lblModeHint = Hint(g, "");
            var claudeOnly = new FlatButton { Kind = ButtonKind.Ghost, Glyph = Glyphs.Shield, Text = "Быстрая настройка: через VPN только Claude и Anthropic",
                Margin = new Padding(Ui.S(2), Ui.S(4), 0, 0) };
            claudeOnly.Size = claudeOnly.GetPreferredSize(Size.Empty);
            claudeOnly.Click += (s, e) => ApplyClaudeOnly();
            g.Controls.Add(claudeOnly);

            // ---- сервисы ----
            Header(g, "Сервисы", "Готовые наборы доменов и адресов. Включите нужные — список обновляется вместе с программой.");
            var flow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 0, 0, Ui.S(4)), Dock = DockStyle.Fill };
            var tips = new ToolTip();
            foreach (var preset in ExclusionCatalog.Presets)
            {
                var pr = preset;
                var b = Chip(preset.Name, null);
                b.Checked = p.ExclusionPresets.Contains(preset.Id);
                b.Glyph = b.Checked ? Glyphs.Check : null;
                b.Click += (s, e) =>
                {
                    b.Checked = !b.Checked;
                    b.Glyph = b.Checked ? Glyphs.Check : null;
                    b.Size = b.GetPreferredSize(Size.Empty);
                    if (b.Checked) { if (!p.ExclusionPresets.Contains(pr.Id)) p.ExclusionPresets.Add(pr.Id); }
                    else p.ExclusionPresets.Remove(pr.Id);
                };
                b.Size = b.GetPreferredSize(Size.Empty);
                tips.SetToolTip(b, preset.Description + " · " + preset.Entries.Length + " " +
                    ExclusionCatalog.Plural(preset.Entries.Length, "запись", "записи", "записей"));
                chips[preset.Id] = b;
                flow.Controls.Add(b);
            }
            g.Controls.Add(flow);

            // ---- приложения ----
            Header(g, "Приложения", "TrustTunnel не умеет исключать программы напрямую, поэтому приложение превращается в список адресов, " +
                                     "к которым оно подключается. Для Discord, Telegram, Steam и др. берётся готовый набор.");
            appsBox = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Fill, Margin = new Padding(0) };
            g.Controls.Add(appsBox);
            var addApp = new FlatButton { Kind = ButtonKind.Secondary, Glyph = Glyphs.Add, Text = "Добавить приложение…", Margin = new Padding(Ui.S(2), Ui.S(4), 0, Ui.S(4)) };
            addApp.Size = addApp.GetPreferredSize(Size.Empty);
            addApp.Click += (s, e) => AddApp();
            g.Controls.Add(addApp);
            RefreshApps();

            // ---- свои адреса ----
            Header(g, "Сайты и IP-адреса", "Домен (example.com — вместе с поддоменами), *.example.com, IP-адрес, подсеть 10.0.0.0/8 или *:порт.");
            txtAdd = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(Ui.S(2), Ui.S(3), Ui.S(8), Ui.S(3)) };
            txtAdd.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; AddOwn(txtAdd.Text); } };
            var bAdd = new FlatButton { Kind = ButtonKind.Primary, Glyph = Glyphs.Add, Text = "Добавить", Margin = new Padding(0) };
            bAdd.Size = new Size(bAdd.GetPreferredSize(Size.Empty).Width, Ui.S(32));
            bAdd.Click += (s, e) => AddOwn(txtAdd.Text);
            var addRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = new Padding(0) };
            addRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            addRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            addRow.Controls.Add(txtAdd, 0, 0);
            addRow.Controls.Add(bAdd, 1, 0);
            g.Controls.Add(addRow);
            lstOwn = new ListBox
            {
                Dock = DockStyle.Fill,
                Height = Ui.S(140),
                IntegralHeight = false,
                SelectionMode = SelectionMode.MultiExtended,
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 9.5f),
                Margin = new Padding(Ui.S(2), Ui.S(4), Ui.S(2), Ui.S(4)),
            };
            lstOwn.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) RemoveOwn(); };
            g.Controls.Add(lstOwn);
            var bRemove = SmallBtn("Удалить выбранные", Glyphs.Delete, (s, e) => RemoveOwn());
            var bPaste = SmallBtn("Вставить список из буфера", Glyphs.Copy, (s, e) => PasteList());
            var bClear = SmallBtn("Очистить", null, (s, e) =>
            {
                if (lstOwn.Items.Count > 0 && MessageBox.Show(this, "Удалить все свои адреса?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                { p.Exclusions.Clear(); RefreshOwn(); }
            });
            g.Controls.Add(Row(bRemove, bPaste, bClear));
            RefreshOwn();

            // ---- локальные сети ----
            Header(g, "Локальные сети", "Адреса вида 192.168.x.x, 10.x.x.x и 172.16–31.x.x: роутер, принтер, NAS — или сети за VPN-сервером.");
            lanDirect = Chip("Напрямую (сеть, где стоит компьютер)", null);
            lanVpn = Chip("Через VPN (сети за сервером)", null);
            lanDirect.Click += (s, e) => SetLan(false);
            lanVpn.Click += (s, e) => SetLan(true);
            g.Controls.Add(Row(lanDirect, lanVpn));
            chkKeepHome = new CheckBox
            {
                AutoSize = true,
                Checked = p.LanKeepHome,
                Text = home.Count > 0 ? "Кроме моей текущей сети (" + string.Join(", ", home) + ")" : "Кроме моей текущей сети (не определена)",
                Margin = new Padding(Ui.S(4), Ui.S(6), 0, Ui.S(2)),
            };
            chkKeepHome.CheckedChanged += (s, e) => { p.LanKeepHome = chkKeepHome.Checked; UpdateLanHint(); };
            g.Controls.Add(chkKeepHome);
            lblLanHint = Hint(g, "");
            if (p.ExcludedRoutes.Count > 0)
                Hint(g, "⚠ В профиле заданы свои «маршруты мимо VPN» — они важнее этого переключателя (вкладка «Тип подключения» в редакторе сервера).");

            scroll.Controls.Add(g);

            var bar = ButtonBar();
            var ok = DialogButton("Сохранить", ButtonKind.Primary);
            var cancel = DialogButton("Отмена", ButtonKind.Secondary);
            cancel.DialogResult = DialogResult.Cancel;
            ok.Click += (s, e) => { Result = p; DialogResult = DialogResult.OK; Close(); };
            bar.Controls.Add(ok);
            bar.Controls.Add(cancel);
            CancelButton = cancel;

            Controls.Add(scroll);
            Controls.Add(bar);
            SetMode(p.VpnMode == "selective");
            SetLan(p.LanViaVpn);
        }

        // ---------- элементы ----------

        static void Header(TableLayoutPanel g, string title, string sub)
        {
            g.Controls.Add(new Label { Text = title, AutoSize = true, Font = Theme.SemiboldFont(11.5f), Margin = new Padding(Ui.S(2), Ui.S(g.Controls.Count == 0 ? 6 : 20), 0, Ui.S(2)) });
            if (sub != null)
                g.Controls.Add(new Label { Text = sub, AutoSize = true, Tag = "muted", MaximumSize = new Size(Ui.S(690), 0), Margin = new Padding(Ui.S(3), 0, 0, Ui.S(8)) });
        }

        static Label Hint(TableLayoutPanel g, string text)
        {
            var l = new Label { Text = text, AutoSize = true, Tag = "muted", Font = Theme.UiFont(9f), MaximumSize = new Size(Ui.S(690), 0), Margin = new Padding(Ui.S(3), Ui.S(4), 0, Ui.S(2)) };
            g.Controls.Add(l);
            return l;
        }

        static FlatButton Chip(string text, string glyph)
        {
            var b = new FlatButton { Kind = ButtonKind.Secondary, Text = text, Glyph = glyph, Margin = new Padding(Ui.S(2), Ui.S(3), Ui.S(8), Ui.S(3)) };
            b.Size = b.GetPreferredSize(Size.Empty);
            return b;
        }

        static FlatButton SmallBtn(string text, string glyph, EventHandler click)
        {
            var b = new FlatButton { Kind = ButtonKind.Ghost, Text = text, Glyph = glyph, Font = Theme.UiFont(9f), Margin = new Padding(Ui.S(2), 0, Ui.S(4), 0) };
            b.Size = new Size(b.GetPreferredSize(Size.Empty).Width, Ui.S(32));
            b.Click += click;
            return b;
        }

        static FlowLayoutPanel Row(params Control[] items)
        {
            var f = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
            f.Controls.AddRange(items);
            return f;
        }

        // ---------- логика ----------

        void SetMode(bool only)
        {
            p.VpnMode = only ? "selective" : "general";
            modeOnly.Checked = only;
            modeExcept.Checked = !only;
            lblModeHint.Text = only
                ? "Через VPN пойдут только выбранные сервисы, приложения и адреса, остальное — напрямую."
                : "Весь трафик идёт через VPN, а выбранные сервисы, приложения и адреса — напрямую, мимо VPN.";
        }

        /// <summary>Сценарий «через VPN только Claude»: выборочный режим и единственный набор Claude / Anthropic.</summary>
        void ApplyClaudeOnly()
        {
            SetMode(true);
            p.ExclusionPresets.Clear();
            p.ExclusionPresets.Add(ExclusionCatalog.ClaudeId);
            foreach (var kv in chips)
            {
                kv.Value.Checked = kv.Key == ExclusionCatalog.ClaudeId;
                kv.Value.Glyph = kv.Value.Checked ? Glyphs.Check : null;
                kv.Value.Size = kv.Value.GetPreferredSize(Size.Empty);
            }
            foreach (var a in p.ExclusionApps) a.Enabled = false;
            RefreshApps();
            lblModeHint.Text = "Через VPN пойдут только Claude (сайт и приложение), Claude Code и API Anthropic. " +
                               "Всё остальное — напрямую, как без VPN." + (p.Exclusions.Count > 0 ? " Свои адреса из списка ниже тоже пойдут через VPN — уберите лишние." : "");
        }

        void SetLan(bool viaVpn)
        {
            p.LanViaVpn = viaVpn;
            lanVpn.Checked = viaVpn;
            lanDirect.Checked = !viaVpn;
            chkKeepHome.Enabled = viaVpn;
            UpdateLanHint();
        }

        void UpdateLanHint()
        {
            lblLanHint.Text = p.LanViaVpn
                ? "Частные сети пойдут через VPN-сервер" + (p.LanKeepHome && home.Count > 0 ? ", а ваша домашняя сеть останется доступна напрямую" : " — домашний роутер и принтер будут недоступны") +
                  ". На сервере должен быть разрешён доступ клиентов к локальной сети (TrustTunnel Panel → сервер → «Доступ к локальной сети»)."
                : "Устройства вашей сети (роутер, принтер, NAS) доступны как обычно, этот трафик не идёт в VPN.";
        }

        void RefreshOwn()
        {
            lstOwn.BeginUpdate();
            lstOwn.Items.Clear();
            foreach (var e in p.Exclusions) lstOwn.Items.Add(e);
            lstOwn.EndUpdate();
        }

        void AddOwn(string text)
        {
            int added = 0;
            foreach (var raw in (text ?? "").Split(new[] { '\r', '\n', ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var e = Normalize(raw);
                if (e.Length == 0 || p.Exclusions.Contains(e, StringComparer.OrdinalIgnoreCase)) continue;
                p.Exclusions.Add(e);
                added++;
            }
            if (added > 0) { RefreshOwn(); lstOwn.SelectedIndex = lstOwn.Items.Count - 1; }
            txtAdd.Clear();
            txtAdd.Focus();
        }

        /// <summary>Превращает вставленную ссылку в домен: https://www.example.com/path → example.com.</summary>
        static string Normalize(string s)
        {
            s = s.Trim().Trim('"', '\'');
            int scheme = s.IndexOf("://", StringComparison.Ordinal);
            if (scheme > 0) s = s.Substring(scheme + 3);
            int slash = s.IndexOf('/');
            if (slash > 0 && !LooksLikeCidr(s)) s = s.Substring(0, slash);
            if (s.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) s = s.Substring(4);
            return s.ToLowerInvariant();
        }

        static bool LooksLikeCidr(string s)
        {
            int slash = s.IndexOf('/');
            int bits;
            return slash > 0 && int.TryParse(s.Substring(slash + 1), out bits) && s.Substring(0, slash).Count(c => c == '.') == 3;
        }

        void RemoveOwn()
        {
            var sel = lstOwn.SelectedItems.Cast<string>().ToList();
            if (sel.Count == 0) return;
            foreach (var e in sel) p.Exclusions.Remove(e);
            RefreshOwn();
        }

        void PasteList()
        {
            string text = "";
            try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; } catch { }
            if (text.Trim().Length == 0) { MessageBox.Show(this, "В буфере обмена нет текста.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            AddOwn(text);
        }

        void RefreshApps()
        {
            appsBox.SuspendLayout();
            appsBox.Controls.Clear();
            if (p.ExclusionApps.Count == 0)
                appsBox.Controls.Add(new Label { Text = "Приложений пока нет.", AutoSize = true, Tag = "muted", Margin = new Padding(Ui.S(3), Ui.S(2), 0, Ui.S(2)) });
            foreach (var app in p.ExclusionApps.ToList())
            {
                var a = app;
                var chk = new CheckBox
                {
                    AutoSize = true,
                    Checked = a.Enabled,
                    Text = a.Name + "  ·  " + a.Entries.Count + " " + ExclusionCatalog.Plural(a.Entries.Count, "адрес", "адреса", "адресов"),
                    Margin = new Padding(Ui.S(3), Ui.S(7), Ui.S(8), 0),
                };
                chk.CheckedChanged += (s, e) => a.Enabled = chk.Checked;
                var del = new FlatButton { Kind = ButtonKind.Ghost, Glyph = Glyphs.Delete, IconOnly = true, Text = "Удалить", Size = new Size(Ui.S(30), Ui.S(30)) };
                del.Click += (s, e) => { p.ExclusionApps.Remove(a); RefreshApps(); };
                var show = new FlatButton { Kind = ButtonKind.Ghost, Text = "адреса", Font = Theme.UiFont(9f) };
                show.Size = new Size(show.GetPreferredSize(Size.Empty).Width, Ui.S(30));
                show.Click += (s, e) => TextViewer.Show(this, a.Name + " — адреса", string.Join("\n", a.Entries));
                var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
                row.Controls.AddRange(new Control[] { chk, show, del });
                appsBox.Controls.Add(row);
            }
            appsBox.ResumeLayout(true);
            Theme.Apply(appsBox);
        }

        void AddApp()
        {
            using (var f = new AppPickerForm())
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                if (f.PresetId != null)
                {
                    if (!p.ExclusionPresets.Contains(f.PresetId)) p.ExclusionPresets.Add(f.PresetId);
                    FlatButton chip;
                    if (chips.TryGetValue(f.PresetId, out chip)) { chip.Checked = true; chip.Glyph = Glyphs.Check; chip.Size = chip.GetPreferredSize(Size.Empty); }
                }
                else if (f.App != null)
                {
                    p.ExclusionApps.RemoveAll(x => string.Equals(x.Exe, f.App.Exe, StringComparison.OrdinalIgnoreCase));
                    p.ExclusionApps.Add(f.App);
                    RefreshApps();
                }
            }
        }
    }

    /// <summary>Выбор приложения и сбор адресов, к которым оно подключается.</summary>
    public class AppPickerForm : ThemedDialog
    {
        public string PresetId { get; private set; }
        public ExclusionApp App { get; private set; }

        readonly ListBox lst;
        readonly Label lblInfo;
        readonly FlatButton ok, scanBtn;
        readonly CheckedListBox found;
        readonly Timer timer = new Timer { Interval = 1000 };
        AppScanner scanner;
        int secondsLeft;
        string scanName, scanExe;

        class Item
        {
            public string Name, Exe, Title;
            public override string ToString() { return string.IsNullOrEmpty(Title) ? Name : Title + "  —  " + Exe; }
        }

        public AppPickerForm()
        {
            Text = "Добавить приложение";
            Size = new Size(Ui.S(620), Ui.S(620));
            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Ui.S(20), Ui.S(12), Ui.S(20), Ui.S(8)), Tag = "bg" };
            var head = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                Tag = "muted",
                MaximumSize = new Size(Ui.S(560), 0),
                Padding = new Padding(0, 0, 0, Ui.S(8)),
                Text = "Выберите запущенную программу. Если для неё есть готовый набор (Discord, Telegram, Steam…) — он и включится. " +
                       "Иначе программа 20 секунд понаблюдает, куда приложение подключается: в это время пользуйтесь им (зайдите в голосовой чат, откройте страницу).",
            };
            lst = new ListBox { Dock = DockStyle.Top, Height = Ui.S(220), IntegralHeight = false, BorderStyle = BorderStyle.None };
            lst.DoubleClick += (s, e) => Start();
            var browse = new FlatButton { Kind = ButtonKind.Ghost, Glyph = Glyphs.Folder, Text = "Выбрать .exe…", Dock = DockStyle.Top, Height = Ui.S(34) };
            browse.Click += (s, e) => Browse();
            lblInfo = new Label { Dock = DockStyle.Top, AutoSize = true, MaximumSize = new Size(Ui.S(560), 0), Padding = new Padding(0, Ui.S(10), 0, Ui.S(6)) };
            found = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false, BorderStyle = BorderStyle.None, Font = new Font("Consolas", 9.5f), Visible = false };
            body.Controls.Add(found);
            body.Controls.Add(lblInfo);
            body.Controls.Add(browse);
            body.Controls.Add(lst);
            body.Controls.Add(head);

            var bar = ButtonBar();
            ok = DialogButton("Добавить", ButtonKind.Primary);
            ok.Enabled = false;
            ok.Click += (s, e) => Finish();
            scanBtn = DialogButton("Выбрать", ButtonKind.Secondary);
            scanBtn.Click += (s, e) => Start();
            var cancel = DialogButton("Отмена", ButtonKind.Secondary);
            cancel.DialogResult = DialogResult.Cancel;
            bar.Controls.Add(ok);
            bar.Controls.Add(scanBtn);
            bar.Controls.Add(cancel);
            CancelButton = cancel;
            Controls.Add(body);
            Controls.Add(bar);
            timer.Tick += (s, e) => Tick();
            FillProcesses();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }

        // системные окна, которые нет смысла исключать
        static readonly HashSet<string> SystemProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "explorer", "TextInputHost", "ApplicationFrameHost", "SystemSettings", "ShellExperienceHost", "StartMenuExperienceHost",
          "SearchHost", "LockApp", "NVIDIA Overlay", "TrustTunnelGUI", "trusttunnel_client", "Taskmgr", "dwm", "ctfmon" };

        void FillProcesses()
        {
            var items = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
            foreach (var pr in Process.GetProcesses())
            {
                try
                {
                    bool known = ExclusionCatalog.ForExecutable(pr.ProcessName) != null;
                    if (SystemProcesses.Contains(pr.ProcessName)) continue;
                    if (pr.MainWindowHandle == IntPtr.Zero && !known) continue;
                    if (pr.Id == Process.GetCurrentProcess().Id) continue;
                    Item it;
                    if (!items.TryGetValue(pr.ProcessName, out it))
                        items[pr.ProcessName] = it = new Item { Name = pr.ProcessName, Exe = pr.ProcessName + ".exe" };
                    if (string.IsNullOrEmpty(it.Title) && !string.IsNullOrEmpty(pr.MainWindowTitle))
                        it.Title = known ? ExclusionCatalog.ForExecutable(pr.ProcessName).Name : pr.MainWindowTitle;
                    if (known && string.IsNullOrEmpty(it.Title)) it.Title = ExclusionCatalog.ForExecutable(pr.ProcessName).Name;
                }
                catch { }
                finally { pr.Dispose(); }
            }
            foreach (var it in items.Values.OrderBy(x => x.ToString(), StringComparer.CurrentCultureIgnoreCase)) lst.Items.Add(it);
        }

        void Browse()
        {
            using (var d = new OpenFileDialog { Filter = "Программы (*.exe)|*.exe", Title = "Выберите программу" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                var name = Path.GetFileNameWithoutExtension(d.FileName);
                var it = new Item { Name = name, Exe = Path.GetFileName(d.FileName), Title = FileVersionInfo.GetVersionInfo(d.FileName).FileDescription };
                lst.Items.Insert(0, it);
                lst.SelectedIndex = 0;
                Start();
            }
        }

        void Start()
        {
            var it = lst.SelectedItem as Item;
            if (it == null) { lblInfo.Text = "Выберите программу в списке."; return; }
            var preset = ExclusionCatalog.ForExecutable(it.Exe);
            if (preset != null)
            {
                PresetId = preset.Id;
                DialogResult = DialogResult.OK;
                Close();
                return;
            }
            scanName = it.Name;
            scanExe = it.Exe;
            scanner = new AppScanner();
            secondsLeft = 20;
            found.Visible = false;
            found.Items.Clear();
            ok.Enabled = false;
            scanBtn.Enabled = false;
            lst.Enabled = false;
            Tick();
            timer.Start();
        }

        void Tick()
        {
            int procs = scanner.Sample(scanName);
            if (procs == 0 && secondsLeft == 20)
            {
                timer.Stop();
                lblInfo.Text = "«" + scanExe + "» сейчас не запущено. Запустите программу и нажмите «Выбрать» ещё раз.";
                scanBtn.Enabled = lst.Enabled = true;
                return;
            }
            lblInfo.Text = "Наблюдаю за «" + scanExe + "»: найдено адресов — " + scanner.Ips.Count + ". Осталось " + secondsLeft + " с — пользуйтесь приложением.";
            if (--secondsLeft >= 0) return;
            timer.Stop();
            var list = scanner.Suggest();
            scanBtn.Enabled = lst.Enabled = true;
            if (list.Count == 0)
            {
                lblInfo.Text = "Приложение не подключалось к интернету за это время. Попробуйте ещё раз, активно пользуясь им.";
                return;
            }
            lblInfo.Text = "Найдено " + list.Count + ". Снимите галочки с лишнего (общие сервисы вроде google.com лучше не исключать) и нажмите «Добавить».";
            foreach (var e in list) found.Items.Add(e, true);
            found.Visible = true;
            ok.Enabled = true;
        }

        void Finish()
        {
            var entries = found.CheckedItems.Cast<string>().ToList();
            if (entries.Count == 0) return;
            App = new ExclusionApp { Name = scanName, Exe = scanExe, Entries = entries, Enabled = true };
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
