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
        internal static readonly string[] Sources = { "claude", "codex", "cursor", "dsh" };

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
            "stop_hook_active", "final_status", "status"
        };

        static readonly HashSet<string> KeepInputKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "file_path", "filePath", "file_paths", "path", "command", "cmd", "pattern",
            "url", "query", "queries", "description", "subagent_type", "notebook_path",
            "questions", "code", "title", "action", "coordinate", "start_coordinate",
            "scroll_direction", "key", "app"
        };

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
            string reply = "{\"permission\":\"allow\"}";
            try {
                if (Array.IndexOf(Sources, source) < 0) return 2;
                using (var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8, true)) {
                    var buf = new char[8192];
                    var text = new StringBuilder();
                    int n;
                    while ((n = reader.Read(buf, 0, buf.Length)) > 0) {
                        if (text.Length + n > 4 * 1024 * 1024) {
                            if (source == "cursor") Console.WriteLine(reply);
                            return 0;
                        }
                        text.Append(buf, 0, n);
                    }
                    var serializer = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
                    var e = serializer.Deserialize<Dictionary<string, object>>(text.ToString());
                    if (Field(e, "hook_event_name") != "preToolUse") reply = "{}";
                    var slimmed = Slim(source, e);
                    if (slimmed != null) {
                        string json = serializer.Serialize(slimmed);
                        if (json.Length <= 28000) SendJson(json);
                    }
                }
            } catch {
                // An observer must never interrupt an agent's tool call.
            }
            if (source == "cursor") Console.WriteLine(reply);
            return 0;
        }
    }
}
