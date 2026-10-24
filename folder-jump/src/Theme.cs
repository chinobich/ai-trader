using System;
using System.Globalization;
using System.Windows.Media;
using Microsoft.Win32;

namespace FolderJump
{
    /// <summary>設定 ([Appearance]) から作る配色と表示の大きさ。</summary>
    internal sealed class Theme
    {
        public bool Light;
        public Brush CardBg, FooterBg, Text1, Text2, Text3, Text4, Accent, Accent2, Match, Green, Hover, Divider,
            ChipBg, ChipBorder, KeyBorder, Selection, CardBorder, Selected, AccentVertical, Line;
        public Color AccentColor, Accent2Color;
        public double ShadowOpacity;
        public double Zoom;
        public double CardWidth;
        public bool Compact;
        public bool Animations;
        public string Signature;

        public static Theme From(Settings s)
        {
            var t = new Theme();
            t.Signature = s.AppearanceSignature + "|" + (s.Theme == "system" ? (SystemUsesLight() ? "L" : "D") : "")
                + (s.Accent.ToLowerInvariant() == "windows" ? "|" + WindowsAccent() : "");
            t.Light = s.Theme == "light" || (s.Theme == "system" && SystemUsesLight());
            t.Zoom = s.Zoom / 100.0;
            t.CardWidth = s.Width;
            t.Compact = s.Compact;
            t.Animations = s.Animations;

            Color a, b;
            ResolveAccent(s.Accent, out a, out b);
            if (t.Light)
            {
                a = Mix(a, Colors.Black, 0.18);
                b = Mix(b, Colors.Black, 0.18);
            }
            t.AccentColor = a;
            t.Accent2Color = b;
            byte bg = (byte)Math.Round(255 * s.BackgroundOpacity / 100.0);

            if (t.Light)
            {
                t.CardBg = Solid(WithAlpha(C("#F8F9FB"), bg));
                t.FooterBg = Solid(C("#08000000"));
                t.Text1 = Solid(C("#161A22"));
                t.Text2 = Solid(C("#3E4554"));
                t.Text3 = Solid(C("#646C7D"));
                t.Text4 = Solid(C("#9AA1AF"));
                t.Match = Solid(Mix(a, Colors.Black, 0.25));
                t.Green = Solid(C("#059669"));
                t.Hover = Solid(C("#0C000000"));
                t.Divider = Solid(C("#16000000"));
                t.KeyBorder = Solid(C("#30000000"));
                t.ChipBg = Solid(WithAlpha(a, 0x18));
                t.Selected = Gradient(WithAlpha(a, 0x26), WithAlpha(b, 0x12), 0);
                t.CardBorder = Gradient(WithAlpha(a, 0x60), WithAlpha(b, 0x60), 0);
                t.ShadowOpacity = 0.22;
            }
            else
            {
                t.CardBg = Solid(WithAlpha(C("#111318"), bg));
                t.FooterBg = Solid(C("#0AFFFFFF"));
                t.Text1 = Solid(C("#F1F3F8"));
                t.Text2 = Solid(C("#B4BCCB"));
                t.Text3 = Solid(C("#7C8597"));
                t.Text4 = Solid(C("#4E5566"));
                t.Match = Solid(Mix(a, Colors.White, 0.35));
                t.Green = Solid(C("#34D399"));
                t.Hover = Solid(C("#0FFFFFFF"));
                t.Divider = Solid(C("#1AFFFFFF"));
                t.KeyBorder = Solid(C("#33FFFFFF"));
                t.ChipBg = Solid(WithAlpha(a, 0x1F));
                t.Selected = Gradient(WithAlpha(a, 0x30), WithAlpha(b, 0x18), 0);
                t.CardBorder = Gradient(WithAlpha(a, 0x70), WithAlpha(b, 0x70), 0);
                t.ShadowOpacity = 0.55;
            }
            t.Accent = Solid(a);
            t.Accent2 = Solid(b);
            t.ChipBorder = Solid(WithAlpha(a, 0x55));
            t.Selection = Solid(WithAlpha(a, 0x55));
            t.AccentVertical = Gradient(a, b, 90);
            t.Line = Gradient(WithAlpha(a, 0x55), WithAlpha(b, 0x00), 0);
            return t;
        }

        /// <summary>アクセント色の名前 → 2 色 (グラデーションの両端)。</summary>
        public static void ResolveAccent(string name, out Color a, out Color b)
        {
            string n = (name ?? "").Trim().ToLowerInvariant();
            foreach (string[] p in Settings.AccentPresets)
            {
                if (n == p[0] || n == p[1].ToLowerInvariant())
                {
                    a = C(p[2]);
                    b = C(p[3]);
                    return;
                }
            }
            Color custom;
            if (n == "windows" || n == "システム") custom = WindowsAccent();
            else if (!TryParseColor(n, out custom)) custom = C(Settings.AccentPresets[0][2]);
            a = custom;
            b = ShiftHue(custom, 45);
        }

        public static bool TryParseColor(string text, out Color c)
        {
            c = Colors.Transparent;
            string s = (text ?? "").Trim().TrimStart('#');
            uint v;
            if (s.Length != 6 || !uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v)) return false;
            c = Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
            return true;
        }

        /// <summary>Windows の「設定 → 個人用設定 → 色」で選ばれているアクセント色。</summary>
        public static Color WindowsAccent()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM"))
                {
                    object v = k == null ? null : k.GetValue("AccentColor");
                    if (v is int)
                    {
                        uint abgr = unchecked((uint)(int)v);
                        return Color.FromRgb((byte)abgr, (byte)(abgr >> 8), (byte)(abgr >> 16));
                    }
                }
            }
            catch (Exception) { }
            return C("#0078D4");
        }

        /// <summary>Windows が「アプリのモード: ライト」になっているか。</summary>
        public static bool SystemUsesLight()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    if (v is int) return (int)v != 0;
                }
            }
            catch (Exception) { }
            return false;
        }

        static Color C(string hex)
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }

        static Color WithAlpha(Color c, byte a)
        {
            return Color.FromArgb(a, c.R, c.G, c.B);
        }

        static Color Mix(Color c, Color with, double amount)
        {
            return Color.FromRgb(
                (byte)Math.Round(c.R + (with.R - c.R) * amount),
                (byte)Math.Round(c.G + (with.G - c.G) * amount),
                (byte)Math.Round(c.B + (with.B - c.B) * amount));
        }

        static Color ShiftHue(Color c, double degrees)
        {
            double r = c.R / 255.0, g = c.G / 255.0, bl = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, bl)), min = Math.Min(r, Math.Min(g, bl));
            double h = 0, s, l = (max + min) / 2;
            double d = max - min;
            if (d < 1e-6) s = 0;
            else
            {
                s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
                if (max == r) h = (g - bl) / d + (g < bl ? 6 : 0);
                else if (max == g) h = (bl - r) / d + 2;
                else h = (r - g) / d + 4;
                h *= 60;
            }
            h = (h + degrees) % 360;
            if (s < 0.15) s = 0.6; // 灰色に近い色でも 2 色目が出るように
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
            return Color.FromRgb(Hue(p, q, h + 120), Hue(p, q, h), Hue(p, q, h - 120));
        }

        static byte Hue(double p, double q, double h)
        {
            h = ((h % 360) + 360) % 360;
            double v;
            if (h < 60) v = p + (q - p) * h / 60;
            else if (h < 180) v = q;
            else if (h < 240) v = p + (q - p) * (240 - h) / 60;
            else v = p;
            return (byte)Math.Round(Math.Max(0, Math.Min(1, v)) * 255);
        }

        static Brush Solid(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        static Brush Gradient(Color a, Color b, double angle)
        {
            var g = new LinearGradientBrush(a, b, angle);
            g.Freeze();
            return g;
        }
    }
}
