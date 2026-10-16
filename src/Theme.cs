using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TrustTunnelGui
{
    /// <summary>Цветовая схема приложения. Светлая и тёмная, по умолчанию — как в Windows.</summary>
    public sealed class Theme
    {
        public bool IsDark;
        public Color Bg, Surface, Input, Border, Text, Muted, Accent, AccentHover, Hover, Selected;
        public Color Green, Orange, Red, Grey;

        public static readonly Theme Light = new Theme
        {
            IsDark = false,
            Bg = Hex(0xF3F5F9),
            Surface = Hex(0xFFFFFF),
            Input = Hex(0xF7F8FA),
            Border = Hex(0xDFE3EA),
            Text = Hex(0x1B1F27),
            Muted = Hex(0x6B7280),
            Accent = Hex(0x2563EB),
            AccentHover = Hex(0x1D4ED8),
            Hover = Hex(0xEEF2F8),
            Selected = Hex(0xE7EEFD),
            Green = Hex(0x16A34A),
            Orange = Hex(0xE08A0B),
            Red = Hex(0xDC2626),
            Grey = Hex(0x8A93A3),
        };

        public static readonly Theme Dark = new Theme
        {
            IsDark = true,
            Bg = Hex(0x14161B),
            Surface = Hex(0x1D2027),
            Input = Hex(0x252932),
            Border = Hex(0x323744),
            Text = Hex(0xE8EAED),
            Muted = Hex(0x9AA1AD),
            Accent = Hex(0x3B82F6),
            AccentHover = Hex(0x60A5FA),
            Hover = Hex(0x272B35),
            Selected = Hex(0x1E2C47),
            Green = Hex(0x22C55E),
            Orange = Hex(0xF59E0B),
            Red = Hex(0xEF4444),
            Grey = Hex(0x6B7383),
        };

        public static Theme Current = Light;

        public static event Action Changed;

        static Color Hex(int rgb) { return Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255); }

        public static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        public static bool SystemPrefersDark()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    return v is int && (int)v == 0;
                }
            }
            catch { return false; }
        }

        /// <summary>mode: "system" | "light" | "dark"</summary>
        public static void Select(string mode)
        {
            bool dark = mode == "dark" || (mode != "light" && SystemPrefersDark());
            var t = dark ? Dark : Light;
            if (t == Current) return;
            Current = t;
            if (Changed != null) Changed();
        }

        public Color ForState(VpnState s)
        {
            switch (s)
            {
                case VpnState.Connected: return Green;
                case VpnState.Connecting:
                case VpnState.Disconnecting: return Orange;
                case VpnState.Error: return Red;
                default: return Grey;
            }
        }

        // ---------- шрифты ----------

        public static Font UiFont(float size, FontStyle style = FontStyle.Regular)
        {
            return new Font("Segoe UI", size, style);
        }

        public static Font SemiboldFont(float size)
        {
            return new Font("Segoe UI Semibold", size);
        }

        static bool? hasIconFont;

        /// <summary>Шрифт значков Windows 10/11 (Segoe MDL2 Assets / Segoe Fluent Icons).</summary>
        public static Font IconFont(float size)
        {
            if (!hasIconFont.HasValue)
            {
                using (var f = new Font("Segoe MDL2 Assets", 10f))
                    hasIconFont = f.Name == "Segoe MDL2 Assets";
            }
            return hasIconFont.Value ? new Font("Segoe MDL2 Assets", size) : null;
        }

        // ---------- применение к стандартным контролам ----------

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);

        /// <summary>Тёмный заголовок окна (Windows 10 1809+ / 11).</summary>
        public static void ApplyTitleBar(Form f)
        {
            if (!f.IsHandleCreated) return;
            int v = Current.IsDark ? 1 : 0;
            try
            {
                if (DwmSetWindowAttribute(f.Handle, 20, ref v, 4) != 0) DwmSetWindowAttribute(f.Handle, 19, ref v, 4);
            }
            catch { }
        }

        /// <summary>Рекурсивно перекрашивает стандартные контролы. Свои контролы рисуются сами из Theme.Current.</summary>
        public static void Apply(Control root)
        {
            var t = Current;
            var form = root as Form;
            if (form != null)
            {
                form.BackColor = t.Bg;
                form.ForeColor = t.Text;
                ApplyTitleBar(form);
            }
            foreach (Control c in root.Controls) ApplyOne(c, t);
        }

        static void ApplyOne(Control c, Theme t)
        {
            var role = c.Tag as string;
            if (c is IThemed) { c.Invalidate(); }
            else if (c is TextBox)
            {
                var tb = (TextBox)c;
                tb.BackColor = role == "log" ? t.Surface : t.Input;
                tb.ForeColor = t.Text;
                tb.BorderStyle = role == "log" ? BorderStyle.None : BorderStyle.FixedSingle;
                DarkScrollbars(c, t);
            }
            else if (c is ComboBox)
            {
                var cb = (ComboBox)c;
                cb.FlatStyle = FlatStyle.Flat;
                cb.BackColor = t.Input;
                cb.ForeColor = t.Text;
                DarkScrollbars(c, t);
            }
            else if (c is NumericUpDown)
            {
                c.BackColor = t.Input;
                c.ForeColor = t.Text;
                ((NumericUpDown)c).BorderStyle = BorderStyle.FixedSingle;
            }
            else if (c is ListBox)
            {
                c.BackColor = role == "sidebar" ? t.Surface : t.Input;
                c.ForeColor = t.Text;
                DarkScrollbars(c, t);
            }
            else if (c is CheckBox || c is RadioButton)
            {
                c.ForeColor = t.Text;
                c.BackColor = Color.Transparent;
            }
            else if (c is Label)
            {
                c.ForeColor = role == "muted" ? t.Muted : role == "accent" ? t.Accent : t.Text;
                c.BackColor = Color.Transparent;
            }
            else if (c is Panel || c is TableLayoutPanel || c is FlowLayoutPanel || c is UserControl)
            {
                c.BackColor = role == "surface" ? t.Surface : role == "bg" ? t.Bg : Color.Transparent;
                c.ForeColor = t.Text;
                if (c is ScrollableControl && ((ScrollableControl)c).AutoScroll) DarkScrollbars(c, t);
            }
            foreach (Control ch in c.Controls) ApplyOne(ch, t);
        }

        static void DarkScrollbars(Control c, Theme t)
        {
            Action set = () => { try { SetWindowTheme(c.Handle, t.IsDark ? "DarkMode_Explorer" : "Explorer", null); } catch { } };
            if (c.IsHandleCreated) set();
            else c.HandleCreated += (s, e) => set();
        }

        // ---------- рисование ----------

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Color c, RectangleF r, float radius)
        {
            using (var p = RoundRect(r, radius))
            using (var b = new SolidBrush(c))
                g.FillPath(b, p);
        }

        public static void DrawRound(Graphics g, Color c, RectangleF r, float radius, float width = 1f)
        {
            using (var p = RoundRect(r, radius))
            using (var pen = new Pen(c, width))
                g.DrawPath(pen, p);
        }
    }

    /// <summary>Маркер для собственных контролов, которые рисуются из Theme.Current.</summary>
    public interface IThemed { }

    /// <summary>Тема для меню (ContextMenuStrip).</summary>
    public class ThemedMenuRenderer : ToolStripProfessionalRenderer
    {
        public ThemedMenuRenderer() : base(new Colors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Current.Text : Theme.Current.Muted;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Theme.Current.Muted;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = e.ImageRectangle;
            using (var pen = new Pen(Theme.Current.Accent, 2f))
                g.DrawLines(pen, new[]
                {
                    new PointF(r.Left + r.Width * 0.2f, r.Top + r.Height * 0.55f),
                    new PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.75f),
                    new PointF(r.Left + r.Width * 0.8f, r.Top + r.Height * 0.3f),
                });
        }

        class Colors : ProfessionalColorTable
        {
            Theme T { get { return Theme.Current; } }
            public override Color ToolStripDropDownBackground { get { return T.Surface; } }
            public override Color ImageMarginGradientBegin { get { return T.Surface; } }
            public override Color ImageMarginGradientMiddle { get { return T.Surface; } }
            public override Color ImageMarginGradientEnd { get { return T.Surface; } }
            public override Color MenuBorder { get { return T.Border; } }
            public override Color MenuItemBorder { get { return T.Hover; } }
            public override Color MenuItemSelected { get { return T.Hover; } }
            public override Color MenuItemSelectedGradientBegin { get { return T.Hover; } }
            public override Color MenuItemSelectedGradientEnd { get { return T.Hover; } }
            public override Color MenuItemPressedGradientBegin { get { return T.Hover; } }
            public override Color MenuItemPressedGradientEnd { get { return T.Hover; } }
            public override Color SeparatorDark { get { return T.Border; } }
            public override Color SeparatorLight { get { return T.Border; } }
            public override Color CheckBackground { get { return T.Selected; } }
            public override Color CheckSelectedBackground { get { return T.Selected; } }
            public override Color CheckPressedBackground { get { return T.Selected; } }
        }
    }
}
