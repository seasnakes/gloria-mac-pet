using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Forms=System.Windows.Forms;

namespace Gloria {
    internal sealed partial class Pet {
        sealed class PeekState {
            internal int Side;
            internal string Monitor;
            internal double Edge, HeadY, Exposure=.95, Tilt, Fidget=.68;
            internal DateTime Updated, DuckUntil, NearSince, NextFidget, FidgetUntil;
            internal Point LastMouse;
            internal bool Found;
            internal readonly RotateTransform Lean=new RotateTransform();
            internal readonly TranslateTransform Offset=new TranslateTransform();
        }
        PeekState peek;
        readonly Random playful=new Random(Guid.NewGuid().GetHashCode());
        DateTime lastPeekLine;
        double? peekLook;
        DateTime hopStart;
        Point hopFrom,hopTo;
        internal bool IsPeeking {get {return peek!=null;}}
        double PetScale {get {return host.Prefs.Size*Art.Scale;}}
        double FaceRadius {get {return PetScale*Art.FaceRadius;}}
        Point Pointer {
            get {var pt=Forms.Cursor.Position;return Dip(pt.X,pt.Y);}
        }
        Forms.Screen MonitorAt(Point position) {
            var device=PresentationSource.FromVisual(this).CompositionTarget.TransformToDevice.Transform(position);
            return Forms.Screen.FromPoint(new System.Drawing.Point((int)device.X,(int)device.Y));
        }
        Rect Area(System.Drawing.Rectangle r) {return new Rect(Dip(r.Left,r.Top),Dip(r.Right,r.Bottom));}
        double HeadLocalX {get {return (Width-body.Width)/2+Art.FaceX*PetScale;}}
        double HeadLocalY {get {return Height-body.Height+Art.FaceY*PetScale;}}
        static double Limit(double n,double lo,double hi) {return Math.Max(lo,Math.Min(n,hi));}
        // Test the whole strip occupied by the hidden body, including staggered monitors.
        bool CanHide(Forms.Screen screen,int side,double y) {
            var bounds=Area(screen.Bounds);
            var strip=new Rect(side>0?bounds.Right:bounds.Left-body.Width*1.2,
                y-FaceRadius-10,body.Width*1.2,(Art.FeetY-Art.FaceY)*PetScale+FaceRadius+20);
            foreach (var other in Forms.Screen.AllScreens) {
                if (other.DeviceName==screen.DeviceName) continue;
                var overlap=Rect.Intersect(strip,Area(other.Bounds));
                if (!overlap.IsEmpty && overlap.Width>.01 && overlap.Height>.01) return false;
            }
            return true;
        }
        bool TryStartPeek(Forms.Screen screen,int side,double y) {
            var area=Area(screen.WorkingArea);
            y=Limit(y,area.Top+FaceRadius+10,area.Bottom-FaceRadius-10);
            if (!CanHide(screen,side,y)) return false;
            var now=DateTime.UtcNow;
            StopMotion();
            peek=new PeekState {Side=side,Monitor=screen.DeviceName,HeadY=y,Updated=now,LastMouse=Pointer,NextFidget=now.AddSeconds(5)};
            hopStart=DateTime.MinValue;
            walkTicket++;
            perched=false;
            var group=new TransformGroup();
            group.Children.Add(peek.Lean);group.Children.Add(peek.Offset);
            body.RenderTransformOrigin=new Point(Art.FaceX/Art.W,Art.FaceY/Art.H);
            body.RenderTransform=group;
            bubble.HorizontalAlignment=side>0?HorizontalAlignment.Right:HorizontalAlignment.Left;
            LayoutPeek(screen);
            if ((now-lastPeekLine).TotalSeconds>20 && playful.Next(2)==0) {
                lastPeekLine=now;Note("idle","嘘……",1.5);
            }
            Tick();
            return true;
        }
        void LayoutPeek(Forms.Screen screen) {
            var bounds=Area(screen.Bounds);
            var area=Area(screen.WorkingArea);
            Width=Math.Max(260,body.Width*1.45); Height=caption+body.Height*1.4;
            peek.HeadY=Limit(peek.HeadY,area.Top+FaceRadius+10,area.Bottom-FaceRadius-10);
            peek.Edge=peek.Side>0?bounds.Right:bounds.Left;
            Left=peek.Side>0?peek.Edge-Width:peek.Edge;
            Top=Limit(peek.HeadY-HeadLocalY,area.Top,area.Bottom-Height);
        }
        internal void PeekAtEdge(int slot) {
            var screen=MonitorAt(Hands);
            var bounds=Area(screen.Bounds);
            int near=Hands.X>(bounds.Left+bounds.Right)/2?1:-1;
            int side=slot%2==0?near:-near;
            double y=Math.Min(peek==null?Top+HeadLocalY:peek.HeadY,Area(screen.WorkingArea).Bottom-body.Height*.6);
            y+=(slot/2)*3.2*FaceRadius;
            if (!TryStartPeek(screen,side,y) && !TryStartPeek(screen,-side,y))
                Play("wave","这块屏幕两边都连着别的屏幕，没地方躲～",2.4);
        }
        bool PeekOnDrop(double speed) {
            if (!host.Prefs.EdgePeek || speed>=1800) return false;
            var pt=Pointer;
            var screen=MonitorAt(pt);
            var bounds=Area(screen.Bounds);
            double zone=Math.Max(34,body.Width*.20);
            int side=pt.X-bounds.Left<zone?-1:bounds.Right-pt.X<zone?1:0;
            return side!=0 && TryStartPeek(screen,side,Top+HeadLocalY);
        }
        void ResetPeek() {
            if (peek==null) return;
            peek=null;peekLook=null;
            body.RenderTransform=Transform.Identity;
            bubble.HorizontalAlignment=HorizontalAlignment.Center;
            bubble.Margin=new Thickness();
            SizePet();
            due=DateTime.MinValue;
        }
        internal void LeavePeek(bool hop) {
            if (peek==null) return;
            var p=peek;
            ResetPeek();
            Top=p.HeadY-HeadLocalY;
            Left=p.Edge-HeadLocalX-p.Side*FaceRadius*.6;
            if (hop) {
                hopFrom=new Point(Left,Top);
                Left-=p.Side*body.Width*.85;Clamp();
                hopTo=new Point(Left,Top);hopStart=DateTime.UtcNow;
                Note("jump","出来啦",.65);
                host.Remember(this);
            }
        }
        void UpdateHop(DateTime now) {
            if (hopStart==DateTime.MinValue) return;
            double t=Limit((now-hopStart).TotalSeconds/.55,0,1);
            Left=hopFrom.X+(hopTo.X-hopFrom.X)*t;
            Top=hopFrom.Y+(hopTo.Y-hopFrom.Y)*t-4*t*(1-t)*body.Height*.27;
            if(t>=1)hopStart=DateTime.MinValue;
        }
        void UpdatePeek(DateTime now,bool speaking) {
            var p=peek;
            var pt=Pointer;
            double dt=Limit((now-p.Updated).TotalSeconds,.001,.15);
            double speed=(pt-p.LastMouse).Length/dt;
            p.LastMouse=pt;p.Updated=now;
            double reach=FaceRadius*4+90;
            var toward=pt-new Point(p.Edge-p.Side*FaceRadius*.4,p.HeadY);
            bool over=body.IsMouseOver;
            double target=.68;
            if (speaking) target=1;
            else {
                if (toward.Length<reach && speed>280 && !over) {p.DuckUntil=now.AddSeconds(1.2);p.Found=false;}
                if ((toward.Length<reach*.8 && speed<70) || over) {
                    if (p.NearSince==DateTime.MinValue)p.NearSince=now;
                } else p.NearSince=DateTime.MinValue;
                bool found=over || (p.NearSince!=DateTime.MinValue && (now-p.NearSince).TotalSeconds>.8);
                if(found && !p.Found && now>p.DuckUntil && (now-lastPeekLine).TotalSeconds>25 && playful.Next(3)==0) {
                    Note("idle","被发现了……",1.8);lastPeekLine=now;
                }
                p.Found=found;
                if(now<p.DuckUntil)target=.2;
                else if(found)target=.95;
                else {
                    if(now>=p.NextFidget) {
                        bool duck=playful.Next(2)==0;p.Fidget=duck?.30:.88;
                        p.FidgetUntil=now.AddSeconds(duck?.9:1.6);
                        p.NextFidget=now.AddSeconds(5+playful.NextDouble()*6);
                    }
                    if(now<p.FidgetUntil)target=p.Fidget;
                }
            }
            p.Exposure+=(target-p.Exposure)*Math.Min(1,dt*(target<p.Exposure?12:2.4));
            p.Tilt+=(-p.Side*28+Math.Sin(now.TimeOfDay.TotalSeconds*1.6)*2.5-p.Tilt)*Math.Min(1,dt*13);
            double inside=-FaceRadius-6+(FaceRadius*2.05+6)*p.Exposure;
            double hx=p.Edge-p.Side*inside;
            p.Lean.Angle=p.Tilt;p.Offset.X=hx-Left-HeadLocalX;p.Offset.Y=p.HeadY-Top-HeadLocalY;
            bubble.Margin=new Thickness(8,Math.Max(1,p.HeadY-Top-FaceRadius-34),8,0);
            double angle=toward.Length>900 ? -p.Side*90 : Math.Atan2(pt.X-hx,p.HeadY-pt.Y)*180/Math.PI;
            peekLook=(angle-p.Tilt)*Math.PI/180;
        }
    }
}
