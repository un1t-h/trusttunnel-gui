using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TrustTunnelGui
{
    /// <summary>Масштабирование под DPI для ручной вёрстки.</summary>
    public static class Ui
    {
        public static float Scale = 1f;

        public static void Init()
        {
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) Scale = g.DpiX / 96f;
        }

        public static int S(float v) { return (int)Math.Round(v * Scale); }

        public static void DrawGlyph(Graphics g, string glyph, float size, Color color, Rectangle r)
        {
            if (string.IsNullOrEmpty(glyph)) return;
            using (var f = Theme.IconFont(size))
            {
                if (f == null) return;
                TextRenderer.DrawText(g, glyph, f, r, color,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }
    }

    /// <summary>Значки из Segoe MDL2 Assets.</summary>
    public static class Glyphs
    {
        public const string Add = "";
        public const string Settings = "";
        public const string More = "";
        public const string Edit = "";
        public const string Delete = "";
        public const string Copy = "";
        public const string Globe = "";
        public const string Timer = "";
        public const string Download = "";
        public const string Upload = "";
        public const string Link = "";
        public const string File = "";
        public const string Folder = "";
        public const string Warning = "";
        public const string Info = "";
        public const string Check = "";
        public const string Close = "";
        public const string Log = "";
        public const string Shield = "";
        public const string Refresh = "";
        public const string Keyboard = "";
        public const string Filter = "";
    }

    public enum ButtonKind { Primary, Secondary, Ghost, Danger, Nav }

    /// <summary>Плоская кнопка с закруглёнными углами и необязательным значком.</summary>
    public class FlatButton : Control, IButtonControl, IThemed
    {
        bool hover, pressed, isDefault, isChecked;
        ButtonKind kind = ButtonKind.Secondary;
        string glyph;

        public FlatButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            Font = Theme.UiFont(9.5f);
            Size = new Size(Ui.S(110), Ui.S(34));
            TabStop = true;
        }

        public ButtonKind Kind { get { return kind; } set { kind = value; Invalidate(); } }
        public string Glyph { get { return glyph; } set { glyph = value; Invalidate(); } }
        public bool Checked { get { return isChecked; } set { isChecked = value; Invalidate(); } }
        /// <summary>Только значок, без текста (текст уходит в подсказку).</summary>
        public bool IconOnly { get; set; }
        public DialogResult DialogResult { get; set; }

        public void NotifyDefault(bool value) { isDefault = value; Invalidate(); }

        public void PerformClick() { if (Enabled && Visible) OnClick(EventArgs.Empty); }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            var f = FindForm();
            if (f != null && DialogResult != DialogResult.None) f.DialogResult = DialogResult;
        }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { pressed = true; Focus(); Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        // Срабатываем, только если клавиша была и нажата, и отпущена на этой кнопке:
        // иначе Enter, закрывший диалог, «долетает» отпусканием до кнопки в главном окне.
        bool keyArmed;

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) keyArmed = true;
            base.OnKeyDown(e);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if ((e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) && keyArmed) { keyArmed = false; PerformClick(); }
            base.OnKeyUp(e);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            return keyData == Keys.Enter || base.IsInputKey(keyData);
        }

        public override Size GetPreferredSize(Size proposed)
        {
            int pad = Ui.S(kind == ButtonKind.Nav ? 12 : 14);
            int w = pad * 2;
            if (!string.IsNullOrEmpty(glyph)) w += Ui.S(16) + (IconOnly || Text.Length == 0 ? 0 : Ui.S(8));
            if (!IconOnly && Text.Length > 0) w += TextRenderer.MeasureText(Text, Font).Width;
            if (IconOnly) w = Ui.S(36);
            return new Size(w, Ui.S(34));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var t = Theme.Current;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            float rad = Ui.S(7);

            Color bg = Color.Transparent, fg = t.Text, border = Color.Transparent;
            switch (kind)
            {
                case ButtonKind.Primary:
                    bg = pressed ? Theme.Mix(t.AccentHover, Color.Black, 0.1f) : hover ? t.AccentHover : t.Accent;
                    fg = Color.White;
                    break;
                case ButtonKind.Danger:
                    bg = pressed ? Theme.Mix(t.Red, Color.Black, 0.2f) : hover ? Theme.Mix(t.Red, Color.Black, 0.1f) : t.Red;
                    fg = Color.White;
                    break;
                case ButtonKind.Secondary:
                    if (isChecked)
                    {
                        // выбранный «чип» / вариант переключателя
                        bg = hover ? Theme.Mix(t.Selected, t.Accent, 0.12f) : t.Selected;
                        border = t.Accent;
                        fg = t.Accent;
                    }
                    else
                    {
                        bg = pressed ? Theme.Mix(t.Hover, t.Border, 0.5f) : hover ? t.Hover : t.Surface;
                        border = t.Border;
                    }
                    break;
                case ButtonKind.Ghost:
                    bg = pressed ? Theme.Mix(t.Hover, t.Border, 0.5f) : isChecked ? t.Selected : hover ? t.Hover : Color.Transparent;
                    fg = isChecked ? t.Accent : t.Text;
                    break;
                case ButtonKind.Nav:
                    bg = isChecked ? t.Selected : hover ? t.Hover : Color.Transparent;
                    fg = isChecked ? t.Accent : t.Text;
                    break;
            }
            if (!Enabled) { fg = t.Muted; if (kind == ButtonKind.Primary || kind == ButtonKind.Danger) bg = Theme.Mix(bg, t.Surface, 0.5f); }

            if (bg.A > 0) Theme.FillRound(g, bg, r, rad);
            if (border.A > 0) Theme.DrawRound(g, border, r, rad);
            if (kind == ButtonKind.Nav && isChecked)
                Theme.FillRound(g, t.Accent, new RectangleF(0, Height * 0.25f, Ui.S(3), Height * 0.5f), Ui.S(1.5f));
            if (Focused && ShowFocusCues)
                Theme.DrawRound(g, t.Accent, new RectangleF(1.5f, 1.5f, Width - 3.5f, Height - 3.5f), rad, 1.5f);

            bool hasGlyph = !string.IsNullOrEmpty(glyph) && Theme.IconFont(9) != null;
            bool hasText = !IconOnly && Text.Length > 0;
            int gw = hasGlyph ? Ui.S(16) : 0, gap = hasGlyph && hasText ? Ui.S(8) : 0;
            int tw = hasText ? TextRenderer.MeasureText(Text, Font).Width : 0;
            int x = kind == ButtonKind.Nav ? Ui.S(14) : (Width - gw - gap - tw) / 2;
            if (hasGlyph)
            {
                Ui.DrawGlyph(g, glyph, 10.5f, fg, new Rectangle(x, 0, gw, Height));
                x += gw + gap;
            }
            if (hasText)
                TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, Width - x, Height), fg,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>Большая круглая кнопка включения VPN.</summary>
    public class PowerButton : Control, IThemed
    {
        readonly Timer anim = new Timer { Interval = 30 };
        float phase;
        bool hover, pressed;
        VpnState state;
        bool warning;

        public PowerButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            TabStop = true;
            anim.Tick += (s, e) => { phase += 0.035f; if (phase > 1f) phase -= 1f; Invalidate(); };
        }

        public void SetState(VpnState s, bool warn)
        {
            if (s == state && warn == warning) return;
            state = s;
            warning = warn;
            bool busy = s == VpnState.Connecting || s == VpnState.Disconnecting;
            if (busy && !anim.Enabled) { phase = 0; anim.Start(); }
            if (!busy && anim.Enabled) anim.Stop();
            Invalidate();
        }

        protected override void Dispose(bool disposing) { if (disposing) anim.Dispose(); base.Dispose(disposing); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        bool keyArmed;

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) keyArmed = true;
            base.OnKeyDown(e);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if ((e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) && keyArmed) { keyArmed = false; OnClick(EventArgs.Empty); }
            base.OnKeyUp(e);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            return keyData == Keys.Enter || base.IsInputKey(keyData);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var t = Theme.Current;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float size = Math.Min(Width, Height);
            float cx = Width / 2f, cy = Height / 2f;
            float r = size * 0.36f;
            if (pressed) r *= 0.97f;

            Color c = warning ? t.Orange : t.ForState(state);
            bool filled = state == VpnState.Connected;

            // ореол
            if (state == VpnState.Connecting || state == VpnState.Disconnecting)
            {
                for (int i = 0; i < 2; i++)
                {
                    float p = (phase + i * 0.5f) % 1f;
                    float hr = r + (size / 2f - r) * p;
                    using (var b = new SolidBrush(Color.FromArgb((int)(70 * (1 - p)), c)))
                        g.FillEllipse(b, cx - hr, cy - hr, hr * 2, hr * 2);
                }
            }
            else if (filled)
            {
                float hr = r + (size / 2f - r) * 0.55f;
                using (var b = new SolidBrush(Color.FromArgb(t.IsDark ? 45 : 35, c)))
                    g.FillEllipse(b, cx - hr, cy - hr, hr * 2, hr * 2);
            }

            // тень
            if (!t.IsDark)
            {
                for (int i = 6; i >= 1; i--)
                {
                    float sr = r + i * Ui.Scale * 1.5f;
                    using (var b = new SolidBrush(Color.FromArgb(5, 0, 0, 30)))
                        g.FillEllipse(b, cx - sr, cy - sr + Ui.S(3), sr * 2, sr * 2);
                }
            }

            // диск
            Color disc = filled ? (hover ? Theme.Mix(c, Color.White, 0.08f) : c) : t.Surface;
            using (var b = new SolidBrush(disc)) g.FillEllipse(b, cx - r, cy - r, r * 2, r * 2);

            float ring = Math.Max(2f, size * 0.022f);
            if (!filled)
            {
                Color rc = state == VpnState.Disconnected ? (hover ? t.Accent : t.Border) : c;
                using (var pen = new Pen(rc, ring)) g.DrawEllipse(pen, cx - r, cy - r, r * 2, r * 2);
                if (state == VpnState.Connecting || state == VpnState.Disconnecting)
                {
                    using (var pen = new Pen(c, ring * 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawArc(pen, cx - r, cy - r, r * 2, r * 2, phase * 360f * 2, 80);
                }
            }

            // значок питания
            Color ic = filled ? Color.White :
                       state == VpnState.Disconnected ? (hover ? t.Accent : t.Muted) : c;
            float ir = r * 0.36f;
            using (var pen = new Pen(ic, Math.Max(2.5f, size * 0.035f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawArc(pen, cx - ir, cy - ir + r * 0.04f, ir * 2, ir * 2, 305, 290);
                g.DrawLine(pen, cx, cy - ir * 1.25f, cx, cy - ir * 0.15f);
            }

            if (Focused && ShowFocusCues)
                using (var pen = new Pen(t.Accent, 1.5f) { DashStyle = DashStyle.Dot })
                    g.DrawEllipse(pen, cx - r - ring * 2, cy - r - ring * 2, (r + ring * 2) * 2, (r + ring * 2) * 2);
        }
    }

    /// <summary>Плитка «подпись / значение».</summary>
    public class InfoTile : Control, IThemed
    {
        string caption = "", value = "—", detail = "", glyph;
        Color? valueColor;

        public InfoTile()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Size = new Size(Ui.S(170), Ui.S(68));
        }

        public string Glyph { get { return glyph; } set { glyph = value; Invalidate(); } }
        public string Caption { get { return caption; } set { caption = value; Invalidate(); } }

        public void SetValue(string v, string d, Color? color = null)
        {
            if (v == value && d == detail && color == valueColor) return;
            value = v; detail = d ?? ""; valueColor = color;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var t = Theme.Current;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Theme.FillRound(g, t.Surface, r, Ui.S(10));
            Theme.DrawRound(g, t.Border, r, Ui.S(10));

            int pad = Ui.S(12);
            int x = pad;
            // В узкой плитке значок прячем, чтобы поместилось значение (например, IP-адрес).
            if (Width >= Ui.S(168))
            {
                int ic = Ui.S(30);
                var icr = new Rectangle(pad, (Height - ic) / 2, ic, ic);
                using (var b = new SolidBrush(Color.FromArgb(t.IsDark ? 50 : 28, t.Accent))) g.FillEllipse(b, icr);
                Ui.DrawGlyph(g, glyph, 10.5f, t.Accent, icr);
                x = icr.Right + Ui.S(10);
            }
            else
            {
                Ui.DrawGlyph(g, glyph, 9f, t.Accent, new Rectangle(Width - pad - Ui.S(16), Ui.S(8), Ui.S(16), Ui.S(16)));
            }
            using (var cf = Theme.UiFont(8.5f))
            using (var vf = Theme.SemiboldFont(11f))
            {
                int ch = cf.Height, vh = vf.Height;
                bool hasDetail = detail.Length > 0;
                int total = ch + vh + (hasDetail ? ch : 0);
                int y = (Height - total) / 2;
                var flags = TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
                TextRenderer.DrawText(g, caption, cf, new Rectangle(x, y, Width - x - pad / 2, ch), t.Muted, flags);
                TextRenderer.DrawText(g, value, vf, new Rectangle(x, y + ch, Width - x - pad / 2, vh), valueColor ?? t.Text, flags);
                if (hasDetail)
                    TextRenderer.DrawText(g, detail, cf, new Rectangle(x, y + ch + vh, Width - x - pad / 2, ch), t.Muted, flags);
            }
        }
    }

    public enum BannerKind { Info, Warning, Error, Success }

    /// <summary>Полоса-уведомление с кнопкой действия.</summary>
    public class Banner : Control, IThemed
    {
        readonly FlatButton action = new FlatButton { Kind = ButtonKind.Secondary };
        BannerKind kind;
        string message = "";

        public event EventHandler ActionClick;

        public Banner()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Height = Ui.S(52);
            Dock = DockStyle.Top;
            Padding = new Padding(Ui.S(16), Ui.S(8), Ui.S(16), 0);
            action.Height = Ui.S(30);
            action.Click += (s, e) => { if (ActionClick != null) ActionClick(this, e); };
            Controls.Add(action);
        }

        public void Set(BannerKind k, string msg, string actionText)
        {
            kind = k;
            message = msg ?? "";
            action.Text = actionText ?? "";
            action.Visible = !string.IsNullOrEmpty(actionText);
            PerformLayout();
            Invalidate();
            action.Invalidate();
        }

        Color KindColor
        {
            get
            {
                var t = Theme.Current;
                switch (kind)
                {
                    case BannerKind.Warning: return t.Orange;
                    case BannerKind.Error: return t.Red;
                    case BannerKind.Success: return t.Green;
                    default: return t.Accent;
                }
            }
        }

        Rectangle Inner { get { return new Rectangle(Padding.Left, Padding.Top, Width - Padding.Horizontal, Height - Padding.Top); } }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            var r = Inner;
            action.Width = action.GetPreferredSize(Size.Empty).Width;
            action.Location = new Point(r.Right - action.Width - Ui.S(7), r.Top + (r.Height - action.Height) / 2);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var t = Theme.Current;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = Inner;
            var c = KindColor;
            var rf = new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1);
            Theme.FillRound(g, Theme.Mix(t.Surface, c, t.IsDark ? 0.16f : 0.09f), rf, Ui.S(8));
            Theme.DrawRound(g, Theme.Mix(t.Surface, c, 0.35f), rf, Ui.S(8));
            string glyph = kind == BannerKind.Info ? Glyphs.Info : kind == BannerKind.Success ? Glyphs.Check : Glyphs.Warning;
            Ui.DrawGlyph(g, glyph, 11f, c, new Rectangle(r.X + Ui.S(10), r.Y, Ui.S(22), r.Height));
            int right = action.Visible ? action.Left - Ui.S(8) : r.Right - Ui.S(10);
            int x = r.X + Ui.S(40);
            using (var f = Theme.UiFont(9.5f))
                TextRenderer.DrawText(g, message, f, new Rectangle(x, r.Y, right - x, r.Height), t.Text,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>Список серверов в виде карточек.</summary>
    public class ServerList : ListBox, IThemed
    {
        int hoverIndex = -1;
        public Func<Profile, VpnState?> StatusOf;
        public Func<Profile, bool> WarningOf;

        public ServerList()
        {
            DrawMode = DrawMode.OwnerDrawFixed;
            BorderStyle = BorderStyle.None;
            IntegralHeight = false;
            ItemHeight = Ui.S(66);
            Tag = "sidebar";
            SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = IndexFromPoint(e.Location);
            if (i != hoverIndex)
            {
                int old = hoverIndex;
                hoverIndex = i;
                if (old >= 0 && old < Items.Count) Invalidate(GetItemRectangle(old));
                if (i >= 0) Invalidate(GetItemRectangle(i));
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (hoverIndex >= 0 && hoverIndex < Items.Count) Invalidate(GetItemRectangle(hoverIndex));
            hoverIndex = -1;
            base.OnMouseLeave(e);
        }

        static readonly Color[] AvatarColors =
        {
            Color.FromArgb(37, 99, 235), Color.FromArgb(124, 58, 237), Color.FromArgb(219, 39, 119),
            Color.FromArgb(5, 150, 105), Color.FromArgb(217, 119, 6), Color.FromArgb(8, 145, 178),
        };

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            var t = Theme.Current;
            var g = e.Graphics;
            using (var bb = new SolidBrush(t.Surface)) g.FillRectangle(bb, e.Bounds);
            if (e.Index < 0 || e.Index >= Items.Count) return;
            var p = Items[e.Index] as Profile;
            if (p == null) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            bool sel = (e.State & DrawItemState.Selected) != 0;
            var card = new RectangleF(e.Bounds.X + Ui.S(8), e.Bounds.Y + Ui.S(3), e.Bounds.Width - Ui.S(16), e.Bounds.Height - Ui.S(6));
            if (sel) Theme.FillRound(g, t.Selected, card, Ui.S(9));
            else if (e.Index == hoverIndex) Theme.FillRound(g, t.Hover, card, Ui.S(9));

            // аватар
            int av = Ui.S(36);
            var ar = new Rectangle((int)card.X + Ui.S(10), (int)(card.Y + (card.Height - av) / 2), av, av);
            var name = p.DisplayName;
            var ac = AvatarColors[(int)((uint)name.GetHashCode() % AvatarColors.Length)];
            using (var b = new SolidBrush(ac)) g.FillEllipse(b, ar);
            using (var f = Theme.SemiboldFont(11f))
                TextRenderer.DrawText(g, name.Substring(0, 1).ToUpperInvariant(), f, ar, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            // статус-точка на аватаре
            var st = StatusOf == null ? null : StatusOf(p);
            if (st.HasValue && st.Value != VpnState.Disconnected)
            {
                bool warn = WarningOf != null && WarningOf(p);
                int d = Ui.S(12);
                var dr = new Rectangle(ar.Right - d + Ui.S(2), ar.Bottom - d + Ui.S(2), d, d);
                using (var b = new SolidBrush(sel ? t.Selected : t.Surface)) g.FillEllipse(b, Rectangle.Inflate(dr, Ui.S(2), Ui.S(2)));
                using (var b = new SolidBrush(warn ? t.Orange : t.ForState(st.Value))) g.FillEllipse(b, dr);
            }

            int x = ar.Right + Ui.S(12);
            int w = (int)card.Right - x - Ui.S(8);
            using (var nf = Theme.SemiboldFont(10f))
            using (var sf = Theme.UiFont(8.5f))
            {
                int y = (int)(card.Y + (card.Height - nf.Height - sf.Height - Ui.S(2)) / 2);
                var flags = TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
                TextRenderer.DrawText(g, name, nf, new Rectangle(x, y, w, nf.Height), sel ? t.Accent : t.Text, flags);
                string sub;
                if (st.HasValue && st.Value == VpnState.Connected) sub = WarningOf != null && WarningOf(p) ? "Трафик идёт мимо VPN" : "Подключено";
                else if (st.HasValue && st.Value == VpnState.Connecting) sub = "Подключение…";
                else if (st.HasValue && st.Value == VpnState.Error) sub = "Ошибка подключения";
                else sub = (p.Addresses.Count > 0 ? p.Addresses[0] : p.Hostname) + " · " + (p.UpstreamProtocol == "http3" ? "HTTP/3" : "HTTP/2");
                Color subColor = st.HasValue && st.Value == VpnState.Connected && !(WarningOf != null && WarningOf(p)) ? t.Green :
                                 st.HasValue && st.Value != VpnState.Disconnected ? t.ForState(st.Value) : t.Muted;
                TextRenderer.DrawText(g, sub, sf, new Rectangle(x, y + nf.Height + Ui.S(2), w, sf.Height), subColor, flags);
            }
        }
    }
}
