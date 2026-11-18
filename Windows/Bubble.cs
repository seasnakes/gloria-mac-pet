using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Gloria {
    internal enum BubbleKind {
        Speech,
        Caption,
        Alert
    }

    internal sealed class BubbleCanvas : FrameworkElement {
        internal BubbleKind Kind = BubbleKind.Speech;
        internal string TagText;
        internal string BodyText = "";
        internal double TailX = 50;

        internal const double TailHeight = 9.0;
        internal const double MaxTextWidth = 250.0;
        internal const double MaxTextHeight = 168.0;
        const double ShadowMargin = 8.0;

        static readonly Typeface BodyFace = new Typeface(new FontFamily("Microsoft YaHei UI, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        static readonly Typeface CaptionFace = new Typeface(new FontFamily("Microsoft YaHei UI, Segoe UI"), FontStyles.Normal, FontWeights.Medium, FontStretches.Normal);
        static readonly Typeface TagFace = new Typeface(new FontFamily("Segoe UI, Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

        public BubbleCanvas() {
            IsHitTestVisible = false;
            Effect = new DropShadowEffect {
                Color = Palette.InkColor,
                BlurRadius = 7,
                ShadowDepth = 1.5,
                Direction = 270,
                Opacity = 0.18
            };
        }

        Size PaddingSize {
            get { return Kind == BubbleKind.Caption ? new Size(10, 6) : new Size(13, 9); }
        }

        FormattedText BuildBodyText() {
            double fontSize = Kind == BubbleKind.Caption ? 11.8 : 13.2;
            var ft = new FormattedText(
                BodyText ?? "",
                System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                Kind == BubbleKind.Caption ? CaptionFace : BodyFace,
                fontSize,
                Palette.Ink,
                Dpi.ScaleX);
            ft.MaxTextWidth = MaxTextWidth;
            ft.MaxTextHeight = MaxTextHeight;
            ft.Trimming = TextTrimming.CharacterEllipsis;
            ft.LineHeight = fontSize + 4;
            return ft;
        }

        FormattedText BuildTagText() {
            if (String.IsNullOrEmpty(TagText)) return null;
            var ft = new FormattedText(
                TagText,
                System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                TagFace,
                9.5,
                Brushes.White,
                Dpi.ScaleX);
            ft.MaxTextWidth = MaxTextWidth - 16;
            ft.MaxTextHeight = 16;
            ft.Trimming = TextTrimming.CharacterEllipsis;
            return ft;
        }

        internal Size MeasureBubble() {
            var ft = BuildBodyText();
            double w = Math.Ceiling(ft.WidthIncludingTrailingWhitespace);
            double h = Math.Ceiling(ft.Height) + 2;
            var tag = BuildTagText();
            if (tag != null) {
                double tw = Math.Ceiling(tag.WidthIncludingTrailingWhitespace) + 14;
                if (tw > w) w = tw;
                h += 19;
            }
            var pad = PaddingSize;
            return new Size(
                Math.Max(44, w + pad.Width * 2 + ShadowMargin * 2),
                h + pad.Height * 2 + TailHeight + ShadowMargin * 2);
        }

        protected override void OnRender(DrawingContext dc) {
            double w = ActualWidth;
            double h = ActualHeight;
            if (w <= ShadowMargin * 2 + 10 || h <= ShadowMargin * 2 + TailHeight + 6) return;

            var body = new Rect(
                ShadowMargin + 1.5,
                ShadowMargin + 1.5,
                w - ShadowMargin * 2 - 3.0,
                h - ShadowMargin * 2 - TailHeight - 2.5);
            double r = Kind == BubbleKind.Caption ? 9.0 : 13.0;
            r = Math.Min(r, Math.Min(body.Width * 0.45, body.Height * 0.45));
            double minTx = body.Left + r + 6.5;
            double maxTx = body.Right - r - 6.5;
            double tx = minTx <= maxTx ? Calc.Clamp(TailX, minTx, maxTx) : (body.Left + body.Right) * 0.5;

            var geo = new StreamGeometry();
            using (var ctx = geo.Open()) {
                ctx.BeginFigure(new Point(body.Left + r, body.Top), true, true);
                ctx.LineTo(new Point(body.Right - r, body.Top), true, true);
                ctx.ArcTo(new Point(body.Right, body.Top + r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
                ctx.LineTo(new Point(body.Right, body.Bottom - r), true, true);
                ctx.ArcTo(new Point(body.Right - r, body.Bottom), new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);

                // Bottom edge with integrated downward speech tail (no internal seam line!)
                ctx.LineTo(new Point(tx + 6.5, body.Bottom), true, true);
                ctx.LineTo(new Point(tx, body.Bottom + TailHeight), true, true);
                ctx.LineTo(new Point(tx - 6.5, body.Bottom), true, true);

                ctx.LineTo(new Point(body.Left + r, body.Bottom), true, true);
                ctx.ArcTo(new Point(body.Left, body.Bottom - r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
                ctx.LineTo(new Point(body.Left, body.Top + r), true, true);
                ctx.ArcTo(new Point(body.Left + r, body.Top), new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
            }
            geo.Freeze();

            Brush fill = Kind == BubbleKind.Caption ? Palette.CaptionFill
                       : Kind == BubbleKind.Alert ? Palette.AlertFill
                       : Palette.SpeechFill;
            Brush strokeBrush = Kind == BubbleKind.Caption ? Palette.Lavender
                              : Kind == BubbleKind.Alert ? Palette.Periwinkle
                              : Palette.Pink;
            var pen = new Pen(strokeBrush, Kind == BubbleKind.Alert ? 2.3 : 1.6) { LineJoin = PenLineJoin.Round };
            pen.Freeze();
            dc.DrawGeometry(fill, pen, geo);

            var pad = PaddingSize;
            double topY = body.Top + pad.Height;
            var tag = BuildTagText();
            if (tag != null) {
                double chipW = tag.WidthIncludingTrailingWhitespace + 12;
                var chipRect = new Rect(body.Left + pad.Width - 2, topY, chipW, 15.5);
                dc.DrawRoundedRectangle(Kind == BubbleKind.Alert ? Palette.Periwinkle : Palette.Pink, null, chipRect, 7.75, 7.75);
                dc.DrawText(tag, new Point(chipRect.Left + 6, chipRect.Top + (15.5 - tag.Height) * 0.5));
                topY += 19;
            }

            var ft = BuildBodyText();
            dc.DrawText(ft, new Point(body.Left + pad.Width, topY));
        }
    }

    internal sealed class BubbleWindow : IDisposable {
        readonly Window window;
        readonly BubbleCanvas canvas = new BubbleCanvas();
        IntPtr hwnd;
        int lastPxX = Int32.MinValue, lastPxY = Int32.MinValue;

        internal bool Visible { get; private set; }
        internal bool Sticky { get; private set; }
        internal BubbleKind Kind { get { return canvas.Kind; } }
        internal string Text { get { return canvas.BodyText; } }

        double shownAt;
        double hideAt;
        double fadeFrom, fadeTo, fadeStart, fadeDuration;
        bool fadingOut;

        string pendingText;
        string pendingTag;
        double? pendingDuration;
        double pendingReadyAt;

        internal BubbleWindow() {
            window = new Window {
                Title = "GLORIA · 气泡",
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = null,
                ShowInTaskbar = false,
                ShowActivated = false,
                Focusable = false,
                Topmost = true,
                ResizeMode = ResizeMode.NoResize,
                IsHitTestVisible = false,
                Width = 100,
                Height = 48,
                Opacity = 0,
                Content = canvas
            };
            window.SourceInitialized += delegate {
                hwnd = new WindowInteropHelper(window).Handle;
                NativeStyle.PassThrough(hwnd);
            };
        }

        internal void Show(string text, string tag, BubbleKind kind, double? duration, bool sticky) {
            if (String.IsNullOrWhiteSpace(text)) {
                if (!Sticky) Hide();
                return;
            }
            double t = Clock.Now;
            // Quiet caption never replaces a sticky alert, and waits 1.4 s before taking over a speech bubble.
            if (kind == BubbleKind.Caption && Visible && !fadingOut && canvas.Kind != BubbleKind.Caption && (Sticky || t < hideAt - 0.3)) {
                if (Sticky || canvas.Kind != BubbleKind.Speech) return;
                double ready = shownAt + 1.4;
                if (t < ready) {
                    pendingText = text;
                    pendingTag = tag;
                    pendingDuration = duration;
                    pendingReadyAt = ready;
                    return;
                }
            }
            if (kind != BubbleKind.Caption) pendingText = null;

            shownAt = t;
            Sticky = sticky;
            fadingOut = false;
            canvas.Kind = kind;
            canvas.TagText = String.IsNullOrEmpty(tag) ? null : tag;
            canvas.BodyText = text;
            double dur = duration.HasValue ? duration.Value : Calc.Clamp(2.4 + text.Length * 0.09, 2.6, 9.0);
            hideAt = t + dur;

            var size = canvas.MeasureBubble();
            window.Width = size.Width;
            window.Height = size.Height;
            canvas.Width = size.Width;
            canvas.Height = size.Height;
            canvas.InvalidateVisual();

            if (!Visible) {
                Visible = true;
                if (!window.IsVisible) window.Show();
                StartFade(window.Opacity, 1.0, 0.16, false);
            } else {
                StartFade(window.Opacity, 1.0, 0.08, false);
            }
        }

        internal void Hide() {
            pendingText = null;
            if (!Visible || fadingOut) return;
            Sticky = false;
            StartFade(window.Opacity, 0.0, 0.22, true);
        }

        internal void Unstick() {
            if (Sticky) {
                Sticky = false;
                hideAt = Clock.Now + 0.8;
            }
        }

        void StartFade(double from, double to, double duration, bool outMode) {
            fadeFrom = from;
            fadeTo = to;
            fadeStart = Clock.Now;
            fadeDuration = Math.Max(0.01, duration);
            fadingOut = outMode;
        }

        internal bool IsAnimating {
            get { return (Visible && fadeDuration > 0) || pendingText != null; }
        }

        // Anchor is the top of her head in DIPs (+Y down).
        internal void Follow(Point anchor, double t) {
            if (pendingText != null && t >= pendingReadyAt) {
                string pt = pendingText, pg = pendingTag;
                double? pd = pendingDuration;
                pendingText = null;
                Show(pt, pg, BubbleKind.Caption, pd, false);
            }
            if (!Visible) return;
            if (!Sticky && !fadingOut && t > hideAt) {
                Hide();
            }
            if (fadeDuration > 0) {
                double u = Calc.Clamp((t - fadeStart) / fadeDuration, 0.0, 1.0);
                window.Opacity = Calc.Lerp(fadeFrom, fadeTo, u);
                if (u >= 1.0) {
                    fadeDuration = 0;
                    if (fadingOut) {
                        fadingOut = false;
                        Visible = false;
                        window.Hide();
                        return;
                    }
                }
            }

            double sw = window.Width;
            double sh = window.Height;
            double x = anchor.X - sw * 0.5;
            double y = anchor.Y - sh + 6; // +6 accounts for bottom shadow margin so tail tip kisses anchor
            var scr = Desktop.NearestScreen(anchor);
            if (scr != null) {
                var v = Dpi.ToDip(scr.WorkingArea);
                x = Calc.Clamp(x, v.Left + 2, Math.Max(v.Left + 2, v.Right - sw - 2));
                y = Math.Max(v.Top + 2, Math.Min(y, v.Bottom - sh - 2));
            }
            double tail = anchor.X - x;
            if (Math.Abs(canvas.TailX - tail) > 0.5) {
                canvas.TailX = tail;
                canvas.InvalidateVisual();
            }

            var px = Dpi.ToPixels(new Point(x, y));
            if (px.X != lastPxX || px.Y != lastPxY) {
                lastPxX = px.X;
                lastPxY = px.Y;
                if (hwnd != IntPtr.Zero) NativeStyle.MovePixels(hwnd, px.X, px.Y);
                else { window.Left = x; window.Top = y; }
            }
        }

        public void Dispose() {
            Visible = false;
            pendingText = null;
            try { window.Close(); } catch { }
        }
    }
}
