using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Gloria {
    internal sealed partial class Host {
        sealed class Delegation {
            internal string Source, Session, Call, Target, Tool;
            internal Pet Sender, Receiver;
            internal bool Delivered, Finished, Failed, Returning;
            internal DateTime Started = DateTime.UtcNow;
        }
        readonly List<Delegation> letters = new List<Delegation>();
        Pet Avatar(string source) {
            string id;
            if (!Prefs.Roles.TryGetValue(source,out id) || !Ids.Contains(id)) id="gloria";
            return Show(id);
        }
        static string AgentName(string source) { return Hooks.Names[Array.IndexOf(Events.Sources,source)]; }
        void HandleAgent(string[] fields) {
            var sender=Avatar(fields[0]);
            if (fields.Length<8 || fields[5]!="begin") sender.React(fields);
            var session=letters.Where(x=>x.Source==fields[0] && x.Session==fields[1]).ToList();
            if (fields[4]=="end") {
                foreach (var link in session.Where(x=>!x.Finished)) Cancel(link);
                return;
            }
            if (fields.Length<8) return; // Previous hook executable
            string phase=fields[5], call=fields[6], target=fields[7];
            var match=call=="" ? session.FirstOrDefault(x=>!x.Finished && x.Target==target && x.Call=="")
                               : session.FirstOrDefault(x=>x.Call==call);
            string tool = fields.Length==10?fields[8]:"";
            if (match==null && call=="" && target=="" && tool!="" && fields[9]=="empty" && (phase=="finish" || phase=="fail")) {
                var possible=session.Where(x=>!x.Finished && x.Tool==tool).Take(2).ToArray();
                if (possible.Length==1) match=possible[0];
            }
            if (phase=="begin" && Events.Sources.Contains(target) && target!=fields[0]) {
                if (call!="" && match!=null) return;
                var link=new Delegation {Source=fields[0],Session=fields[1],Call=call,Target=target,Tool=tool,
                                         Sender=sender,Receiver=Avatar(target)};
                letters.Add(link);
                link.Receiver.PendingLetters++;
                sender.SendingLetters++;
                sender.React(fields);
                Pass(link,false);
            } else if (phase=="begin") sender.React(fields);
            else if ((phase=="finish" || phase=="fail") && match!=null && !match.Finished) {
                match.Finished=true;
                match.Failed=phase=="fail";
                if (match.Delivered) Pass(match,true);
            }
        }
        void Cancel(Delegation link) {
            if (letters.Remove(link)) {link.Receiver.PendingLetters--;link.Sender.SendingLetters--;}
        }
        void ExpireLetters() {
            foreach (var link in letters.Where(x=>!x.Sender.IsVisible || !x.Receiver.IsVisible ||
                        (DateTime.UtcNow-x.Started).TotalHours>=4).ToArray()) Cancel(link);
        }
        void Pass(Delegation link,bool reply) {
            if (!letters.Contains(link) || (reply && link.Returning)) return;
            if (reply) link.Returning=true;
            var from=reply?link.Receiver:link.Sender;
            var to=reply?link.Sender:link.Receiver;
            string say=reply?(link.Failed?"这个没搞定……":"做好啦，交给你～"):AgentName(link.Target)+"，这个交给你啦～";
            string received=reply?(link.Failed?"没关系，我来想办法。":"收到 "+AgentName(link.Target)+" 的结果，谢啦！"):
                                  "收到 "+AgentName(link.Source)+" 的委托！";
            if (from==to) {
                from.Note(reply && link.Failed?"failed":"wave",reply?received:say,2);
                if (reply) Cancel(link); else link.Delivered=true;
                return;
            }
            Courier.Send(from,to,reply && link.Failed?"failed":"wave",say,
                delegate {return letters.Contains(link) && from.IsVisible && to.IsVisible;},
                delegate {
                    to.CatchLetter(from,received);
                    if (reply) Cancel(link);
                    else {
                        link.Delivered=true;
                        if (link.Finished) Later(650,delegate {Pass(link,true);});
                    }
                });
        }
        static void Later(int ms,Action action) {
            var timer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(ms) };
            timer.Tick+=delegate {timer.Stop();action();};
            timer.Start();
        }
        void DemoLetter() {
            var key=Guid.NewGuid().ToString("N");
            HandleAgent(new[]{"claude",key,"work","接活了","open","begin",key,"codex"});
            Later(5200,delegate {
                HandleAgent(new[]{"claude",key,"work","收到结果","open","finish",key,"codex"});
                HandleAgent(new[]{"claude",key,"wave","做完啦","end","state","",""});
            });
        }
    }

    internal sealed partial class Pet {
        internal int PendingLetters, SendingLetters;
        Mood note;
        DateTime faceUntil;
        double faceAngle;
        internal void FacePet(Pet other) {
            faceAngle = other.Hands.X>Hands.X ? Math.PI/2 : -Math.PI/2;
            faceUntil = DateTime.UtcNow.AddSeconds(2.2);
        }
        internal void CatchLetter(Pet from,string text) {
            FacePet(from);
            Note("jump",text,1.3);
            var grid=(Grid)Content;
            var point=Hands-new Point(Left,Top);
            var glint=new TextBlock {Text="✉",FontSize=19,Foreground=Brushes.MediumPurple,
                HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top,
                Margin=new Thickness(Math.Max(0,point.X-8),Math.Max(0,point.Y-20),0,0),IsHitTestVisible=false};
            var drift=new TranslateTransform();glint.RenderTransform=drift;grid.Children.Add(glint);
            var rise=new System.Windows.Media.Animation.DoubleAnimation(0,-22,TimeSpan.FromSeconds(.6));
            var fade=new System.Windows.Media.Animation.DoubleAnimation(.95,0,TimeSpan.FromSeconds(.6));
            fade.Completed+=delegate {grid.Children.Remove(glint);};
            drift.BeginAnimation(TranslateTransform.YProperty,rise);glint.BeginAnimation(UIElement.OpacityProperty,fade);
        }
        int walkTicket;
        internal void Note(string pose,string text,double seconds) {
            note=new Mood {Pose=pose,Text=text,Time=DateTime.UtcNow,Until=DateTime.UtcNow.AddSeconds(seconds)};
        }
        internal Point Hands {
            get {
                if (peek!=null) return new Point(peek.Edge-peek.Side*FaceRadius*.6,peek.HeadY+FaceRadius*.4);
                return new Point(Left+Width*.5,Top+caption+body.Height*.5);
            }
        }
        internal double BodyWidth {get {return body.Width;}}
        internal int BeginWalk(Pet other) {
            if (held || falling || boarding || other.falling || other.boarding || peek!=null || perched!=other.perched || other.peek!=null || other.held ||
                sessions.Values.Any(x=>x.Pose=="waiting" && x.Until>DateTime.UtcNow) ||
                Math.Abs(Top+Height-other.Top-other.Height)>12 ||
                MonitorAt(Hands).DeviceName!=MonitorAt(other.Hands).DeviceName ||
                Math.Abs(Hands.X-other.Hands.X)<1.9*body.Width) return 0;
            return ++walkTicket;
        }
        internal bool Walk(int ticket,Pet other,double dt) {
            if (ticket!=walkTicket || held || peek!=null || other.peek!=null) return false;
            double distance=other.Hands.X-Hands.X;
            double step=Math.Min(Math.Abs(distance)-body.Width*1.2,dt*body.Width*2.5);
            if (step<=1) return false;
            Left+=Math.Sign(distance)*step;
            if (perched) seatOffset+=Math.Sign(distance)*step;
            Note(distance>0?"runRight":"runLeft","",.12);
            return true;
        }
    }

    internal static class Courier {
        static Window Envelope(double size,RotateTransform tilt) {
            var ink=new SolidColorBrush(Color.FromRgb(224,109,151));
            var letter=new Canvas {Width=36,Height=27,RenderTransformOrigin=new Point(.5,.5),RenderTransform=tilt};
            var paper=new Rectangle {Width=34,Height=24,RadiusX=4,RadiusY=4,StrokeThickness=1.5,
                Stroke=ink,Fill=new SolidColorBrush(Color.FromRgb(255,232,240))};
            Canvas.SetLeft(paper,1);Canvas.SetTop(paper,1);letter.Children.Add(paper);
            letter.Children.Add(new Path {Data=Geometry.Parse("M2,3 L18,15 L34,3"),Stroke=ink,StrokeThickness=1.4});
            letter.Children.Add(new Path {Data=Geometry.Parse("M18,19 C16,17 11,14 14,11 C16,9 18,11 18,12 C20,8 25,11 22,15 Z"),Fill=ink});
            var box=new Viewbox {Width=size,Height=size*.75,Child=letter};
            var w=new Window {Title="GLORIA · 信封",WindowStyle=WindowStyle.None,AllowsTransparency=true,Background=Brushes.Transparent,
                ShowActivated=false,Focusable=false,ShowInTaskbar=false,Topmost=true,ResizeMode=ResizeMode.NoResize,
                Width=size*1.6,Height=size*1.6,Content=new Grid { Children={box} }};
            w.SourceInitialized+=delegate { NativeStyle.PassThrough(new WindowInteropHelper(w).Handle); };
            return w;
        }
        internal static void Send(Pet from,Pet to,string pose,string text,Func<bool> alive,Action arrived) {
            var clock=Stopwatch.StartNew();
            var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(16)};
            var tilt=new RotateTransform();
            Window window=null;
            double previous=0,start=0,duration=0;
            int walk=from.BeginWalk(to);
            timer.Tick+=delegate {
                if (!alive()) {timer.Stop();if(window!=null)window.Close();return;}
                double now=clock.Elapsed.TotalSeconds,dt=Math.Min(.1,now-previous);
                previous=now;
                if (window==null && walk!=0 && now<2.6 && from.Walk(walk,to,dt)) return;
                Point a=from.Hands,b=to.Hands;
                if (window==null) {
                    from.FacePet(to);from.Note(pose,text,2.4);
                    window=Envelope(Math.Max(27,from.BodyWidth*34/192),tilt);
                    duration=Math.Min(1.7,.65+(b-a).Length/1700);
                    start=now;
                }
                double u=Math.Min(1,(now-start)/duration);
                if (u>=1) {timer.Stop();window.Close();arrived();return;}
                double e=u<.5?2*u*u:1-2*(1-u)*(1-u),v=1-e;
                double cx=(a.X+b.X)/2,cy=Math.Min(a.Y,b.Y)-Math.Max(70,Math.Abs(b.X-a.X)*.35);
                double x=v*v*a.X+2*v*e*cx+e*e*b.X,y=v*v*a.Y+2*v*e*cy+e*e*b.Y;
                double angle=Math.Atan2(v*(cy-a.Y)+e*(b.Y-cy),v*(cx-a.X)+e*(b.X-cx))*180/Math.PI;
                if(angle>90)angle-=180;else if(angle< -90)angle+=180;
                tilt.Angle=angle*.5-Math.Sign(b.X-a.X)*Math.Sin(u*Math.PI)*20;
                window.Left=x-window.Width/2;window.Top=y-window.Height/2;
                window.Opacity=Math.Min(1,Math.Min(u*8,(1-u)*10));
                if (!window.IsVisible) window.Show();
            };
            timer.Start();
        }
    }
}
