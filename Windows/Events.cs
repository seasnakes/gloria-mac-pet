using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Web.Script.Serialization;

namespace Gloria {
    // Message-only HWND + user-scoped named pipe for control commands. No network ports.
    internal static class Events {
        internal const int WM_COPYDATA = 0x4a;
        internal const int TagLegacy = 0x474c52;
        internal const int TagJson = 0x474c53;
        internal static readonly string[] Sources = { "claude", "codex", "cursor", "dsh", "antigravity" };

        internal static string Address {
            get {
                string profile = Environment.GetEnvironmentVariable("GLORIA_DATA_DIR") ?? "";
                using (var sha = SHA256.Create()) {
                    return "GLORIA.Windows." + WindowsIdentity.GetCurrent().User.Value + "."
                         + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(profile)), 0, 8);
                }
            }
        }

        internal static string ControlPipeName {
            get { return Address + ".Ctl"; }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Packet {
            public IntPtr tag;
            public int size;
            public IntPtr data;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);

        [DllImport("user32.dll")]
        static extern IntPtr SendMessageTimeout(IntPtr hwnd, int msg, IntPtr from, ref Packet packet, uint flags, uint timeout, out IntPtr result);

        internal static string Field(Dictionary<string, object> d, string k) {
            object v;
            if (d == null || !d.TryGetValue(k, out v) || v == null) return "";
            return v as string ?? v.ToString();
        }

        static readonly HashSet<string> KeepTopKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "hook_event_name", "session_id", "conversation_id", "cwd", "workspace_roots",
            "tool_name", "tool_input", "tool_use_id", "call_id", "tool_call_id",
            "error", "error_message", "is_interrupt", "prompt", "message", "title",
            "notification_type", "last_assistant_message", "text", "agent_id", "subagent_id",
            "agent_type", "subagent_type", "source", "reason", "trigger",
            "stop_hook_active", "final_status", "status", "fully_idle"
        };

        static readonly HashSet<string> KeepInputKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "file_path", "filePath", "file_paths", "path", "command", "cmd", "pattern",
            "url", "query", "queries", "description", "subagent_type", "notebook_path",
            "questions", "focus", "code", "title", "action", "coordinate", "start_coordinate",
            "scroll_direction", "key", "app", "x", "y"
        };

        static readonly Dictionary<string, string> AntigravityToolMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            { "view_file", "Read" }, { "read_file", "Read" }, { "view_file_outline", "Read" }, { "view_code_item", "Read" }, { "read_terminal", "Read" },
            { "write_to_file", "Write" }, { "write_file", "Write" },
            { "replace_file_content", "Edit" }, { "multi_replace_file_content", "Edit" }, { "edit_file", "Edit" },
            { "run_command", "Bash" }, { "send_command_input", "Bash" },
            { "grep_search", "Grep" }, { "codebase_search", "Grep" },
            { "find_by_name", "Glob" }, { "list_dir", "Glob" },
            { "search_web", "WebSearch" }, { "read_url_content", "WebFetch" }, { "view_content_chunk", "WebFetch" },
            { "update_steps", "TodoWrite" }, { "schedule", "TodoWrite" },
            { "invoke_subagent", "Agent" }, { "define_subagent", "Agent" }, { "send_message", "Agent" }, { "manage_subagents", "Agent" }
        };

        internal static string AntigravityEventName(Dictionary<string, object> e, string eventHint) {
            string ev = Field(e, "hookEventName");
            if (!String.IsNullOrEmpty(ev)) return ev;
            if (!String.IsNullOrEmpty(eventHint)) return eventHint;
            if (e != null && (e.ContainsKey("terminationReason") || e.ContainsKey("fullyIdle") || e.ContainsKey("executionNum"))) return "Stop";
            if (e != null && e.ContainsKey("toolCall")) return !String.IsNullOrEmpty(Field(e, "error")) ? "PostToolUse" : "PreToolUse";
            if (e != null && e.ContainsKey("invocationNum")) return "PreInvocation";
            return "SessionStart";
        }

        static object FirstPresent(Dictionary<string, object> d, params string[] keys) {
            if (d == null) return null;
            for (int i = 0; i < keys.Length; i++) {
                object v;
                if (d.TryGetValue(keys[i], out v) && v != null) {
                    string s = v as string;
                    if (s == null || s != "") return v;
                }
            }
            return null;
        }

        static string LastAntigravityReply(string transcriptPath) {
            if (String.IsNullOrEmpty(transcriptPath)) return "";
            try {
                string path = transcriptPath;
                if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith(@"~\", StringComparison.Ordinal)) {
                    path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Substring(2));
                }
                if (!File.Exists(path)) return "";
                byte[] buf;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
                    long len = fs.Length;
                    if (len <= 0) return "";
                    int take = (int)Math.Min(65536L, len);
                    fs.Seek(len - take, SeekOrigin.Begin);
                    buf = new byte[take];
                    int read = 0;
                    while (read < take) {
                        int n = fs.Read(buf, read, take - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    if (read < take) {
                        var slimBuf = new byte[read];
                        Buffer.BlockCopy(buf, 0, slimBuf, 0, read);
                        buf = slimBuf;
                    }
                }
                string text = Encoding.UTF8.GetString(buf);
                string[] lines = text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                var ser = new JavaScriptSerializer { MaxJsonLength = 2 * 1024 * 1024 };
                for (int i = lines.Length - 1; i >= 0; i--) {
                    Dictionary<string, object> obj;
                    try { obj = ser.Deserialize<Dictionary<string, object>>(lines[i]); } catch { continue; }
                    if (obj == null) continue;
                    string kind = Field(obj, "type");
                    if (kind == "USER_INPUT") break;
                    if (kind == "PLANNER_RESPONSE") {
                        string content = Field(obj, "content").Trim();
                        if (!String.IsNullOrEmpty(content)) return CapStr(content, 1200);
                    }
                }
            } catch { }
            return "";
        }

        internal static Dictionary<string, object> NormalizeAntigravity(Dictionary<string, object> e, string eventHint) {
            if (e == null) return null;
            string name = AntigravityEventName(e, eventHint);
            var o = new Dictionary<string, object>();
            string cid = Field(e, "conversationId");
            if (cid == "") cid = Field(e, "session_id");
            if (cid == "") cid = "default";
            o["session_id"] = "antigravity:" + cid;
            string cwd = Field(e, "cwd");
            if (cwd == "") {
                object wsObj;
                var ws = e.TryGetValue("workspacePaths", out wsObj) ? wsObj as IList : null;
                if (ws != null && ws.Count > 0 && ws[0] != null) cwd = ws[0].ToString();
            }
            if (cwd != "") o["cwd"] = cwd;

            if (name == "SessionStart") {
                o["hook_event_name"] = "SessionStart";
                o["source"] = "startup";
                return o;
            }
            if (name == "PreInvocation") {
                o["hook_event_name"] = "PreInvocation";
                o["prompt"] = Field(e, "lastUserInput");
                return o;
            }
            if (name == "PreToolUse" || name == "PostToolUse") {
                object tcObj, argsObj, stepObj;
                var tc = e.TryGetValue("toolCall", out tcObj) ? tcObj as Dictionary<string, object> : null;
                string rawTool = tc != null ? Field(tc, "name") : Field(e, "tool_name");
                string lower = rawTool.ToLowerInvariant();
                var args = tc != null && tc.TryGetValue("args", out argsObj) ? argsObj as Dictionary<string, object>
                         : (e.TryGetValue("tool_input", out argsObj) ? argsObj as Dictionary<string, object> : null);
                string tid = tc != null ? Field(tc, "id") : Field(e, "tool_use_id");
                if (tid == "" && e.TryGetValue("stepIdx", out stepObj) && stepObj != null) tid = "step:" + stepObj.ToString();
                if (tid != "") o["tool_use_id"] = tid;
                string err = Field(e, "error");
                if (err != "") o["error"] = err;

                if (name == "PreToolUse") {
                    if (lower == "ask_question") {
                        o["hook_event_name"] = "Notification";
                        o["notification_type"] = "agent_needs_input";
                        string q = "";
                        object qsObj;
                        var qs = args != null && args.TryGetValue("questions", out qsObj) ? qsObj as IList : null;
                        if (qs != null && qs.Count > 0) {
                            var firstQ = qs[0] as Dictionary<string, object>;
                            if (firstQ != null) q = Field(firstQ, "question");
                        }
                        o["message"] = String.IsNullOrEmpty(q) ? "Antigravity 想问你几个问题。" : q;
                        return o;
                    }
                    if (lower == "ask_permission") {
                        o["hook_event_name"] = "Notification";
                        o["notification_type"] = "permission_prompt";
                        string reason = Field(args, "Reason");
                        if (reason == "") reason = Field(args, "reason");
                        string act = Field(args, "Action");
                        if (act == "") act = Field(args, "action");
                        string tgt = Field(args, "Target");
                        if (tgt == "") tgt = Field(args, "target");
                        string desc = !String.IsNullOrEmpty(reason) ? reason : (act + " " + tgt).Trim();
                        o["message"] = String.IsNullOrEmpty(desc) ? "需要你批准操作" : desc;
                        return o;
                    }
                    object blkObj;
                    if (lower == "notify_user" && args != null && args.TryGetValue("BlockedOnUser", out blkObj) && Object.Equals(blkObj, true)) {
                        o["hook_event_name"] = "Notification";
                        o["notification_type"] = "agent_needs_input";
                        string msg = Field(args, "Message");
                        o["message"] = String.IsNullOrEmpty(msg) ? "等待你的回复" : msg;
                        return o;
                    }
                    if (lower == "invoke_subagent") {
                        o["hook_event_name"] = "SubagentStart";
                        o["agent_id"] = tid != "" ? tid : Guid.NewGuid().ToString("N");
                        string role = Field(args, "Role");
                        if (role == "") role = Field(args, "TypeName");
                        if (role == "" && args != null) {
                            object subsObj;
                            var subs = args.TryGetValue("Subagents", out subsObj) ? subsObj as IList : null;
                            if (subs != null && subs.Count > 0) {
                                var firstSub = subs[0] as Dictionary<string, object>;
                                if (firstSub != null) {
                                    role = Field(firstSub, "Role");
                                    if (role == "") role = Field(firstSub, "TypeName");
                                }
                            }
                        }
                        o["agent_type"] = role;
                        return o;
                    }
                    if (lower == "swarm" && String.Equals(Field(args, "action"), "add", StringComparison.OrdinalIgnoreCase)) {
                        o["hook_event_name"] = "SubagentStart";
                        string wn = Field(args, "name");
                        if (wn == "") wn = "swarm";
                        o["agent_id"] = wn;
                        o["agent_type"] = wn;
                        return o;
                    }
                } else if (err == "") {
                    if (lower == "swarm" && String.Equals(Field(args, "action"), "kill", StringComparison.OrdinalIgnoreCase)) {
                        o["hook_event_name"] = "SubagentStop";
                        string wn = Field(args, "name");
                        if (wn != "") o["agent_id"] = wn;
                        return o;
                    }
                    if (lower == "manage_subagents") {
                        string act = Field(args, "Action").ToLowerInvariant();
                        if (act == "kill_all") {
                            o["hook_event_name"] = "SubagentStop";
                            return o;
                        }
                        if (act == "kill") {
                            o["hook_event_name"] = "SubagentStop";
                            object idsObj;
                            var ids = args != null && args.TryGetValue("ConversationIds", out idsObj) ? idsObj as IList : null;
                            if (ids != null && ids.Count > 0 && ids[0] != null) o["agent_id"] = ids[0].ToString();
                            return o;
                        }
                    }
                }

                o["hook_event_name"] = name;
                string mappedTool;
                if (!AntigravityToolMap.TryGetValue(lower, out mappedTool)) {
                    if (lower.StartsWith("mcp__", StringComparison.Ordinal)) mappedTool = rawTool;
                    else if (lower.StartsWith("mcp_", StringComparison.Ordinal)) mappedTool = "mcp__" + rawTool.Substring(4);
                    else if (rawTool.IndexOf(':') >= 0) mappedTool = "mcp__" + rawTool.Replace(":", "__");
                    else mappedTool = rawTool;
                }
                o["tool_name"] = mappedTool;

                var i = new Dictionary<string, object>();
                object fp = FirstPresent(args, "AbsolutePath", "TargetFile", "Path", "file_path", "path");
                if (fp != null) i["file_path"] = fp;
                object cmd = FirstPresent(args, "CommandLine", "command", "cmd", "Input");
                if (cmd != null) i["command"] = cmd;
                object pat = FirstPresent(args, "Query", "Pattern", "pattern");
                if (pat != null) {
                    i["pattern"] = pat;
                } else {
                    object dirObj = FirstPresent(args, "DirectoryPath", "SearchDirectory");
                    string dirStr = dirObj as string;
                    if (!String.IsNullOrEmpty(dirStr)) {
                        string[] parts = dirStr.Replace('\\', '/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                        i["pattern"] = parts.Length > 0 ? parts[parts.Length - 1] : dirStr;
                    }
                }
                object qv = FirstPresent(args, "query", "Query");
                if (qv != null) i["query"] = qv;
                object uv = FirstPresent(args, "Url", "url");
                if (uv != null) i["url"] = uv;
                object dv = FirstPresent(args, "Description", "toolSummary", "description");
                if (dv != null) i["description"] = dv;
                object fv = FirstPresent(args, "focus", "Focus");
                if (fv != null) i["focus"] = fv;
                object stv = FirstPresent(args, "TypeName", "subagent_type");
                if (stv != null) i["subagent_type"] = stv;
                object kv = FirstPresent(args, "key", "Key");
                if (kv != null) i["key"] = kv;
                object av = FirstPresent(args, "action", "Action");
                if (av != null) i["action"] = av;
                object cv = FirstPresent(args, "coordinate");
                if (cv != null) i["coordinate"] = cv;
                object xv = FirstPresent(args, "x", "X");
                if (xv != null) i["x"] = xv;
                object yv = FirstPresent(args, "y", "Y");
                if (yv != null) i["y"] = yv;
                object tv = FirstPresent(args, "text", "Text", "value", "Value");
                if (tv is string) i["text"] = tv;
                if (i.Count > 0) o["tool_input"] = i;
                return o;
            }
            if (name == "Stop") {
                string reason = Field(e, "terminationReason").ToLowerInvariant();
                string err = Field(e, "error");
                object idleObj;
                if (e.TryGetValue("fullyIdle", out idleObj) && idleObj is bool) o["fully_idle"] = idleObj;
                if (!String.IsNullOrEmpty(err) || reason == "error") {
                    o["hook_event_name"] = "StopFailure";
                    o["error"] = err;
                } else {
                    o["hook_event_name"] = "Stop";
                    if (reason == "user_canceled" || reason == "cancelled" || reason == "aborted") {
                        o["reason"] = "user_canceled";
                    } else {
                        string reply = LastAntigravityReply(Field(e, "transcriptPath"));
                        if (!String.IsNullOrEmpty(reply)) o["last_assistant_message"] = reply;
                    }
                }
                return o;
            }
            return null;
        }

        static readonly HashSet<string> StripKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "content", "old_string", "new_string", "new_source", "edits"
        };

        static string CapStr(string s, int maxLen) {
            if (String.IsNullOrEmpty(s)) return "";
            string t = s.Replace("\r", "\n");
            return t.Length > maxLen ? t.Substring(0, maxLen) : t;
        }

        static object TrimValue(object v, int depth) {
            if (v == null) return null;
            string s = v as string;
            if (s != null) return CapStr(s, 300);
            var dict = v as Dictionary<string, object>;
            if (dict != null) {
                if (depth > 2) return null;
                var o = new Dictionary<string, object>();
                foreach (var kv in dict) {
                    if (StripKeys.Contains(kv.Key)) continue;
                    var tv = TrimValue(kv.Value, depth + 1);
                    if (tv != null) o[kv.Key] = tv;
                }
                return o;
            }
            var list = v as IList;
            if (list != null) {
                if (depth > 2) return null;
                var arr = new List<object>();
                int count = Math.Min(8, list.Count);
                for (int i = 0; i < count; i++) {
                    var tv = TrimValue(list[i], depth + 1);
                    if (tv != null) arr.Add(tv);
                }
                return arr;
            }
            return v;
        }

        // Slims incoming hook JSON (never forwards raw file contents or typed passwords).
        internal static Dictionary<string, object> Slim(string source, Dictionary<string, object> e) {
            return Slim(source, e, null);
        }

        internal static Dictionary<string, object> Slim(string source, Dictionary<string, object> e, string eventHint) {
            if (source == "antigravity" && e != null) {
                e = NormalizeAntigravity(e, eventHint);
            }
            if (e == null || String.IsNullOrEmpty(Field(e, "hook_event_name"))) return null;
            var o = new Dictionary<string, object>();
            o["_source"] = source;
            foreach (var kv in e) {
                if (!KeepTopKeys.Contains(kv.Key)) continue;
                var tv = TrimValue(kv.Value, 0);
                if (tv != null) o[kv.Key] = tv;
            }
            object inputObj;
            var input = e.TryGetValue("tool_input", out inputObj) ? inputObj as Dictionary<string, object> : null;
            if (input != null) {
                var slimInput = new Dictionary<string, object>();
                foreach (var kv in input) {
                    if (!KeepInputKeys.Contains(kv.Key)) continue;
                    var tv = TrimValue(kv.Value, 1);
                    if (tv != null) slimInput[kv.Key] = tv;
                }
                object codeObj;
                if (input.TryGetValue("code", out codeObj) && codeObj is string) {
                    slimInput["code"] = CapStr((string)codeObj, 2400);
                }
                object textObj;
                if (input.TryGetValue("text", out textObj) && textObj is string) {
                    string txt = (string)textObj;
                    string act = Field(input, "action");
                    if (String.Equals(act, "key", StringComparison.OrdinalIgnoreCase) ||
                        String.Equals(act, "hold_key", StringComparison.OrdinalIgnoreCase)) {
                        slimInput["text"] = CapStr(txt, 24);
                    } else {
                        slimInput["text_len"] = txt.Length;
                    }
                }
                object actsObj;
                var acts = input.TryGetValue("actions", out actsObj) ? actsObj as IList : null;
                if (acts != null) {
                    var slimActs = new List<object>();
                    int n = Math.Min(16, acts.Count);
                    for (int i = 0; i < n; i++) {
                        var item = acts[i] as Dictionary<string, object>;
                        if (item == null) continue;
                        object innerObj;
                        var inner = item.TryGetValue("input", out innerObj) ? innerObj as Dictionary<string, object> : null;
                        var entry = new Dictionary<string, object>();
                        entry["name"] = Field(item, "name");
                        entry["action"] = Field(inner, "action");
                        slimActs.Add(entry);
                    }
                    slimInput["actions"] = slimActs;
                }
                o["tool_input"] = slimInput;
            } else {
                o["_input_empty"] = true;
            }
            object replyObj;
            if (e.TryGetValue("last_assistant_message", out replyObj) && replyObj is string) {
                o["last_assistant_message"] = CapStr((string)replyObj, 1200);
            }
            object respObj;
            var resp = e.TryGetValue("tool_response", out respObj) ? respObj as Dictionary<string, object> : null;
            if (resp != null) {
                object isErr, exitCode;
                bool failed = false;
                if (resp.TryGetValue("is_error", out isErr) && Object.Equals(isErr, true)) failed = true;
                if (resp.TryGetValue("exit_code", out exitCode) && exitCode is int && (int)exitCode != 0) failed = true;
                if (failed) o["_tool_failed"] = true;
            }
            string target = AgentCalls.Target(source, Field(e, "tool_name"), input);
            if (!String.IsNullOrEmpty(target)) {
                o["_callee"] = target;
                string task = AgentCalls.TaskSummary(input);
                if (!String.IsNullOrEmpty(task)) o["_task"] = task;
            }
            return o;
        }

        internal static void Send(string[] legacyFields) {
            SendRaw(TagLegacy, String.Join("\t", legacyFields));
        }

        internal static void SendJson(string json) {
            SendRaw(TagJson, json);
        }

        static void SendRaw(int tag, string payload) {
            if (String.IsNullOrEmpty(payload)) return;
            IntPtr hwnd = FindWindowEx(new IntPtr(-3), IntPtr.Zero, null, Address);
            if (hwnd == IntPtr.Zero) return;
            Packet p = new Packet {
                tag = (IntPtr)tag,
                size = payload.Length * 2,
                data = Marshal.StringToHGlobalUni(payload)
            };
            try {
                IntPtr result;
                SendMessageTimeout(hwnd, WM_COPYDATA, IntPtr.Zero, ref p, 2, 500, out result);
            } finally {
                Marshal.FreeHGlobal(p.data);
            }
        }

        internal static int Control(string json) {
            try {
                if (String.IsNullOrWhiteSpace(json)) json = "{\"ctl\":\"status\"}";
                using (var client = new NamedPipeClientStream(".", ControlPipeName, PipeDirection.InOut)) {
                    client.Connect(1200);
                    using (var guard = new System.Threading.Timer(delegate {
                        try { client.Dispose(); } catch { }
                    }, null, 4500, System.Threading.Timeout.Infinite)) {
                        byte[] body = Encoding.UTF8.GetBytes(json);
                        byte[] req = new byte[body.Length + 1];
                        Buffer.BlockCopy(body, 0, req, 0, body.Length);
                        req[body.Length] = 0; // NUL request frame terminator (never valid inside UTF-8 JSON)
                        client.Write(req, 0, req.Length);
                        client.Flush();
                        using (var ms = new MemoryStream()) {
                            byte[] buf = new byte[8192];
                            int n;
                            while ((n = client.Read(buf, 0, buf.Length)) > 0) {
                                ms.Write(buf, 0, n);
                                if (ms.Length > (2 << 20)) break;
                            }
                            byte[] outBytes = ms.ToArray();
                            using (var stdout = Console.OpenStandardOutput()) {
                                stdout.Write(outBytes, 0, outBytes.Length);
                                byte[] nl = Encoding.UTF8.GetBytes(Environment.NewLine);
                                stdout.Write(nl, 0, nl.Length);
                                stdout.Flush();
                            }
                            return 0;
                        }
                    }
                }
            } catch {
                Console.Error.WriteLine("GLORIA 没在运行");
                return 1;
            }
        }

        internal static int Forward(string source) {
            return Forward(source, null);
        }

        internal static int Forward(string source, string eventHint) {
            string reply = source == "antigravity"
                ? ((eventHint == "PreToolUse" || eventHint == "Stop") ? "{\"decision\":\"allow\"}" : "{}")
                : "{\"permission\":\"allow\"}";
            try {
                if (Array.IndexOf(Sources, source) < 0) return 2;
                using (var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8, true)) {
                    var buf = new char[8192];
                    var text = new StringBuilder();
                    int n;
                    while ((n = reader.Read(buf, 0, buf.Length)) > 0) {
                        if (text.Length + n > 4 * 1024 * 1024) {
                            if (source == "cursor" || source == "antigravity") Console.WriteLine(reply);
                            return 0;
                        }
                        text.Append(buf, 0, n);
                    }
                    var serializer = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
                    var e = serializer.Deserialize<Dictionary<string, object>>(text.ToString());
                    if (source == "antigravity") {
                        string agEv = AntigravityEventName(e, eventHint);
                        reply = (agEv == "PreToolUse" || agEv == "Stop") ? "{\"decision\":\"allow\"}" : "{}";
                    } else if (Field(e, "hook_event_name") != "preToolUse") {
                        reply = "{}";
                    }
                    var slimmed = Slim(source, e, eventHint);
                    if (slimmed != null) {
                        string json = serializer.Serialize(slimmed);
                        if (json.Length <= 28000) SendJson(json);
                    }
                }
            } catch {
                // An observer must never interrupt an agent's tool call.
            }
            if (source == "cursor" || source == "antigravity") Console.WriteLine(reply);
            return 0;
        }
    }
}
