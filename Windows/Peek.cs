using System;
using System.Windows;
using Forms = System.Windows.Forms;

namespace Gloria {
    internal sealed partial class Pet {
        internal sealed class PeekState {
            internal int Side; // -1 = left edge, +1 = right edge
            internal string MonitorDevice;
            internal double EdgeX;
            internal double HeadY;
            internal double Exposure = 0.95;
            internal double DuckUntil;
            internal double? NearSince;
            internal bool Found;
            internal double? FidgetUntil;
            internal double FidgetTarget = 0.68;
            internal double NextFidget;
        }

        PeekState peek;
        double lastPeekLine = -60;
        const double PeekTiltDegrees = 28.0;

        internal bool IsPeeking { get { return mode == PetMode.Peeking && peek != null; } }

        struct HeadGeometry {
            internal double Dx; // sideways from feet center, upright
            internal double Hc; // distance UP from feet to face center, upright (> 0)
            internal double R;  // face radius
        }

        HeadGeometry HeadGeo {
            get {
                double s = PetScale;
                if (Art.HasFace) {
                    return new HeadGeometry {
                        Dx = (Art.FaceX - Art.W * 0.5) * s,
                        Hc = (Art.FeetY - Art.FaceY) * s,
                        R = Art.FaceRadius * s
                    };
                }
                double rise = (Art.FeetY - Art.TopOfCell(Art.CellIndex("idle", 0))) * s;
                if (rise < SpriteHeight * 0.3) rise = SpriteHeight * 0.8;
                double neck = Calc.Clamp(Art.Hang * s, rise * 0.35, rise * 0.8);
                double r = Math.Max((rise - neck) * 0.5, SpriteWidth * 0.12);
                return new HeadGeometry { Dx = 0, Hc = rise - r, R = r };
            }
        }

        // Offset from Feet to face center in screen DIPs (+X right, +Y down) when rotated clockwise by cwDeg.
        Vector FaceOffset(double cwDeg) {
            var g = HeadGeo;
            double r = cwDeg * Math.PI / 180.0;
            double cr = Math.Cos(r), sr = Math.Sin(r);
            // Upright vector from feet to face is (g.Dx, -g.Hc)
            return new Vector(g.Dx * cr + g.Hc * sr, g.Dx * sr - g.Hc * cr);
        }

        Point PeekHeadPoint {
            get {
                var o = FaceOffset(rot);
                return new Point(Feet.X + o.X, Feet.Y + o.Y);
            }
        }

        internal Forms.Screen CurrentScreen {
            get {
                if (IsPeeking) return Desktop.NearestScreen(new Point(peek.EdgeX - peek.Side * 4, peek.HeadY));
                return Desktop.NearestScreen(new Point(Feet.X, Feet.Y - 4));
            }
        }

        // Checks if the outer edge of `scr` has no adjacent monitor overlapping the hidden body strip.
        internal Forms.Screen EdgeFree(int side, double edgeX, double headY) {
            var probe = new Point(edgeX - side * 4, headY);
            var scr = Desktop.ScreenAt(probe) ?? Desktop.NearestScreen(probe);
            if (scr == null) return null;
            var f = Dpi.ToDip(scr.Bounds);
            if (Math.Abs((side > 0 ? f.Right : f.Left) - edgeX) > 2.0) return null;

            var g = HeadGeo;
            var v = Dpi.ToDip(scr.WorkingArea);
            double y = Calc.Clamp(headY, v.Top + g.R + 10, v.Bottom - g.R - 10);
            double w = SpriteWidth * 1.2;
            // In +Y down: hidden body extends from head (y - g.R - 10) down to feet (y + g.Hc + 10)
            var hideStrip = new Rect(side > 0 ? edgeX : edgeX - w, y - g.R - 10, w, g.Hc + g.R + 20);

            foreach (var other in Forms.Screen.AllScreens) {
                if (other.DeviceName == scr.DeviceName) continue;
                var overlap = Rect.Intersect(hideStrip, Dpi.ToDip(other.Bounds));
                if (!overlap.IsEmpty && overlap.Width > 0.5 && overlap.Height > 0.5) return null;
            }
            return scr;
        }

        internal void StartPeek(int side, Forms.Screen scr, double headY, bool quiet = false) {
            double t = Clock.Now;
            var g = HeadGeo;
            var f = Dpi.ToDip(scr.Bounds);
            var v = Dpi.ToDip(scr.WorkingArea);
            double edgeX = side > 0 ? f.Right : f.Left;

            peek = new PeekState {
                Side = side,
                MonitorDevice = scr.DeviceName,
                EdgeX = edgeX,
                HeadY = Calc.Clamp(headY, v.Top + g.R + 10, v.Bottom - g.R - 10),
                Exposure = 0.95,
                NextFidget = t + 5.0
            };
            seat = null;
            hostRect = null;
            hop = null;
            chasing = false;
            vel = new Vector(0, 0);
            mode = PetMode.Peeking;
            Wake(true);

            if (!quiet && t - lastPeekLine > 20.0 && rng.Next(2) == 0) {
                lastPeekLine = t;
                string[] lines = { "躲好了～", "嘘……", "从这里偷偷看你" };
                Say(lines[rng.Next(lines.Length)], null, BubbleKind.Speech, 2.0, false);
            }
            EnsureActiveLoop();
        }

        internal void PeekAtEdge(int slot = 0) {
            var scr = CurrentScreen;
            if (scr == null) return;
            var f = Dpi.ToDip(scr.Bounds);
            var v = Dpi.ToDip(scr.WorkingArea);
            var g = HeadGeo;

            int near = Feet.X > (f.Left + f.Right) * 0.5 ? 1 : -1;
            int[] order = (slot % 2 == 0) ? new[] { near, -near } : new[] { -near, near };
            double baseHeadY = IsPeeking ? peek.HeadY : (Feet.Y - g.Hc);
            double y = baseHeadY + (slot / 2) * g.R * 3.2;
            y = Math.Min(y, v.Bottom - SpriteHeight * 0.6);

            foreach (int side in order) {
                double edgeX = side > 0 ? f.Right : f.Left;
                var ok = EdgeFree(side, edgeX, y);
                if (ok != null) {
                    StartPeek(side, ok, y, false);
                    return;
                }
            }
            Play("wave", 1);
            Say("这块屏幕两边都连着别的屏幕，没地方躲～", null, BubbleKind.Speech, 2.4, false);
        }

        internal void LeavePeek() {
            if (!IsPeeking) return;
            var pk = peek;
            var g = HeadGeo;
            peek = null;
            Feet = new Point(pk.EdgeX - pk.Side * SpriteWidth * 0.45, pk.HeadY + g.Hc);
            Say("出来啦～", null, BubbleKind.Speech, 1.2, false);
            ThrowHer(-pk.Side * 380.0, 560.0);
            host.Remember(this);
        }

        void PeekStep(double t, double dt) {
            if (peek == null) { mode = PetMode.Airborne; return; }
            var scr = Desktop.ScreenAt(new Point(peek.EdgeX - peek.Side * 4, peek.HeadY));
            if (scr == null || EdgeFree(peek.Side, peek.EdgeX, peek.HeadY) == null) {
                peek = null;
                Summon(false, 0, null);
                return;
            }

            var g = HeadGeo;
            var m = PointerPosition;
            var head = new Point(peek.EdgeX - peek.Side * g.R * 0.4, peek.HeadY);
            double d = (m - head).Length;
            double reach = g.R * 4.0 + 90.0;
            bool over = HitsScreenPoint(m);

            double want = 0.68;
            if (shot != null || Awaiting != null || Working || (bubble.Visible && !String.IsNullOrEmpty(bubble.Text)) || IsActing) {
                want = 1.0;
            } else {
                if (d < reach && mouseSpeed > 280.0 && !over) {
                    peek.DuckUntil = t + 1.2;
                    peek.Found = false;
                }
                if ((d < reach * 0.8 && mouseSpeed < 70.0) || over) {
                    if (!peek.NearSince.HasValue) peek.NearSince = t;
                } else {
                    peek.NearSince = null;
                }

                bool found = over || (peek.NearSince.HasValue && (t - peek.NearSince.Value) > 0.8);
                if (found && !peek.Found && t > peek.DuckUntil && t - lastPeekLine > 25.0 && rng.Next(3) == 0) {
                    lastPeekLine = t;
                    string[] foundLines = { "被发现了……", "嘿嘿", "我只是路过～" };
                    Say(foundLines[rng.Next(foundLines.Length)], null, BubbleKind.Speech, 1.8, false);
                }
                peek.Found = found;

                if (t < peek.DuckUntil) want = 0.20;
                else if (found) want = 0.95;
                else {
                    if (!peek.FidgetUntil.HasValue && t > peek.NextFidget) {
                        bool duck = rng.Next(2) == 0;
                        peek.FidgetTarget = duck ? 0.30 : 0.88;
                        peek.FidgetUntil = t + (duck ? 0.9 : 1.6);
                        peek.NextFidget = t + 5.0 + rng.NextDouble() * 6.0;
                    }
                    if (peek.FidgetUntil.HasValue) {
                        if (t < peek.FidgetUntil.Value) want = peek.FidgetTarget;
                        else peek.FidgetUntil = null;
                    }
                }
            }

            double rate = want < peek.Exposure ? 12.0 : 2.4;
            peek.Exposure += (want - peek.Exposure) * Math.Min(1.0, rate * dt);

            double inside = Calc.Lerp(-g.R - 6.0, g.R * 1.05, peek.Exposure);
            var o = FaceOffset(rot);
            Feet = new Point(peek.EdgeX - peek.Side * inside - o.X, peek.HeadY - o.Y);
        }

        double PeekLookDegrees() {
            var pk = peek;
            var m = PointerPosition;
            var h = PeekHeadPoint;
            double dist = (m - h).Length;
            double worldDeg;
            if (dist > 900.0) {
                worldDeg = pk.Side > 0 ? 270.0 : 90.0;
            } else {
                worldDeg = Math.Atan2(m.X - h.X, h.Y - m.Y) * 180.0 / Math.PI;
            }
            return worldDeg - rot;
        }
    }
}
