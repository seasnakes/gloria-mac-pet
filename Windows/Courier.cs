using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Gloria {
    // Subagent mini-clone window (0.62x scale, 0.88 opacity, click-through) matching Sources/Pet.swift Clone.
    internal sealed class SubagentClone : IDisposable {
        internal readonly string Id;
        internal readonly string SessionId;
        internal readonly double Born = Clock.Now;
        internal double? LeavingAt;
        internal double Offset;
        internal readonly double TargetOffset;

        readonly Character art;
        readonly Window window;
        readonly Image image;
        readonly ModelScene model;
        readonly FrameworkElement visual;
        IntPtr hwnd;
        double scale;
        double footLocalX, footLocalY;
        int shownCell = -1;
        int lastPxX = Int32.MinValue, lastPxY = Int32.MinValue;

        internal SubagentClone(Character art, string id, string sessionId, double petScale, double targetOffset) {
            this.art = art;
            Id = id;
            SessionId = sessionId ?? "";
            TargetOffset = targetOffset;

            if (art.IsModel) {
                model = new ModelScene(art, art.Id);
                visual = model;
            } else {
                image = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                visual = image;
            }

            var canvas = new Canvas { IsHitTestVisible = false };
            canvas.Children.Add(visual);

            window = new Window {
                Title = "GLORIA · 分身 · " + art.Id,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = null,
                ShowInTaskbar = false,
                ShowActivated = false,
                Focusable = false,
                Topmost = true,
                ResizeMode = ResizeMode.NoResize,
                IsHitTestVisible = false,
                Opacity = 0.88,
                Content = canvas
            };
            window.SourceInitialized += delegate {
                hwnd = new WindowInteropHelper(window).Handle;
                NativeStyle.PassThrough(hwnd);
            };
            Resize(petScale * 0.62);
            window.Show();
        }

        internal void Resize(double cloneScale) {
            scale = cloneScale;
            double sw = art.W * scale;
            double sh = art.H * scale;
            double footInset = Math.Ceiling(16 * scale) + 2;
            double winW = Math.Ceiling(sw * 1.2);
            double winH = Math.Ceiling(sh * 1.1) + footInset;

            footLocalX = winW * 0.5;
            footLocalY = winH - footInset;

            visual.Width = sw;
            visual.Height = sh;
            Canvas.SetLeft(visual, footLocalX - sw * 0.5);
            Canvas.SetTop(visual, footLocalY - sh * (art.FeetY / art.H));

            window.Width = winW;
            window.Height = winH;
            lastPxX = Int32.MinValue;
        }

        // Updates clone position & pose; returns false once fade-out finishes.
        internal bool Step(Point parentFeet, Ledge? seat, double petScale, double t, double dt) {
            double speed = 120.0 * petScale / 0.62;
            string animName = "work";
            int frame = art.FrameAt("work", t - Born, 1.0);

            if (LeavingAt.HasValue) {
                double u = t - LeavingAt.Value;
                if (u < 0.85) {
                    animName = "review";
                    frame = (int)(u / 0.14) % art.Get("review").Count;
                } else if (u < 1.4) {
                    animName = "wave";
                    frame = (int)(u / 0.12) % art.Get("wave").Count;
                } else {
                    return false;
                }
                window.Opacity = Calc.Clamp(0.88 * (1.5 - u) / 0.55, 0.0, 0.88);
            } else if (Math.Abs(TargetOffset - Offset) > 1.0) {
                double dir = TargetOffset > Offset ? 1.0 : -1.0;
                Offset += dir * Math.Min(Math.Abs(TargetOffset - Offset), speed * dt);
                animName = dir > 0 ? "runRight" : "runLeft";
                frame = (int)(t / 0.075) % art.Get(animName).Count;
            }

            if (image != null) {
                int cell = art.CellIndex(animName, frame);
                if (cell != shownCell) {
                    shownCell = cell;
                    image.Source = art.Cell(cell);
                }
            } else if (model != null) {
                model.Draw(art.Pose(animName), frame, 0, t, null);
            }

            double x = parentFeet.X + Offset;
            if (seat.HasValue) {
                x = Calc.Clamp(x, seat.Value.Left + 20, Math.Max(seat.Value.Left + 20, seat.Value.Right - 20));
            }
            var px = Dpi.ToPixels(new Point(x - footLocalX, parentFeet.Y - footLocalY));
            if (px.X != lastPxX || px.Y != lastPxY) {
                lastPxX = px.X;
                lastPxY = px.Y;
                if (hwnd != IntPtr.Zero) NativeStyle.MovePixels(hwnd, px.X, px.Y);
                else { window.Left = x - footLocalX; window.Top = parentFeet.Y - footLocalY; }
            }
            return true;
        }

        public void Dispose() {
            try { window.Close(); } catch { }
        }
    }

    // Pastel heart-sealed envelope flying along a quadratic Bezier arc between two pets (Sources/Together.swift).
    internal static class Courier {
        static Window MakeEnvelope(double size, RotateTransform tilt) {
            var ink = Palette.Frozen(Color.FromRgb(224, 109, 151));
            var letter = new Canvas {
                Width = 36,
                Height = 27,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = tilt
            };
            var paper = new Rectangle {
                Width = 34,
                Height = 24,
                RadiusX = 4,
                RadiusY = 4,
                StrokeThickness = 1.5,
                Stroke = ink,
                Fill = Palette.Blush
            };
            Canvas.SetLeft(paper, 1);
            Canvas.SetTop(paper, 1);
            letter.Children.Add(paper);
            letter.Children.Add(new Path {
                Data = Geometry.Parse("M2,3 L18,15 L34,3"),
                Stroke = ink,
                StrokeThickness = 1.4,
                StrokeLineJoin = PenLineJoin.Round
            });
            letter.Children.Add(new Path {
                Data = Geometry.Parse("M18,19 C16,17 11,14 14,11 C16,9 18,11 18,12 C20,8 25,11 22,15 Z"),
                Fill = ink
            });
            var box = new Viewbox { Width = size, Height = size * 0.75, Child = letter };
            var w = new Window {
                Title = "GLORIA · 信封",
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = null,
                ShowActivated = false,
                Focusable = false,
                ShowInTaskbar = false,
                Topmost = true,
                ResizeMode = ResizeMode.NoResize,
                IsHitTestVisible = false,
                Width = size * 1.6,
                Height = size * 1.6,
                Content = new Grid { Children = { box } }
            };
            w.SourceInitialized += delegate {
                NativeStyle.PassThrough(new WindowInteropHelper(w).Handle);
            };
            return w;
        }

        internal static void Fly(Pet from, Pet to, Func<bool> alive, Action arrived) {
            var tilt = new RotateTransform();
            double size = Math.Max(27.0, from.SpriteWidth * 34.0 / 192.0);
            var window = MakeEnvelope(size, tilt);
            IntPtr hwnd = IntPtr.Zero;
            window.SourceInitialized += delegate { hwnd = new WindowInteropHelper(window).Handle; };

            Point a = from.Hands;
            Point b = to.Hands;
            double start = Clock.Now;
            double duration = Calc.Clamp(0.65 + (b - a).Length / 1700.0, 0.65, 1.45);
            int lastPxX = Int32.MinValue, lastPxY = Int32.MinValue;
            EventHandler renderHandler = null;

            renderHandler = delegate {
                if (alive != null && !alive()) {
                    CompositionTarget.Rendering -= renderHandler;
                    try { window.Close(); } catch { }
                    return;
                }
                if (from.IsVisible) a = from.Hands;
                if (to.IsVisible) b = to.Hands;

                double u = (Clock.Now - start) / duration;
                if (u >= 1.0) {
                    CompositionTarget.Rendering -= renderHandler;
                    try { window.Close(); } catch { }
                    if (arrived != null) arrived();
                    return;
                }

                double e = u < 0.5 ? 2.0 * u * u : 1.0 - 2.0 * (1.0 - u) * (1.0 - u);
                double v = 1.0 - e;
                double lift = Math.Max(70.0, Math.Abs(b.X - a.X) * 0.35);
                double cx = (a.X + b.X) * 0.5;
                double cy = Math.Min(a.Y, b.Y) - lift; // Arc upward (-Y)

                double x = v * v * a.X + 2.0 * v * e * cx + e * e * b.X;
                double y = v * v * a.Y + 2.0 * v * e * cy + e * e * b.Y;

                double angle = Math.Atan2(v * (cy - a.Y) + e * (b.Y - cy), v * (cx - a.X) + e * (b.X - cx)) * 180.0 / Math.PI;
                if (angle > 90) angle -= 180;
                else if (angle < -90) angle += 180;
                double spin = (b.X >= a.X ? -1.0 : 1.0) * Math.Sin(u * Math.PI) * 20.0;
                tilt.Angle = angle * 0.5 + spin;

                window.Opacity = Math.Min(1.0, Math.Min(u * 8.0, (1.0 - u) * 10.0));
                double leftDip = x - window.Width * 0.5;
                double topDip = y - window.Height * 0.5;
                if (!window.IsVisible) {
                    window.Left = leftDip;
                    window.Top = topDip;
                    window.Show();
                    hwnd = new WindowInteropHelper(window).Handle;
                } else {
                    var px = Dpi.ToPixels(new Point(leftDip, topDip));
                    if (px.X != lastPxX || px.Y != lastPxY) {
                        lastPxX = px.X;
                        lastPxY = px.Y;
                        if (hwnd != IntPtr.Zero) NativeStyle.MovePixels(hwnd, px.X, px.Y);
                        else { window.Left = leftDip; window.Top = topDip; }
                    }
                }
            };
            CompositionTarget.Rendering += renderHandler;
        }
    }
}
