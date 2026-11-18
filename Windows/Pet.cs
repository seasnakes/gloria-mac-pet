using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Forms=System.Windows.Forms;

namespace Gloria {
    internal sealed class Mood {
        internal string Pose,Text;
        internal DateTime Time,Until;
    }
    internal sealed partial class Pet : Window {
        readonly Host host;
        internal readonly string Id;
        internal readonly Character Art;
        readonly Image image;
        readonly ModelScene model;
        readonly FrameworkElement body;
        int displayedFrame;
        readonly TextBlock label=new TextBlock{Foreground=new SolidColorBrush(Color.FromRgb(57,40,62)),FontSize=13,TextAlignment=TextAlignment.Center};
        readonly Border bubble=new Border{Background=Brushes.White,BorderBrush=new SolidColorBrush(Color.FromRgb(234,174,201)),BorderThickness=new Thickness(1.5),CornerRadius=new CornerRadius(12),Padding=new Thickness(8,3,8,3),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Top};
        readonly DispatcherTimer timer=new DispatcherTimer(DispatcherPriority.Input);
        readonly Dictionary<string,Mood> sessions=new Dictionary<string,Mood>();
        string shown="";int frame; DateTime due,ambientAt,audioUntil;
        Mood completion;
        Mood demo; bool held,dragged,perched,sleeping; Point down; double left0,top0;
        DateTime dragSample; Point lastDragPoint; double dragSpeed;
        const double caption=34;
        internal Pet(Host host,string id) {
            this.host=host; Id=id; Art=new Character(id);
            WindowStyle=WindowStyle.None;AllowsTransparency=true;Background=Brushes.Transparent;
            ShowInTaskbar=false;ShowActivated=false;Topmost=true;ResizeMode=ResizeMode.NoResize;
            if (Art.IsModel) {
                model = new ModelScene(Art, id) { IsHitTestVisible=false };
                if (id=="fourteen") {
                    model.VerticalAlignment=VerticalAlignment.Stretch;
                    body=new Border {Child=model,VerticalAlignment=VerticalAlignment.Bottom};
                } else body=model;
            } else {
                image = new Image { Stretch=Stretch.Fill, VerticalAlignment=VerticalAlignment.Bottom };
                body = image;
            }
            var grid=new Grid {ClipToBounds=true, Background=Brushes.Transparent};grid.Children.Add(body);bubble.Child=label;grid.Children.Add(bubble);Content=grid;
            SourceInitialized+=delegate {
                var ptr=new WindowInteropHelper(this).Handle;
                NativeStyle.NoActivate(ptr);
            };
            MouseLeftButtonDown+=(s,e)=>{
                if(e.ClickCount==2 && peek!=null){LeavePeek(true);return;}
                if(e.ClickCount==2){Play("dance","一起跳舞",8);return;}
                walkTicket++;hopStart=DateTime.MinValue;StopMotion();dragSpeed=0;dragVelocity=new Vector();dragSample=DateTime.UtcNow;lastDragPoint=Pointer;
                down=PointToScreen(e.GetPosition(this)); left0=Left;top0=Top;held=true;dragged=false;
                CaptureMouse();Tick();
            };
            MouseMove+=(s,e)=>{
                if(!held)return;var pt=PointToScreen(e.GetPosition(this));var dv=PixelsToDip(pt-down);
                if(!dragged && Math.Abs(dv.X)+Math.Abs(dv.Y)<4)return;
                if(!dragged && peek!=null) {
                    LeavePeek(false);left0=Left;top0=Top;
                }
                var cursor=Pointer;
                var now=DateTime.UtcNow;
                dragVelocity=(cursor-lastDragPoint)/Math.Max(.008,(now-dragSample).TotalSeconds);
                dragSpeed=dragVelocity.Length;
                lastDragPoint=cursor;dragSample=now;
                dragged=true;Left=left0+dv.X;Top=top0+dv.Y;
            };
            MouseLeftButtonUp+=(s,e)=>{
                if(!held)return;held=false;ReleaseMouseCapture();
                if(dragged){
                    var release=(DateTime.UtcNow-dragSample).TotalSeconds>.12 ? new Vector() : dragVelocity;
                    if (!PeekOnDrop(release.Length)) StartFall(release);
                }else if(peek==null) Play("wave","你好呀",2);
                Tick();
            };
            LostMouseCapture+=delegate{held=false;};
            MouseRightButtonUp+=(s,e)=>{host.Menu(this);e.Handled=true;};
            timer.Interval=TimeSpan.FromMilliseconds(35);timer.Tick+=delegate{Tick();};
            Loaded+=delegate{SizePet();Place();Tick();timer.Start();};
            Closed+=delegate{timer.Stop();};
        }
        Vector PixelsToDip(Vector p) {return PresentationSource.FromVisual(this).CompositionTarget.TransformFromDevice.Transform(p);}
        Point Dip(int x,int y) {var v=PixelsToDip(new Vector(x,y));return new Point(v.X,v.Y);}
        internal void SizePet() {
            double k=host.Prefs.Size*Art.Scale;body.Width=Art.W*k;body.Height=Art.H*k;
            Width=Math.Max(170,body.Width);Height=caption+body.Height;
        }
        internal void Place() {
            var xy=host.Prefs.Positions.ContainsKey(Id)?host.Prefs.Positions[Id]:null;
            if(xy!=null && xy.Length==2 && !xy.Any(x=>Double.IsInfinity(x)||Double.IsNaN(x))) {Left=xy[0];Top=xy[1];}
            else Recall();
            Clamp();
        }
        internal void Recall() {
            StopMotion();ResetPeek();hopStart=DateTime.MinValue;walkTicket++;
            var a=Forms.Screen.PrimaryScreen.WorkingArea;
            var p=Dip(a.Right,a.Bottom);Left=p.X-Width-12-host.Slot(Id)*(Width*.70);Top=p.Y-Height+4; Clamp();
        }
        internal void Clamp() {
            if(peek!=null) {
                var screen=Forms.Screen.AllScreens.FirstOrDefault(s=>s.DeviceName==peek.Monitor);
                if (screen!=null && CanHide(screen,peek.Side,peek.HeadY)){LayoutPeek(screen);return;}
                ResetPeek();
            }
            var tr=PresentationSource.FromVisual(this).CompositionTarget.TransformToDevice;
            var pt=tr.Transform(new Point(Left+Width/2,Top+Height/2));
            var a=Forms.Screen.FromPoint(new System.Drawing.Point((int)pt.X,(int)pt.Y)).WorkingArea;
            var lo=Dip(a.Left,a.Top);var hi=Dip(a.Right,a.Bottom);
            Left=Math.Max(lo.X,Math.Min(Left,hi.X-Width));Top=Math.Max(lo.Y,Math.Min(Top,hi.Y-Height+4));
        }
        internal void Play(string pose,string text,double duration){
            var now=DateTime.UtcNow;demo=new Mood{Pose=pose,Text=text,Time=now,Until=now.AddSeconds(duration)};Tick();
        }
        internal void React(string[] f) {
            var now=DateTime.UtcNow;string session=f[0]+":"+f[1];
            string text=Hooks.Names[Array.IndexOf(Events.Sources,f[0])]+" · "+f[3];
            if(f[4]=="end"){sessions.Remove(session);completion=new Mood{Pose=f[2],Text=text,Time=now,Until=now.AddSeconds(3)};}
            else {
                bool shortPose=f[2]=="wave" || f[2]=="failed";
                sessions[session]=new Mood{Pose=f[2],Text=text,Time=now,Until=shortPose?now.AddSeconds(4):now.AddHours(4)};
            }
            Tick();
        }
        void Ambient(DateTime now) {
            sleeping=Desktop.Sleeping();
            if(peek!=null) {Clamp();return;}
            if(host.Prefs.Music && host.AudioPlaying) audioUntil=now.AddSeconds(1);
        }

        Vector dragVelocity, flight;
        bool falling, boarding;
        IntPtr lastWindow;
        DateTime motionAt, boardStart;
        Point boardFrom;
        double seatOffset;
        readonly ScaleTransform compression=new ScaleTransform();

        void StopMotion() {
            falling=boarding=false;
            compression.BeginAnimation(ScaleTransform.ScaleXProperty,null);
            compression.BeginAnimation(ScaleTransform.ScaleYProperty,null);
            if (body.RenderTransform==compression) body.RenderTransform=Transform.Identity;
        }
        void StartFall(Vector release) {
            StopMotion();
            flight=release.Length>3200 ? release*(3200/release.Length) : release;
            perched=false; falling=true; motionAt=DateTime.UtcNow;
        }
        void Land(DateTime now,double impact) {
            falling=boarding=false;
            double amount=Limit(impact/2400,.08,.28);
            body.RenderTransformOrigin=new Point(.5,Art.FeetY/Art.H);
            body.RenderTransform=compression;
            // Start at impact so even a slow renderer shows the compressed pose.
            var duration=new Duration(TimeSpan.FromSeconds(.28));
            var ease=new QuadraticEase {EasingMode=EasingMode.EaseOut};
            compression.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(1+.6*amount,1,duration) {EasingFunction=ease});
            compression.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(1-amount,1,duration) {EasingFunction=ease});
            host.Remember(this);
        }
        void Motion(DateTime now) {
            if (held || peek!=null || hopStart!=DateTime.MinValue) return;
            Desktop.Box r=new Desktop.Box(); IntPtr window=IntPtr.Zero;
            bool ledge=host.Prefs.Follow && Desktop.Window(out r,out window);
            double left=0,right=0,seatTop=0;
            if (ledge) {
                var corner=Dip(r.Left,r.Top);
                var screen=Area(MonitorAt(corner).WorkingArea);
                left=Math.Max(screen.Left,corner.X);
                right=Math.Min(screen.Right,Dip(r.Right,r.Top).X);
                seatTop=corner.Y-caption-Art.Seat*PetScale;
                ledge=seatTop>=screen.Top && right-left>=body.Width*.6;
            }
            if (!ledge) window=IntPtr.Zero;
            bool changed=window!=lastWindow;
            lastWindow=window;
            if (!falling && (perched || boarding) && !ledge) StartFall(new Vector());
            if (falling) {
                double dt=Limit((now-motionAt).TotalSeconds,0,.2);
                motionAt=now;
                double dy=flight.Y*dt+1300*dt*dt;
                flight.Y+=2600*dt; flight.X*=Math.Pow(.7,dt);
                var nextLeft=Left+flight.X*dt;
                var nextTop=Top+dy;
                var area=Area(MonitorAt(new Point(nextLeft+Width/2,nextTop+Height/2)).WorkingArea);
                var x=Limit(nextLeft,area.Left,area.Right-Width);
                if (x!=nextLeft) flight.X*=-.45;
                Left=x;
                if (nextTop<area.Top && flight.Y<0) {nextTop=area.Top;flight.Y*=-.3;}
                double center=Left+Width/2;
                if (ledge && flight.Y>=0 && Top<=seatTop+1 && nextTop>=seatTop && center>=left && center<=right) {
                    seatOffset=center-left; Top=seatTop;perched=true;Land(now,flight.Y);
                } else if (nextTop>=area.Bottom-Height+4 && flight.Y>=0) {
                    Top=area.Bottom-Height+4;Land(now,flight.Y);
                } else Top=nextTop;
                return;
            }
            if (!ledge) return;
            double margin=Math.Min(body.Width*.3,(right-left)/2);
            if (changed) {
                boardFrom=new Point(Left,Top);boardStart=now;boarding=true;perched=false;
                seatOffset=Limit(Left+Width/2-left,margin,right-left-margin);
            }
            if (!perched && !boarding) return;
            double target=left+Limit(seatOffset,margin,right-left-margin)-Width/2;
            if (boarding) {
                double t=Limit((now-boardStart).TotalSeconds/.42,0,1);
                double ease=t*t*(3-2*t);
                Left=boardFrom.X+(target-boardFrom.X)*ease;
                Top=boardFrom.Y+(seatTop-boardFrom.Y)*ease-4*t*(1-t)*body.Height*.22;
                if (t>=1) {perched=true;Land(now,300);}
            } else {Left=target;Top=seatTop;}
            Clamp();
        }
        void Tick() {
            var now=DateTime.UtcNow;
            // Only search for expired sessions on the slower ambient tick.
            if(now>=ambientAt) foreach(var key in sessions.Where(p=>p.Value.Until<=now).Select(p=>p.Key).ToArray())sessions.Remove(key);
            Mood mood=null;
            foreach(var m in sessions.Values) {
                if(mood==null || (m.Pose=="waiting" && mood.Pose!="waiting") || ((m.Pose=="waiting")== (mood.Pose=="waiting") && m.Time>mood.Time))mood=m;
            }
            if(note!=null && note.Until>now && (mood==null || mood.Pose!="waiting")) mood=note;
            if(mood==null && PendingLetters>0) mood=new Mood {Pose="work",Text="正在处理委托"};
            if(completion!=null && completion.Until>now && (mood==null || mood.Pose!="waiting"))
                mood=completion;
            if(mood==null && demo!=null && demo.Until>now)mood=demo;
            if(now>=ambientAt){ambientAt=now.AddMilliseconds(260);Ambient(now);}
            Motion(now);
            UpdateHop(now);
            if(peek!=null) UpdatePeek(now,mood!=null);
            string pose=(held && peek==null) || (falling && Id=="fourteen")?"dangle":mood!=null?mood.Pose:peek!=null?"idle":falling || boarding?"jump":audioUntil>now?"dance":sleeping?"sleep":perched?"perch":"idle";
            bool moving=held || falling || boarding || hopStart!=DateTime.MinValue || perched || (Id=="fourteen" && pose=="dance");
            var interval=TimeSpan.FromMilliseconds(moving ? 12 : 35);
            if (timer.Interval!=interval) timer.Interval=interval;
            string line=held?"":mood==null?"":mood.Text;
            if(label.Text!=line)label.Text=line;
            bubble.Visibility=line==""?Visibility.Collapsed:Visibility.Visible;
            pose=Art.Pose(pose);
            bool track=peek!=null && pose=="idle";
            string display=track?"peek":pose;
            if(display!=shown){shown=display;frame=0;due=DateTime.MinValue;Title="GLORIA · "+Id+" · "+display;}
            if (now >= due) {
                var anim = Art.Get(pose);
                displayedFrame = frame;
                if (image != null) image.Source = Art.Cell(anim.Start+frame);
                due = now.AddMilliseconds(anim.Times[frame]);
                frame = (frame+1) % anim.Count;
            }
            double? look=track ? peekLook : pose=="idle" && now<faceUntil ? (double?)faceAngle : null;
            if (look.HasValue && image!=null) {
                int direction=((int)Math.Round(look.Value*8/Math.PI)%16+16)%16;
                image.Source=Art.Cell(72+direction);
            }
            if (model!=null) {
                model.InFlight=falling;
                model.CarryVelocity=falling ? flight.X : (now-dragSample).TotalSeconds<.12 ? dragVelocity.X : 0;
                model.Draw(pose,displayedFrame,now,look);
            }
        }
    }
    static class NativeStyle {
        [System.Runtime.InteropServices.DllImport("user32.dll")]static extern int GetWindowLong(IntPtr h,int index);
        [System.Runtime.InteropServices.DllImport("user32.dll")]static extern int SetWindowLong(IntPtr h,int index,int value);
        internal static void PassThrough(IntPtr h){NoActivate(h);SetWindowLong(h,-20,GetWindowLong(h,-20)|0x20);}
        internal static void NoActivate(IntPtr h){SetWindowLong(h,-20,GetWindowLong(h,-20)|0x08000000|0x80);}
    }
}
