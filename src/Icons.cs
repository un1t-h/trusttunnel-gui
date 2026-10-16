using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace TrustTunnelGui
{
    /// <summary>Иконки-«щиты» разных цветов для окна и трея, рисуются в рантайме.</summary>
    public static class Icons
    {
        public static readonly Color Grey = Color.FromArgb(120, 128, 140);
        public static readonly Color Orange = Color.FromArgb(232, 150, 30);
        public static readonly Color Green = Color.FromArgb(34, 170, 90);
        public static readonly Color Red = Color.FromArgb(214, 64, 64);
        public static readonly Color Accent = Color.FromArgb(38, 110, 220);

        static readonly Dictionary<string, Icon> cache = new Dictionary<string, Icon>();

        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

        public static Color ForState(VpnState s)
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

        public static Icon Shield(Color c, int size)
        {
            string key = c.ToArgb() + "_" + size;
            Icon ic;
            if (cache.TryGetValue(key, out ic)) return ic;
            using (var bmp = DrawShield(c, size))
            {
                IntPtr h = bmp.GetHicon();
                ic = (Icon)Icon.FromHandle(h).Clone();
                DestroyIcon(h);
            }
            cache[key] = ic;
            return ic;
        }

        public static Bitmap DrawShield(Color c, int size)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                float w = size, h = size;
                float m = size * 0.06f;
                using (var path = new GraphicsPath())
                {
                    // щит
                    path.AddLine(w / 2, m, w - m, h * 0.18f);
                    path.AddBezier(w - m, h * 0.18f, w - m, h * 0.62f, w * 0.72f, h * 0.84f, w / 2, h - m);
                    path.AddBezier(w / 2, h - m, w * 0.28f, h * 0.84f, m, h * 0.62f, m, h * 0.18f);
                    path.CloseFigure();
                    using (var br = new LinearGradientBrush(new RectangleF(0, 0, w, h), Lighten(c, 0.18f), c, 90f))
                        g.FillPath(br, path);
                }
                // «туннель» — арка внутри щита
                float pw = Math.Max(1.5f, size * 0.09f);
                using (var pen = new Pen(Color.White, pw))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    float aw = w * 0.44f, ah = h * 0.44f;
                    float ax = (w - aw) / 2, ay = h * 0.30f;
                    g.DrawArc(pen, ax, ay, aw, ah, 180, 180);
                    g.DrawLine(pen, ax, ay + ah / 2, ax, ay + ah * 0.85f);
                    g.DrawLine(pen, ax + aw, ay + ah / 2, ax + aw, ay + ah * 0.85f);
                }
            }
            return bmp;
        }

        static Color Lighten(Color c, float k)
        {
            return Color.FromArgb(c.A,
                (int)(c.R + (255 - c.R) * k),
                (int)(c.G + (255 - c.G) * k),
                (int)(c.B + (255 - c.B) * k));
        }
    }
}
