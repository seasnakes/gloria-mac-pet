using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Gloria {
    internal static class Clock {
        static readonly long origin = Stopwatch.GetTimestamp();
        static readonly double tickSeconds = 1.0 / Stopwatch.Frequency;
        internal static double Now {
            get { return (Stopwatch.GetTimestamp() - origin) * tickSeconds; }
        }
    }

    internal static class Calc {
        internal static double Clamp(double v, double lo, double hi) {
            return v < lo ? lo : (v > hi ? hi : v);
        }
        internal static int Clamp(int v, int lo, int hi) {
            return v < lo ? lo : (v > hi ? hi : v);
        }
        internal static double Lerp(double a, double b, double t) {
            return a + (b - a) * t;
        }
        internal static string Cap(string s, int maxLen) {
            if (String.IsNullOrEmpty(s)) return "";
            string flat = s.Replace("\r", " ").Replace("\n", " ").Trim();
            while (flat.Contains("  ")) flat = flat.Replace("  ", " ");
            if (flat.Length <= maxLen) return flat;
            return flat.Substring(0, Math.Max(1, maxLen - 1)) + "…";
        }
    }

    // GLORIA 柔彩 palette (matching Sources/Support.swift)
    internal static class Palette {
        internal static readonly Color PinkColor = Color.FromRgb(246, 158, 204);
        internal static readonly Color BlushColor = Color.FromRgb(255, 237, 245);
        internal static readonly Color LavenderColor = Color.FromRgb(194, 179, 242);
        internal static readonly Color PeriwinkleColor = Color.FromRgb(140, 168, 250);
        internal static readonly Color CyanColor = Color.FromRgb(115, 209, 245);
        internal static readonly Color InkColor = Color.FromRgb(74, 54, 84);
        internal static readonly Color ButterColor = Color.FromRgb(255, 237, 158);

        internal static readonly SolidColorBrush Pink = Frozen(PinkColor);
        internal static readonly SolidColorBrush Blush = Frozen(BlushColor);
        internal static readonly SolidColorBrush Lavender = Frozen(LavenderColor);
        internal static readonly SolidColorBrush Periwinkle = Frozen(PeriwinkleColor);
        internal static readonly SolidColorBrush Cyan = Frozen(CyanColor);
        internal static readonly SolidColorBrush Ink = Frozen(InkColor);
        internal static readonly SolidColorBrush Butter = Frozen(ButterColor);
        internal static readonly SolidColorBrush SpeechFill = Frozen(Color.FromArgb(248, 255, 255, 255));
        internal static readonly SolidColorBrush CaptionFill = Frozen(Color.FromArgb(238, 255, 237, 245));
        internal static readonly SolidColorBrush AlertFill = Frozen(Color.FromArgb(250, 255, 255, 255));

        internal static SolidColorBrush Frozen(Color c) {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }

    internal static class Dpi {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        [DllImport("gdi32.dll")] static extern int GetDeviceCaps(IntPtr hdc, int index);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

        static double scaleX = 1.0, scaleY = 1.0;
        static bool initialized;

        internal static void Init() {
            if (initialized) return;
            initialized = true;
            try { SetProcessDPIAware(); } catch { }
            RefreshScale();
        }

        internal static void RefreshScale() {
            IntPtr hdc = GetDC(IntPtr.Zero);
            if (hdc != IntPtr.Zero) {
                try {
                    int dx = GetDeviceCaps(hdc, 88); // LOGPIXELSX
                    int dy = GetDeviceCaps(hdc, 90); // LOGPIXELSY
                    if (dx > 0) scaleX = dx / 96.0;
                    if (dy > 0) scaleY = dy / 96.0;
                } finally {
                    ReleaseDC(IntPtr.Zero, hdc);
                }
            }
        }

        internal static double ScaleX { get { if (!initialized) Init(); return scaleX; } }
        internal static double ScaleY { get { if (!initialized) Init(); return scaleY; } }

        internal static Point ToDip(int pxX, int pxY) {
            return new Point(pxX / ScaleX, pxY / ScaleY);
        }
        internal static Point ToDip(System.Drawing.Point pt) {
            return new Point(pt.X / ScaleX, pt.Y / ScaleY);
        }
        internal static Rect ToDip(System.Drawing.Rectangle r) {
            return new Rect(r.Left / ScaleX, r.Top / ScaleY, r.Width / ScaleX, r.Height / ScaleY);
        }
        internal static System.Drawing.Point ToPixels(Point dip) {
            return new System.Drawing.Point((int)Math.Round(dip.X * ScaleX), (int)Math.Round(dip.Y * ScaleY));
        }
    }

    internal static class NativeStyle {
        const int GWL_EXSTYLE = -20;
        const int WS_EX_TRANSPARENT = 0x00000020;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        const int WS_EX_NOACTIVATE = 0x08000000;
        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOZORDER = 0x0004;
        const uint SWP_NOACTIVATE = 0x0010;
        const uint SWP_NOSENDCHANGING = 0x0400;
        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int index, int value);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        internal static void NoActivate(IntPtr h) {
            if (h == IntPtr.Zero) return;
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        }

        internal static void PassThrough(IntPtr h) {
            if (h == IntPtr.Zero) return;
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT);
        }

        internal static void SetClickThrough(IntPtr h, bool passThrough) {
            if (h == IntPtr.Zero) return;
            int ex = GetWindowLong(h, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            int desired = passThrough ? (ex | WS_EX_TRANSPARENT) : (ex & ~WS_EX_TRANSPARENT);
            if (desired != ex || ((GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TRANSPARENT) != 0) != passThrough) {
                SetWindowLong(h, GWL_EXSTYLE, desired);
            }
        }

        internal static void MovePixels(IntPtr h, int x, int y) {
            if (h == IntPtr.Zero) return;
            SetWindowPos(h, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOSENDCHANGING);
        }

        internal static void RaiseTopmost(IntPtr h) {
            if (h == IntPtr.Zero) return;
            SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | 0x0002 /* SWP_NOMOVE */);
        }
    }

    internal static class Startup {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "GLORIA";

        static string ExeCommand {
            get { return "\"" + System.Reflection.Assembly.GetExecutingAssembly().Location + "\""; }
        }

        internal static bool IsEnabled() {
            try {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey, false)) {
                    if (key == null) return false;
                    var val = key.GetValue(ValueName) as string;
                    return !String.IsNullOrEmpty(val);
                }
            } catch {
                return false;
            }
        }

        internal static string Set(bool enabled) {
            try {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey)) {
                    if (key == null) return "无法打开注册表启动项";
                    if (enabled) key.SetValue(ValueName, ExeCommand);
                    else key.DeleteValue(ValueName, false);
                }
                return "ok";
            } catch (Exception ex) {
                return ex.Message;
            }
        }
    }

    public sealed class Preferences {
        public string[] Visible { get; set; }
        public double Size { get; set; }
        public bool WindowSit { get; set; }
        public bool Follow { get; set; }
        public bool Music { get; set; }
        public bool BeatTap { get; set; }
        public bool BrowserMusic { get; set; }
        public bool EdgePeek { get; set; }
        public bool Wander { get; set; }
        public bool Nap { get; set; }
        public bool Chase { get; set; }
        public bool Chime { get; set; }
        public bool Onboarded { get; set; }
        public Dictionary<string, double[]> Positions { get; set; }
        public Dictionary<string, string> Roles { get; set; }

        public Preferences() {
            Visible = new[] { "gloria" };
            Size = 0.62;
            WindowSit = true;
            Follow = false;
            Music = true;
            BeatTap = true;
            BrowserMusic = true;
            EdgePeek = true;
            Wander = true;
            Nap = true;
            Chase = true;
            Chime = true;
            Onboarded = false;
            Positions = new Dictionary<string, double[]>();
            Roles = new Dictionary<string, string> {
                { "claude", "usagi" },
                { "codex", "doro" },
                { "cursor", "gloria" },
                { "dsh", "grokbot" },
                { "antigravity", "fourteen" }
            };
        }

        internal void Sanitize() {
            if (Visible == null || Visible.Length == 0) Visible = new[] { "gloria" };
            if (Double.IsNaN(Size) || Double.IsInfinity(Size) || Size < 0.4 || Size > 1.5) Size = 0.62;
            if (Positions == null) Positions = new Dictionary<string, double[]>();
            if (Roles == null) Roles = new Dictionary<string, string>();
            var defaults = new Dictionary<string, string> {
                { "claude", "usagi" }, { "codex", "doro" }, { "cursor", "gloria" }, { "dsh", "grokbot" }, { "antigravity", "fourteen" }
            };
            foreach (var kv in defaults) {
                if (!Roles.ContainsKey(kv.Key) || String.IsNullOrEmpty(Roles[kv.Key])) Roles[kv.Key] = kv.Value;
            }
        }
    }
}
