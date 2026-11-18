using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms=System.Windows.Forms;

namespace Gloria {
    public sealed class Preferences {
        public string[] Visible {get;set;}
        public double Size {get;set;}
        public bool Follow {get;set;}
        public bool Music {get;set;}
        public bool EdgePeek {get;set;}
        public Dictionary<string,double[]> Positions {get;set;}
        public Dictionary<string,string> Roles {get;set;}
        public Preferences() {
            EdgePeek=true;Size=.85;Visible=new[]{"gloria"};Positions=new Dictionary<string,double[]>();
            Roles=new Dictionary<string,string>{{"claude","usagi"},{"codex","doro"},{"cursor","gloria"},{"dsh","grokbot"}};
        }
    }
    internal sealed partial class Host : IDisposable {
        internal static readonly string[] Ids={"gloria","doro","usagi","grokbot","fourteen"};
        static readonly string[] names={"GLORIA · 柔彩","Doro","抽象乌萨奇","GrokBot","14酱"};
        readonly Dictionary<string,Pet> pets=new Dictionary<string,Pet>();
        internal readonly Preferences Prefs;
        readonly string settings;
        readonly Forms.NotifyIcon tray;
        readonly Sound sound=new Sound();
        readonly DispatcherTimer audioTimer=new DispatcherTimer();
        internal bool AudioPlaying;
        readonly HwndSource receiver;
        Forms.ContextMenuStrip menu;
        internal Host() {
            settings=Path.Combine(Environment.GetEnvironmentVariable("GLORIA_DATA_DIR")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"GLORIA"),"windows.json");
            try{Prefs=new JavaScriptSerializer().Deserialize<Preferences>(File.ReadAllText(settings));}catch(IOException){}catch(ArgumentException){}catch(InvalidOperationException){}
            if(Prefs==null || Prefs.Roles==null || Prefs.Positions==null || Prefs.Visible==null)Prefs=new Preferences();
            if(!(Prefs.Size>=.5 && Prefs.Size<=1.5))Prefs.Size=.85;
            var p=new HwndSourceParameters(Events.Address){ParentWindow=new IntPtr(-3),WindowStyle=0,Width=0,Height=0};
            receiver=new HwndSource(p);receiver.AddHook(Receive);
            tray=new Forms.NotifyIcon{Icon=System.Drawing.Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location),Text="GLORIA 桌宠",Visible=true};
            tray.MouseClick+=(s,e)=>{if(e.Button==Forms.MouseButtons.Right)Menu(null);};
            tray.DoubleClick+=delegate{Recall();};
            foreach(var id in Prefs.Visible.Distinct().Where(x=>Ids.Contains(x)).ToArray())Show(id);
            audioTimer.Interval=TimeSpan.FromMilliseconds(250);audioTimer.Tick+=delegate{AudioPlaying=Prefs.Music && sound.Playing();ExpireLetters();};audioTimer.Start();
        }
        IntPtr Receive(IntPtr hwnd,int msg,IntPtr w,IntPtr l,ref bool handled) {
            if(msg!=Events.WM_COPYDATA || l==IntPtr.Zero)return IntPtr.Zero;
            var p=(Events.Packet)Marshal.PtrToStructure(l,typeof(Events.Packet));
            if(p.tag!=(IntPtr)Events.Tag || p.size<2 || p.size>1024 || p.size%2!=0 || p.data==IntPtr.Zero)return IntPtr.Zero;
            var f=Marshal.PtrToStringUni(p.data,p.size/2).Split('\t');
            if(f.Length==1 && f[0]=="recall"){Application.Current.Dispatcher.BeginInvoke(new Action(Recall));}
            else if((f.Length==5 || f.Length==8 || f.Length==10) && Events.Sources.Contains(f[0])) {
                Application.Current.Dispatcher.BeginInvoke(new Action(delegate{HandleAgent(f);}));
            }
            handled=true;return (IntPtr)1;
        }
        internal int Slot(string id){return Array.IndexOf(Ids.Where(x=>pets.ContainsKey(x)).ToArray(),id);}
        Pet Show(string id) {
            Pet pet;if(!pets.TryGetValue(id,out pet)){
                pet=new Pet(this,id);pets.Add(id,pet);pet.Show();
            }
            return pet;
        }
        void Recall(){if(pets.Count==0)Show("gloria");foreach(var pet in pets.Values){pet.Recall();Remember(pet);}Save();}
        internal void Remember(Pet pet){Prefs.Positions[pet.Id]=new[]{pet.Left,pet.Top};Save();}
        void Save() {
            Prefs.Visible=Ids.Where(x=>pets.ContainsKey(x)).ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(settings));
            string tmp=settings+".tmp";
            File.WriteAllText(tmp,new JavaScriptSerializer().Serialize(Prefs));
            if(File.Exists(settings))File.Replace(tmp,settings,null);else File.Move(tmp,settings);
        }
        static Forms.ToolStripMenuItem Item(Forms.ToolStripItemCollection list,string title,bool active,Action action) {
            var i=new Forms.ToolStripMenuItem(title){Checked=active};list.Add(i);
            if(action!=null)i.Click+=delegate{try{action();}catch(Exception ex){MessageBox.Show(ex.Message,"GLORIA");}};return i;
        }
        internal void Menu(Pet target) {
            if(menu!=null)menu.Dispose();menu=new Forms.ContextMenuStrip();
            var charMenu=Item(menu.Items,"出场角色",false,null).DropDownItems;
            foreach(string cid in Ids){string id=cid;Item(charMenu,names[Array.IndexOf(Ids,id)],pets.ContainsKey(id),delegate{
                if(pets.ContainsKey(id)){pets[id].Close();pets.Remove(id);}else Show(id);Save();
            });}
            var agentMenu=Item(menu.Items,"Agent 化身",false,null).DropDownItems;
            foreach(string src in Events.Sources) {string source=src;var a=Item(agentMenu,Hooks.Names[Array.IndexOf(Events.Sources,source)],false,null).DropDownItems;
                bool on=Hooks.Installed(source);Item(a,on?"断开接入":"接入此 Agent",on,delegate{
                    Hooks.Set(source,!on);
                    MessageBox.Show(on?"已移除桌宠的钩子。":"已写入钩子。请重新打开 Agent 会话。","GLORIA");
                });
                if (on && source=="cursor") Item(a,"刷新接入",false,delegate{Hooks.Set(source,true);});
                a.Add(new Forms.ToolStripSeparator());
                foreach(string cid in Ids){string id=cid;Item(a,names[Array.IndexOf(Ids,id)],Prefs.Roles.ContainsKey(source)&&Prefs.Roles[source]==id,delegate{Prefs.Roles[source]=id;Save();});}
            }
            var sizes=Item(menu.Items,"大小",false,null).DropDownItems;
            foreach(double k in new[]{.65,.85,1.0,1.25}) {double scale=k;Item(sizes,k.ToString("P0"),Prefs.Size==k,delegate{
                Prefs.Size=scale;foreach(var pet in pets.Values){pet.SizePet();pet.Clamp();}Save();
            });}
            Item(menu.Items,"坐在前台窗口上",Prefs.Follow,delegate{Prefs.Follow=!Prefs.Follow;if(!Prefs.Follow)Recall();Save();});
            Item(menu.Items,"随电脑播放的声音跳舞",Prefs.Music,delegate{Prefs.Music=!Prefs.Music;Save();});
            Item(menu.Items,"拖到屏幕左右边缘会躲起来探头",Prefs.EdgePeek,delegate{Prefs.EdgePeek=!Prefs.EdgePeek;Save();});
            if (target!=null && target.IsPeeking) Item(menu.Items,"从边上跳回来",false,delegate{target.LeavePeek(true);});
            else Item(menu.Items,"躲到屏幕边上偷看",false,delegate{
                if (target!=null) target.PeekAtEdge(0);
                else {int slot=0;foreach(var pet in pets.Values)pet.PeekAtEdge(slot++);}
            });
            Item(menu.Items,"演示：Claude 请 Codex 帮忙",false,DemoLetter);
            Item(menu.Items,"跳一支舞",false,delegate{if(target!=null)target.Play("dance","♪",12);else {if(pets.Count==0)Show("gloria");foreach(var pet in pets.Values)pet.Play("dance","♪",12);}});
            Item(menu.Items,"叫她们回来",false,Recall);
            menu.Items.Add(new Forms.ToolStripSeparator());Item(menu.Items,"退出 GLORIA",false,delegate{Application.Current.Shutdown();});
            menu.Show(Forms.Cursor.Position);
        }
        public void Dispose(){audioTimer.Stop();sound.Dispose();receiver.Dispose();tray.Visible=false;tray.Dispose();if(menu!=null)menu.Dispose();}
    }
    static class Program {
        [STAThread] static int Main(string[] args) {
            try {
                if(args.Length==2 && args[0]=="--gloria-hook") return Events.Forward(args[1]);
                if(args.Length==3 && args[0]=="--hooks" && (args[2]=="on" || args[2]=="off")){Hooks.Set(args[1],args[2]=="on");return 0;}
                if(args.Length!=0)return 2;
                bool fresh;
                using(var mutex=new Mutex(true,@"Local\"+Events.Address,out fresh)) {
                    if(!fresh){Events.Send(new[]{"recall"});return 0;}
                    var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
                    using(var host=new Host())app.Run();
                }
                return 0;
            } catch(Exception ex){if(args.Length==0)MessageBox.Show(ex.Message,"GLORIA 启动失败");else Console.Error.WriteLine(ex.Message);return 1;}
        }
    }
}
