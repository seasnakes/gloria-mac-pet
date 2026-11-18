using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Gloria {
    internal sealed class Animation {
        internal int Start, Count;
        internal int[] Times;
    }
    internal sealed class Character {
        internal readonly string Name;
        internal readonly int W,H,Seat;
        internal readonly double Scale, FeetY, FaceX, FaceY, FaceRadius;
        internal readonly bool IsModel;
        internal readonly string DirectoryPath;
        readonly BitmapSource sheet;
        readonly Dictionary<int,BitmapSource> cells=new Dictionary<int,BitmapSource>();
        readonly Dictionary<string,Animation> animations=new Dictionary<string,Animation>();
        internal Character(string id) {
            string path=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"characters",id);
            var json=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(path,id+".json")));
            Name=(string)json["name"]; Seat=Convert.ToInt32(json["seatY"]); Scale=Convert.ToDouble(json["scale"]);
            var size=(System.Collections.IList)json["cell"]; W=(int)size[0]; H=(int)size[1];
            FeetY=Convert.ToDouble(json["feetY"]);
            var face = (System.Collections.IList)json["face"];
            FaceX=Convert.ToDouble(face[0]); FaceY=Convert.ToDouble(face[1]); FaceRadius=Convert.ToDouble(face[2]);
            DirectoryPath = path;
            IsModel = json.ContainsKey("model");
            if (!IsModel) {
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.UriSource = new Uri(Path.Combine(path,id+".png"));
                img.EndInit();
                img.Freeze();
                sheet = img;
            }
            foreach(var item in (Dictionary<string,object>)json["anims"]) {
                var a=(Dictionary<string,object>)item.Value;
                int count=(int)a["count"], start=(int)a["row"]*8;
                var list=a["frameMs"] as System.Collections.IList;
                int[] ms=list!=null?list.Cast<object>().Select(Convert.ToInt32).ToArray():Enumerable.Repeat(Convert.ToInt32(a["frameMs"]),count).ToArray();
                if(ms.Length!=count || ms.Any(x=>x<=0) || (!IsModel && (sheet.PixelWidth!=8*W || (start+count+7)/8*H>sheet.PixelHeight)))
                    throw new InvalidDataException(id+": "+item.Key);
                animations.Add(item.Key,new Animation{Start=start,Count=count,Times=ms});
            }
        }
        internal string Pose(string key){return animations.ContainsKey(key)?key:key=="scroll" && animations.ContainsKey("drag")?"drag":"work";}
        internal Animation Get(string key){return animations[Pose(key)];}
        internal BitmapSource Cell(int index) {
            BitmapSource b;
            if(!cells.TryGetValue(index,out b)) {b=new CroppedBitmap(sheet,new Int32Rect(index%8*W,index/8*H,W,H));b.Freeze();cells.Add(index,b);}
            return b;
        }
    }
}
