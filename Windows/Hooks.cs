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
        internal static readonly string[] Names = { "Claude Code", "Codex", "Cursor", "DeepSeek Harness", "Antigravity" };
        internal static readonly string[] ShortNames = { "Claude", "Codex", "Cursor", "DSH", "Antigravity" };
        internal static readonly string[] Tags = { "CLAUDE CODE", "CODEX", "CURSOR", "DSH", "ANTIGRAVITY" };
        internal static readonly string[] Homes = { ".claude", ".codex", ".cursor", ".dsh", @".gemini\config" };

        static readonly string[] events = {
            "SessionStart UserPromptSubmit PreToolUse PostToolUse PostToolUseFailure PermissionRequest Notification Stop StopFailure SubagentStart SubagentStop PreCompact SessionEnd",
            "SessionStart UserPromptSubmit PreToolUse PermissionRequest PostToolUse Stop SessionEnd PreCompact SubagentStart SubagentStop",
            // The preToolUse observer returns Cursor's allow verdict.
            "sessionStart sessionEnd beforeSubmitPrompt preToolUse postToolUse postToolUseFailure subagentStart subagentStop stop afterAgentResponse preCompact",
            "SessionStart UserPromptSubmit PreToolUse PostToolUse PostToolUseFailure Stop SubagentStart SubagentStop SessionEnd",
            "SessionStart PreInvocation PreToolUse PostToolUse Stop"
        };

        static bool AntigravityGrouped(string eventName) {
            return eventName == "PreToolUse" || eventName == "PostToolUse";
        }

        internal static bool AgentPresent(string source) {
            int ix = Array.IndexOf(Events.Sources, source);
            if (ix < 0) return false;
            string cfg = Config(source);
            string dir = Path.GetDirectoryName(cfg);
            if (Directory.Exists(dir) || File.Exists(cfg)) return true;
            if (source == "antigravity") {
                string home = Environment.GetEnvironmentVariable("GLORIA_HOOK_HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string gem = Path.Combine(home, ".gemini");
                return Directory.Exists(Path.Combine(gem, "antigravity"))
                    || Directory.Exists(Path.Combine(gem, "antigravity-ide"))
                    || Directory.Exists(Path.Combine(gem, "antigravity-cli"));
            }
            return false;
        }

        internal static string Config(string source) {
            var env = source == "claude" ? "CLAUDE_CONFIG_DIR"
                    : source == "codex" ? "CODEX_HOME"
                    : source == "dsh" ? "DSH_HOME"
                    : source == "antigravity" ? "ANTIGRAVITY_CONFIG_DIR" : null;
            string dir = env == null ? null : Environment.GetEnvironmentVariable(env);
            if (String.IsNullOrWhiteSpace(dir)) {
                string home = Environment.GetEnvironmentVariable("GLORIA_HOOK_HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                dir = source == "antigravity" ? Path.Combine(home, ".gemini", "config") : Path.Combine(home, "." + source);
            }
            return Path.Combine(dir, source == "claude" ? "settings.json" : "hooks.json");
        }

        static XElement Node(string name, string type, params object[] children) {
            return new XElement(name, new XAttribute("type", type), children);
        }

        static void Require(XElement n, string type) {
            if (n == null || (string)n.Attribute("type") != type)
                throw new InvalidDataException("配置格式不正确，已保留原文件。");
        }

        static XElement Read(byte[] bytes) {
            if (bytes == null) return Node("root", "object");
            string raw = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
            if (String.IsNullOrWhiteSpace(raw)) return Node("root", "object");
            using (var reader = JsonReaderWriterFactory.CreateJsonReader(Encoding.UTF8.GetBytes(raw), XmlDictionaryReaderQuotas.Max)) {
                var root = XElement.Load(reader, LoadOptions.PreserveWhitespace);
                Require(root, "object");
                return root;
            }
        }

        static readonly string EncodedPrefix = "powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand ";
        internal static string Helper { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GLORIA-Hook.exe"); } }

        internal static string Command(string source) {
            return Command(source, null);
        }

        internal static string Command(string source, string eventName) {
            string suffix = source == "antigravity" && !String.IsNullOrEmpty(eventName) ? (" " + eventName) : "";
            string ps = "& '" + Helper.Replace("'", "''") + "' --gloria-hook " + source + suffix;
            return source == "cursor" ? ps : EncodedPrefix + Convert.ToBase64String(Encoding.Unicode.GetBytes(ps));
        }

        static bool Own(XElement e, string source) {
            string cmd = (string)e.Element("command") ?? "";
            if (source == "claude") {
                var args = e.Element("args");
                return cmd.EndsWith(@"\GLORIA-Hook.exe", StringComparison.OrdinalIgnoreCase) && args != null &&
                    args.Elements().Select(x => x.Value).SequenceEqual(new[] { "--gloria-hook", source });
            }
            if (source != "cursor") {
                if (!cmd.StartsWith(EncodedPrefix, StringComparison.Ordinal)) return false;
                try {
                    cmd = Encoding.Unicode.GetString(Convert.FromBase64String(cmd.Substring(EncodedPrefix.Length)));
                } catch (FormatException) {
                    return false;
                }
            }
            string marker = @"\GLORIA-Hook.exe' --gloria-hook " + source;
            int pos = cmd.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            return cmd.StartsWith("& '", StringComparison.Ordinal) && pos > 0 &&
                   (pos + marker.Length == cmd.Length || (source == "antigravity" && cmd[pos + marker.Length] == ' '));
        }

        static bool PointsHere(XElement e, string source, string eventName) {
            if (!Own(e, source)) return false;
            string cmd = (string)e.Element("command") ?? "";
            if (source == "claude") return String.Equals(cmd, Helper, StringComparison.OrdinalIgnoreCase);
            return String.Equals(cmd, Command(source, eventName), StringComparison.Ordinal);
        }

        static XElement Entry(string source, string eventName) {
            string timeout = (source == "codex" && eventName == "SessionEnd") || source == "antigravity" ? "3" : "5";
            var item = Node("item", "object",
                Node("command", "string", source == "claude" ? Helper : Command(source, eventName)),
                Node("timeout", "number", timeout));
            if (source == "cursor") return item;
            item.Add(Node("type", "string", "command"));
            if (source == "antigravity") {
                if (!AntigravityGrouped(eventName)) return item;
                return Node("item", "object", Node("matcher", "string", "*"), Node("hooks", "array", item));
            }
            if (source == "claude") {
                item.Add(Node("args", "array", Node("item", "string", "--gloria-hook"), Node("item", "string", source)));
            }
            return Node("item", "object", Node("hooks", "array", item));
        }

        internal static bool Installed(string source) {
            try {
                var p = Config(source);
                if (!File.Exists(p)) return false;
                var root = Read(File.ReadAllBytes(p));
                if (source == "antigravity") {
                    var g = root.Element("gloria");
                    if (g == null || (string)g.Attribute("type") != "object" || (string)g.Element("enabled") == "false") return false;
                    return g.Descendants("item").Any(x => Own(x, source));
                }
                return root.Descendants("item").Any(x => Own(x, source));
            } catch {
                return false;
            }
        }

        internal static bool UpToDate(string source) {
            try {
                int ix = Array.IndexOf(Events.Sources, source);
                if (ix < 0) return false;
                var p = Config(source);
                if (!File.Exists(p)) return false;
                var root = Read(File.ReadAllBytes(p));
                if (source == "antigravity") {
                    var g = root.Element("gloria");
                    if (g == null || (string)g.Attribute("type") != "object" || (string)g.Element("enabled") == "false") return false;
                    foreach (string evName in events[ix].Split(' ')) {
                        var list = g.Element(evName);
                        if (list == null || !list.Descendants("item").Any(x => PointsHere(x, source, evName))) return false;
                    }
                    return true;
                }
                var hooks = root.Element("hooks");
                if (hooks == null) return false;
                foreach (string evName in events[ix].Split(' ')) {
                    var list = hooks.Element(evName);
                    if (list == null || !list.Descendants("item").Any(x => PointsHere(x, source, evName))) return false;
                }
                return true;
            } catch {
                return false;
            }
        }

        internal static void Set(string source, bool on) {
            int ix = Array.IndexOf(Events.Sources, source);
            if (ix < 0) throw new ArgumentException("未知 Agent");
            string path = Config(source);
            var before = File.Exists(path) ? File.ReadAllBytes(path) : null;
            if (before == null && !on) return;
            var root = Read(before);
            var original = new XElement(root);
            if (source == "antigravity") {
                foreach (var named in root.Elements().ToList()) {
                    if ((string)named.Attribute("type") != "object") continue;
                    bool namedRemoved = false;
                    foreach (var ev in named.Elements().ToList()) {
                        if (ev.Name.LocalName == "enabled" || (string)ev.Attribute("type") != "array") continue;
                        bool grouped = AntigravityGrouped(ev.Name.LocalName), removed = false;
                        foreach (var entry in ev.Elements().ToList()) {
                            if (!grouped) {
                                if (Own(entry, source)) { entry.Remove(); removed = true; }
                                continue;
                            }
                            var array = entry.Element("hooks");
                            if (array == null || (string)array.Attribute("type") != "array") continue;
                            var own = array.Elements().Where(x => Own(x, source)).ToList();
                            if (own.Count == 0) continue;
                            own.ForEach(x => x.Remove());
                            removed = true;
                            if (!array.HasElements) entry.Remove();
                        }
                        if (removed) {
                            namedRemoved = true;
                            if (!ev.HasElements) ev.Remove();
                        }
                    }
                    if (namedRemoved && !named.Elements().Any(x => x.Name.LocalName != "enabled")) named.Remove();
                }
                if (on) {
                    var g = root.Element("gloria");
                    if (g == null) { g = Node("gloria", "object"); root.Add(g); }
                    Require(g, "object");
                    var en = g.Element("enabled");
                    if (en != null) en.Remove();
                    foreach (var name in events[ix].Split(' ')) {
                        var list = g.Element(name);
                        if (list == null) { list = Node(name, "array"); g.Add(list); }
                        Require(list, "array");
                        list.Add(Entry(source, name));
                    }
                }
            } else {
                var hooks = root.Element("hooks");
                if (hooks == null) { hooks = Node("hooks", "object"); root.Add(hooks); }
                Require(hooks, "object");
                foreach (var ev in hooks.Elements().ToList()) {
                    Require(ev, "array");
                    bool removed = false;
                    foreach (var group in ev.Elements().ToList()) {
                        if (source == "cursor") {
                            if (Own(group, source)) { group.Remove(); removed = true; }
                            continue;
                        }
                        var array = group.Element("hooks");
                        if (array == null) continue;
                        Require(array, "array");
                        var own = array.Elements().Where(x => Own(x, source)).ToList();
                        if (own.Count == 0) continue;
                        own.ForEach(x => x.Remove());
                        removed = true;
                        if (!array.HasElements) group.Remove();
                    }
                    if (removed && !ev.HasElements) ev.Remove();
                }
                if (on) {
                    foreach (var name in events[ix].Split(' ')) {
                        var list = hooks.Element(name);
                        if (list == null) { list = Node(name, "array"); hooks.Add(list); }
                        list.Add(Entry(source, name));
                    }
                }
                if (!hooks.HasElements) hooks.Remove();
                if (on && source == "cursor" && root.Element("version") == null) root.Add(Node("version", "number", "1"));
            }
            if (XNode.DeepEquals(original, root)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                using (var f = File.Create(tmp))
                using (var writer = JsonReaderWriterFactory.CreateJsonWriter(f, new UTF8Encoding(false), false, true, "  ")) {
                    root.WriteTo(writer);
                }
                if (before == null) {
                    File.Move(tmp, path);
                } else {
                    if (!File.ReadAllBytes(path).SequenceEqual(before))
                        throw new IOException("配置刚被其他程序修改，请再试一次。");
                    File.Replace(tmp, path, path + ".gloria-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fffffff") + ".bak");
                }
            } finally {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }
    }
}
