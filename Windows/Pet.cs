using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Gloria {
    internal enum PetMode {
        Perched,
        Walking,
        Held,
        Airborne,
        Hopping,
        Peeking
    }

    internal sealed partial class Pet : Window {
        sealed class ShotAnim {
            internal string Anim;
            internal double Start;
            internal int Loops;
            internal int[] Cells;
        }

        sealed class HopState {
            internal Point From;
            internal Point To;
            internal Ledge TargetLedge;
            internal double Start;
            internal double Duration;
            internal double Arc;
        }

        sealed class ActingState {
            internal string Anim;
            internal double Start;
            internal double Until;
            internal bool Flip;
        }

        sealed class MeetState {
            internal Pet Target;
            internal double Deadline;
            internal Action Fire;
        }

        readonly Host host;
        internal readonly string Id;
        internal readonly Character Art;
        internal readonly BubbleWindow bubble = new BubbleWindow();
        readonly Random rng = new Random(Guid.NewGuid().GetHashCode());

        readonly Canvas rootCanvas = new Canvas { Background = null, ClipToBounds = false };
        readonly Canvas fxCanvas = new Canvas { IsHitTestVisible = false, ClipToBounds = false };
        readonly Image image;
        readonly ModelScene model;
        readonly FrameworkElement visual;

        readonly ScaleTransform poseScale = new ScaleTransform(1, 1);
        readonly RotateTransform poseRotate = new RotateTransform(0);
        readonly TranslateTransform poseTranslate = new TranslateTransform(0, 0);

        readonly DispatcherTimer calmTimer = new DispatcherTimer(DispatcherPriority.Normal);
        bool renderingSubscribed;
        IntPtr hwnd;
        bool passThroughEnabled;
        int lastPxX = Int32.MinValue, lastPxY = Int32.MinValue;

        PetMode mode = PetMode.Airborne;
        internal PetMode Mode { get { return mode; } }
        internal Point Feet = new Point(500, 500);
        Vector vel;
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int nIndex);

        static bool PrimaryMouseButtonDown() {
            int vk = GetSystemMetrics(23 /* SM_SWAPBUTTON */) != 0 ? 0x02 : 0x01;
            return (GetAsyncKeyState(vk) & 0x8000) != 0;
        }

        Ledge? seat;
        internal Ledge? Seat { get { return seat; } }
        Rect? hostRect;
        IntPtr lastAutoForeground;
        double lastAutoScanTime = -1;
        double? seatLostAt;
        double? coveredSince;
        double walkTarget;
        HopState hop;
        readonly List<MeetState> meets = new List<MeetState>();
        internal double LandedAt { get; private set; }

        double footInset;
        double footLocalX, footLocalY;

        // Animation & expressions
        ShotAnim shot;
        ActingState acting;
        double lastActHop;
        double idleStart = Clock.Now;
        double idleHold = 2.5;
        double nextIdea = Clock.Now + 6.0;
        double lookUntil;
        double lookDir = 90.0;
        bool looking;
        double lookSwitched;
        double lookDeg = 90.0;
        double jostleUntil;
        double glitchUntil;
        Tuple<double, double> landSquash; // (start, amount)
        double rot; // Clockwise degrees in WPF (+X right, +Y down)
        double lastTick = Clock.Now;
        double lastModelDraw;
        int lastBeatIndex = -1;
        bool wasDancing;
        bool wasSitting;
        double headRise;
        bool asleep;
        double lastZ;
        double lastIdleCheck;
        internal double ForceSleepUntil;
        Tuple<double, double, Point> synthHold; // (start, until, center)
        double heldSince;
        int activeParticles;

        int shownCell = -1;
        string shownPoseTitle = "";

        // Cursor & pokes
        bool chasing;
        double? chaseLost;
        double lastPounce;
        Point lastMouse;
        double mouseSpeed;
        readonly List<double> clicks = new List<double>();
        double grumpyUntil;
        internal Tuple<Point, double> FakePointer;

        // Mouse drag
        bool mousePressed;
        bool hardwareDrag;
        bool dragging;
        Point pressAt;
        Vector grabOffset;
        readonly List<Tuple<double, Point>> dragSamples = new List<Tuple<double, Point>>();
        double heldVX;

        // Husk / Agent state (managed by HuskBrain)
        bool working;
        double workSince;
        internal bool Working {
            get { return working; }
            set {
                if (value && !working) workSince = Clock.Now;
                working = value;
                EnsureActiveLoop();
            }
        }
        internal string Awaiting;
        internal string AwaitingTag = "CLAUDE CODE";
        internal readonly List<string> Roles = new List<string>();
        readonly List<SubagentClone> clones = new List<SubagentClone>();

        internal bool IsAsleep { get { return asleep; } }
        internal bool IsActing { get { return acting != null && Clock.Now < acting.Until; } }
        internal int ActiveCloneCount { get { return clones.Count(c => !c.LeavingAt.HasValue); } }

        internal double PetScale { get { return host.Prefs.Size * Art.ScaleMul; } }
        internal double SpriteWidth { get { return Art.W * PetScale; } }
        internal double SpriteHeight { get { return Art.H * PetScale; } }
        const double Gravity = 2600.0;

        Point PointerPosition {
            get {
                if (FakePointer != null && Clock.Now < FakePointer.Item2) return FakePointer.Item1;
                return Desktop.CursorDip;
            }
        }

        internal Point HeadTop {
            get {
                if (IsPeeking) {
                    var g = HeadGeo;
                    return new Point(peek.EdgeX - peek.Side * g.R * 0.7, peek.HeadY - g.R);
                }
                double rise = headRise > 4 ? headRise : SpriteHeight * 0.94;
                return new Point(Feet.X, Feet.Y - rise);
            }
        }

        internal Point Hands {
            get {
                if (IsPeeking) {
                    var g = HeadGeo;
                    return new Point(peek.EdgeX - peek.Side * g.R * 0.6, peek.HeadY + g.R * 0.4);
                }
                return new Point(Feet.X, Feet.Y - SpriteHeight * 0.5);
            }
        }

        IEnumerable<Pet> Others {
            get { return host.AllPets.Where(p => p != this); }
        }

        internal Pet(Host host, string id) {
            this.host = host;
            Id = id;
            Art = new Character(id);

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = null;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;

            if (Art.IsModel) {
                model = new ModelScene(Art, id);
                // Viewport3D stays IsHitTestVisible=false during mouse move (0ms O(1) routing via Border),
                // while Win32 ULW_ALPHA handles exact per-pixel hardware click-through outside opaque triangles.
                visual = new Border { Child = model, Background = Brushes.Transparent };
            } else {
                image = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                // Sprite cells have imperceptible alpha<=16 dust stripped to Alpha=0 at load time,
                // so Win32 ULW_ALPHA provides 0ms click-through and 0ms first-click catch with no timer lag.
                visual = new Border { Child = image, Background = Brushes.Transparent };
            }

            var tg = new TransformGroup();
            tg.Children.Add(poseScale);
            tg.Children.Add(poseRotate);
            tg.Children.Add(poseTranslate);
            visual.RenderTransformOrigin = new Point(0.5, Art.FeetY / Art.H);
            visual.RenderTransform = tg;

            rootCanvas.Children.Add(visual);
            rootCanvas.Children.Add(fxCanvas);
            Content = rootCanvas;

            SizePet();

            SourceInitialized += delegate {
                hwnd = new WindowInteropHelper(this).Handle;
                NativeStyle.NoActivate(hwnd);
                UpdateClickThrough(PointerPosition);
            };

            MouseLeftButtonDown += OnLeftDown;
            MouseMove += OnMouseMove;
            MouseLeftButtonUp += OnLeftUp;
            LostMouseCapture += delegate {
                if (mousePressed && (!hardwareDrag || !PrimaryMouseButtonDown())) {
                    FinishLeftUp();
                }
            };
            MouseRightButtonDown += (s, e) => {
                host.Menu(this);
                e.Handled = true;
            };
            MouseRightButtonUp += (s, e) => {
                e.Handled = true;
            };

            calmTimer.Interval = TimeSpan.FromMilliseconds(25);
            calmTimer.Tick += delegate {
                if (!renderingSubscribed) StepFrame();
            };

            Closed += delegate {
                UnsubscribeRendering();
                calmTimer.Stop();
                foreach (var c in clones) c.Dispose();
                clones.Clear();
                bubble.Dispose();
            };
        }

        internal void Start(Point? savedFeet, int slot, bool entrance) {
            Show();
            lastTick = Clock.Now;
            lastMouse = PointerPosition;
            if (savedFeet.HasValue) {
                Feet = savedFeet.Value;
                ClampFeetToNearestScreen();
                var snap = host.GetDesktop(0.0);
                var floor = snap.Ledges.FirstOrDefault(l => l.IsFloor && Feet.X >= l.Left - 8 && Feet.X <= l.Right + 8 && Math.Abs(Feet.Y - l.Y) < 24);
                var winLedge = snap.Ledges.FirstOrDefault(l => !l.IsFloor && Feet.X >= l.Left - 8 && Feet.X <= l.Right + 8 && Math.Abs(Feet.Y - l.Y) < 20);
                if (winLedge.Win != IntPtr.Zero) Land(winLedge, 0);
                else if (floor.ScreenDevice != null) Land(floor, 0);
                else {
                    mode = PetMode.Airborne;
                    vel = new Vector(0, 40);
                }
                PlaceBodyWindow();
            } else {
                Summon(entrance, slot, null);
            }
            StepFrame();
            calmTimer.Start();
            EnsureActiveLoop();
        }

        void SubscribeRendering() {
            if (renderingSubscribed) return;
            renderingSubscribed = true;
            lastTick = Clock.Now;
            CompositionTarget.Rendering += OnRendering;
        }

        void UnsubscribeRendering() {
            if (!renderingSubscribed) return;
            renderingSubscribed = false;
            CompositionTarget.Rendering -= OnRendering;
        }

        void OnRendering(object sender, EventArgs e) {
            double now = Clock.Now;
            if (now - lastTick < 0.007) return; // Cap at ~140 Hz so 240Hz monitors don't waste cycles
            StepFrame();
            if (!NeedsHighRate(now)) {
                UnsubscribeRendering();
            }
        }

        bool NeedsHighRate(double t) {
            if (mousePressed || dragging) return true;
            if (mode == PetMode.Held || mode == PetMode.Airborne || mode == PetMode.Hopping || mode == PetMode.Walking) return true;
            if (mode == PetMode.Peeking) return true;
            if (shot != null || landSquash != null || synthHold != null) return true;
            if (t < jostleUntil || t < glitchUntil || activeParticles > 0) return true;
            if (host.Music.Dancing || Working || Awaiting != null || IsActing) return true;
            if (clones.Count > 0 || bubble.IsAnimating) return true;
            if (Math.Abs(rot) > 0.1) return true;
            if (model != null && (!model.Settled || looking)) return true;
            return false;
        }

        internal void EnsureActiveLoop() {
            if (IsLoaded || IsVisible) SubscribeRendering();
        }

        internal void SizePet() {
            double s = PetScale;
            double sw = Art.W * s;
            double sh = Art.H * s;
            footInset = Math.Ceiling(58.0 * s) + 3.0;
            double winW = Math.Ceiling(sw * 1.9);
            double winH = Math.Ceiling(sh * 1.62) + footInset;

            footLocalX = winW * 0.5;
            footLocalY = winH - footInset;

            visual.Width = sw;
            visual.Height = sh;
            if (model != null) {
                model.Width = sw;
                model.Height = sh;
            }
            Canvas.SetLeft(visual, footLocalX - sw * 0.5);
            Canvas.SetTop(visual, footLocalY - sh * (Art.FeetY / Art.H));

            fxCanvas.Width = winW;
            fxCanvas.Height = winH;
            rootCanvas.Width = winW;
            rootCanvas.Height = winH;
            Width = winW;
            Height = winH;
            lastPxX = Int32.MinValue;

            foreach (var c in clones) c.Resize(s);
            PlaceBodyWindow();
        }

        void ClampFeetToNearestScreen() {
            var scr = Desktop.NearestScreen(Feet);
            if (scr == null) return;
            var v = Dpi.ToDip(scr.WorkingArea);
            double half = SpriteWidth * 0.3;
            Feet = new Point(
                Calc.Clamp(Feet.X, v.Left + half, Math.Max(v.Left + half, v.Right - half)),
                Calc.Clamp(Feet.Y, v.Top + SpriteHeight * 0.6, v.Bottom));
        }

        bool FitsHeadroom(Ledge l) {
            return l.IsFloor || l.Headroom >= SpriteHeight * 0.85;
        }

        internal Point ScreenToWindowPoint(Point screenDip) {
            return new Point(screenDip.X - (Feet.X - footLocalX), screenDip.Y - (Feet.Y - footLocalY));
        }

        internal void Summon(bool entrance, int slot = 0, Forms.Screen targetScreen = null) {
            peek = null;
            hop = null;
            chasing = false;
            var snap = host.GetDesktop(0.0);
            var cursor = PointerPosition;
            var scr = targetScreen ?? Desktop.NearestScreen(cursor) ?? Forms.Screen.PrimaryScreen;
            var f = Dpi.ToDip(scr.Bounds);
            var v = Dpi.ToDip(scr.WorkingArea);
            var mouse = f.Contains(cursor) ? cursor : new Point(f.Left + f.Width * 0.5, f.Top + f.Height * 0.5);

            var onScreen = snap.Ledges.Where(l =>
                l.Y >= v.Top && l.Y <= v.Bottom + 2 && l.Right > v.Left && l.Left < v.Right && FitsHeadroom(l)).ToList();

            var roomy = host.Prefs.WindowSit
                ? onScreen.Where(l => !l.IsFloor && (l.Right - l.Left) >= SpriteWidth * 0.85).ToList()
                : new List<Ledge>();

            Ledge chosen;
            if (roomy.Count > 0) chosen = roomy[0];
            else if (onScreen.Any(l => l.IsFloor)) chosen = onScreen.First(l => l.IsFloor);
            else chosen = new Ledge { Win = IntPtr.Zero, Left = v.Left, Right = v.Right, Y = v.Bottom, Headroom = v.Height, ScreenDevice = scr.DeviceName };

            double inset = Math.Min(SpriteWidth * 0.32, (chosen.Right - chosen.Left) * 0.5);
            int[] spreadOrder = { 0, 1, -1, 2, -2 };
            double spread = spreadOrder[Math.Min(Math.Max(0, slot), 4)] * SpriteWidth * 0.95;
            double baseX = (!chosen.IsFloor && mouse.X > chosen.Left + inset && mouse.X < chosen.Right - inset)
                ? mouse.X
                : Calc.Clamp(chosen.Right - Math.Max(90.0, (chosen.Right - chosen.Left) * 0.22), chosen.Left + inset, chosen.Right - inset);
            double x = Calc.Clamp(baseX - spread, chosen.Left + inset, Math.Max(chosen.Left + inset, chosen.Right - inset));

            // Drop from above target, below any higher ledge over that X so she lands cleanly on `chosen`.
            double highestStartY = v.Top + SpriteHeight + 4.0;
            foreach (var o in onScreen) {
                if (o.Y < chosen.Y && x >= o.Left - 8 && x <= o.Right + 8) {
                    highestStartY = Math.Max(highestStartY, o.Y + 2.0);
                }
            }
            double startY = Math.Max(highestStartY, chosen.Y - (entrance ? 300.0 : 150.0));
            Feet = new Point(x, Math.Min(chosen.Y, startY));
            seat = null;
            hostRect = null;
            mode = PetMode.Airborne;
            vel = new Vector(0, entrance ? 180.0 : 0.0);
            PlaceBodyWindow();
            EnsureActiveLoop();

            if (entrance) {
                Host.Later(1150, delegate {
                    if (!IsVisible) return;
                    Play("wave", 2);
                    Say(Art.Lines.Greeting, Art.Tag, BubbleKind.Speech, 3.4, false);
                });
            }
        }

        // Recall used by tray double-click / CLI `recall`: summons gracefully onto active desktop.
        internal void Recall() {
            Summon(false, host.Slot(Id), null);
        }

        // MARK: Reactions & Particles

        internal void Say(string text, string tag = null, BubbleKind kind = BubbleKind.Speech, double? duration = null, bool sticky = false) {
            bubble.Show(text, tag, kind, duration, sticky);
            bubble.Follow(HeadTop, Clock.Now);
            EnsureActiveLoop();
        }

        internal void Play(string animName, int loops = 1, int[] cells = null) {
            if (mode == PetMode.Held || mode == PetMode.Airborne || mode == PetMode.Hopping) return;
            if (Awaiting != null && animName != "waiting") return;
            if (mode == PetMode.Walking) mode = PetMode.Perched;
            shot = new ShotAnim {
                Anim = Art.Pose(animName),
                Start = Clock.Now,
                Loops = Math.Max(1, loops),
                Cells = cells
            };
            EnsureActiveLoop();
            UpdateDisplayImmediate();
        }

        internal void Glitch(double seconds = 1.0) {
            glitchUntil = Clock.Now + seconds;
            EnsureActiveLoop();
        }

        internal void Sparkle(string text = "♥", SolidColorBrush brush = null, int count = 3) {
            if (brush == null) brush = Palette.Pink;
            for (int i = 0; i < count; i++) {
                int delayMs = i * 120;
                if (delayMs == 0) SpawnParticle(text, brush, 15.0 * PetScale / 0.62);
                else Host.Later(delayMs, delegate { if (IsVisible) SpawnParticle(text, brush, 15.0 * PetScale / 0.62); });
            }
        }

        void SpawnParticle(string text, SolidColorBrush color, double fontSize) {
            fontSize = Calc.Clamp(fontSize, 11.0, 28.0);
            double sx = footLocalX + (rng.NextDouble() * 0.84 - 0.42) * SpriteWidth;
            double sy = footLocalY - SpriteHeight * (0.50 + rng.NextDouble() * 0.30);

            var drift = new TranslateTransform(0, 0);
            var grow = new ScaleTransform(0.6, 0.6);
            var tg = new TransformGroup();
            tg.Children.Add(grow);
            tg.Children.Add(drift);

            var tb = new TextBlock {
                Text = text,
                FontSize = fontSize,
                FontWeight = FontWeights.Bold,
                Foreground = color,
                IsHitTestVisible = false,
                Opacity = 0,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = tg,
                Effect = new DropShadowEffect { Color = Colors.White, BlurRadius = 3, ShadowDepth = 0, Opacity = 0.9 }
            };
            Canvas.SetLeft(tb, sx - fontSize * 0.5);
            Canvas.SetTop(tb, sy - fontSize * 0.5);
            fxCanvas.Children.Add(tb);
            activeParticles++;
            EnsureActiveLoop();

            var dur = new Duration(TimeSpan.FromSeconds(1.25));
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            double dx = rng.NextDouble() * 36.0 - 18.0;
            double dy = -Math.Min(sy - fontSize, SpriteHeight * 0.50);

            drift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, dx, dur) { EasingFunction = ease });
            drift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, dy, dur) { EasingFunction = ease });
            grow.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.6, 1.15, dur) { EasingFunction = ease });
            grow.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.6, 1.15, dur) { EasingFunction = ease });

            var fade = new DoubleAnimationUsingKeyFrames();
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.18))));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.75))));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.25))));
            fade.Completed += delegate {
                fxCanvas.Children.Remove(tb);
                activeParticles = Math.Max(0, activeParticles - 1);
            };
            tb.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        // `vyUp` > 0 throws her UPWARD into the air (matching Sources/Pet.swift throwHer).
        internal void ThrowHer(double vx, double vyUp) {
            peek = null;
            seat = null;
            hostRect = null;
            hop = null;
            chasing = false;
            mode = PetMode.Airborne;
            vel = new Vector(vx, -vyUp);
            EnsureActiveLoop();
        }

        internal void SimulateHold(double seconds) {
            double t = Clock.Now;
            synthHold = Tuple.Create(t, t + seconds, new Point(Feet.X, Feet.Y - SpriteHeight * 1.2));
            grabOffset = new Vector(0, SpriteHeight * 1.2);
            StartHold();
        }

        void StartHold() {
            peek = null;
            mode = PetMode.Held;
            heldSince = Clock.Now;
            seat = null;
            hostRect = null;
            shot = null;
            hop = null;
            chasing = false;
            Wake(true);
            NativeStyle.RaiseTopmost(hwnd);
            if (!bubble.Sticky) bubble.Hide();
            EnsureActiveLoop();
        }

        Point HeldFeet(Point cursorDip, double t) {
            if (!Art.HasExtras) return new Point(cursorDip.X + grabOffset.X, cursorDip.Y + grabOffset.Y);
            // Hang below cursor by neck `Hang * PetScale` (+Y down -> Feet.Y = cursor.Y + Hang * PetScale)
            var hangVec = new Vector(0, Art.Hang * PetScale);
            double u = Calc.Clamp((t - heldSince) / 0.16, 0.0, 1.0);
            return new Point(
                cursorDip.X + Calc.Lerp(grabOffset.X, hangVec.X, u),
                cursorDip.Y + Calc.Lerp(grabOffset.Y, hangVec.Y, u));
        }

        // MARK: Subagent Clones

        internal void SpawnClone(string cloneId, string sessionId = "") {
            int activeCount = clones.Count(x => !x.LeavingAt.HasValue);
            if (activeCount >= 4 || clones.Any(x => x.Id == cloneId && !x.LeavingAt.HasValue)) return;
            double side = (activeCount % 2 == 0) ? 1.0 : -1.0;
            double dist = SpriteWidth * (0.62 + 0.42 * (activeCount / 2));
            var clone = new SubagentClone(Art, cloneId, sessionId, PetScale, side * dist);
            clone.Step(Feet, seat, PetScale, Clock.Now, 0.016);
            clones.Add(clone);
            Glitch(0.35);
            EnsureActiveLoop();
        }

        internal void RetireClone(string cloneId, string sessionId = null) {
            double t = Clock.Now;
            if (!String.IsNullOrEmpty(cloneId)) {
                foreach (var c in clones) {
                    if (c.Id == cloneId && !c.LeavingAt.HasValue) c.LeavingAt = t;
                }
            } else {
                // If SubagentStop lacked an agent_id, retire only ONE active clone from this session (not all clones on the pet)
                var target = clones.FirstOrDefault(c => !c.LeavingAt.HasValue && (String.IsNullOrEmpty(sessionId) || c.SessionId == sessionId))
                          ?? clones.FirstOrDefault(c => !c.LeavingAt.HasValue);
                if (target != null) target.LeavingAt = t;
            }
            EnsureActiveLoop();
        }

        internal void RetireClonesForSession(string sessionId) {
            double t = Clock.Now;
            foreach (var c in clones) {
                if (!c.LeavingAt.HasValue && (String.IsNullOrEmpty(sessionId) || String.IsNullOrEmpty(c.SessionId) || c.SessionId == sessionId)) {
                    c.LeavingAt = t;
                }
            }
            EnsureActiveLoop();
        }

        internal void RetireAllClones() {
            double t = Clock.Now;
            foreach (var c in clones) {
                if (!c.LeavingAt.HasValue) c.LeavingAt = t;
            }
            EnsureActiveLoop();
        }

        void UpdateClones(double t, double dt) {
            if (clones.Count == 0) return;
            for (int i = clones.Count - 1; i >= 0; i--) {
                if (!clones[i].Step(Feet, seat, PetScale, t, dt)) {
                    clones[i].Dispose();
                    clones.RemoveAt(i);
                }
            }
        }

        // MARK: Mouse Input

        void OnLeftDown(object sender, MouseButtonEventArgs e) {
            if (e.ClickCount >= 2) {
                mousePressed = false;
                hardwareDrag = false;
                dragging = false;
                ReleaseMouseCapture();
                if (IsPeeking) {
                    LeavePeek();
                } else {
                    Play("jump", 1);
                    Sparkle("✦", Palette.Periwinkle, 2);
                }
                e.Handled = true;
                return;
            }

            var m = PointerPosition;
            pressAt = m;
            grabOffset = Feet - m;
            mousePressed = true;
            hardwareDrag = PrimaryMouseButtonDown();
            dragging = false;
            heldVX = 0;
            dragSamples.Clear();
            dragSamples.Add(Tuple.Create(Clock.Now, m));
            NativeStyle.SetClickThrough(hwnd, false);
            passThroughEnabled = false;
            CaptureMouse();
            EnsureActiveLoop();
            e.Handled = true;
        }

        void AdvanceDrag(Point m) {
            if (!mousePressed) return;
            if (!dragging && (m - pressAt).Length > 3.5) {
                dragging = true;
                StartHold();
            }
            if (!dragging) return;

            double t = Clock.Now;
            if (dragSamples.Count == 0 || (m - dragSamples[dragSamples.Count - 1].Item2).Length > 0.25 || t - dragSamples[dragSamples.Count - 1].Item1 > 0.014) {
                dragSamples.Add(Tuple.Create(t, m));
                dragSamples.RemoveAll(s => t - s.Item1 > 0.10);
            }
            Feet = HeldFeet(m, t);
            PlaceBodyWindow();
            bubble.Follow(HeadTop, t);
        }

        void OnMouseMove(object sender, MouseEventArgs e) {
            if (!mousePressed) return;
            AdvanceDrag(PointerPosition);
        }

        Vector SampleDragVelocity() {
            if (dragSamples.Count < 2) return new Vector(0, 0);
            var a = dragSamples[0];
            var b = dragSamples[dragSamples.Count - 1];
            double dt = b.Item1 - a.Item1;
            if (dt <= 0.010 || Clock.Now - b.Item1 > 0.12) return new Vector(0, 0);
            return (b.Item2 - a.Item2) / dt;
        }

        void FinishLeftUp() {
            if (!mousePressed) return;
            mousePressed = false;
            hardwareDrag = false;
            ReleaseMouseCapture();

            if (dragging) {
                dragging = false;
                ReleaseAt(PointerPosition, SampleDragVelocity());
            } else if (!IsPeeking) {
                Pat();
            }
        }

        void OnLeftUp(object sender, MouseButtonEventArgs e) {
            if (!mousePressed) return;
            FinishLeftUp();
            e.Handled = true;
        }

        void ReleaseAt(Point cursorDip, Vector velocity) {
            double sp = velocity.Length;
            if (sp > 3200.0) velocity *= (3200.0 / sp);

            if (host.Prefs.EdgePeek && sp < 1800.0) {
                var scr = Desktop.ScreenAt(cursorDip);
                if (scr != null) {
                    var f = Dpi.ToDip(scr.Bounds);
                    double zone = Math.Max(34.0, Art.HalfWidth * PetScale * 0.7);
                    int side = (cursorDip.X - f.Left < zone) ? -1 : (f.Right - cursorDip.X < zone ? 1 : 0);
                    if (side != 0) {
                        double headY = Feet.Y - HeadGeo.Hc;
                        var ok = EdgeFree(side, side > 0 ? f.Right : f.Left, headY);
                        if (ok != null) {
                            StartPeek(side, ok, headY, false);
                            host.Remember(this);
                            return;
                        }
                    }
                }
            }

            mode = PetMode.Airborne;
            vel = velocity;
            if (sp > 1400.0) {
                string[] lines = { "哇啊啊——", "飞起来了！", "轻点轻点！" };
                Say(lines[rng.Next(lines.Length)], null, BubbleKind.Speech, 1.6, false);
            }
            EnsureActiveLoop();
        }

        void Pat() {
            if (asleep) { Wake(false); return; }
            double t = Clock.Now;
            clicks.Add(t);
            clicks.RemoveAll(c => t - c > 3.0);

            if (t < grumpyUntil) {
                Say("哼！", null, BubbleKind.Speech, 1.2, false);
                return;
            }
            if (clicks.Count >= 5 && Awaiting == null) {
                clicks.Clear();
                grumpyUntil = t + 5.0;
                Play("failed", 1);
                Sparkle("💢", Palette.Pink, 2);
                Say(Art.Lines.Angry[rng.Next(Art.Lines.Angry.Length)], null, BubbleKind.Speech, 2.6, false);
                return;
            }
            if (Awaiting != null) {
                string who = AwaitingTag == "CODEX" ? "Codex" : (AwaitingTag == "CURSOR" ? "Cursor" : "Claude Code");
                Say("要你回到 " + who + " 里批准哦～我只负责提醒。", AwaitingTag, BubbleKind.Speech, 4.0, false);
                return;
            }
            Play("wave", 2);
            Sparkle("♥", Palette.Pink, 3);
            if (rng.Next(3) == 0 || !bubble.Visible) {
                Say(Art.Lines.Pats[rng.Next(Art.Lines.Pats.Length)], null, BubbleKind.Speech, 2.4, false);
            }
        }

        // MARK: Computer Use & Together

        internal void Act(CUAction a, WinInfo? targetWin, Point? screenPt, string tag) {
            double t = Clock.Now;
            Wake(true);
            if (chasing) { chasing = false; if (mode == PetMode.Walking) mode = PetMode.Perched; }

            Point? target = screenPt;
            if (!target.HasValue && targetWin.HasValue) {
                var wr = targetWin.Value.Rect;
                target = new Point(wr.Left + wr.Width * 0.5, wr.Top + 80);
            }

            if (targetWin.HasValue && (mode == PetMode.Perched || mode == PetMode.Walking) &&
                (!seat.HasValue || seat.Value.Win != targetWin.Value.Handle) && t - lastActHop > 2.5 && Awaiting == null) {
                var snap = host.GetDesktop(0.0);
                var w = targetWin.Value;
                var winLedge = snap.Ledges.FirstOrDefault(l => l.Win == w.Handle && (l.Right - l.Left) >= SpriteWidth * 0.85);
                if (winLedge.Win != IntPtr.Zero) {
                    lastActHop = t;
                    double margin = SpriteWidth * 0.3;
                    double x = target.HasValue ? target.Value.X : (winLedge.Left + winLedge.Right) * 0.5;
                    x += (x - winLedge.Left > (winLedge.Right - winLedge.Left) * 0.5 ? -1.0 : 1.0) * SpriteWidth * 0.7;
                    HopTo(winLedge, Calc.Clamp(x, winLedge.Left + margin, winLedge.Right - margin));
                } else if (seat.HasValue && seat.Value.IsFloor && target.HasValue && Math.Abs(target.Value.X - Feet.X) > SpriteWidth * 1.3) {
                    lastActHop = t;
                    double margin = SpriteWidth * 0.3;
                    walkTarget = Calc.Clamp(
                        target.Value.X + (target.Value.X > Feet.X ? -1.0 : 1.0) * SpriteWidth * 0.75,
                        seat.Value.Left + margin,
                        Math.Max(seat.Value.Left + margin, seat.Value.Right - margin));
                    mode = PetMode.Walking;
                }
            }

            string animName = Art.Pose(a.Anim);
            var spec = Art.Get(animName);
            double sideX = hop != null ? hop.To.X : Feet.X;
            bool flip = target.HasValue && (
                (spec.Facing == "left" && target.Value.X > sideX + 24.0) ||
                (spec.Facing == "right" && target.Value.X < sideX - 24.0));
            double hold = Math.Max(Math.Min(spec.TotalDuration, 3.2), a.Kind == CUKind.Type ? 2.4 : 1.3);

            if (acting != null && acting.Anim == animName && t < acting.Until) {
                acting.Until = t + hold;
                acting.Flip = flip;
            } else {
                acting = new ActingState { Anim = animName, Start = t, Until = t + hold, Flip = flip };
            }
            if (target.HasValue && String.IsNullOrEmpty(spec.Facing)) FaceX(target.Value.X, hold);

            double k = PetScale / 0.62;
            switch (a.Kind) {
                case CUKind.Tap:
                case CUKind.Right:
                    if (screenPt.HasValue) Effects.Ripple(screenPt.Value, Palette.Pink, false);
                    break;
                case CUKind.Double:
                    if (screenPt.HasValue) Effects.Ripple(screenPt.Value, Palette.Pink, true);
                    break;
                case CUKind.Key:
                    SpawnParticle(String.IsNullOrEmpty(a.Keys) ? "⌨" : a.Keys, Palette.Periwinkle, 14.0 * k);
                    break;
                case CUKind.Drag:
                    if (screenPt.HasValue && targetWin.HasValue && a.Point2.HasValue) {
                        Effects.Trail(screenPt.Value, ComputerUse.ScreenPoint(a.Point2.Value, targetWin.Value));
                    } else if (screenPt.HasValue) {
                        Effects.Ripple(screenPt.Value, Palette.Periwinkle, false);
                    }
                    break;
                case CUKind.Snap:
                case CUKind.Look:
                    if (targetWin.HasValue) Effects.Flash(targetWin.Value.Rect);
                    SpawnParticle("✦", Palette.Butter, 16.0 * k);
                    break;
                case CUKind.Type: {
                    string[] glyphs = { "⌨", "a", "s", "d", "·", "k" };
                    for (int i = 0; i < 3; i++) {
                        Host.Later(i * 220, delegate {
                            if (IsVisible) SpawnParticle(glyphs[rng.Next(glyphs.Length)], Palette.Periwinkle, 13.0 * k);
                        });
                    }
                    break;
                }
                case CUKind.Scroll:
                    SpawnParticle("⇣", Palette.Cyan, 16.0 * k);
                    break;
                case CUKind.Open:
                    SpawnParticle("✧", Palette.Lavender, 15.0 * k);
                    break;
            }

            string wherePrefix = targetWin.HasValue ? (targetWin.Value.Owner + "：") : "";
            Say(wherePrefix + a.Label, null, BubbleKind.Caption, Math.Max(1.6, hold), false);
            EnsureActiveLoop();
            UpdateDisplayImmediate();
        }

        internal bool SameLedge(Pet other) {
            if (!seat.HasValue || !other.seat.HasValue) return false;
            var a = seat.Value;
            var b = other.seat.Value;
            if (!a.IsFloor && !b.IsFloor) return a.Win == b.Win;
            return a.IsFloor && b.IsFloor && Math.Abs(a.Y - b.Y) < 2.0 && a.ScreenDevice == b.ScreenDevice;
        }

        internal void FaceX(double x, double seconds = 2.2) {
            lookDir = x > Feet.X ? 90.0 : 270.0;
            lookUntil = Clock.Now + seconds;
            EnsureActiveLoop();
        }

        internal void PassNote(Pet to, string sayPose, string sayText, string sayTag, Func<bool> alive, Action onDelivered) {
            Action fire = delegate {
                if (alive != null && !alive()) return;
                FaceX(to.Feet.X);
                Play(sayPose, 1);
                if (!String.IsNullOrEmpty(sayText)) Say(sayText, sayTag, BubbleKind.Speech, 3.2, false);
                Courier.Fly(this, to, alive, onDelivered);
            };

            double w = SpriteWidth;
            if ((mode == PetMode.Perched || mode == PetMode.Walking) && SameLedge(to) &&
                Math.Abs(to.Feet.X - Feet.X) > w * 1.9 && Awaiting == null && seat.HasValue) {
                var s = seat.Value;
                double side = to.Feet.X > Feet.X ? -1.0 : 1.0;
                double margin = w * 0.3;
                walkTarget = Calc.Clamp(to.Feet.X + side * w * 1.2, s.Left + margin, Math.Max(s.Left + margin, s.Right - margin));
                chasing = false;
                mode = PetMode.Walking;
                meets.Add(new MeetState { Target = to, Deadline = Clock.Now + 1.4, Fire = fire });
                EnsureActiveLoop();
            } else {
                fire();
            }
        }

        internal void GotNote(Pet from, string noteText, string noteTag) {
            Wake(true);
            if (from != null) FaceX(from.Feet.X);
            Play("jump", 1);
            Sparkle("✉", Palette.Periwinkle, 1);
            if (!String.IsNullOrEmpty(noteText)) Say(noteText, noteTag, BubbleKind.Speech, 3.6, false);
        }

        void KeepApart(double t) {
            if (mode != PetMode.Perched || shot != null || meets.Count > 0 || chasing || !seat.HasValue) return;
            var s = seat.Value;
            double w = SpriteWidth;
            var overlap = Others.FirstOrDefault(o =>
                (o.mode == PetMode.Perched || o.mode == PetMode.Walking) &&
                SameLedge(o) &&
                Math.Abs(o.Feet.X - Feet.X) < w * 0.55 &&
                o.LandedAt <= LandedAt);
            if (overlap == null) return;

            double margin = w * 0.3;
            double prefer = Feet.X >= overlap.Feet.X ? 1.0 : -1.0;
            foreach (double side in new[] { prefer, -prefer }) {
                double x = overlap.Feet.X + side * w * 0.95;
                if (x >= s.Left + margin && x <= s.Right - margin) {
                    walkTarget = x;
                    mode = PetMode.Walking;
                    EnsureActiveLoop();
                    return;
                }
            }
        }

        // MARK: Main Frame Step

        void StepFrame() {
            double t = Clock.Now;
            double dt = Calc.Clamp(t - lastTick, 0.0005, 0.05);
            lastTick = t;

            if (peek != null && mode != PetMode.Peeking) peek = null;

            double scanMaxAge = (mode == PetMode.Perched || mode == PetMode.Walking) ? 0.16 : 0.06;
            var desk = host.GetDesktop(scanMaxAge);

            if (mode == PetMode.Walking && (Awaiting != null || host.Music.Dancing)) mode = PetMode.Perched;
            if (mode == PetMode.Walking && IsActing && Math.Abs(walkTarget - Feet.X) < 2.0) mode = PetMode.Perched;

            if (synthHold != null) {
                var m = new Point(synthHold.Item3.X + Math.Sin((t - synthHold.Item1) * 2.6) * 160.0, synthHold.Item3.Y);
                dragSamples.Add(Tuple.Create(t, m));
                dragSamples.RemoveAll(s => t - s.Item1 > 0.10);
                Feet = HeldFeet(m, t);
                if (t > synthHold.Item2) {
                    synthHold = null;
                    mode = PetMode.Airborne;
                    vel = new Vector(600, -500);
                }
            }

            if (t - lastIdleCheck > 1.0) {
                lastIdleCheck = t;
                CheckSleep(t);
                KeepApart(t);
            }

            if (mousePressed) {
                AdvanceDrag(PointerPosition);
                if (hardwareDrag && !PrimaryMouseButtonDown()) {
                    FinishLeftUp();
                }
            }

            if (meets.Count > 0) {
                for (int i = meets.Count - 1; i >= 0; i--) {
                    var m = meets[i];
                    if (mode == PetMode.Perched || t > m.Deadline) {
                        meets.RemoveAt(i);
                        m.Fire();
                    }
                }
            }

            switch (mode) {
                case PetMode.Held: break;
                case PetMode.Airborne: FlyStep(dt, desk); break;
                case PetMode.Hopping: HopStep(t); break;
                case PetMode.Walking: WalkStep(dt); break;
                case PetMode.Perched: break;
                case PetMode.Peeking: PeekStep(t, dt); break;
            }

            if (mode == PetMode.Perched || mode == PetMode.Walking) {
                HoldSeat(t, desk);
                CheckAutoFollow(t, desk);
            }

            ChaseStep(t, dt);
            AnimateBody(t, dt);
            PlaceBodyWindow();
            UpdateClones(t, dt);
            bubble.Follow(HeadTop, t);
            UpdateClickThrough(PointerPosition);

            if (mode == PetMode.Perched && shot == null && !host.Music.Dancing && Awaiting == null && !IsActing) {
                Think(t, desk);
            }
        }

        void PlaceBodyWindow() {
            double leftDip = Feet.X - footLocalX;
            double topDip = Feet.Y - footLocalY;
            var px = Dpi.ToPixels(new Point(leftDip, topDip));
            if (px.X != lastPxX || px.Y != lastPxY) {
                lastPxX = px.X;
                lastPxY = px.Y;
                if (hwnd != IntPtr.Zero) NativeStyle.MovePixels(hwnd, px.X, px.Y);
                else { Left = leftDip; Top = topDip; }
            }
        }

        void UpdateClickThrough(Point cursorDip) {
            if (hwnd == IntPtr.Zero) return;
            // Both 3D Viewport3D and 2D sprite cells render Alpha=0 outside visible character pixels,
            // so Win32 ULW_ALPHA handles per-pixel hardware click-through and first-click catch at 0ms latency
            // without toggling WS_EX_TRANSPARENT on a timer.
            if (passThroughEnabled) {
                passThroughEnabled = false;
                NativeStyle.SetClickThrough(hwnd, false);
            }
        }

        internal bool HitsScreenPoint(Point screenDip, int? overrideCell = null) {
            double winLeft = Feet.X - footLocalX;
            double winTop = Feet.Y - footLocalY;
            var winRect = new Rect(winLeft, winTop, Width, Height);
            if (!winRect.Contains(screenDip)) return false;

            // Map screenDip into local sprite coordinates, inverting poseTranslate -> poseRotate -> poseScale around feet
            double lx = screenDip.X - winLeft - footLocalX - poseTranslate.X;
            double ly = screenDip.Y - winTop - footLocalY - poseTranslate.Y;

            if (Math.Abs(poseRotate.Angle) > 0.01) {
                double rad = -poseRotate.Angle * Math.PI / 180.0;
                double cr = Math.Cos(rad), sr = Math.Sin(rad);
                double rx = lx * cr - ly * sr;
                double ry = lx * sr + ly * cr;
                lx = rx; ly = ry;
            }
            if (Math.Abs(poseScale.ScaleX) > 0.01) lx /= poseScale.ScaleX;
            if (Math.Abs(poseScale.ScaleY) > 0.01) ly /= poseScale.ScaleY;

            double sw = SpriteWidth, sh = SpriteHeight;
            double spriteX = lx + sw * 0.5;
            double spriteY = ly + sh * (Art.FeetY / Art.H);
            if (spriteX < 0 || spriteY < 0 || spriteX >= sw || spriteY >= sh) return false;

            if (model != null) {
                return model.HitsNormalized(spriteX / sw, spriteY / sh);
            }
            int cell = overrideCell.HasValue ? overrideCell.Value : (shownCell >= 0 ? shownCell : Art.CellIndex("idle", 0));
            int cx = (int)(spriteX / PetScale);
            int cy = (int)(spriteY / PetScale);
            return Art.AlphaAt(cell, cx, cy) > 16;
        }

        // MARK: Physics & Window Riding

        void FlyStep(double dt, Host.DesktopSnapshot desk) {
            vel.Y += Gravity * dt;
            vel.X *= Math.Pow(0.7, dt);
            var next = new Point(Feet.X + vel.X * dt, Feet.Y + vel.Y * dt);
            double half = SpriteWidth * 0.3;

            var here = Desktop.ScreenAt(Feet) ?? Desktop.NearestScreen(Feet);
            if (here != null) {
                var f = Dpi.ToDip(here.Bounds);
                double midY = next.Y - SpriteHeight * 0.5;
                if (next.X < f.Left + half && Desktop.ScreenAt(new Point(f.Left - 2, midY)) == null) {
                    next.X = f.Left + half;
                    vel.X = Math.Abs(vel.X) * 0.45;
                }
                if (next.X > f.Right - half && Desktop.ScreenAt(new Point(f.Right + 2, midY)) == null) {
                    next.X = f.Right - half;
                    vel.X = -Math.Abs(vel.X) * 0.45;
                }
                if (next.Y - SpriteHeight < f.Top && vel.Y < 0 && Desktop.ScreenAt(new Point(next.X, f.Top - 2)) == null) {
                    next.Y = f.Top + SpriteHeight;
                    vel.Y = Math.Abs(vel.Y) * 0.3;
                }
            }

            if (vel.Y >= 0) {
                Ledge? best = null;
                double bestCrossX = next.X;
                for (int i = 0; i < desk.Ledges.Count; i++) {
                    var l = desk.Ledges[i];
                    if ((!host.Prefs.WindowSit && !l.IsFloor) || !FitsHeadroom(l)) continue;
                    // Falling downward (+Y): check X at the exact moment the trajectory crosses `l.Y`
                    if (Feet.Y <= l.Y + 0.5 && next.Y >= l.Y) {
                        double dy = next.Y - Feet.Y;
                        double frac = dy > 1e-4 ? Calc.Clamp((l.Y - Feet.Y) / dy, 0.0, 1.0) : 1.0;
                        double crossX = Feet.X + (next.X - Feet.X) * frac;
                        if (crossX >= l.Left - 6 && crossX <= l.Right + 6) {
                            if (!best.HasValue || l.Y < best.Value.Y) {
                                best = l;
                                bestCrossX = crossX;
                            }
                        }
                    }
                }
                if (best.HasValue) {
                    double impact = vel.Y;
                    Feet = new Point(bestCrossX, best.Value.Y);
                    Land(best.Value, impact);
                    return;
                }

                if (here != null) {
                    var v = Dpi.ToDip(here.WorkingArea);
                    if (next.Y > v.Bottom + 2) {
                        var floor = new Ledge { Win = IntPtr.Zero, Left = v.Left, Right = v.Right, Y = v.Bottom, Headroom = v.Height, ScreenDevice = here.DeviceName };
                        Feet = new Point(next.X, floor.Y);
                        Land(floor, vel.Y);
                        return;
                    }
                }
            }
            Feet = next;
        }

        void Land(Ledge l, double impact) {
            mode = PetMode.Perched;
            vel = new Vector(0, 0);
            seat = l;
            var desk = host.GetDesktop(0.2);
            if (!l.IsFloor) {
                var hw = desk.Wins.FirstOrDefault(w => w.Handle == l.Win);
                hostRect = hw.Handle != IntPtr.Zero ? (Rect?)hw.Rect : null;
            } else {
                hostRect = null;
            }
            seatLostAt = null;
            coveredSince = null;

            double margin = Math.Min(SpriteWidth * 0.3, (l.Right - l.Left) * 0.5);
            double x = Calc.Clamp(Feet.X, l.Left + margin, Math.Max(l.Left + margin, l.Right - margin));
            var scr = Desktop.NearestScreen(new Point(x, l.Y - 2));
            if (scr != null) {
                var v = Dpi.ToDip(scr.WorkingArea);
                x = Calc.Clamp(x, v.Left + margin, Math.Max(v.Left + margin, v.Right - margin));
            }
            Feet = new Point(x, l.Y);

            if (impact > 40) {
                landSquash = Tuple.Create(Clock.Now, Calc.Clamp(impact / 2400.0, 0.08, 0.28));
            }
            if (impact > 1700.0) {
                shot = new ShotAnim { Anim = Art.Pose("failed"), Start = Clock.Now, Loops = 1 };
                Say(Art.Lines.Ouch[rng.Next(Art.Lines.Ouch.Length)], null, BubbleKind.Speech, 2.0, false);
            } else if (impact > 300.0 && Art.Has("jump") && Art.Get("jump").Count >= 5) {
                shot = new ShotAnim { Anim = "jump", Start = Clock.Now, Loops = 1, Cells = new[] { 3, 4 } };
            }

            nextIdea = Clock.Now + 4.0 + rng.NextDouble() * 5.0;
            LandedAt = Clock.Now;
            host.Remember(this);
            EnsureActiveLoop();
        }

        internal void Fall() {
            seat = null;
            hostRect = null;
            mode = PetMode.Airborne;
            vel = new Vector(0, -60.0);
            EnsureActiveLoop();
        }

        void HopTo(Ledge l, double targetX) {
            double margin = Math.Min(SpriteWidth * 0.3, (l.Right - l.Left) * 0.5);
            var to = new Point(Calc.Clamp(targetX, l.Left + margin, Math.Max(l.Left + margin, l.Right - margin)), l.Y);
            double d = (to - Feet).Length;
            // In +Y down: jumping to a higher window means `to.Y < Feet.Y`, height diff = `Feet.Y - to.Y`
            double climb = Math.Max(0.0, Feet.Y - to.Y);
            hop = new HopState {
                From = Feet,
                To = to,
                TargetLedge = l,
                Start = Clock.Now,
                Duration = 0.48 + Math.Min(0.45, d / 1600.0),
                Arc = Math.Max(40.0, climb) + 46.0 * PetScale / 0.62
            };
            mode = PetMode.Hopping;
            seat = null;
            hostRect = null;
            EnsureActiveLoop();
        }

        void HopStep(double t) {
            if (hop == null) { mode = PetMode.Airborne; return; }
            const double crouch = 0.12;
            if (t - hop.Start < crouch) return;
            double u = Calc.Clamp((t - hop.Start - crouch) / hop.Duration, 0.0, 1.0);
            double baseY = Calc.Lerp(hop.From.Y, hop.To.Y, u);
            double liftUp = 4.0 * u * (1.0 - u) * hop.Arc;
            Feet = new Point(Calc.Lerp(hop.From.X, hop.To.X, u), baseY - liftUp);

            if (u >= 1.0) {
                var finished = hop;
                hop = null;
                var snap = host.GetDesktop(0.0);
                if (!finished.TargetLedge.IsFloor) {
                    var match = snap.Ledges.FirstOrDefault(l =>
                        l.Win == finished.TargetLedge.Win &&
                        finished.To.X >= l.Left - 8 && finished.To.X <= l.Right + 8);
                    if (match.Win != IntPtr.Zero) {
                        Feet = new Point(Feet.X, match.Y);
                        Land(match, 400);
                        return;
                    }
                    Fall();
                } else {
                    Land(finished.TargetLedge, 400);
                }
            }
        }

        void WalkStep(double dt) {
            double speed = 95.0 * PetScale / 0.62 * (chasing ? 1.8 : 1.0);
            double dx = walkTarget - Feet.X;
            if (Math.Abs(dx) < speed * dt + 0.5) {
                Feet = new Point(walkTarget, Feet.Y);
                mode = PetMode.Perched;
                nextIdea = Clock.Now + 5.0 + rng.NextDouble() * 7.0;
                host.Remember(this);
                return;
            }
            Feet = new Point(Feet.X + (dx > 0 ? 1.0 : -1.0) * speed * dt, Feet.Y);
        }

        void HoldSeat(double t, Host.DesktopSnapshot desk) {
            if (!seat.HasValue) return;
            var s = seat.Value;
            if (s.IsFloor) {
                var scr = Desktop.NearestScreen(new Point(Feet.X, Feet.Y - 2));
                if (scr != null) {
                    var v = Dpi.ToDip(scr.WorkingArea);
                    if (Math.Abs(v.Bottom - Feet.Y) > 0.5) {
                        if (Feet.Y < v.Bottom - 2) Fall();
                        else {
                            Feet = new Point(Feet.X, v.Bottom);
                            s.Y = v.Bottom;
                            seat = s;
                        }
                    }
                }
                return;
            }

            if (!host.Prefs.WindowSit) {
                Fall();
                return;
            }

            int winIdx = -1;
            for (int i = 0; i < desk.Wins.Count; i++) {
                if (desk.Wins[i].Handle == s.Win) { winIdx = i; break; }
            }
            if (winIdx < 0) {
                if (!seatLostAt.HasValue) seatLostAt = t;
                if (t - seatLostAt.Value > 0.25) Fall();
                return;
            }
            seatLostAt = null;

            var w = desk.Wins[winIdx];
            if (hostRect.HasValue && hostRect.Value != w.Rect) {
                double dx = w.Rect.Left - hostRect.Value.Left;
                double moved = Math.Abs(dx) + Math.Abs(w.Rect.Top - hostRect.Value.Top);
                if (Math.Abs(w.Rect.Width - hostRect.Value.Width) < 1.0) {
                    Feet = new Point(Feet.X + dx, Feet.Y);
                    walkTarget += dx;
                    s.Left += dx;
                    s.Right += dx;
                }
                if (moved > 2.0) {
                    jostleUntil = t + 0.35;
                    EnsureActiveLoop();
                }
            }
            hostRect = w.Rect;

            // Keep the uncovered, work-area-clipped ledge span rather than expanding onto occluded/offscreen edges
            var matched = desk.Ledges.FirstOrDefault(l => l.Win == s.Win && Feet.X >= l.Left - 12 && Feet.X <= l.Right + 12 && FitsHeadroom(l));
            if (matched.Win != IntPtr.Zero) {
                s = matched;
            } else {
                s.Left = Math.Max(s.Left, w.Rect.Left);
                s.Right = Math.Min(s.Right, w.Rect.Right);
                if (s.Right - s.Left < 48) { s.Left = w.Rect.Left; s.Right = w.Rect.Right; }
                s.Y = w.Rect.Top;
            }
            seat = s;

            double spanW = Math.Max(1.0, s.Right - s.Left);
            double margin = Math.Min(SpriteWidth * 0.22, spanW * 0.5);
            Feet = new Point(Calc.Clamp(Feet.X, s.Left + margin, Math.Max(s.Left + margin, s.Right - margin)), w.Rect.Top);

            var curScr = Desktop.ScreenAt(new Point(Feet.X, Feet.Y + 2));
            if (curScr != null) {
                var v = Dpi.ToDip(curScr.WorkingArea);
                if (Feet.Y - SpriteHeight * 0.85 < v.Top - 2 || Feet.Y > v.Bottom - 4) {
                    Fall();
                    return;
                }
            }

            // If another window in front of `w` covers her body for > 0.7s, hop up onto the closest covering ledge!
            var probe = new Point(Feet.X, Feet.Y - SpriteHeight * 0.35);
            IntPtr coverHandle = IntPtr.Zero;
            for (int i = 0; i < winIdx; i++) {
                if (desk.Wins[i].Rect.Contains(probe)) {
                    coverHandle = desk.Wins[i].Handle;
                    break;
                }
            }
            if (coverHandle != IntPtr.Zero) {
                if (!coveredSince.HasValue) coveredSince = t;
                if (t - coveredSince.Value > 0.7) {
                    coveredSince = null;
                    var upper = desk.Ledges
                        .Where(l => l.Win == coverHandle && (l.Right - l.Left) >= SpriteWidth * 0.8 && FitsHeadroom(l))
                        .OrderBy(l => Feet.X < l.Left ? (l.Left - Feet.X) : (Feet.X > l.Right ? (Feet.X - l.Right) : 0.0))
                        .FirstOrDefault();
                    if (upper.Win != IntPtr.Zero) HopTo(upper, Feet.X);
                    else Fall();
                }
            } else {
                coveredSince = null;
            }
        }

        void CheckAutoFollow(double t, Host.DesktopSnapshot desk) {
            if (!host.Prefs.Follow || !host.Prefs.WindowSit || Awaiting != null) return;
            IntPtr fg = Desktop.GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == lastAutoForeground) return;
            if (seat.HasValue && seat.Value.Win == fg) {
                lastAutoForeground = fg;
                return;
            }
            if (desk.Time == lastAutoScanTime) return;
            lastAutoScanTime = desk.Time;
            var ledge = desk.Ledges
                .Where(l => l.Win == fg && (l.Right - l.Left) >= SpriteWidth * 0.85 && FitsHeadroom(l))
                .OrderBy(l => Feet.X < l.Left ? (l.Left - Feet.X) : (Feet.X > l.Right ? (Feet.X - l.Right) : 0.0))
                .FirstOrDefault();
            if (ledge.Win != IntPtr.Zero) {
                lastAutoForeground = fg;
                int slot = host.Slot(Id);
                double spread = (new[] { 0, 1, -1, 2, -2 })[Math.Min(Math.Max(0, slot), 4)] * SpriteWidth * 0.9;
                HopTo(ledge, Feet.X + spread);
            }
        }

        // MARK: Cursor Chase, Idle Thoughts & Sleep

        void ChaseStep(double t, double dt) {
            var m = PointerPosition;
            double v = (m - lastMouse).Length / Math.Max(dt, 0.001);
            mouseSpeed += (v - mouseSpeed) * 0.2;
            lastMouse = m;

            if (!host.Prefs.Chase || !seat.HasValue || (mode != PetMode.Perched && mode != PetMode.Walking) ||
                shot != null || Working || Awaiting != null || IsActing || host.Music.Dancing || asleep || t <= grumpyUntil) {
                if (chasing) {
                    chasing = false;
                    if (mode == PetMode.Walking) mode = PetMode.Perched;
                }
                return;
            }

            var s = seat.Value;
            // Band around her body above `Feet.Y` (in +Y down: `Feet.Y - SpriteHeight * 1.1 <= m.Y <= Feet.Y + 12`)
            bool inBand = m.Y >= Feet.Y - SpriteHeight * 1.1 && m.Y <= Feet.Y + 12.0 && m.X >= s.Left && m.X <= s.Right;
            double dx = m.X - Feet.X;
            double standOff = Art.HalfWidth * PetScale + 14.0 * PetScale / 0.62;

            if (!chasing) {
                if (inBand && Math.Abs(dx) > standOff + 40.0 && Math.Abs(dx) < 460.0 && mouseSpeed > 60.0 &&
                    t - lastPounce > 2.5 && !HitsScreenPoint(m) && !Others.Any(o => o.chasing)) {
                    chasing = true;
                    chaseLost = null;
                    mode = PetMode.Walking;
                    EnsureActiveLoop();
                }
                return;
            }

            if (!inBand) {
                if (!chaseLost.HasValue) chaseLost = t;
                if (t - chaseLost.Value > 0.7) { chasing = false; mode = PetMode.Perched; }
                return;
            }
            chaseLost = null;

            double margin = SpriteWidth * 0.3;
            double side = dx >= 0 ? 1.0 : -1.0;
            double target = Calc.Clamp(m.X - side * standOff, s.Left + margin, Math.Max(s.Left + margin, s.Right - margin));
            walkTarget = target;
            if (mode != PetMode.Walking && Math.Abs(target - Feet.X) > 2.0) mode = PetMode.Walking;

            if (Math.Abs(target - Feet.X) < 8.0 * PetScale / 0.62 || (mode == PetMode.Perched && Math.Abs(dx) <= standOff + 4.0)) {
                chasing = false;
                mode = PetMode.Perched;
                lastPounce = t;
                lookDir = dx >= 0 ? 90.0 : 270.0;
                lookUntil = t + 1.4;
                Play("jump", 1);
                Sparkle("✦", Palette.Periwinkle, 2);
                if (rng.Next(3) == 0) {
                    string[] catchLines = { "抓到了！", "嘿！", "跑不掉的～" };
                    Say(catchLines[rng.Next(catchLines.Length)], null, BubbleKind.Speech, 1.4, false);
                }
            }
        }

        void Think(double t, Host.DesktopSnapshot desk) {
            if (t <= nextIdea || !host.Prefs.Wander || !seat.HasValue) return;
            nextIdea = t + 7.0 + rng.NextDouble() * 9.0;
            if (Working || asleep) return;

            var s = seat.Value;
            double r = rng.NextDouble();
            double margin = SpriteWidth * 0.3;

            if (r < 0.42) {
                double lo = s.Left + margin;
                double hi = Math.Max(lo + 1.0, s.Right - margin);
                double x = lo + rng.NextDouble() * (hi - lo);
                var scr = Desktop.NearestScreen(Feet);
                if (scr != null) {
                    var v = Dpi.ToDip(scr.WorkingArea);
                    x = Calc.Clamp(x, v.Left + margin, Math.Max(v.Left + margin, v.Right - margin));
                }
                if (Math.Abs(x - Feet.X) > 50.0) {
                    walkTarget = x;
                    mode = PetMode.Walking;
                    EnsureActiveLoop();
                }
            } else if (r < 0.58) {
                Play("wave", 1);
            } else if (r < 0.74) {
                lookDir = rng.Next(2) == 0 ? 90.0 : 270.0;
                lookUntil = t + 1.6;
                EnsureActiveLoop();
            } else if (r < 0.80) {
                var friend = Others.FirstOrDefault(o => SameLedge(o) && Math.Abs(o.Feet.X - Feet.X) < 600.0 && o.mode == PetMode.Perched);
                if (friend != null) {
                    FaceX(friend.Feet.X);
                    Play("wave", 1);
                    if (rng.Next(2) == 0) Say(Art.Lines.Hello[rng.Next(Art.Lines.Hello.Length)], null, BubbleKind.Speech, 2.0, false);
                    Host.Later(500, delegate {
                        if (friend.IsVisible && friend.mode == PetMode.Perched && !friend.Working && friend.Awaiting == null) {
                            friend.FaceX(Feet.X);
                            friend.Play("wave", 1);
                        }
                    });
                }
            } else if (r < 0.84 && host.Prefs.WindowSit) {
                var near = desk.Ledges.Where(l =>
                    !l.IsFloor && l.Win != s.Win && (l.Right - l.Left) >= SpriteWidth * 0.85 && FitsHeadroom(l) &&
                    Math.Abs(Calc.Clamp(Feet.X, l.Left + margin, l.Right - margin) - Feet.X) < 420.0 &&
                    Math.Abs(l.Y - Feet.Y) < 260.0).ToList();
                if (near.Count > 0) {
                    var pick = near[rng.Next(near.Count)];
                    HopTo(pick, Calc.Clamp(Feet.X, pick.Left + margin, pick.Right - margin));
                }
            }
        }

        void CheckSleep(double t) {
            if (!Art.HasExtras) return;
            bool forced = t < ForceSleepUntil;
            bool calm = mode == PetMode.Perched && shot == null && Awaiting == null && (forced || (!Working && !host.Music.Dancing));
            bool drowsy = forced || (host.Prefs.Nap && Desktop.Sleeping());
            if (!asleep && calm && drowsy) {
                asleep = true;
                if (bubble.Visible && !bubble.Sticky) bubble.Hide();
            } else if (asleep && (!calm || !drowsy)) {
                Wake(!calm);
            }
        }

        internal void Wake(bool quiet) {
            if (!asleep) return;
            asleep = false;
            ForceSleepUntil = 0;
            if (quiet || mode != PetMode.Perched) return;
            if (Art.Has("jump")) Play("jump", 1, new[] { 1, 0 });
            Say(Art.Lines.Wake[rng.Next(Art.Lines.Wake.Length)], null, BubbleKind.Speech, 2.4, false);
        }

        // MARK: Pose & Frame Animation

        bool CanSit {
            get { return mode == PetMode.Perched && seat.HasValue && !seat.Value.IsFloor && host.Prefs.WindowSit; }
        }

        void UpdateDisplayImmediate() {
            AnimateBody(Clock.Now, 0.001);
        }

        void AnimateBody(double t, double dt) {
            string animName = "idle";
            int frame = 0;
            double blend = 0;
            double? activeLook = null;

            double targetCwRot = 0;
            double sx = 1.0, sy = 1.0, dx = 0.0, lift = 0.0;
            double alpha = 1.0;
            double pivot = 0.0;
            double k = PetScale / 0.62;
            bool extras = Art.HasExtras;
            bool dancing = host.Music.Dancing && mode == PetMode.Perched && Awaiting == null && !Working && !IsActing && !asleep;

            if (dancing != wasDancing) {
                wasDancing = dancing;
                if (dancing) {
                    lastBeatIndex = -1;
                    if (!host.Music.Forced && !bubble.Visible) {
                        string who = String.IsNullOrEmpty(host.Music.PlayerName) ? "" : (host.Music.PlayerName + " 在放歌，");
                        Say(who + "跳起来～ ♪", null, BubbleKind.Speech, 2.6, false);
                    }
                }
            }

            switch (mode) {
                case PetMode.Held: {
                    double vx = SampleDragVelocity().X;
                    heldVX += (vx - heldVX) * 0.25;
                    if (extras) {
                        var da = Art.Get("dangle");
                        double phase = ((t - heldSince) / da.FrameTime);
                        animName = "dangle";
                        frame = ((int)Math.Floor(phase)) % da.Count;
                        blend = phase - Math.Floor(phase);
                        targetCwRot = Calc.Clamp(heldVX * 0.02, -30.0, 30.0) + Math.Sin(t * 4.2) * 4.0;
                        pivot = Art.Hang * PetScale;
                    } else {
                        if (Math.Abs(heldVX) > 160.0) {
                            animName = heldVX > 0 ? "runRight" : "runLeft";
                            frame = ((int)(t / 0.06)) % Art.Get(animName).Count;
                        } else {
                            animName = "waiting";
                            frame = ((int)(t / 0.2)) % Math.Min(3, Art.Get("waiting").Count);
                        }
                        targetCwRot = Calc.Clamp(heldVX * 0.018, -24.0, 24.0) + Math.Sin(t * 7.0) * 3.0;
                    }
                    break;
                }
                case PetMode.Airborne: {
                    if (extras) {
                        animName = "dangle";
                        double phase = t / 0.09;
                        frame = ((int)Math.Floor(phase)) % Art.Get("dangle").Count;
                        blend = phase - Math.Floor(phase);
                    } else {
                        animName = "jump";
                        frame = Math.Min(2, Art.Get("jump").Count - 1);
                    }
                    targetCwRot = Calc.Clamp(vel.X * 0.014, -32.0, 32.0) + Math.Sin(t * 16.0) * Math.Min(8.0, Math.Abs(vel.X) / 120.0);
                    break;
                }
                case PetMode.Hopping: {
                    animName = "jump";
                    if (hop != null) {
                        double u = (t - hop.Start - 0.12) / hop.Duration;
                        int cnt = Art.Get("jump").Count;
                        frame = Math.Min(cnt - 1, u < 0 ? 1 : (u < 0.8 ? 2 : 3));
                        if (u < 0) { sy = 0.92; sx = 1.05; }
                        targetCwRot = hop.To.X > hop.From.X ? 6.0 : -6.0;
                    }
                    break;
                }
                case PetMode.Walking: {
                    animName = walkTarget > Feet.X ? "runRight" : "runLeft";
                    var ra = Art.Get(animName);
                    double phase = t / ra.FrameTime;
                    frame = ((int)Math.Floor(phase)) % ra.Count;
                    blend = phase - Math.Floor(phase);
                    lift = Math.Abs(Math.Sin(t * 14.0)) * 2.0 * k;
                    break;
                }
                case PetMode.Peeking: {
                    targetCwRot = -(peek != null ? peek.Side : 1) * PeekTiltDegrees + Math.Sin(t * 1.6) * 2.5;
                    double shotLift;
                    if (Awaiting != null) {
                        animName = "waiting";
                        frame = Art.FrameAt("waiting", t, 1.0);
                    } else if (TryShotFrame(t, k, out animName, out frame, out shotLift)) {
                        lift = shotLift * 0.5;
                    } else if (IsActing) {
                        animName = acting.Anim;
                        frame = Art.FrameAt(animName, t - acting.Start, 1.0);
                    } else if (Working) {
                        animName = "work";
                        frame = Art.FrameAt("work", t - workSince, 1.0);
                    } else {
                        animName = "idle";
                        frame = 0;
                        activeLook = PeekLookDegrees();
                    }
                    break;
                }
                case PetMode.Perched: {
                    double shotLift;
                    if (Awaiting != null) {
                        shot = null;
                        animName = "waiting";
                        frame = Art.FrameAt("waiting", t, 1.0);
                        double u = t % 2.2;
                        if (u < 0.36) lift = Math.Sin(u / 0.36 * Math.PI) * 9.0 * k;
                    } else if (TryShotFrame(t, k, out animName, out frame, out shotLift)) {
                        lift = shotLift;
                    } else if (IsActing) {
                        animName = acting.Anim;
                        frame = Art.FrameAt(animName, t - acting.Start, 1.0);
                        if (acting.Flip) sx = -1.0;
                    } else if (asleep && extras && Art.Has("sleep")) {
                        animName = "sleep";
                        frame = Art.FrameAt("sleep", t, 1.3);
                        if (t - lastZ > 1.5) {
                            lastZ = t;
                            SpawnParticle("z", Palette.Periwinkle, (12.0 + rng.NextDouble() * 5.0) * k);
                        }
                    } else if (dancing) {
                        DancePose(t, k, out animName, out frame, out blend, out activeLook, out targetCwRot, out sx, out sy, out lift);
                    } else if (t < jostleUntil) {
                        animName = (CanSit && Art.Has("perch")) ? "perch" : "waiting";
                        frame = 0;
                        targetCwRot = Math.Sin(t * 30.0) * 5.0;
                    } else if (Working) {
                        animName = "work";
                        frame = Art.FrameAt("work", t - workSince, 1.0);
                    } else if (CanSit && extras && Art.Has("perch")) {
                        animName = "perch";
                        frame = PerchFrame(t);
                    } else if (TryCursorLook(t, out lookDeg)) {
                        animName = "idle";
                        frame = IdleFrame(t);
                        activeLook = lookDeg;
                    } else {
                        animName = "idle";
                        frame = IdleFrame(t);
                    }
                    break;
                }
            }

            bool sitting = animName == "perch";
            if (sitting) lift -= Art.SeatRise * PetScale;
            if (sitting != wasSitting) {
                wasSitting = sitting;
                if (mode == PetMode.Perched) landSquash = Tuple.Create(t, 0.07);
            }

            if (landSquash != null) {
                double u = (t - landSquash.Item1) / 0.28;
                if (u >= 1.0) landSquash = null;
                else {
                    double s = landSquash.Item2 * Math.Sin(u * Math.PI) * (1.0 - u * 0.4);
                    sy *= 1.0 - s;
                    sx *= (sx < 0 ? -1.0 : 1.0) * (Math.Abs(sx) + s * 0.6);
                }
            }

            if (t < glitchUntil) {
                dx += (rng.NextDouble() * 8.0 - 4.0) * k;
                alpha = 0.45 + rng.NextDouble() * 0.55;
                if (((int)(t * 30.0)) % 3 == 0) activeLook = rng.Next(16) * 22.5;
            }

            double follow = (mode == PetMode.Held || mode == PetMode.Airborne) ? 0.25 : 0.35;
            rot += (targetCwRot - rot) * Math.Min(1.0, follow * dt * 60.0);
            if (Math.Abs(rot) < 0.05 && Math.Abs(targetCwRot) < 0.01) rot = 0;

            if (pivot > 0) {
                double r = rot * Math.PI / 180.0;
                dx -= pivot * Math.Sin(r);
                lift += pivot * (1.0 - Math.Cos(r));
            }

            string effectivePose = Art.Pose(animName);
            string titleTag = (mode == PetMode.Peeking && activeLook.HasValue) ? "peek" : effectivePose;
            if (titleTag != shownPoseTitle) {
                shownPoseTitle = titleTag;
                Title = "GLORIA · " + Id + " · " + titleTag;
            }

            int cell = activeLook.HasValue ? Art.LookCell(activeLook.Value) : Art.CellIndex(effectivePose, frame);
            headRise = (Art.FeetY - Art.TopOfCell(cell)) * PetScale + lift;

            if (image != null) {
                if (cell != shownCell) {
                    shownCell = cell;
                    image.Source = Art.Cell(cell);
                }
            } else if (model != null) {
                // Smooth 3D rendering (~60 fps when moving/dancing, ~32 fps otherwise; 0 cost when settled)
                double minInterval = (mode != PetMode.Perched || dancing || blend > 0) ? (1.0 / 62.0) : (1.0 / 34.0);
                if (t - lastModelDraw >= minInterval || cell != shownCell) {
                    shownCell = cell;
                    lastModelDraw = t;
                    model.Draw(effectivePose, frame, blend, t, activeLook);
                }
            }

            poseScale.ScaleX = sx;
            poseScale.ScaleY = sy;
            poseRotate.Angle = rot;
            poseTranslate.X = dx;
            poseTranslate.Y = -lift; // +lift is upward, WPF +Y is downward
            visual.Opacity = alpha;
        }

        bool TryShotFrame(double t, double k, out string animName, out int frame, out double lift) {
            animName = "idle";
            frame = 0;
            lift = 0;
            if (shot == null) return false;

            var spec = Art.Get(shot.Anim);
            double ft = spec.FrameTime;
            int seqLen = shot.Cells != null ? shot.Cells.Length : spec.Count;
            int idx = (int)((t - shot.Start) / ft);
            if (idx >= seqLen * shot.Loops) {
                shot = null;
                idleStart = t;
                return false;
            }
            if (shot.Anim == "jump" && shot.Cells == null && idx < seqLen) {
                double u = (t - shot.Start) / (ft * seqLen);
                lift = Math.Max(0.0, Math.Sin(u * Math.PI)) * 16.0 * k;
            }
            animName = shot.Anim;
            frame = shot.Cells != null ? shot.Cells[idx % seqLen] : (idx % seqLen);
            return true;
        }

        int IdleFrame(double t) {
            var spec = Art.Get("idle");
            if (spec.Loop) return Art.FrameAt("idle", t, 1.0);
            double u = t - idleStart;
            if (u < idleHold) return 0;
            int i = (int)((u - idleHold) / spec.FrameTime) + 1;
            if (i >= spec.Count) {
                idleStart = t;
                idleHold = 1.8 + rng.NextDouble() * 2.4;
                return 0;
            }
            return i;
        }

        int PerchFrame(double t) {
            var spec = Art.Get("perch");
            double u = t - idleStart;
            if (u < idleHold) return 0;
            double v = u - idleHold;
            for (int i = 1; i < spec.Durations.Length; i++) {
                v -= spec.Durations[i];
                if (v < 0) return i;
            }
            idleStart = t;
            idleHold = 2.0 + rng.NextDouble() * 2.5;
            return 0;
        }

        bool TryCursorLook(double t, out double deg) {
            if (t < lookUntil) {
                deg = lookDir;
                return true;
            }
            var m = PointerPosition;
            var head = new Point(Feet.X, Feet.Y - SpriteHeight * 0.72);
            double d = (m - head).Length;
            double k = PetScale / 0.62;
            int? testCell = model == null ? (int?)Art.CellIndex("idle", 0) : null;
            bool want = d < 300.0 * k + 120.0 && d > SpriteWidth * 0.35 && !HitsScreenPoint(m, testCell);
            if (want != looking && t - lookSwitched > 0.35) {
                looking = want;
                lookSwitched = t;
                if (looking) EnsureActiveLoop();
            }
            if (!looking) {
                deg = 90.0;
                return false;
            }
            deg = Math.Atan2(m.X - head.X, head.Y - m.Y) * 180.0 / Math.PI;
            return true;
        }

        void DancePose(double t, double k, out string animName, out int frame, out double blend, out double? look,
                       out double cwRot, out double sx, out double sy, out double lift) {
            blend = 0;
            look = null;
            var b = host.Music.Beat();
            double amp = 0.55 + 0.45 * b.Energy;
            double ph = b.Phase;
            int i = b.Index;

            if (i != lastBeatIndex) {
                lastBeatIndex = i;
                if (rng.NextDouble() < 0.45) {
                    string[] notes = { "♪", "♫", "♬", "♩" };
                    SolidColorBrush[] cols = { Palette.Pink, Palette.Periwinkle, Palette.Lavender, Palette.Cyan };
                    SpawnParticle(notes[rng.Next(notes.Length)], cols[rng.Next(cols.Length)], 15.0 * k);
                }
            }

            double beatPos = (i % 2) + ph;
            double swing = Math.Cos(Math.PI * beatPos);
            double bounce = Math.Abs(Math.Sin(Math.PI * ph));
            double hit = Math.Max(0.0, 1.0 - ph * 5.0);
            sx = 1.0 + 0.04 * hit * amp;
            sy = 1.0 - 0.06 * hit * amp;
            int bar = ((i % 16) + 16) % 16;

            if (Art.HasExtras && Art.Has("dance") && bar < 12) {
                var spec = Art.Get("dance");
                animName = "dance";
                cwRot = -swing * 3.0 * amp;
                lift = bounce * 3.0 * k * amp;
                if (spec.Sub > 1) {
                    int keys = Math.Max(1, spec.Count / spec.Sub);
                    int key = (((i / 16) * 12 + bar) % keys + keys) % keys;
                    int s = ph < 0.55 ? 0 : Math.Min(spec.Sub - 1, 1 + (int)((ph - 0.55) / 0.45 * (spec.Sub - 1)));
                    frame = key * spec.Sub + s;
                } else {
                    double exact = beatPos * (spec.Count * 0.5);
                    frame = ((int)Math.Floor(exact)) % spec.Count;
                    blend = exact - Math.Floor(exact);
                }
                return;
            }

            if (bar < 14 && Art.Has("jump") && Art.Get("jump").Count >= 4) {
                animName = "jump";
                if (ph < 0.16) {
                    frame = 1; cwRot = 0; sx = 1.05; sy = 0.92; lift = 0;
                } else if (ph < 0.72) {
                    double u = (ph - 0.16) / 0.56;
                    frame = 2; cwRot = (i % 2 == 0) ? -4.0 : 4.0; sx = 0.98; sy = 1.03;
                    lift = Math.Sin(u * Math.PI) * 15.0 * k * amp;
                } else {
                    frame = 3; cwRot = 0; lift = 0;
                }
                return;
            }

            if (bar == 15 && Art.Has("review")) {
                animName = "review";
                frame = Math.Min(3, Art.Get("review").Count - 1);
                cwRot = 0;
                lift = bounce * 4.0 * k;
                return;
            }

            animName = "idle";
            frame = 0;
            look = (i % 2 == 0) ? 90.0 : 270.0;
            cwRot = ((i % 2 == 0) ? 6.0 : -6.0) * amp;
            lift = bounce * 4.0 * k * amp;
        }

        // Status snapshot for context menu & `--ctl status`
        internal Dictionary<string, object> BuildStatus() {
            var b = host.Music.Beat();
            var s = host.Music.Tracker.Snapshot();
            string seatOwner = "";
            if (seat.HasValue) {
                if (seat.Value.IsFloor) seatOwner = "floor";
                else {
                    var hw = host.GetDesktop(0.5).Wins.FirstOrDefault(w => w.Handle == seat.Value.Win);
                    seatOwner = hw.Handle != IntPtr.Zero ? hw.Owner : "窗口";
                }
            }
            var d = new Dictionary<string, object>();
            d["character"] = Id;
            d["mode"] = mode.ToString().ToLowerInvariant();
            d["feet"] = new[] { Math.Round(Feet.X, 1), Math.Round(Feet.Y, 1) };
            d["seatOwner"] = seatOwner;
            d["sitting"] = wasSitting;
            d["asleep"] = asleep;
            d["chasing"] = chasing;
            d["working"] = Working;
            d["awaiting"] = Awaiting ?? "";
            d["clones"] = ActiveCloneCount;
            d["acting"] = IsActing ? acting.Anim : "";
            d["flipped"] = IsActing && acting.Flip;
            d["bubble"] = bubble.Visible ? bubble.Text : "";
            d["passThrough"] = passThroughEnabled;
            d["dancing"] = host.Music.Dancing;
            d["musicActive"] = host.Music.Active;
            d["player"] = host.Music.PlayerName;
            d["bpm"] = Math.Round(s.Bpm, 1);
            d["confidence"] = Math.Round(s.Confidence, 2);
            d["tap"] = host.Music.TapNote;
            if (IsPeeking) {
                var pk = new Dictionary<string, object>();
                pk["side"] = peek.Side;
                pk["edge"] = Math.Round(peek.EdgeX, 1);
                pk["headY"] = Math.Round(peek.HeadY, 1);
                pk["out"] = Math.Round(peek.Exposure, 2);
                d["peek"] = pk;
            }
            return d;
        }
    }
}
