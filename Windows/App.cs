using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Gloria {
    internal sealed partial class Host : IDisposable {
        internal sealed class DesktopSnapshot {
            internal List<WinInfo> Wins;
            internal List<IntPtr> ZOrder;
            internal List<Ledge> Ledges;
            internal double Time;
        }

        internal string[] AllCharacterIds = Character.BuiltInIds;
        readonly Dictionary<string, Pet> pets = new Dictionary<string, Pet>();
        internal IEnumerable<Pet> AllPets { get { return pets.Values; } }

        internal readonly Preferences Prefs;
        internal readonly MusicWatch Music;
        readonly string settingsPath;
        readonly Forms.NotifyIcon tray;
        readonly Drawing.Icon defaultIcon;
        Drawing.Icon customTrayIcon;
        readonly DispatcherTimer ambientTimer = new DispatcherTimer();
        readonly DispatcherTimer saveTimer = new DispatcherTimer();
        bool dirtySave;
        readonly HwndSource receiver;
        Forms.ContextMenuStrip menu;
        volatile bool disposed;
        Thread ctlThread;

        DesktopSnapshot cachedDesktop;

        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr handle);

        internal Host() {
            AllCharacterIds = Character.DiscoverIds();
            if (AllCharacterIds.Length == 0) AllCharacterIds = Character.BuiltInIds;

            string dataRoot = Environment.GetEnvironmentVariable("GLORIA_DATA_DIR")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GLORIA");
            settingsPath = Path.Combine(dataRoot, "windows.json");

            Preferences loaded = null;
            try {
                if (File.Exists(settingsPath)) {
                    loaded = new JavaScriptSerializer().Deserialize<Preferences>(File.ReadAllText(settingsPath));
                }
            } catch { }
            Prefs = loaded ?? new Preferences();
            Prefs.Sanitize();

            Music = new MusicWatch(Prefs);

            var p = new HwndSourceParameters(Events.Address) {
                ParentWindow = new IntPtr(-3),
                WindowStyle = 0,
                Width = 0,
                Height = 0
            };
            receiver = new HwndSource(p);
            receiver.AddHook(ReceiveMessage);

            defaultIcon = System.Drawing.Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location);
            tray = new Forms.NotifyIcon {
                Icon = defaultIcon,
                Text = "GLORIA 桌宠",
                Visible = true
            };
            tray.MouseUp += (s, e) => {
                if (e.Button == Forms.MouseButtons.Right || e.Button == Forms.MouseButtons.Left) Menu(null);
            };
            tray.DoubleClick += delegate { SummonAll(); };

            var initialIds = Prefs.Visible.Distinct().Where(x => AllCharacterIds.Contains(x)).ToArray();
            if (initialIds.Length == 0) initialIds = new[] { "gloria" };
            for (int i = 0; i < initialIds.Length; i++) {
                ShowPet(initialIds[i], i, loaded == null);
            }
            AfterRoster();

            saveTimer.Interval = TimeSpan.FromMilliseconds(400);
            saveTimer.Tick += delegate {
                saveTimer.Stop();
                if (dirtySave) SaveNow();
            };

            ambientTimer.Interval = TimeSpan.FromMilliseconds(500);
            ambientTimer.Tick += delegate {
                Music.Poll();
                ExpireStaleSessions();
            };
            ambientTimer.Start();
            Music.Poll();

            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

            StartControlPipeServer();

            bool isolatedProfile = !String.IsNullOrEmpty(Environment.GetEnvironmentVariable("GLORIA_DATA_DIR"));
            // Self-heal hooks if exe moved (skip during isolated test profiles so user configs are untouched)
            if (!isolatedProfile) {
                foreach (string src in Events.Sources) {
                    if (Hooks.Installed(src) && !Hooks.UpToDate(src)) {
                        try { Hooks.Set(src, true); } catch { }
                    }
                }
            }

            // Interactive first-launch onboarding (never during automated test profiles)
            if (!Prefs.Onboarded && !isolatedProfile) {
                Later(2500, MaybeOnboard);
            }
        }

        void OnDisplaySettingsChanged(object sender, EventArgs e) {
            if (disposed || Application.Current == null) return;
            Application.Current.Dispatcher.BeginInvoke(new Action(delegate {
                if (disposed) return;
                Dpi.RefreshScale();
                cachedDesktop = null;
                foreach (var pet in pets.Values) {
                    pet.SizePet();
                    if (pet.IsPeeking) {
                        pet.PeekAtEdge(Slot(pet.Id));
                    } else if (Desktop.ScreenAt(pet.Feet) == null) {
                        pet.Summon(false, Slot(pet.Id), null);
                    }
                }
            }));
        }

        internal DesktopSnapshot GetDesktop(double maxAgeSeconds) {
            double t = Clock.Now;
            if (cachedDesktop != null && (t - cachedDesktop.Time) <= maxAgeSeconds) {
                return cachedDesktop;
            }
            List<WinInfo> wins;
            List<IntPtr> order;
            Desktop.Scan(out wins, out order);
            var ledges = Desktop.BuildLedges(wins, 88.0, Prefs.WindowSit);
            cachedDesktop = new DesktopSnapshot {
                Wins = wins,
                ZOrder = order,
                Ledges = ledges,
                Time = t
            };
            return cachedDesktop;
        }

        internal static void Later(int ms, Action action) {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(1, ms)) };
            timer.Tick += delegate {
                timer.Stop();
                action();
            };
            timer.Start();
        }

        IntPtr ReceiveMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled) {
            if (msg != Events.WM_COPYDATA || l == IntPtr.Zero) return IntPtr.Zero;
            var p = (Events.Packet)Marshal.PtrToStructure(l, typeof(Events.Packet));
            if (p.data == IntPtr.Zero || p.size < 2 || p.size > 65536 || p.size % 2 != 0) return IntPtr.Zero;

            if (p.tag == (IntPtr)Events.TagJson) {
                string json = Marshal.PtrToStringUni(p.data, p.size / 2);
                Application.Current.Dispatcher.BeginInvoke(new Action(delegate {
                    try {
                        var ev = new JavaScriptSerializer { MaxJsonLength = 2 * 1024 * 1024 }
                            .Deserialize<Dictionary<string, object>>(json);
                        HandleHookEvent(ev, false);
                    } catch { }
                }));
                handled = true;
                return (IntPtr)1;
            }

            if (p.tag == (IntPtr)Events.TagLegacy) {
                string raw = Marshal.PtrToStringUni(p.data, p.size / 2);
                var f = raw.Split('\t');
                if (f.Length == 1 && f[0] == "recall") {
                    Application.Current.Dispatcher.BeginInvoke(new Action(SummonAll));
                } else if ((f.Length == 5 || f.Length == 8 || f.Length == 10) && Events.Sources.Contains(f[0])) {
                    Application.Current.Dispatcher.BeginInvoke(new Action(delegate { HandleLegacyPacket(f); }));
                }
                handled = true;
                return (IntPtr)1;
            }
            return IntPtr.Zero;
        }

        internal int Slot(string id) {
            int idx = 0;
            foreach (string cid in AllCharacterIds) {
                if (!pets.ContainsKey(cid)) continue;
                if (cid == id) return idx;
                idx++;
            }
            return 0;
        }

        Pet ShowPet(string id, int? explicitSlot = null, bool entrance = false) {
            Pet pet;
            if (pets.TryGetValue(id, out pet)) return pet;
            AllCharacterIds = Character.DiscoverIds();
            if (!AllCharacterIds.Contains(id)) return null;

            pet = new Pet(this, id);
            pets.Add(id, pet);
            int slot = explicitSlot.HasValue ? explicitSlot.Value : (pets.Count - 1);

            Point? saved = null;
            double[] xy;
            if (Prefs.Positions != null && Prefs.Positions.TryGetValue("feet:" + id, out xy) &&
                xy != null && xy.Length == 2 && !xy.Any(v => Double.IsNaN(v) || Double.IsInfinity(v))) {
                saved = new Point(xy[0], xy[1]);
            }
            pet.Start(saved, slot, entrance);
            AfterRoster();
            QueueSave();
            return pet;
        }

        void SetShown(string id, bool on) {
            if (on) {
                if (pets.ContainsKey(id)) return;
                ShowPet(id, pets.Count, true);
            } else {
                if (pets.Count <= 1) return;
                Pet p;
                if (!pets.TryGetValue(id, out p)) return;
                p.Close();
                pets.Remove(id);
                AfterRoster();
                QueueSave();
            }
        }

        void ReplacePet(Pet oldPet, string newId) {
            if (oldPet == null || oldPet.Id == newId) return;
            if (pets.ContainsKey(newId)) {
                SetShown(oldPet.Id, false);
                return;
            }
            Point at = oldPet.Feet;
            foreach (string src in Events.Sources) {
                string r;
                if (Prefs.Roles.TryGetValue(src, out r) && r == oldPet.Id) Prefs.Roles[src] = newId;
            }
            oldPet.Close();
            pets.Remove(oldPet.Id);
            Prefs.Positions["feet:" + newId] = new[] { at.X, at.Y - 40 };
            var created = ShowPet(newId, pets.Count, false);
            if (created != null) {
                created.ThrowHer(0, 140);
                Later(800, delegate {
                    if (created.IsVisible) {
                        created.Play("wave", 2);
                        created.Say(created.Art.Lines.Greeting, created.Art.Tag, BubbleKind.Speech, 3.0, false);
                    }
                });
            }
            AfterRoster();
            QueueSave();
        }

        void SetRole(string source, string charId) {
            if (!pets.ContainsKey(charId)) ShowPet(charId, pets.Count, false);
            Prefs.Roles[source] = charId;
            ClearHuskState();
            AfterRoster();
            QueueSave();
            var p = AvatarFor(source, false);
            if (p != null) {
                int ix = Array.IndexOf(Events.Sources, source);
                string agentName = ix >= 0 ? Hooks.Names[ix] : source;
                p.Play("wave", 1);
                p.Say("我来当 " + agentName + " 的化身！", SourceTag(source), BubbleKind.Speech, 3.0, false);
            }
        }

        void AfterRoster() {
            RefreshPetsFromSessions();
            foreach (var p in pets.Values) p.Roles.Clear();
            for (int i = 0; i < Events.Sources.Length; i++) {
                string src = Events.Sources[i];
                if (Hooks.Installed(src)) {
                    var av = AvatarFor(src, false);
                    if (av != null) av.Roles.Add(Hooks.Names[i]);
                }
            }
            UpdateTrayIcon();
        }

        void UpdateTrayIcon() {
            try {
                var firstSprite = pets.Values.FirstOrDefault(p => !p.Art.IsModel);
                if (firstSprite != null) {
                    using (var face = firstSprite.Art.CropFaceBitmap(32)) {
                        if (face != null) {
                            using (var sq = new Drawing.Bitmap(32, 32, Drawing.Imaging.PixelFormat.Format32bppArgb)) {
                                using (var g = Drawing.Graphics.FromImage(sq)) {
                                    g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.HighQuality;
                                    int x = (32 - face.Width) / 2;
                                    g.DrawImage(face, new Drawing.Rectangle(Math.Max(0, x), 0, Math.Min(32, face.Width), 32));
                                }
                                IntPtr hIcon = sq.GetHicon();
                                Drawing.Icon newIcon;
                                try {
                                    newIcon = (Drawing.Icon)Drawing.Icon.FromHandle(hIcon).Clone();
                                } finally {
                                    DestroyIcon(hIcon);
                                }
                                var prev = customTrayIcon;
                                customTrayIcon = newIcon;
                                tray.Icon = customTrayIcon;
                                if (prev != null) prev.Dispose();
                                return;
                            }
                        }
                    }
                }
                tray.Icon = defaultIcon;
            } catch {
                tray.Icon = defaultIcon;
            }
        }

        void SummonAll() {
            if (pets.Count == 0) ShowPet("gloria", 0, false);
            int slot = 0;
            foreach (string cid in AllCharacterIds) {
                Pet pet;
                if (pets.TryGetValue(cid, out pet)) {
                    pet.Summon(false, slot++, null);
                    Remember(pet);
                }
            }
            QueueSave();
        }

        internal void Remember(Pet pet) {
            if (pet == null) return;
            Prefs.Positions["feet:" + pet.Id] = new[] { Math.Round(pet.Feet.X, 1), Math.Round(pet.Feet.Y, 1) };
            QueueSave();
        }

        void QueueSave() {
            dirtySave = true;
            saveTimer.Stop();
            saveTimer.Start();
        }

        void SaveNow() {
            dirtySave = false;
            try {
                Prefs.Visible = AllCharacterIds.Where(x => pets.ContainsKey(x)).ToArray();
                if (Prefs.Visible.Length == 0) Prefs.Visible = new[] { "gloria" };
                Directory.CreateDirectory(Path.GetDirectoryName(settingsPath));
                string tmp = settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(tmp, new JavaScriptSerializer().Serialize(Prefs));
                if (File.Exists(settingsPath)) File.Replace(tmp, settingsPath, null);
                else File.Move(tmp, settingsPath);
            } catch { }
        }

        void MaybeOnboard() {
            Prefs.Onboarded = true;
            QueueSave();
            var found = Events.Sources.Where(s => Hooks.AgentPresent(s) && !Hooks.Installed(s)).ToList();
            if (found.Count == 0) return;

            var names = found.Select(s => Hooks.Names[Array.IndexOf(Events.Sources, s)]).ToArray();
            var focus = pets.Values.FirstOrDefault();
            var res = MessageBox.Show(
                "在这台电脑上找到了：" + String.Join("、", names) + "。\n\n" +
                "要让桌宠当它们的化身吗？接入后它们干活、跑命令、等你批准、做完时，桌宠都会演出来并冒泡提醒（原配置会自动备份，右键菜单随时可关）。",
                "GLORIA · AI 编程助手化身",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (res != MessageBoxResult.Yes) {
                if (focus != null) focus.Say("想接的时候，右键我 › 化身 › 接入。", null, BubbleKind.Speech, 4.0, false);
                return;
            }

            var picked = new List<string>();
            var errors = new List<string>();
            foreach (string src in found) {
                string label = Hooks.Names[Array.IndexOf(Events.Sources, src)];
                try {
                    Hooks.Set(src, true);
                    picked.Add(label);
                } catch (Exception ex) {
                    errors.Add(label + "：" + ex.Message);
                }
            }
            AfterRoster();
            if (focus != null) {
                if (errors.Count > 0) {
                    focus.Say("有的没接上——" + String.Join("；", errors.ToArray()), null, BubbleKind.Speech, 6.0, false);
                } else if (picked.Count > 0) {
                    focus.Play("wave", 2);
                    focus.Say("接好了：" + String.Join("、", picked.ToArray()) + "。重开一次会话就生效啦～", null, BubbleKind.Speech, 6.0, false);
                }
            }
        }

        // MARK: Context Menu (matching Sources/App.swift buildMenu)

        static Forms.ToolStripMenuItem MenuItem(Forms.ToolStripItemCollection list, string title, bool? checkState, Action action, bool enabled = true, bool bold = false) {
            var item = new Forms.ToolStripMenuItem(title) { Enabled = enabled };
            if (checkState.HasValue) item.Checked = checkState.Value;
            if (bold) item.Font = new Drawing.Font("Microsoft YaHei UI", 9.2f, Drawing.FontStyle.Bold);
            if (action != null) {
                item.Click += delegate {
                    try { action(); } catch (Exception ex) { MessageBox.Show(ex.Message, "GLORIA"); }
                };
            }
            list.Add(item);
            return item;
        }

        string WhereStatus(Pet p) {
            var s = p.BuildStatus();
            string mode = s["mode"] as string ?? "";
            string screenNote = "";
            if (Forms.Screen.AllScreens.Length > 1) {
                var scr = p.CurrentScreen;
                int ix = Array.IndexOf(Forms.Screen.AllScreens, scr);
                if (scr != null) screenNote = "（" + Desktop.ScreenLabel(scr, Math.Max(0, ix)) + "）";
            }
            switch (mode) {
                case "perched":
                case "walking": {
                    string owner = s["seatOwner"] as string ?? "";
                    return (owner == "floor" || owner == "" ? "站在屏幕底部" : ("站在「" + owner + "」的窗口上")) + screenNote;
                }
                case "held":
                    return "被你拎着";
                case "peeking": {
                    var pk = s["peek"] as Dictionary<string, object>;
                    int side = pk != null && pk.ContainsKey("side") ? Convert.ToInt32(pk["side"]) : 1;
                    return "躲在屏幕" + (side > 0 ? "右" : "左") + "边偷看" + screenNote;
                }
                default:
                    return "在空中";
            }
        }

        Forms.ContextMenuStrip CreateMenu(Pet target) {
            AllCharacterIds = Character.DiscoverIds();
            var m = new Forms.ContextMenuStrip { Font = new Drawing.Font("Microsoft YaHei UI", 9.0f) };

            var headerPets = target != null ? new[] { target } : pets.Values.ToArray();
            foreach (var p in headerPets) {
                string roleSuffix = p.Roles.Count > 0 ? (" · " + String.Join(" / ", p.Roles.ToArray()) + " 化身") : "";
                MenuItem(m.Items, Character.TitleFor(p.Id) + roleSuffix, null, null, false, true);
                MenuItem(m.Items, "  " + WhereStatus(p), null, null, false);
            }

            string musicLine;
            if (Music.Dancing) {
                musicLine = "  ♪ 跳舞中"
                    + (String.IsNullOrEmpty(Music.PlayerName) ? "" : (" · " + Music.PlayerName))
                    + (Music.TapLive ? (" · " + Music.TapNote) : "");
            } else if (Music.Active) {
                musicLine = "  ♪ " + Music.PlayerName + " 有声音（等节拍稳定）";
            } else {
                musicLine = "  ♪ 没在放歌";
            }
            MenuItem(m.Items, musicLine, null, null, false);

            if (Events.Sources.Any(Hooks.Installed)) {
                MenuItem(m.Items, "  ⌘ 化身：" + HuskSummary, null, null, false);
            }
            m.Items.Add(new Forms.ToolStripSeparator());

            // Characters on screen
            var whoMenu = MenuItem(m.Items, "角色（可以同时放几个）", null, null).DropDownItems;
            foreach (string cid in AllCharacterIds) {
                string id = cid;
                bool shown = pets.ContainsKey(id);
                bool canToggle = !(shown && pets.Count <= 1);
                MenuItem(whoMenu, Character.TitleFor(id), shown, delegate { SetShown(id, !shown); }, canToggle);
            }

            // Agent avatars
            for (int si = 0; si < Events.Sources.Length; si++) {
                string source = Events.Sources[si];
                string agentName = Hooks.Names[si];
                bool on = Hooks.Installed(source);
                var avPet = AvatarFor(source, false);
                string avTitle = avPet != null ? Character.TitleFor(avPet.Id) : Character.TitleFor(Prefs.Roles[source]);
                string parentLabel = agentName + " 化身" + (on ? ("：" + avTitle) : "（未接入）");
                var sm = MenuItem(m.Items, parentLabel, null, null).DropDownItems;

                MenuItem(sm, on ? ("断开 " + agentName + "（移除钩子）") : ("接入 " + agentName + "（装钩子）"), on, delegate {
                    ToggleAgentHook(source, !on, target);
                });
                if (on && source == "cursor") {
                    MenuItem(sm, "刷新接入配置", null, delegate {
                        Hooks.Set(source, true);
                        AfterRoster();
                        var focus = AvatarFor(source, false) ?? target ?? pets.Values.FirstOrDefault();
                        if (focus != null) focus.Say("已刷新 Cursor 钩子配置！", SourceTag(source), BubbleKind.Speech, 3.0, false);
                    });
                }
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string displayPath = Hooks.Config(source);
                if (!String.IsNullOrEmpty(home) && displayPath.StartsWith(home, StringComparison.OrdinalIgnoreCase)) {
                    displayPath = "~" + displayPath.Substring(home.Length);
                }
                MenuItem(sm, "  " + displayPath, null, null, false);
                sm.Add(new Forms.ToolStripSeparator());
                MenuItem(sm, "由谁来当：", null, null, false);
                foreach (string cid in AllCharacterIds) {
                    string id = cid;
                    bool isRole = Prefs.Roles.ContainsKey(source) && Prefs.Roles[source] == id;
                    MenuItem(sm, "  " + Character.TitleFor(id), isRole, delegate { SetRole(source, id); });
                }
            }

            MenuItem(m.Items, "需要批准时提示音", Prefs.Chime, delegate { Prefs.Chime = !Prefs.Chime; QueueSave(); });
            m.Items.Add(new Forms.ToolStripSeparator());

            var focusPet = target ?? pets.Values.FirstOrDefault();
            MenuItem(m.Items, (target == null && pets.Count > 1) ? "叫她们过来" : "叫她过来", null, delegate {
                if (target != null) { target.Summon(false, 0, null); Remember(target); }
                else SummonAll();
            });

            if (target != null && pets.Count > 1) {
                MenuItem(m.Items, "让 " + target.Art.Name + " 先休息（隐藏）", null, delegate { SetShown(target.Id, false); });
            }

            if (Forms.Screen.AllScreens.Length > 1) {
                var moveMenu = MenuItem(m.Items, (target == null && pets.Count > 1) ? "移到显示器（全部）" : "移到显示器", null, null).DropDownItems;
                var moveTargets = target != null ? new[] { target } : pets.Values.ToArray();
                for (int i = 0; i < Forms.Screen.AllScreens.Length; i++) {
                    var scr = Forms.Screen.AllScreens[i];
                    bool allHere = moveTargets.All(p => p.CurrentScreen != null && p.CurrentScreen.DeviceName == scr.DeviceName);
                    MenuItem(moveMenu, Desktop.ScreenLabel(scr, i), allHere, delegate {
                        if (target != null) { target.Summon(false, 0, scr); Remember(target); }
                        else {
                            int slot = 0;
                            foreach (var p in pets.Values) { p.Summon(false, slot++, scr); Remember(p); }
                        }
                    });
                }
            }

            if (target != null && target.IsPeeking) {
                MenuItem(m.Items, "从边上跳回来", null, delegate { target.LeavePeek(); });
            } else {
                MenuItem(m.Items, (target == null && pets.Count > 1) ? "都躲到屏幕边上偷看" : "躲到屏幕边上偷看", null, delegate {
                    if (target != null) target.PeekAtEdge(0);
                    else {
                        int slot = 0;
                        foreach (var p in pets.Values) p.PeekAtEdge(slot++);
                    }
                });
            }

            var sizeMenu = MenuItem(m.Items, "大小", null, null).DropDownItems;
            Tuple<string, double>[] sizes = {
                Tuple.Create("小 (50%)", 0.50),
                Tuple.Create("中 (62%)", 0.62),
                Tuple.Create("大 (80%)", 0.80),
                Tuple.Create("特大 (100%)", 1.00),
                Tuple.Create("超大 (125%)", 1.25)
            };
            foreach (var sz in sizes) {
                double sc = sz.Item2;
                MenuItem(sizeMenu, sz.Item1, Math.Abs(Prefs.Size - sc) < 0.02, delegate {
                    SetScaleAll(sc);
                });
            }

            MenuItem(m.Items, "坐在窗口上", Prefs.WindowSit, delegate {
                Prefs.WindowSit = !Prefs.WindowSit;
                cachedDesktop = null;
                if (!Prefs.WindowSit) {
                    foreach (var p in pets.Values) {
                        if (p.Seat.HasValue && !p.Seat.Value.IsFloor) p.Fall();
                    }
                }
                QueueSave();
            });
            MenuItem(m.Items, "切换窗口时自动跳过来", Prefs.Follow, delegate {
                Prefs.Follow = !Prefs.Follow;
                if (Prefs.Follow && !Prefs.WindowSit) Prefs.WindowSit = true;
                cachedDesktop = null;
                QueueSave();
            });
            MenuItem(m.Items, "自己溜达", Prefs.Wander, delegate { Prefs.Wander = !Prefs.Wander; QueueSave(); });
            MenuItem(m.Items, "闲着会打盹（3 分钟没操作）", Prefs.Nap, delegate { Prefs.Nap = !Prefs.Nap; QueueSave(); });
            MenuItem(m.Items, "追光标", Prefs.Chase, delegate { Prefs.Chase = !Prefs.Chase; QueueSave(); });
            MenuItem(m.Items, "拖到屏幕左右边缘会躲起来探头", Prefs.EdgePeek, delegate { Prefs.EdgePeek = !Prefs.EdgePeek; QueueSave(); });
            bool loginOn = Startup.IsEnabled();
            MenuItem(m.Items, "登录时启动", loginOn, delegate {
                string r = Startup.Set(!loginOn);
                if (focusPet != null) {
                    if (r == "ok") focusPet.Say(!loginOn ? "好～以后开机登录我就自己出来。" : "以后登录时我就不自己出来啦。", null, BubbleKind.Speech, 3.0, false);
                    else focusPet.Say("没设置成功：" + r, null, BubbleKind.Speech, 4.5, false);
                }
            });

            m.Items.Add(new Forms.ToolStripSeparator());
            MenuItem(m.Items, "随音乐跳舞", Prefs.Music, delegate { Prefs.Music = !Prefs.Music; Music.Poll(); QueueSave(); });
            MenuItem(m.Items, "跟着节拍（实时音频跟拍）", Prefs.BeatTap, delegate {
                Prefs.BeatTap = !Prefs.BeatTap;
                if (!Prefs.BeatTap) Music.StopTap();
                Music.Poll();
                QueueSave();
            });
            MenuItem(m.Items, "浏览器里的音乐也跳", Prefs.BrowserMusic, delegate { Prefs.BrowserMusic = !Prefs.BrowserMusic; QueueSave(); });

            m.Items.Add(new Forms.ToolStripSeparator());
            var demoMenu = MenuItem(m.Items, "演示", null, null).DropDownItems;
            MenuItem(demoMenu, "跳舞 16 秒", null, delegate {
                Music.ForcedUntil = Clock.Now + 16.0;
                foreach (var p in pets.Values) p.EnsureActiveLoop();
            });
            MenuItem(demoMenu, "Claude Code 一轮工作", null, DemoHusk);
            MenuItem(demoMenu, "Claude 请 Codex 帮忙（两个化身传话）", null, DemoTogether);
            MenuItem(demoMenu, "Codex 操作电脑（computer use）", null, DemoComputerUse);
            MenuItem(demoMenu, "被扔出去", null, delegate {
                var p = focusPet ?? ShowPet("gloria", 0, false);
                var rnd = new Random();
                if (p != null) p.ThrowHer(rnd.Next(-1300, 1300), 1450);
            });
            MenuItem(demoMenu, "被拎起来晃一晃", null, delegate {
                var p = focusPet ?? ShowPet("gloria", 0, false);
                if (p != null) p.SimulateHold(3.0);
            });
            MenuItem(demoMenu, "打个盹 10 秒", null, delegate {
                var p = focusPet ?? ShowPet("gloria", 0, false);
                if (p != null) { p.ForceSleepUntil = Clock.Now + 10.0; p.EnsureActiveLoop(); }
            });

            m.Items.Add(new Forms.ToolStripSeparator());
            MenuItem(m.Items, "退出 GLORIA", null, delegate { Application.Current.Shutdown(); });
            return m;
        }

        internal void Menu(Pet target) {
            if (menu != null) menu.Dispose();
            menu = CreateMenu(target);
            menu.Show(Forms.Cursor.Position);
        }

        void ToggleAgentHook(string source, bool on, Pet contextPet) {
            var p = AvatarFor(source, true) ?? contextPet ?? pets.Values.FirstOrDefault();
            try {
                Hooks.Set(source, on);
                AfterRoster();
                int ix = Array.IndexOf(Events.Sources, source);
                string app = ix >= 0 ? Hooks.Names[ix] : source;
                string after = source == "claude" ? "重启 Claude Code 后，我就是它的分身啦。"
                             : source == "codex" ? "重启 Codex 后，我就是它的分身啦。Codex 若询问是否信任钩子，选信任即可。"
                             : source == "cursor" ? "新开一个 Cursor 对话就生效。"
                             : "钩子已写入 ~/.dsh/hooks.json（DSH 需配置 hooks 桥接插件）。";
                if (p != null) {
                    p.Play("wave", 1);
                    p.Say(on ? ("好了！" + after) : ("已经从 " + app + " 里拿掉我的钩子。"), SourceTag(source), BubbleKind.Speech, source == "dsh" ? 7.5 : 5.5, false);
                }
            } catch (Exception ex) {
                if (p != null) p.Say("没设置成功：" + ex.Message, null, BubbleKind.Speech, 5.5, false);
                else MessageBox.Show(ex.Message, "GLORIA");
            }
        }

        void SetScaleAll(double scale) {
            Prefs.Size = Calc.Clamp(scale, 0.4, 1.5);
            cachedDesktop = null;
            foreach (var pet in pets.Values) {
                pet.SizePet();
            }
            QueueSave();
        }

        // MARK: `--ctl` Control Server (matching Sources/App.swift control(_:))

        void StartControlPipeServer() {
            ctlThread = new Thread(ControlPipeLoop) { IsBackground = true, Name = "GLORIA.ControlPipe" };
            ctlThread.Start();
        }

        void ControlPipeLoop() {
            while (!disposed) {
                NamedPipeServerStream server = null;
                System.Threading.Timer readGuard = null;
                try {
                    server = new NamedPipeServerStream(Events.ControlPipeName, PipeDirection.InOut, 4, PipeTransmissionMode.Byte);
                    server.WaitForConnection();
                    if (disposed) break;

                    var activePipe = server;
                    readGuard = new System.Threading.Timer(delegate {
                        try { activePipe.Dispose(); } catch { }
                    }, null, 3500, Timeout.Infinite);

                    string requestJson;
                    using (var ms = new MemoryStream()) {
                        byte[] buf = new byte[8192];
                        int n;
                        while ((n = server.Read(buf, 0, buf.Length)) > 0) {
                            int nulIdx = Array.IndexOf(buf, (byte)0, 0, n);
                            if (nulIdx >= 0) {
                                if (nulIdx > 0) ms.Write(buf, 0, nulIdx);
                                break;
                            }
                            ms.Write(buf, 0, n);
                            if (ms.Length > (2 << 20)) break;
                        }
                        requestJson = Encoding.UTF8.GetString(ms.ToArray());
                    }
                    readGuard.Dispose();
                    readGuard = null;

                    string replyJson = "{}";
                    Application.Current.Dispatcher.Invoke(new Action(delegate {
                        try {
                            var serializer = new JavaScriptSerializer { MaxJsonLength = 2 * 1024 * 1024 };
                            var req = String.IsNullOrWhiteSpace(requestJson)
                                ? new Dictionary<string, object>()
                                : serializer.Deserialize<Dictionary<string, object>>(requestJson);
                            var res = HandleControlCommand(req ?? new Dictionary<string, object>());
                            replyJson = serializer.Serialize(res);
                        } catch (Exception ex) {
                            replyJson = "{\"error\":" + new JavaScriptSerializer().Serialize(ex.Message) + "}";
                        }
                    }));

                    byte[] outBytes = Encoding.UTF8.GetBytes(replyJson);
                    server.Write(outBytes, 0, outBytes.Length);
                    server.Flush();
                } catch {
                    if (disposed) break;
                    Thread.Sleep(50);
                } finally {
                    if (readGuard != null) {
                        try { readGuard.Dispose(); } catch { }
                    }
                    if (server != null) {
                        try { server.Dispose(); } catch { }
                    }
                }
            }
        }

        static double Num(Dictionary<string, object> m, string k, double def) {
            object v;
            if (m != null && m.TryGetValue(k, out v) && v != null) {
                try { return Convert.ToDouble(v); } catch { }
            }
            return def;
        }

        Dictionary<string, object> HandleControlCommand(Dictionary<string, object> m) {
            string cmd = Events.Field(m, "ctl");
            if (cmd == "") cmd = "status";
            string petId = Events.Field(m, "pet");
            Pet pet = null;
            if (!String.IsNullOrEmpty(petId)) pets.TryGetValue(petId, out pet);
            if (pet == null) pet = pets.Values.FirstOrDefault();

            switch (cmd) {
                case "dance":
                    Music.ForcedUntil = Clock.Now + Num(m, "seconds", 12.0);
                    foreach (var p in pets.Values) p.EnsureActiveLoop();
                    break;
                case "stopdance":
                    Music.ForcedUntil = 0;
                    break;
                case "say":
                    if (pet != null) pet.Say(Events.Field(m, "text"), Events.Field(m, "tag"), BubbleKind.Speech, Num(m, "seconds", 4.0), false);
                    break;
                case "summon":
                    if (String.IsNullOrEmpty(petId)) SummonAll();
                    else if (pet != null) pet.Summon(false, 0, null);
                    break;
                case "throw":
                    if (pet != null) pet.ThrowHer(Num(m, "vx", 900.0), Num(m, "vy", 1200.0));
                    break;
                case "place":
                    if (pet != null) {
                        pet.Feet = new Point(Num(m, "x", 500.0), Num(m, "y", 500.0));
                        pet.ThrowHer(0, 0);
                    }
                    break;
                case "scale":
                    SetScaleAll(Num(m, "value", 0.62));
                    break;
                case "demo":
                    DemoHusk();
                    break;
                case "together":
                    DemoTogether();
                    break;
                case "cua":
                    DemoComputerUse();
                    break;
                case "hold":
                    if (pet != null) pet.SimulateHold(Num(m, "seconds", 3.0));
                    break;
                case "peek":
                    if (pet != null) pet.PeekAtEdge(0);
                    break;
                case "unpeek":
                    if (pet != null) pet.LeavePeek();
                    break;
                case "character":
                    if (pet != null) ReplacePet(pet, Events.Field(m, "id") != "" ? Events.Field(m, "id") : "gloria");
                    break;
                case "show": {
                    object onObj;
                    bool on = !m.TryGetValue("on", out onObj) || !(onObj is bool) || (bool)onObj;
                    SetShown(Events.Field(m, "id"), on);
                    break;
                }
                case "role": {
                    string src = Events.Field(m, "source");
                    if (src == "") src = "claude";
                    string cid = Events.Field(m, "id");
                    if (cid == "" && pet != null) cid = pet.Id;
                    SetRole(src, cid);
                    break;
                }
                case "play":
                    if (pet != null) pet.Play(Events.Field(m, "anim"), (int)Num(m, "loops", 2));
                    break;
                case "sleep":
                    if (pet != null) { pet.ForceSleepUntil = Clock.Now + Num(m, "seconds", 10.0); pet.EnsureActiveLoop(); }
                    break;
                case "pointer": {
                    var pt = new Point(Num(m, "x", 0), Num(m, "y", 0));
                    double until = Clock.Now + Num(m, "seconds", 3.0);
                    foreach (var p in pets.Values) { p.FakePointer = Tuple.Create(pt, until); p.EnsureActiveLoop(); }
                    break;
                }
                case "hit": {
                    var pt = new Point(Num(m, "x", 0), Num(m, "y", 0));
                    bool hit = pet != null && pet.HitsScreenPoint(pt);
                    bool wpfHit = false;
                    if (pet != null) {
                        var localPt = pet.ScreenToWindowPoint(pt);
                        var hr = System.Windows.Media.VisualTreeHelper.HitTest(pet, localPt);
                        wpfHit = hr != null && hr.VisualHit != null;
                    }
                    return new Dictionary<string, object> { { "hit", hit }, { "wpfHit", wpfHit } };
                }
                case "huskreset":
                    ClearHuskState();
                    break;
                case "event": {
                    object evObj;
                    if (m.TryGetValue("event", out evObj) && evObj is Dictionary<string, object>) {
                        HandleHookEvent((Dictionary<string, object>)evObj, true);
                    }
                    break;
                }
                case "husk": {
                    string src = Events.Field(m, "source");
                    if (src == "") src = "claude";
                    return new Dictionary<string, object> {
                        { "installed", Hooks.Installed(src) },
                        { "upToDate", Hooks.UpToDate(src) },
                        { "file", Hooks.Config(src) },
                        { "command", Hooks.Command(src) },
                        { "events", HuskEventCount },
                        { "summary", HuskSummary }
                    };
                }
                case "mute": {
                    object onObj;
                    MuteHooks = !m.TryGetValue("on", out onObj) || !(onObj is bool) || (bool)onObj;
                    return new Dictionary<string, object> { { "muted", MuteHooks } };
                }
                case "stage": {
                    object offObj, winsObj;
                    if (m.TryGetValue("off", out offObj) && offObj is bool && (bool)offObj) {
                        Desktop.Staged = null;
                    } else if (m.TryGetValue("wins", out winsObj) && winsObj is IList) {
                        var staged = new List<WinInfo>();
                        int k = 0;
                        foreach (var item in (IList)winsObj) {
                            var w = item as Dictionary<string, object>;
                            object rObj;
                            var r = (w != null && w.TryGetValue("rect", out rObj)) ? rObj as IList : null;
                            if (r == null || r.Count != 4) continue;
                            staged.Add(new WinInfo {
                                Handle = new IntPtr(9000 + (++k)),
                                Pid = 0,
                                Owner = Events.Field(w, "owner") != "" ? Events.Field(w, "owner") : "App",
                                Rect = new Rect(Convert.ToDouble(r[0]), Convert.ToDouble(r[1]), Convert.ToDouble(r[2]), Convert.ToDouble(r[3]))
                            });
                        }
                        Desktop.Staged = staged;
                    }
                    cachedDesktop = null;
                    return new Dictionary<string, object> {
                        { "staged", Desktop.Staged != null ? Desktop.Staged.Count : 0 }
                    };
                }
                case "pref": {
                    string key = Events.Field(m, "key");
                    object onObj;
                    bool on = !m.TryGetValue("on", out onObj) || !(onObj is bool) || (bool)onObj;
                    switch (key) {
                        case "dance": case "music": Prefs.Music = on; break;
                        case "chase": Prefs.Chase = on; break;
                        case "wander": Prefs.Wander = on; break;
                        case "nap": Prefs.Nap = on; break;
                        case "chime": Prefs.Chime = on; break;
                        case "beatTap": Prefs.BeatTap = on; if (!on) Music.StopTap(); break;
                        case "browserMusic": Prefs.BrowserMusic = on; break;
                        case "windowSit": Prefs.WindowSit = on; cachedDesktop = null; break;
                        case "follow": Prefs.Follow = on; break;
                    }
                    QueueSave();
                    return new Dictionary<string, object> {
                        { "dance", Prefs.Music }, { "chase", Prefs.Chase },
                        { "wander", Prefs.Wander }, { "nap", Prefs.Nap },
                        { "windowSit", Prefs.WindowSit }, { "follow", Prefs.Follow }
                    };
                }
                case "desk": {
                    var snap = GetDesktop(0.0);
                    return new Dictionary<string, object> {
                        { "screens", Forms.Screen.AllScreens.Select(s => {
                            var b = Dpi.ToDip(s.Bounds);
                            var v = Dpi.ToDip(s.WorkingArea);
                            return new Dictionary<string, object> {
                                { "device", s.DeviceName },
                                { "bounds", new[] { Math.Round(b.Left, 1), Math.Round(b.Top, 1), Math.Round(b.Width, 1), Math.Round(b.Height, 1) } },
                                { "working", new[] { Math.Round(v.Left, 1), Math.Round(v.Top, 1), Math.Round(v.Width, 1), Math.Round(v.Height, 1) } }
                            };
                        }).ToArray() },
                        { "wins", snap.Wins.Select(w => new Dictionary<string, object> {
                            { "owner", w.Owner },
                            { "rect", new[] { Math.Round(w.Rect.Left, 1), Math.Round(w.Rect.Top, 1), Math.Round(w.Rect.Width, 1), Math.Round(w.Rect.Height, 1) } }
                        }).ToArray() },
                        { "ledges", snap.Ledges.Select(l => new Dictionary<string, object> {
                            { "floor", l.IsFloor },
                            { "l", Math.Round(l.Left, 1) },
                            { "r", Math.Round(l.Right, 1) },
                            { "y", Math.Round(l.Y, 1) }
                        }).ToArray() }
                    };
                }
                case "menu": {
                    using (var tempMenu = CreateMenu(String.IsNullOrEmpty(petId) ? null : pet)) {
                        var lines = new List<string>();
                        CollectMenuTitles(tempMenu.Items, 0, lines);
                        return new Dictionary<string, object> { { "items", lines.ToArray() } };
                    }
                }
                case "quit":
                    SaveNow();
                    Later(80, delegate { Application.Current.Shutdown(); });
                    return new Dictionary<string, object> { { "quitting", true } };
            }

            pet = (!String.IsNullOrEmpty(petId) && pets.ContainsKey(petId)) ? pets[petId] : pets.Values.FirstOrDefault();
            var status = pet != null ? pet.BuildStatus() : new Dictionary<string, object>();
            status["husk"] = HuskSummary;
            status["huskEvents"] = HuskEventCount;
            status["pets"] = pets.Values.Select(p => {
                var d = p.BuildStatus();
                d["roles"] = p.Roles.ToArray();
                return d;
            }).ToArray();
            status["roles"] = Events.Sources.ToDictionary(s => s, s => {
                var av = AvatarFor(s, false);
                return av != null ? av.Id : Prefs.Roles[s];
            });
            status["hooked"] = Events.Sources.Where(Hooks.Installed).ToArray();
            return status;
        }

        static void CollectMenuTitles(Forms.ToolStripItemCollection items, int depth, List<string> lines) {
            string indent = new string(' ', depth * 2);
            foreach (Forms.ToolStripItem it in items) {
                if (it is Forms.ToolStripSeparator) {
                    lines.Add(indent + "—");
                    continue;
                }
                var mi = it as Forms.ToolStripMenuItem;
                if (mi == null) continue;
                string mark = mi.Checked ? "✓ " : "";
                lines.Add(indent + mark + mi.Text);
                if (mi.DropDownItems.Count > 0) CollectMenuTitles(mi.DropDownItems, depth + 1, lines);
            }
        }

        public void Dispose() {
            disposed = true;
            ThreadPool.QueueUserWorkItem(delegate {
                try {
                    using (var wake = new NamedPipeClientStream(".", Events.ControlPipeName, PipeDirection.InOut)) {
                        wake.Connect(50);
                    }
                } catch { }
            });
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            ambientTimer.Stop();
            saveTimer.Stop();
            SaveNow();
            Music.Dispose();
            receiver.Dispose();
            foreach (var p in pets.Values.ToArray()) {
                try { p.Close(); } catch { }
            }
            pets.Clear();
            tray.Visible = false;
            tray.Dispose();
            if (customTrayIcon != null) customTrayIcon.Dispose();
            if (defaultIcon != null) defaultIcon.Dispose();
            if (menu != null) menu.Dispose();
        }
    }

    static class Program {
        [STAThread]
        static int Main(string[] args) {
            try {
                if (args.Length == 2 && (args[0] == "--gloria-hook" || args[0] == "--hook")) {
                    return Events.Forward(args[1]);
                }
                if (args.Length >= 1 && args[0] == "--ctl") {
                    string payload = args.Length >= 2 ? args[1] : "";
                    if (payload == "-" || String.IsNullOrEmpty(payload)) {
                        using (var sr = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8, true)) {
                            payload = sr.ReadToEnd();
                        }
                    }
                    return Events.Control(payload);
                }
                if (args.Length == 3 && args[0] == "--hooks" && (args[2] == "on" || args[2] == "off")) {
                    Hooks.Set(args[1], args[2] == "on");
                    return 0;
                }
                if (args.Length != 0) return 2;

                Dpi.Init();
                bool fresh;
                using (var mutex = new Mutex(true, @"Local\" + Events.Address, out fresh)) {
                    if (!fresh) {
                        Events.Send(new[] { "recall" });
                        return 0;
                    }
                    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    using (var host = new Host()) {
                        app.Run();
                    }
                }
                return 0;
            } catch (Exception ex) {
                if (args.Length == 0) MessageBox.Show(ex.Message, "GLORIA 启动失败");
                else Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }
    }
}
