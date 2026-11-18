using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using Forms = System.Windows.Forms;

namespace Gloria {
    internal struct WinInfo {
        internal IntPtr Handle;
        internal uint Pid;
        internal string Owner;
        internal Rect Rect; // In DIPs (origin top-left, +Y down)
    }

    internal struct Ledge {
        internal IntPtr Win; // IntPtr.Zero = screen floor (top of taskbar)
        internal double Left;
        internal double Right;
        internal double Y;
        internal double Headroom;
        internal string ScreenDevice;
        internal bool IsFloor { get { return Win == IntPtr.Zero; } }
    }

    internal static class Desktop {
        [StructLayout(LayoutKind.Sequential)]
        internal struct RawRect { public int Left, Top, Right, Bottom; }

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RawRect rect);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RawRect pvAttribute, int cbAttribute);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

        [StructLayout(LayoutKind.Sequential)]
        struct LastInput { public uint size, time; }
        [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LastInput info);

        const int GWL_STYLE = -16;
        const int GWL_EXSTYLE = -20;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        const int WS_EX_TRANSPARENT = 0x00000020;
        const int WS_EX_NOACTIVATE = 0x08000000;
        const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        const int DWMWA_CLOAKED = 14;

        static readonly uint selfPid = (uint)Process.GetCurrentProcess().Id;
        static readonly object pidNamesLock = new object();
        static readonly Dictionary<uint, Tuple<string, double>> pidNames = new Dictionary<uint, Tuple<string, double>>();
        static readonly HashSet<string> skipClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
            "NotifyIconOverflowWindow", "Windows.UI.Core.CoreWindow",
            "MultitaskingViewFrame", "TaskListThumbnailWnd", "XamlExplorerHostIslandWindow"
        };

        // Fake windows for `--ctl stage` testing
        internal static List<WinInfo> Staged;

        internal static double UserIdleSeconds() {
            var d = new LastInput { size = 8 };
            if (!GetLastInputInfo(ref d)) return 0;
            uint elapsedMs = unchecked((uint)Environment.TickCount - d.time);
            return elapsedMs / 1000.0;
        }

        internal static bool Sleeping() {
            return UserIdleSeconds() > 180.0;
        }

        internal static string OwnerName(uint pid) {
            double now = Clock.Now;
            Tuple<string, double> cached;
            lock (pidNamesLock) {
                if (pidNames.TryGetValue(pid, out cached) && now - cached.Item2 < 30.0) return cached.Item1;
            }
            string name = "App";
            try {
                using (var p = Process.GetProcessById((int)pid)) {
                    name = p.ProcessName ?? "App";
                }
            } catch { }
            lock (pidNamesLock) {
                if (pidNames.Count > 256) pidNames.Clear();
                pidNames[pid] = Tuple.Create(name, now);
            }
            return name;
        }

        internal static bool TryGetVisibleBoundsDip(IntPtr hWnd, out Rect boundsDip) {
            boundsDip = Rect.Empty;
            RawRect r;
            if (DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf(typeof(RawRect))) != 0) {
                if (!GetWindowRect(hWnd, out r)) return false;
            }
            if (r.Right <= r.Left || r.Bottom <= r.Top) return false;
            var tl = Dpi.ToDip(r.Left, r.Top);
            var br = Dpi.ToDip(r.Right, r.Bottom);
            boundsDip = new Rect(tl, br);
            return true;
        }

        // Scans visible normal windows front-to-back in Z-order.
        internal static void Scan(out List<WinInfo> wins, out List<IntPtr> zOrder) {
            if (Staged != null) {
                wins = new List<WinInfo>(Staged);
                zOrder = Staged.Select(w => w.Handle).ToList();
                return;
            }
            var foundWins = new List<WinInfo>(32);
            var foundOrder = new List<IntPtr>(64);
            var clsBuf = new StringBuilder(96);

            EnumWindows((hWnd, lParam) => {
                if (!IsWindowVisible(hWnd) || IsIconic(hWnd)) return true;
                int cloaked;
                if (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out cloaked, 4) == 0 && cloaked != 0) return true;
                foundOrder.Add(hWnd);

                uint pid;
                GetWindowThreadProcessId(hWnd, out pid);
                if (pid == 0 || pid == selfPid) return true;

                int exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
                if ((exStyle & (WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE)) != 0) return true;

                clsBuf.Clear();
                GetClassName(hWnd, clsBuf, clsBuf.Capacity);
                string cls = clsBuf.ToString();
                if (skipClasses.Contains(cls)) return true;

                Rect rect;
                if (!TryGetVisibleBoundsDip(hWnd, out rect)) return true;
                if (rect.Width < 160 || rect.Height < 90) return true;

                foundWins.Add(new WinInfo {
                    Handle = hWnd,
                    Pid = pid,
                    Owner = OwnerName(pid),
                    Rect = rect
                });
                return true;
            }, IntPtr.Zero);

            wins = foundWins;
            zOrder = foundOrder;
        }

        // Uncovered spans of window top edges with enough headroom above them, plus each screen's floor.
        internal static List<Ledge> BuildLedges(List<WinInfo> wins, double headroom, bool allowWindows) {
            var outList = new List<Ledge>(wins.Count + 4);
            if (allowWindows) {
                var above = new List<Rect>(wins.Count);
                for (int idx = 0; idx < wins.Count; idx++) {
                    var w = wins[idx];
                    double y = w.Rect.Top;
                    var spans = new List<Tuple<double, double>> { Tuple.Create(w.Rect.Left, w.Rect.Right) };

                    for (int j = 0; j < above.Count; j++) {
                        var c = above[j];
                        // In +Y down: covering window covers edge `y` if `c.Top <= y + 1 && c.Bottom > y`
                        if (y >= c.Top - 1 && y < c.Bottom) {
                            var nextSpans = new List<Tuple<double, double>>();
                            for (int k = 0; k < spans.Count; k++) {
                                var s = spans[k];
                                if (c.Right <= s.Item1 || c.Left >= s.Item2) {
                                    nextSpans.Add(s);
                                } else {
                                    if (c.Left > s.Item1) nextSpans.Add(Tuple.Create(s.Item1, c.Left));
                                    if (c.Right < s.Item2) nextSpans.Add(Tuple.Create(c.Right, s.Item2));
                                }
                            }
                            spans = nextSpans;
                            if (spans.Count == 0) break;
                        }
                    }

                    if (spans.Count > 0) {
                        var screens = Forms.Screen.AllScreens;
                        for (int si = 0; si < screens.Length; si++) {
                            var scr = screens[si];
                            var v = Dpi.ToDip(scr.WorkingArea);
                            if (w.Rect.Right <= v.Left || w.Rect.Left >= v.Right) continue;
                            // Enough headroom above `y` (`y - headroom >= v.Top - 1`) and above the taskbar (`y < v.Bottom - 4`)
                            if (y - headroom >= v.Top - 1 && y < v.Bottom - 4) {
                                double hr = y - v.Top;
                                for (int k = 0; k < spans.Count; k++) {
                                    double l = Math.Max(spans[k].Item1, v.Left);
                                    double r = Math.Min(spans[k].Item2, v.Right);
                                    if (r - l >= 48) {
                                        outList.Add(new Ledge {
                                            Win = w.Handle,
                                            Left = l,
                                            Right = r,
                                            Y = y,
                                            Headroom = hr,
                                            ScreenDevice = scr.DeviceName
                                        });
                                    }
                                }
                            }
                        }
                    }
                    above.Add(w.Rect);
                }
            }

            foreach (var s in Forms.Screen.AllScreens) {
                var v = Dpi.ToDip(s.WorkingArea);
                outList.Add(new Ledge {
                    Win = IntPtr.Zero,
                    Left = v.Left,
                    Right = v.Right,
                    Y = v.Bottom,
                    Headroom = v.Height,
                    ScreenDevice = s.DeviceName
                });
            }
            return outList;
        }

        internal static Forms.Screen ScreenAt(Point dip) {
            foreach (var s in Forms.Screen.AllScreens) {
                if (Dpi.ToDip(s.Bounds).Contains(dip)) return s;
            }
            return null;
        }

        internal static Forms.Screen NearestScreen(Point dip) {
            var exact = ScreenAt(dip);
            if (exact != null) return exact;
            Forms.Screen best = Forms.Screen.PrimaryScreen;
            double bestDist = Double.MaxValue;
            foreach (var s in Forms.Screen.AllScreens) {
                var r = Dpi.ToDip(s.Bounds);
                double dx = Math.Max(Math.Max(r.Left - dip.X, 0), dip.X - r.Right);
                double dy = Math.Max(Math.Max(r.Top - dip.Y, 0), dip.Y - r.Bottom);
                double d = dx * dx + dy * dy;
                if (d < bestDist) { bestDist = d; best = s; }
            }
            return best;
        }

        internal static string ScreenLabel(Forms.Screen s, int index) {
            if (s == null) return "显示器";
            string raw = (s.DeviceName ?? "").Replace(@"\\.\DISPLAY", "显示器 ");
            if (String.IsNullOrWhiteSpace(raw)) raw = "显示器 " + (index + 1);
            return s.Primary ? raw + "（主显示器）" : raw;
        }

        internal static Point CursorDip {
            get { return Dpi.ToDip(Forms.Cursor.Position); }
        }
    }
}
