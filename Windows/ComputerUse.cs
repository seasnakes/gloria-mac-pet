using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Gloria {
    internal enum CUKind {
        Tap, Double, Right, Type, Key, Scroll, Drag, Snap, Look, Open
    }

    internal sealed class CUAction {
        internal CUKind Kind;
        internal Point? Point1;
        internal Point? Point2;
        internal string Keys;
        internal int? Count;

        internal string Anim {
            get {
                switch (Kind) {
                    case CUKind.Tap:
                    case CUKind.Double:
                    case CUKind.Right:
                    case CUKind.Key:
                        return "tap";
                    case CUKind.Type:
                        return "type";
                    case CUKind.Scroll:
                        return "scroll";
                    case CUKind.Drag:
                        return "drag";
                    default:
                        return "snap";
                }
            }
        }

        internal string Label {
            get {
                switch (Kind) {
                    case CUKind.Tap: return "点一下";
                    case CUKind.Double: return "双击";
                    case CUKind.Right: return "右键";
                    case CUKind.Type: return Count.HasValue && Count.Value > 0 ? "打字（" + Count.Value + " 个字）" : "打字";
                    case CUKind.Key: return "按 " + (String.IsNullOrEmpty(Keys) ? "按键" : Keys);
                    case CUKind.Scroll: return "滚动页面";
                    case CUKind.Drag: return "拖过去";
                    case CUKind.Snap: return "看一眼屏幕";
                    case CUKind.Look: return "找找窗口";
                    case CUKind.Open: return "打开应用";
                    default: return "操作电脑";
                }
            }
        }
    }

    internal sealed class CUParsed {
        internal string App;
        internal List<CUAction> Actions;
    }

    internal static class ComputerUse {
        static readonly Regex CallRegex = new Regex(
            @"\.(click|doubleClick|rightClick|tripleClick|typeText|type|fill|setValue|pressKey|press|keyPress|scroll|scrollBy|drag|dragTo|getScreenshot|screenshot|getAXStateAndScreenshot|getAXState|domSnapshot|getState|listApps|getApp|getTab|createBrowserTab|goto|navigate|hover|moveMouse)\s*\(",
            RegexOptions.Compiled);
        static readonly Regex PairRegex = new Regex(@"\[\s*(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)\s*\]", RegexOptions.Compiled);
        static readonly Regex StrRegex = new Regex(@"^\s*(['""`])((?:\\.|(?!\1).)*)\1", RegexOptions.Compiled);
        static readonly Regex AppArgRegex = new Regex(@"getApp\s*\(\s*(['""`])((?:\\.|(?!\1).)*)\1", RegexOptions.Compiled);

        internal static CUParsed Parse(string source, string tool, Dictionary<string, object> input) {
            if (String.IsNullOrEmpty(tool)) return null;
            string t = tool.ToLowerInvariant();
            string code = Events.Field(input, "code");
            if (!String.IsNullOrEmpty(code) && (t.Contains("cua") || code.Contains("cua.") || t == "js")) {
                var acts = ParseCodex(code);
                if (acts.Count == 0) return null;
                var appMatch = AppArgRegex.Match(code);
                return new CUParsed {
                    App = appMatch.Success ? appMatch.Groups[2].Value : null,
                    Actions = acts
                };
            }

            if (t.StartsWith("mcp__computer-use__")) {
                string actName = t.Substring("mcp__computer-use__".Length);
                var act = ParseClaude(actName, input);
                return act != null ? new CUParsed { Actions = new List<CUAction> { act } } : null;
            }

            if (t == "computer" || t.EndsWith("__computer") || t.Contains("computer")) {
                string action = Events.Field(input, "action").ToLowerInvariant();
                if (!String.IsNullOrEmpty(action)) {
                    var act = ParseClaude(action, input);
                    if (act != null) return new CUParsed { Actions = new List<CUAction> { act } };
                }
            }

            if (t.EndsWith("__browser_batch") && input != null) {
                object listObj;
                var list = input.TryGetValue("actions", out listObj) ? listObj as IList : null;
                if (list != null) {
                    var acts = new List<CUAction>();
                    foreach (var item in list) {
                        var d = item as Dictionary<string, object>;
                        if (d == null) continue;
                        string n = Events.Field(d, "name").ToLowerInvariant();
                        string a = Events.Field(d, "action").ToLowerInvariant();
                        if (n.EndsWith("computer") && !String.IsNullOrEmpty(a)) {
                            var ca = ParseClaude(a, null);
                            if (ca != null) acts.Add(ca);
                        } else if (n.EndsWith("navigate")) {
                            acts.Add(new CUAction { Kind = CUKind.Open });
                        }
                    }
                    if (acts.Count > 0) return new CUParsed { Actions = acts };
                }
            }
            return null;
        }

        internal static bool MovesPointer(string tool) {
            if (String.IsNullOrEmpty(tool)) return false;
            string t = tool.ToLowerInvariant();
            return t.StartsWith("mcp__computer-use__") && (t.EndsWith("click") || t.EndsWith("drag"));
        }

        static Point? ReadCoord(Dictionary<string, object> input, string key) {
            if (input == null) return null;
            object v;
            var list = input.TryGetValue(key, out v) ? v as IList : null;
            if (list != null && list.Count >= 2) {
                try { return new Point(Convert.ToDouble(list[0]), Convert.ToDouble(list[1])); } catch { }
            }
            return null;
        }

        static CUAction ParseClaude(string action, Dictionary<string, object> input) {
            Point? pt = ReadCoord(input, "coordinate");
            Point? startPt = ReadCoord(input, "start_coordinate");
            switch (action) {
                case "left_click":
                case "click":
                case "middle_click":
                case "left_mouse_down":
                case "left_mouse_up":
                    return new CUAction { Kind = CUKind.Tap, Point1 = pt };
                case "double_click":
                case "triple_click":
                    return new CUAction { Kind = CUKind.Double, Point1 = pt };
                case "right_click":
                    return new CUAction { Kind = CUKind.Right, Point1 = pt };
                case "type": {
                    int? len = null;
                    object lv;
                    if (input != null && input.TryGetValue("text_len", out lv)) {
                        try { len = Convert.ToInt32(lv); } catch { }
                    }
                    return new CUAction { Kind = CUKind.Type, Point1 = pt, Count = len };
                }
                case "key":
                case "hold_key": {
                    string k = Events.Field(input, "text");
                    if (k == "") k = Events.Field(input, "key");
                    return new CUAction { Kind = CUKind.Key, Keys = PrettyKey(k) };
                }
                case "scroll":
                case "scroll_to":
                    return new CUAction { Kind = CUKind.Scroll, Point1 = pt };
                case "left_click_drag":
                case "drag":
                    return new CUAction { Kind = CUKind.Drag, Point1 = startPt ?? pt, Point2 = startPt.HasValue ? pt : null };
                case "screenshot":
                case "zoom":
                case "read_page":
                case "get_page_text":
                    return new CUAction { Kind = CUKind.Snap };
                case "open_application":
                case "switch_display":
                    return new CUAction { Kind = CUKind.Open };
                default:
                    return null;
            }
        }

        static string ExtractCallArgs(string code, int start) {
            if (start >= code.Length) return "";
            int depth = 1;
            char quote = '\0';
            int limit = Math.Min(code.Length, start + 400);
            for (int i = start; i < limit; i++) {
                char c = code[i];
                if (quote != '\0') {
                    if (c == '\\') { i++; continue; }
                    if (c == quote) quote = '\0';
                    continue;
                }
                if (c == '\'' || c == '"' || c == '`') { quote = c; continue; }
                if (c == '(') depth++;
                else if (c == ')') {
                    depth--;
                    if (depth == 0) return code.Substring(start, i - start);
                }
            }
            return code.Substring(start, limit - start);
        }

        static List<CUAction> ParseCodex(string code) {
            var outList = new List<CUAction>();
            var matches = CallRegex.Matches(code);
            foreach (Match m in matches) {
                if (outList.Count >= 8) break;
                string name = m.Groups[1].Value;
                int restStart = m.Index + m.Length;
                string args = ExtractCallArgs(code, restStart);
                var pts = new List<Point>();
                foreach (Match pm in PairRegex.Matches(args)) {
                    double px, py;
                    if (Double.TryParse(pm.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out px) &&
                        Double.TryParse(pm.Groups[2].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out py)) {
                        pts.Add(new Point(px, py));
                    }
                }
                var sm = StrRegex.Match(args);
                string s1 = sm.Success ? sm.Groups[2].Value : null;
                Point? p1 = pts.Count > 0 ? (Point?)pts[0] : null;
                Point? p2 = pts.Count > 1 ? (Point?)pts[1] : null;

                switch (name) {
                    case "click": {
                        bool right = args.Length > 0 && args.Substring(0, Math.Min(120, args.Length)).IndexOf("right", StringComparison.OrdinalIgnoreCase) >= 0;
                        outList.Add(new CUAction { Kind = right ? CUKind.Right : CUKind.Tap, Point1 = p1 });
                        break;
                    }
                    case "doubleClick":
                    case "tripleClick":
                        outList.Add(new CUAction { Kind = CUKind.Double, Point1 = p1 });
                        break;
                    case "rightClick":
                        outList.Add(new CUAction { Kind = CUKind.Right, Point1 = p1 });
                        break;
                    case "typeText":
                    case "type":
                    case "fill":
                    case "setValue":
                        outList.Add(new CUAction { Kind = CUKind.Type, Point1 = p1, Count = s1 != null ? (int?)s1.Length : null });
                        break;
                    case "pressKey":
                    case "press":
                    case "keyPress":
                        outList.Add(new CUAction { Kind = CUKind.Key, Keys = PrettyKey(s1 ?? "") });
                        break;
                    case "scroll":
                    case "scrollBy":
                        outList.Add(new CUAction { Kind = CUKind.Scroll, Point1 = p1 });
                        break;
                    case "drag":
                    case "dragTo":
                        outList.Add(new CUAction { Kind = CUKind.Drag, Point1 = p1, Point2 = p2 });
                        break;
                    case "getScreenshot":
                    case "screenshot":
                    case "getAXStateAndScreenshot":
                    case "getAXState":
                    case "domSnapshot":
                        if (outList.Count == 0 || outList[outList.Count - 1].Kind != CUKind.Snap)
                            outList.Add(new CUAction { Kind = CUKind.Snap });
                        break;
                    case "getState":
                    case "listApps":
                        if (outList.Count == 0) outList.Add(new CUAction { Kind = CUKind.Look });
                        break;
                    case "goto":
                    case "navigate":
                    case "createBrowserTab":
                    case "getTab":
                        outList.Add(new CUAction { Kind = CUKind.Open });
                        break;
                }
            }
            return outList;
        }

        internal static string PrettyKey(string k) {
            if (String.IsNullOrWhiteSpace(k)) return "";
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                { "super", "Win+" }, { "win", "Win+" }, { "cmd", "Ctrl+" }, { "command", "Ctrl+" },
                { "meta", "Win+" }, { "ctrl", "Ctrl+" }, { "control", "Ctrl+" }, { "alt", "Alt+" },
                { "option", "Alt+" }, { "shift", "Shift+" }, { "return", "↩" }, { "enter", "↩" },
                { "tab", "⇥" }, { "escape", "Esc" }, { "esc", "Esc" }, { "backspace", "⌫" },
                { "delete", "Del" }, { "space", "␣" }, { "up", "↑" }, { "down", "↓" },
                { "left", "←" }, { "right", "→" }
            };
            string[] parts = k.Split(new[] { '+', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < parts.Length; i++) {
                string p = parts[i];
                string mapped;
                if (map.TryGetValue(p, out mapped)) sb.Append(mapped);
                else sb.Append(p.Length == 1 ? p.ToUpperInvariant() : p);
            }
            string res = sb.ToString().TrimEnd('+');
            return Calc.Cap(res, 14);
        }

        internal static WinInfo? FindTargetWindow(string appHint) {
            List<WinInfo> wins;
            List<IntPtr> order;
            Desktop.Scan(out wins, out order);
            if (wins.Count == 0) return null;
            if (String.IsNullOrWhiteSpace(appHint)) {
                IntPtr fg = Desktop.GetForegroundWindow();
                for (int i = 0; i < wins.Count; i++) {
                    if (wins[i].Handle == fg) return wins[i];
                }
                return wins[0];
            }
            for (int i = 0; i < wins.Count; i++) {
                string owner = wins[i].Owner ?? "";
                if (String.Equals(owner, appHint, StringComparison.OrdinalIgnoreCase) ||
                    appHint.IndexOf(owner, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    owner.IndexOf(appHint, StringComparison.OrdinalIgnoreCase) >= 0) {
                    return wins[i];
                }
            }
            return wins[0];
        }

        internal static Point ScreenPoint(Point local, WinInfo w) {
            double qx = local.X, qy = local.Y;
            if (qx > w.Rect.Width + 4 || qy > w.Rect.Height + 4) {
                qx /= Dpi.ScaleX;
                qy /= Dpi.ScaleY;
            }
            qx = Calc.Clamp(qx, 0, w.Rect.Width);
            qy = Calc.Clamp(qy, 0, w.Rect.Height);
            return new Point(w.Rect.Left + qx, w.Rect.Top + qy);
        }
    }

    // Click-through visual effects on screen: click ripples, camera viewfinder flash, and drag trail.
    internal static class Effects {
        static double lastFlash;

        static Window MakeOverlay(Rect dipBounds, Canvas root) {
            var win = new Window {
                Title = "GLORIA · 特效",
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = null,
                ShowInTaskbar = false,
                ShowActivated = false,
                Focusable = false,
                Topmost = true,
                ResizeMode = ResizeMode.NoResize,
                IsHitTestVisible = false,
                Left = dipBounds.Left,
                Top = dipBounds.Top,
                Width = Math.Max(8, dipBounds.Width),
                Height = Math.Max(8, dipBounds.Height),
                Content = root
            };
            win.SourceInitialized += delegate {
                NativeStyle.PassThrough(new WindowInteropHelper(win).Handle);
            };
            win.Show();
            return win;
        }

        static void Retire(Window win, double seconds) {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            timer.Tick += delegate {
                timer.Stop();
                try { win.Close(); } catch { }
            };
            timer.Start();
        }

        internal static void Ripple(Point p, SolidColorBrush strokePrimary = null, bool isDouble = false) {
            if (strokePrimary == null) strokePrimary = Palette.Pink;
            const double size = 96;
            var root = new Canvas { Width = size, Height = size, IsHitTestVisible = false };
            var win = MakeOverlay(new Rect(p.X - size * 0.5, p.Y - size * 0.5, size, size), root);

            double[] delays = isDouble ? new[] { 0.0, 0.16 } : new[] { 0.0, 0.10 };
            for (int k = 0; k < 2; k++) {
                var scale = new ScaleTransform(0.4, 0.4);
                var ring = new Ellipse {
                    Width = 22,
                    Height = 22,
                    Stroke = k == 0 ? strokePrimary : Palette.Periwinkle,
                    StrokeThickness = k == 0 ? 3.0 : 2.0,
                    Fill = k == 0 ? Palette.Frozen(Color.FromArgb(60, strokePrimary.Color.R, strokePrimary.Color.G, strokePrimary.Color.B)) : Brushes.Transparent,
                    Opacity = 0,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = scale
                };
                Canvas.SetLeft(ring, size * 0.5 - 11);
                Canvas.SetTop(ring, size * 0.5 - 11);
                root.Children.Add(ring);

                var begin = TimeSpan.FromSeconds(delays[k]);
                var dur = new Duration(TimeSpan.FromSeconds(0.6));
                var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
                var growX = new DoubleAnimation(0.4, 3.5, dur) { BeginTime = begin, EasingFunction = ease };
                var growY = new DoubleAnimation(0.4, 3.5, dur) { BeginTime = begin, EasingFunction = ease };
                var fade = new DoubleAnimationUsingKeyFrames { BeginTime = begin };
                fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.09))));
                fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.60))));

                scale.BeginAnimation(ScaleTransform.ScaleXProperty, growX);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, growY);
                ring.BeginAnimation(UIElement.OpacityProperty, fade);
            }
            Retire(win, 0.85);
        }

        internal static void Flash(Rect r) {
            double t = Clock.Now;
            if (r.Width < 30 || r.Height < 30 || t - lastFlash < 2.0) return;
            lastFlash = t;

            var root = new Canvas { Width = r.Width, Height = r.Height, IsHitTestVisible = false };
            var win = MakeOverlay(r, root);

            var veil = new Border {
                Width = r.Width,
                Height = r.Height,
                CornerRadius = new CornerRadius(10),
                Background = Brushes.White,
                Opacity = 0
            };
            root.Children.Add(veil);

            var flashAnim = new DoubleAnimationUsingKeyFrames();
            flashAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            flashAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0.30, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.08))));
            flashAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.42))));
            veil.BeginAnimation(UIElement.OpacityProperty, flashAnim);

            double m = 10, k = Math.Min(36, r.Width / 6.0);
            var geo = new StreamGeometry();
            using (var ctx = geo.Open()) {
                double[,] corners = {
                    { m, m, 1, 1 },
                    { r.Width - m, m, -1, 1 },
                    { m, r.Height - m, 1, -1 },
                    { r.Width - m, r.Height - m, -1, -1 }
                };
                for (int i = 0; i < 4; i++) {
                    double cx = corners[i, 0], cy = corners[i, 1], sx = corners[i, 2], sy = corners[i, 3];
                    ctx.BeginFigure(new Point(cx + k * sx, cy), false, false);
                    ctx.LineTo(new Point(cx, cy), true, false);
                    ctx.LineTo(new Point(cx, cy + k * sy), true, false);
                }
            }
            geo.Freeze();

            var brackets = new Path {
                Data = geo,
                Stroke = Palette.Pink,
                StrokeThickness = 4,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Opacity = 0
            };
            root.Children.Add(brackets);

            var bracketAnim = new DoubleAnimationUsingKeyFrames();
            bracketAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            bracketAnim.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.07))));
            bracketAnim.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.50))));
            bracketAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.72))));
            brackets.BeginAnimation(UIElement.OpacityProperty, bracketAnim);

            Retire(win, 0.80);
        }

        internal static void Trail(Point a, Point b) {
            var bounds = new Rect(
                Math.Min(a.X, b.X) - 20,
                Math.Min(a.Y, b.Y) - 20,
                Math.Abs(b.X - a.X) + 40,
                Math.Abs(b.Y - a.Y) + 40);
            var root = new Canvas { Width = bounds.Width, Height = bounds.Height, IsHitTestVisible = false };
            var win = MakeOverlay(bounds, root);

            var line = new Line {
                X1 = a.X - bounds.Left,
                Y1 = a.Y - bounds.Top,
                X2 = a.X - bounds.Left,
                Y2 = a.Y - bounds.Top,
                Stroke = Palette.Periwinkle,
                StrokeThickness = 3,
                StrokeDashArray = new DoubleCollection(new[] { 1.5, 3.0 }),
                StrokeDashCap = PenLineCap.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            root.Children.Add(line);

            var dur = new Duration(TimeSpan.FromSeconds(0.55));
            line.BeginAnimation(Line.X2Property, new DoubleAnimation(a.X - bounds.Left, b.X - bounds.Left, dur));
            line.BeginAnimation(Line.Y2Property, new DoubleAnimation(a.Y - bounds.Top, b.Y - bounds.Top, dur));
            line.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, new Duration(TimeSpan.FromSeconds(0.35))) {
                BeginTime = TimeSpan.FromSeconds(0.75)
            });

            Ripple(a, Palette.Periwinkle);
            var endTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.55) };
            endTimer.Tick += delegate {
                endTimer.Stop();
                Ripple(b, Palette.Pink);
            };
            endTimer.Start();

            Retire(win, 1.25);
        }
    }
}
