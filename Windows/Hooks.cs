using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Runtime.Serialization.Json;
using System.Collections.Generic;

namespace Gloria {
    // Work on the lossless XML representation of JSON values; keep unknown settings/handlers.
    internal static class Hooks {
        internal static readonly string[] Names={"Claude Code","Codex","Cursor","DSH"};
        static readonly string[] events={
            "SessionStart UserPromptSubmit PreToolUse PostToolUse PostToolUseFailure PermissionRequest Notification Stop SubagentStart SubagentStop PreCompact SessionEnd",
            "SessionStart UserPromptSubmit PreToolUse PermissionRequest PostToolUse Stop SessionEnd PreCompact SubagentStart SubagentStop",
            // The preToolUse observer returns Cursor's continue verdict.
            "sessionStart preToolUse postToolUse postToolUseFailure subagentStop stop preCompact sessionEnd",
            "SessionStart UserPromptSubmit PreToolUse PostToolUse PostToolUseFailure Stop SubagentStart SubagentStop SessionEnd"
        };
        internal static string Config(string source) {
            var env=source=="claude"?"CLAUDE_CONFIG_DIR":source=="codex"?"CODEX_HOME":source=="dsh"?"DSH_HOME":null;
            string dir=env==null?null:Environment.GetEnvironmentVariable(env);
            if(String.IsNullOrWhiteSpace(dir)) dir=Path.Combine(Environment.GetEnvironmentVariable("GLORIA_HOOK_HOME")??Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"."+source);
            return Path.Combine(dir, source=="claude"?"settings.json":"hooks.json");
        }
        static XElement Node(string name,string type,params object[] children) {return new XElement(name,new XAttribute("type",type),children);}
        static void Require(XElement n,string type) {if(n==null || (string)n.Attribute("type")!=type) throw new InvalidDataException("配置格式不正确，已保留原文件。");}
        static XElement Read(byte[] bytes) {
            if(bytes==null) return Node("root","object");
            string raw=new UTF8Encoding(false,true).GetString(bytes).TrimStart('\uFEFF');
            using(var reader=JsonReaderWriterFactory.CreateJsonReader(Encoding.UTF8.GetBytes(raw),XmlDictionaryReaderQuotas.Max)) {
                var root=XElement.Load(reader,LoadOptions.PreserveWhitespace); Require(root,"object"); return root;
            }
        }
        static string EncodedPrefix="powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand ";
        static string Helper {get{return Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"GLORIA-Hook.exe");}}
        static string Command(string source) {
            string ps="& '"+Helper.Replace("'","''")+"' --gloria-hook "+source;
            return source=="cursor"?ps:EncodedPrefix+Convert.ToBase64String(Encoding.Unicode.GetBytes(ps));
        }
        static bool Own(XElement e,string source) {
            string cmd=(string)e.Element("command")??"";
            if(source=="claude") {
                var args=e.Element("args");
                return cmd.EndsWith(@"\GLORIA-Hook.exe",StringComparison.OrdinalIgnoreCase) && args!=null &&
                    args.Elements().Select(x=>x.Value).SequenceEqual(new[]{"--gloria-hook",source});
            }
            if(source!="cursor") {
                if(!cmd.StartsWith(EncodedPrefix,StringComparison.Ordinal)) return false;
                try {cmd=Encoding.Unicode.GetString(Convert.FromBase64String(cmd.Substring(EncodedPrefix.Length)));} catch(FormatException){return false;}
            }
            return cmd.StartsWith("& '",StringComparison.Ordinal) && cmd.EndsWith(@"\GLORIA-Hook.exe' --gloria-hook "+source,StringComparison.OrdinalIgnoreCase);
        }
        static XElement Entry(string source,string eventName) {
            string timeout=source=="codex" && eventName=="SessionEnd" ? "3" : "5";
            var item=Node("item","object",Node("command","string",source=="claude"?Helper:Command(source)), Node("timeout","number",timeout));
            if(source=="cursor") return item;
            item.Add(Node("type","string","command"));
            if(source=="claude") item.Add(Node("args","array",Node("item","string","--gloria-hook"),Node("item","string",source)));
            return Node("item","object",Node("hooks","array",item));
        }
        internal static bool Installed(string source) {
            try {
                var p=Config(source); if(!File.Exists(p)) return false;
                return Read(File.ReadAllBytes(p)).Descendants("item").Any(x=>Own(x,source));
            } catch{return false;}
        }
        internal static void Set(string source,bool on) {
            int ix=Array.IndexOf(Events.Sources,source); if(ix<0) throw new ArgumentException("未知 Agent");
            string path=Config(source); var before=File.Exists(path)?File.ReadAllBytes(path):null;
            if(before==null && !on) return;
            var root=Read(before); var original=new XElement(root); var hooks=root.Element("hooks");
            if(hooks==null) {hooks=Node("hooks","object");root.Add(hooks);}
            Require(hooks,"object");
            foreach(var ev in hooks.Elements().ToList()) {
                Require(ev,"array"); bool removed=false;
                foreach(var group in ev.Elements().ToList()) {
                    if(source=="cursor") {if(Own(group,source)){group.Remove();removed=true;} continue;}
                    var array=group.Element("hooks"); if(array==null) continue;
                    Require(array,"array");
                    var own=array.Elements().Where(x=>Own(x,source)).ToList(); if(own.Count==0)continue;
                    own.ForEach(x=>x.Remove()); removed=true;
                    if(!array.HasElements) group.Remove();
                }
                if(removed && !ev.HasElements) ev.Remove();
            }
            if(on) foreach(var name in events[ix].Split(' ')) {
                var list=hooks.Element(name);
                if(list==null){list=Node(name,"array");hooks.Add(list);}
                list.Add(Entry(source,name));
            }
            if(!hooks.HasElements) hooks.Remove();
            if(on && source=="cursor" && root.Element("version")==null) root.Add(Node("version","number","1"));
            if(XNode.DeepEquals(original,root)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try {
                using(var f=File.Create(tmp))
                using(var writer=JsonReaderWriterFactory.CreateJsonWriter(f,new UTF8Encoding(false),false,true,"  ")) root.WriteTo(writer);
                if(before==null) File.Move(tmp,path);
                else {
                    if(!File.ReadAllBytes(path).SequenceEqual(before)) throw new IOException("配置刚被其他程序修改，请再试一次。");
                    File.Replace(tmp,path,path+".gloria-"+DateTime.Now.ToString("yyyyMMdd-HHmmss-fffffff")+".bak");
                }
            } finally {if(File.Exists(tmp)) File.Delete(tmp);}
        }
    }
}
