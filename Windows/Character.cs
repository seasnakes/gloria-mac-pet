using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Gloria {
    internal sealed class Animation {
        internal string Name;
        internal int Row, Start, Count, Sub;
        internal int[] TimesMs;
        internal double[] Durations;
        internal double TotalDuration, FrameTime;
        internal bool Loop;
        internal string Facing;
    }

    internal sealed class PersonaLines {
        internal string Title, Tag, Greeting;
        internal string[] Pats, Wake, Ouch, Angry, Hello;
    }

    internal sealed class Character {
        internal static readonly string[] BuiltInIds = { "gloria", "doro", "usagi", "grokbot", "fourteen" };

        internal readonly string Id;
        internal readonly string Name;
        internal readonly string Title;
        internal readonly string Tag;
        internal readonly int W, H, Seat;
        internal readonly double Scale, PersonaScale, FeetY, SeatRise;
        internal readonly double FaceX, FaceY, FaceRadius;
        internal readonly bool HasFace;
        internal readonly Point Grab;
        internal readonly double Hang;
        internal readonly int FixedTop;
        internal readonly bool IsModel;
        internal readonly bool HasExtras;
        internal readonly string DirectoryPath;
        internal readonly PersonaLines Lines;

        readonly BitmapSource sheet;
        readonly Dictionary<int, BitmapSource> cells = new Dictionary<int, BitmapSource>();
        readonly Dictionary<int, byte[]> masks = new Dictionary<int, byte[]>();
        readonly Dictionary<int, int> tops = new Dictionary<int, int>();
        readonly Dictionary<string, Animation> animations = new Dictionary<string, Animation>();
        readonly Dictionary<int, string> cellOwner = new Dictionary<int, string>();
        int cachedHalfWidth = -1;

        internal double ScaleMul { get { return Scale * PersonaScale; } }

        static string UserCharactersDir {
            get {
                string baseDir = Environment.GetEnvironmentVariable("GLORIA_DATA_DIR")
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GLORIA");
                return Path.Combine(baseDir, "characters");
            }
        }

        internal static string ResolveDirectory(string id) {
            if (String.IsNullOrEmpty(id)) return null;
            string userDir = Path.Combine(UserCharactersDir, id);
            if (ValidDir(userDir, id)) return userDir;
            string appDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "characters", id);
            if (ValidDir(appDir, id)) return appDir;
            return null;
        }

        static bool ValidDir(string dir, string id) {
            if (!Directory.Exists(dir) || !File.Exists(Path.Combine(dir, id + ".json"))) return false;
            return File.Exists(Path.Combine(dir, id + ".png")) || File.Exists(Path.Combine(dir, id + ".mesh"));
        }

        internal static string[] DiscoverIds() {
            var list = new List<string>();
            foreach (string id in BuiltInIds) {
                if (ResolveDirectory(id) != null) list.Add(id);
            }
            try {
                string udir = UserCharactersDir;
                if (Directory.Exists(udir)) {
                    foreach (string sub in Directory.GetDirectories(udir).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)) {
                        string id = Path.GetFileName(sub);
                        if (!list.Contains(id) && ValidDir(sub, id)) list.Add(id);
                    }
                }
            } catch { }
            return list.ToArray();
        }

        internal static string TitleFor(string id) {
            try {
                string dir = ResolveDirectory(id);
                if (dir != null) {
                    var json = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(dir, id + ".json")));
                    object pObj;
                    if (json.TryGetValue("persona", out pObj) && pObj is Dictionary<string, object>) {
                        var p = (Dictionary<string, object>)pObj;
                        object tObj;
                        if (p.TryGetValue("title", out tObj) && tObj is string && !String.IsNullOrEmpty((string)tObj))
                            return (string)tObj;
                    }
                }
            } catch { }
            if (id == "gloria") return "GLORIA · 柔彩";
            if (id == "doro") return "Doro";
            if (id == "usagi") return "抽象乌萨奇";
            if (id == "grokbot") return "GrokBot";
            if (id == "fourteen") return "14酱";
            return id;
        }

        internal Character(string id) {
            Id = id;
            string path = ResolveDirectory(id);
            if (path == null) throw new FileNotFoundException("找不到角色资源: " + id);
            DirectoryPath = path;

            var json = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(path, id + ".json")));
            object val;
            Name = json.TryGetValue("name", out val) && val is string ? (string)val : id;
            var size = (IList)json["cell"];
            W = Convert.ToInt32(size[0]);
            H = Convert.ToInt32(size[1]);
            FeetY = json.TryGetValue("feetY", out val) ? Convert.ToDouble(val) : 202.0;
            Seat = json.TryGetValue("seatY", out val) ? Convert.ToInt32(val) : 149;
            SeatRise = FeetY - Seat;
            Scale = json.TryGetValue("scale", out val) ? Convert.ToDouble(val) : 1.0;
            FixedTop = json.TryGetValue("top", out val) ? Convert.ToInt32(val) : 6;

            if (json.TryGetValue("face", out val) && val is IList && ((IList)val).Count == 3) {
                var f = (IList)val;
                FaceX = Convert.ToDouble(f[0]);
                FaceY = Convert.ToDouble(f[1]);
                FaceRadius = Convert.ToDouble(f[2]);
                HasFace = true;
            } else {
                FaceX = W * 0.5;
                FaceY = H * 0.32;
                FaceRadius = W * 0.18;
            }

            if (json.TryGetValue("grab", out val) && val is IList && ((IList)val).Count >= 2) {
                var g = (IList)val;
                Grab = new Point(Convert.ToDouble(g[0]), Convert.ToDouble(g[1]));
            } else {
                Grab = new Point(W * 0.5, 40);
            }
            Hang = FeetY - Grab.Y;

            double pScale;
            Lines = BuildPersona(id, Name, json, out pScale);
            PersonaScale = pScale;
            Title = Lines.Title;
            Tag = Lines.Tag;

            IsModel = json.ContainsKey("model");
            if (!IsModel) {
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.UriSource = new Uri(Path.Combine(path, id + ".png"));
                img.EndInit();
                img.Freeze();
                sheet = img;
            }

            foreach (var item in (Dictionary<string, object>)json["anims"]) {
                var a = (Dictionary<string, object>)item.Value;
                int row = Convert.ToInt32(a["row"]);
                int count = Convert.ToInt32(a["count"]);
                int start = row * 8;
                var list = a["frameMs"] as IList;
                int[] ms = list != null
                    ? list.Cast<object>().Select(Convert.ToInt32).ToArray()
                    : Enumerable.Repeat(Convert.ToInt32(a["frameMs"]), count).ToArray();
                if (ms.Length != count || ms.Any(x => x <= 0) || (!IsModel && (sheet.PixelWidth != 8 * W || (start + count + 7) / 8 * H > sheet.PixelHeight)))
                    throw new InvalidDataException(id + ": " + item.Key);
                double[] durs = ms.Select(x => x / 1000.0).ToArray();
                double total = durs.Sum();
                object loopObj, subObj, faceObj;
                bool loop = a.TryGetValue("loop", out loopObj) && loopObj is bool && (bool)loopObj;
                int sub = a.TryGetValue("sub", out subObj) ? Math.Max(1, Convert.ToInt32(subObj)) : 1;
                string facing = a.TryGetValue("facing", out faceObj) ? faceObj as string : null;

                var anim = new Animation {
                    Name = item.Key,
                    Row = row,
                    Start = start,
                    Count = count,
                    Sub = sub,
                    TimesMs = ms,
                    Durations = durs,
                    TotalDuration = total,
                    FrameTime = total / count,
                    Loop = loop,
                    Facing = facing
                };
                animations.Add(item.Key, anim);
                for (int i = 0; i < count; i++) {
                    if (!cellOwner.ContainsKey(start + i)) cellOwner[start + i] = item.Key;
                }
            }
            HasExtras = animations.ContainsKey("dangle");
        }

        static PersonaLines BuildPersona(string id, string rawName, Dictionary<string, object> json, out double pScale) {
            PersonaLines lines;
            if (id == "doro") {
                pScale = 1.15;
                lines = new PersonaLines {
                    Title = "Doro", Tag = "DORO", Greeting = "Doro～！欧润吉在哪里？",
                    Pats = new[] { "Doro～", "欧润吉！", "嘿嘿嘿", "Doro Doro～", "要欧润吉！" },
                    Wake = new[] { "Doro？", "欧润吉……梦到欧润吉了……" },
                    Ouch = new[] { "呜哇——", "Doro 晕了……", "欧润吉掉了！" },
                    Angry = new[] { "Doro 生气了！", "不给欧润吉就别戳！", "哼——" },
                    Hello = new[] { "Doro～", "分你一个欧润吉！", "嘿！" }
                };
            } else if (id == "usagi") {
                pScale = 1.0;
                lines = new PersonaLines {
                    Title = "抽象乌萨奇", Tag = "USAGI", Greeting = "呀哈——！",
                    Pats = new[] { "呀哈！", "乌拉！", "哈？", "噗噜噜噜噜——", "呼嗯。" },
                    Wake = new[] { "呀哈？！", "哈？" },
                    Ouch = new[] { "乌拉——！", "噗噜噜……", "哈？？" },
                    Angry = new[] { "哈？？？", "乌拉！！", "噗噜噜噜噜噜！" },
                    Hello = new[] { "呀哈！", "乌拉～", "哈！" }
                };
            } else if (id == "grokbot") {
                pScale = 1.0;
                lines = new PersonaLines {
                    Title = "GrokBot", Tag = "GROKBOT", Greeting = "哔——GrokBot 上线！耳朵天线已展开。",
                    Pats = new[] { "哔哔～", "收到！", "嘀——好感度 +1", "（耳朵转了转）", "扫描到主人，开心。" },
                    Wake = new[] { "系统唤醒……哔！", "刚才在省电模式～" },
                    Ouch = new[] { "警告：姿态异常！", "陀螺仪晕了……", "哔——撞到了！" },
                    Angry = new[] { "过载了！别戳！", "哔！哔！哔！", "进入防御模式。" },
                    Hello = new[] { "哔～你好！", "信号已连接。", "嘀嘀！" }
                };
            } else {
                pScale = 1.0;
                lines = new PersonaLines {
                    Title = id == "gloria" ? "GLORIA · 柔彩" : rawName,
                    Tag = id == "gloria" ? "GLORIA" : rawName.ToUpperInvariant(),
                    Greeting = "嗨～我是 GLORIA！把我拖到任何窗口上，我会坐在上面陪你。",
                    Pats = new[] { "嘿嘿～", "摸摸头！", "干嘛呀～", "我在呢。", "今天也一起加油！", "要不要放首歌？我想跳舞。" },
                    Wake = new[] { "唔……我没睡！", "啊，你回来啦～", "眯了一会儿……" },
                    Ouch = new[] { "哎呦……", "头好晕～", "下次轻一点嘛！" },
                    Angry = new[] { "别戳啦！", "再戳我就生气了！", "哼！" },
                    Hello = new[] { "嗨～", "一起加油！", "你也在呀～" }
                };
            }

            object pObj;
            if (json.TryGetValue("persona", out pObj) && pObj is Dictionary<string, object>) {
                var p = (Dictionary<string, object>)pObj;
                object v;
                if (p.TryGetValue("title", out v) && v is string) lines.Title = (string)v;
                if (p.TryGetValue("tag", out v) && v is string) lines.Tag = (string)v;
                if (p.TryGetValue("scale", out v)) pScale = Convert.ToDouble(v);
                if (p.TryGetValue("greeting", out v) && v is string) lines.Greeting = (string)v;
                lines.Pats = ReadList(p, "pats", lines.Pats);
                lines.Wake = ReadList(p, "wake", lines.Wake);
                lines.Ouch = ReadList(p, "ouch", lines.Ouch);
                lines.Angry = ReadList(p, "angry", lines.Angry);
                lines.Hello = ReadList(p, "hello", lines.Hello);
            }
            return lines;
        }

        static string[] ReadList(Dictionary<string, object> d, string k, string[] fallback) {
            object v;
            var list = d.TryGetValue(k, out v) ? v as IList : null;
            if (list == null || list.Count == 0) return fallback;
            var arr = list.Cast<object>().Select(x => x == null ? "" : x.ToString()).Where(x => x.Length > 0).ToArray();
            return arr.Length > 0 ? arr : fallback;
        }

        internal bool Has(string key) {
            return animations.ContainsKey(key);
        }

        // Exact Mac fallback rules (Sources/Character.swift pose(for:))
        internal string Pose(string key) {
            if (animations.ContainsKey(key)) return key;
            if (key == "scroll") return animations.ContainsKey("drag") ? "drag" : "work";
            if (key == "tap") return animations.ContainsKey("wave") ? "wave" : "work";
            if (key == "snap") return animations.ContainsKey("review") ? "review" : "work";
            if (animations.ContainsKey("work")) return "work";
            return "idle";
        }

        internal Animation Get(string key) {
            return animations[Pose(key)];
        }

        internal int CellIndex(string animKey, int frame) {
            var a = Get(animKey);
            int j = ((frame % a.Count) + a.Count) % a.Count;
            return a.Start + j;
        }

        internal int FrameAt(string animKey, double t, double speed) {
            var a = Get(animKey);
            double total = Math.Max(0.001, a.TotalDuration * speed);
            double u = t % total;
            if (u < 0) u += total;
            for (int i = 0; i < a.Durations.Length; i++) {
                u -= a.Durations[i] * speed;
                if (u < 0) return i;
            }
            return a.Durations.Length - 1;
        }

        internal int LookCell(double degrees) {
            double d = degrees % 360.0;
            if (d < 0) d += 360.0;
            int i = ((int)Math.Round(d / 22.5)) % 16;
            return 72 + i; // rows 9-10
        }

        internal string AnimOfCell(int cellIndex) {
            string a;
            return cellOwner.TryGetValue(cellIndex, out a) ? a : null;
        }

        internal BitmapSource Cell(int index) {
            if (IsModel) return null;
            BitmapSource b;
            if (!cells.TryGetValue(index, out b)) {
                int rx = (index % 8) * W;
                int ry = (index / 8) * H;
                if (rx + W > sheet.PixelWidth || ry + H > sheet.PixelHeight) index = 0;
                rx = (index % 8) * W;
                ry = (index / 8) * H;
                var crop = new CroppedBitmap(sheet, new Int32Rect(rx, ry, W, H));
                var bgra = crop.Format == System.Windows.Media.PixelFormats.Bgra32
                    ? (BitmapSource)crop
                    : new FormatConvertedBitmap(crop, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                int stride = W * 4;
                byte[] raw = new byte[stride * H];
                bgra.CopyPixels(raw, stride, 0);
                byte[] m = new byte[W * H];
                int topRow = H;
                for (int y = 0; y < H; y++) {
                    int rowOff = y * stride;
                    int maskOff = y * W;
                    for (int x = 0; x < W; x++) {
                        int pxOff = rowOff + x * 4;
                        byte a = raw[pxOff + 3];
                        if (a <= 16) {
                            // Strip imperceptible PNG dust so Win32 ULW_ALPHA clicks through empty space at 0ms latency
                            raw[pxOff] = 0;
                            raw[pxOff + 1] = 0;
                            raw[pxOff + 2] = 0;
                            raw[pxOff + 3] = 0;
                            a = 0;
                        }
                        m[maskOff + x] = a;
                        if (a > 40 && y < topRow) topRow = y;
                    }
                }
                masks[index] = m;
                tops[index] = topRow < H ? topRow : 6;
                b = BitmapSource.Create(W, H, 96.0, 96.0, System.Windows.Media.PixelFormats.Bgra32, null, raw, stride);
                b.Freeze();
                cells[index] = b;
            }
            return b;
        }

        byte[] EnsureMask(int index) {
            if (IsModel) return null;
            byte[] m;
            if (!masks.TryGetValue(index, out m)) {
                Cell(index);
                masks.TryGetValue(index, out m);
            }
            return m;
        }

        internal int TopOfCell(int index) {
            if (IsModel) return FixedTop;
            EnsureMask(index);
            int top;
            return tops.TryGetValue(index, out top) ? top : FixedTop;
        }

        internal byte AlphaAt(int index, int x, int yTop) {
            if (IsModel || x < 0 || yTop < 0 || x >= W || yTop >= H) return 0;
            byte[] m = EnsureMask(index);
            return m != null ? m[yTop * W + x] : (byte)0;
        }

        internal double HalfWidth {
            get {
                if (cachedHalfWidth >= 0) return cachedHalfWidth;
                if (IsModel) { cachedHalfWidth = 50; return 50; }
                int idle0 = CellIndex("idle", 0);
                byte[] m = EnsureMask(idle0);
                int r = 0;
                int footX = W / 2;
                for (int y = 0; y < H; y++) {
                    int off = y * W;
                    for (int x = 0; x < W; x++) {
                        if (m[off + x] > 120) {
                            int d = Math.Abs(x - footX);
                            if (d > r) r = d;
                        }
                    }
                }
                cachedHalfWidth = r > 12 ? r : 50;
                return cachedHalfWidth;
            }
        }

        internal System.Drawing.Bitmap CropFaceBitmap(int heightPx) {
            if (IsModel) return null;
            try {
                int c0 = CellIndex("idle", 0);
                var cell = Cell(c0);
                int top = TopOfCell(c0);
                double h = Id == "usagi" ? 100.0 : (FeetY - top) * 0.62;
                double y = Id == "usagi" ? top + 22 : top;
                double w = h * 1.08;
                int ix = Calc.Clamp((int)Math.Round(W * 0.5 - w / 2), 0, W - 8);
                int iy = Calc.Clamp((int)Math.Round(y), 0, H - 8);
                int iw = Calc.Clamp((int)Math.Round(w), 8, W - ix);
                int ih = Calc.Clamp((int)Math.Round(h), 8, H - iy);
                var crop = new CroppedBitmap(cell, new Int32Rect(ix, iy, iw, ih));
                var bgra = crop.Format == System.Windows.Media.PixelFormats.Pbgra32 || crop.Format == System.Windows.Media.PixelFormats.Bgra32
                    ? (BitmapSource)crop
                    : new FormatConvertedBitmap(crop, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                int outW = Math.Max(8, (int)Math.Round(heightPx * (double)iw / ih));
                var dst = new System.Drawing.Bitmap(outW, heightPx, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                try {
                    int stride = iw * 4;
                    byte[] buf = new byte[stride * ih];
                    bgra.CopyPixels(buf, stride, 0);
                    using (var srcBmp = new System.Drawing.Bitmap(iw, ih, System.Drawing.Imaging.PixelFormat.Format32bppArgb)) {
                        var data = srcBmp.LockBits(new System.Drawing.Rectangle(0, 0, iw, ih), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                        try {
                            for (int r = 0; r < ih; r++) {
                                System.Runtime.InteropServices.Marshal.Copy(buf, r * stride, IntPtr.Add(data.Scan0, r * data.Stride), stride);
                            }
                        } finally {
                            srcBmp.UnlockBits(data);
                        }
                        using (var g = System.Drawing.Graphics.FromImage(dst)) {
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                            g.DrawImage(srcBmp, new System.Drawing.Rectangle(0, 0, outW, heightPx));
                        }
                    }
                    return dst;
                } catch {
                    dst.Dispose();
                    throw;
                }
            } catch {
                return null;
            }
        }
    }
}
