using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Media;
using System.Windows;

namespace Gloria {
    internal sealed partial class Host {
        sealed class AgentSession {
            internal string Source;
            internal bool Busy;
            internal string Awaiting;
            internal string LastTool = "";
            internal string LastPrompt = "";
            internal double LastAt = Clock.Now;
            internal string Reply = "";
        }

        sealed class DelegationLink {
            internal readonly string Key = Guid.NewGuid().ToString("N");
            internal string FromSource;
            internal string SessionId;
            internal string CallId;
            internal string ToolName;
            internal string ToSource;
            internal string Task;
            internal Pet Sender;
            internal Pet Receiver;
            internal bool Delivered;
            internal bool Finished;
            internal bool Failed;
            internal bool Returning;
            internal double StartedAt = Clock.Now;
        }

        readonly Dictionary<string, AgentSession> sessions = new Dictionary<string, AgentSession>();
        readonly List<DelegationLink> delegations = new List<DelegationLink>();
        readonly Dictionary<string, string> cuaSessionApp = new Dictionary<string, string>();
        readonly Dictionary<string, double> lastCaptionAt = new Dictionary<string, double>();
        double lastFailSayAt;
        internal int HuskEventCount { get; private set; }
        internal bool MuteHooks;

        static readonly Dictionary<string, string> CursorToolMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            { "Shell", "Bash" }, { "StrReplace", "Edit" }, { "MultiEdit", "Edit" },
            { "EditNotebook", "NotebookEdit" }, { "Delete", "Delete" }, { "LS", "Glob" },
            { "ReadLints", "Read" }, { "SemanticSearch", "Grep" }, { "Task", "Agent" }, { "TodoWrite", "TodoWrite" }
        };

        static readonly Dictionary<string, string> DshToolMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            { "pwsh", "Bash" }, { "bash", "Bash" }, { "shell", "Bash" }, { "exec", "Bash" },
            { "read", "Read" }, { "read_image", "Read" }, { "write", "Write" },
            { "edit", "Edit" }, { "str_replace_editor", "Edit" }, { "glob", "Glob" },
            { "grep", "Grep" }, { "web_search", "WebSearch" }, { "web_fetch", "WebFetch" },
            { "subagent", "Agent" }, { "subagent_fork", "Agent" }, { "workflow", "TodoWrite" }, { "todo", "TodoWrite" }
        };

        static readonly Dictionary<string, string> CursorEventMap = new Dictionary<string, string>(StringComparer.Ordinal) {
            { "sessionStart", "SessionStart" }, { "sessionEnd", "SessionEnd" },
            { "beforeSubmitPrompt", "UserPromptSubmit" }, { "preToolUse", "PreToolUse" },
            { "postToolUse", "PostToolUse" }, { "postToolUseFailure", "PostToolUseFailure" },
            { "subagentStart", "SubagentStart" }, { "subagentStop", "SubagentStop" },
            { "stop", "Stop" }, { "afterAgentResponse", "AgentResponse" }, { "preCompact", "PreCompact" }
        };

        static string WhoName(string src) {
            int ix = Array.IndexOf(Events.Sources, src);
            return ix >= 0 ? Hooks.ShortNames[ix] : src;
        }

        static string SourceTag(string src) {
            int ix = Array.IndexOf(Events.Sources, src);
            return ix >= 0 ? Hooks.Tags[ix] : src.ToUpperInvariant();
        }

        Pet AvatarFor(string source, bool ensureShown) {
            string id;
            if (!Prefs.Roles.TryGetValue(source, out id) || String.IsNullOrEmpty(id)) id = "gloria";
            if (!AllCharacterIds.Contains(id)) id = "gloria";
            Pet pet;
            if (pets.TryGetValue(id, out pet)) return pet;
            if (ensureShown) return ShowPet(id);
            return pets.Values.FirstOrDefault();
        }

        internal string HuskSummary {
            get {
                var waiting = sessions.Values.FirstOrDefault(s => !String.IsNullOrEmpty(s.Awaiting));
                if (waiting != null) return WhoName(waiting.Source) + " 等你批准";
                var busySources = sessions.Values.Where(s => s.Busy).Select(s => s.Source).ToList();
                if (busySources.Count > 0) {
                    var parts = new List<string>();
                    foreach (string id in Events.Sources) {
                        int n = busySources.Count(x => x == id);
                        if (n > 0) parts.Add(WhoName(id) + (n > 1 ? " ×" + n : ""));
                    }
                    return String.Join("、", parts.ToArray()) + " 工作中";
                }
                return sessions.Count == 0 ? "空闲" : "待命";
            }
        }

        static Dictionary<string, object> AdaptEvent(Dictionary<string, object> raw, string source) {
            if (source == "antigravity" && String.IsNullOrEmpty(Events.Field(raw, "hook_event_name"))) {
                raw = Events.NormalizeAntigravity(raw, null);
                if (raw == null) return null;
            }
            string rawEv = Events.Field(raw, "hook_event_name");
            if (String.IsNullOrEmpty(rawEv)) return null;

            var o = new Dictionary<string, object>(raw, StringComparer.OrdinalIgnoreCase);
            string baseSid = Events.Field(raw, "session_id");
            if (baseSid == "") baseSid = Events.Field(raw, "conversation_id");
            if (baseSid == "") baseSid = "default";
            if (!baseSid.StartsWith(source + ":", StringComparison.Ordinal)) baseSid = source + ":" + baseSid;
            o["session_id"] = baseSid;

            object inObj;
            var input = raw.TryGetValue("tool_input", out inObj) && inObj is Dictionary<string, object>
                ? new Dictionary<string, object>((Dictionary<string, object>)inObj, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            if (source == "cursor") {
                string mappedEv;
                if (!CursorEventMap.TryGetValue(rawEv, out mappedEv)) {
                    // Case-insensitive fallback
                    mappedEv = CursorEventMap.FirstOrDefault(kv => String.Equals(kv.Key, rawEv, StringComparison.OrdinalIgnoreCase)).Value;
                }
                if (String.IsNullOrEmpty(mappedEv)) return null;
                o["hook_event_name"] = mappedEv;

                string t = Events.Field(raw, "tool_name");
                if (!String.IsNullOrEmpty(t)) {
                    string mt;
                    if (CursorToolMap.TryGetValue(t, out mt)) o["tool_name"] = mt;
                    else if (t.StartsWith("MCP:", StringComparison.OrdinalIgnoreCase))
                        o["tool_name"] = "mcp__" + t.Substring(4).Replace(":", "__");
                }
                if (mappedEv == "PostToolUseFailure" && String.IsNullOrEmpty(Events.Field(o, "error"))) {
                    string em = Events.Field(raw, "error_message");
                    o["error"] = String.IsNullOrEmpty(em) ? "failed" : em;
                } else if (mappedEv == "SubagentStart" || mappedEv == "SubagentStop") {
                    if (String.IsNullOrEmpty(Events.Field(o, "agent_id"))) o["agent_id"] = Events.Field(raw, "subagent_id");
                    if (String.IsNullOrEmpty(Events.Field(o, "agent_type"))) o["agent_type"] = Events.Field(raw, "subagent_type");
                } else if (mappedEv == "Stop" && String.Equals(Events.Field(raw, "status"), "error", StringComparison.OrdinalIgnoreCase)) {
                    o["hook_event_name"] = "StopFailure";
                } else if (mappedEv == "AgentResponse") {
                    o["last_assistant_message"] = Events.Field(raw, "text");
                }
                o["tool_input"] = input;
                return o;
            }

            if (source == "dsh") {
                string t = Events.Field(raw, "tool_name");
                string mt;
                if (!String.IsNullOrEmpty(t) && DshToolMap.TryGetValue(t, out mt)) o["tool_name"] = mt;
                if (String.IsNullOrEmpty(Events.Field(input, "file_path"))) {
                    string fp = Events.Field(input, "filePath");
                    if (fp == "") fp = Events.Field(input, "path");
                    if (fp != "") input["file_path"] = fp;
                }
                if (String.IsNullOrEmpty(Events.Field(input, "query"))) {
                    object qObj;
                    var qList = input.TryGetValue("queries", out qObj) ? qObj as IList : null;
                    if (qList != null && qList.Count > 0 && qList[0] != null) input["query"] = qList[0].ToString();
                }
                o["tool_input"] = input;
                return o;
            }

            // Claude Code & Codex common normalizations
            string toolName = Events.Field(raw, "tool_name");
            if (String.Equals(rawEv, "PermissionRequest", StringComparison.OrdinalIgnoreCase)) {
                o["hook_event_name"] = "Notification";
                o["notification_type"] = "permission_prompt";
                if (String.IsNullOrEmpty(Events.Field(o, "message"))) o["message"] = Events.Field(input, "description");
            }
            if (String.Equals(toolName, "apply_patch", StringComparison.OrdinalIgnoreCase)) {
                string patch = Events.Field(input, "command");
                var files = new List<string>();
                foreach (string line in patch.Split('\n')) {
                    string trimmed = line.Trim();
                    foreach (string prefix in new[] { "*** Update File: ", "*** Add File: ", "*** Delete File: " }) {
                        if (trimmed.StartsWith(prefix, StringComparison.Ordinal)) {
                            files.Add(trimmed.Substring(prefix.Length).Trim());
                        }
                    }
                }
                o["tool_name"] = "Edit";
                input["file_paths"] = files.ToArray();
                if (files.Count > 0) input["file_path"] = files[0];
            } else if (String.Equals(toolName, "exec_command", StringComparison.OrdinalIgnoreCase) ||
                       String.Equals(toolName, "shell_command", StringComparison.OrdinalIgnoreCase) ||
                       String.Equals(toolName, "PowerShell", StringComparison.OrdinalIgnoreCase) ||
                       String.Equals(toolName, "pwsh", StringComparison.OrdinalIgnoreCase)) {
                o["tool_name"] = "Bash";
                if (String.IsNullOrEmpty(Events.Field(input, "command"))) input["command"] = Events.Field(input, "cmd");
            }
            if (String.Equals(rawEv, "PreToolUse", StringComparison.OrdinalIgnoreCase) &&
                String.Equals(toolName, "request_user_input", StringComparison.OrdinalIgnoreCase)) {
                o["hook_event_name"] = "Notification";
                o["notification_type"] = "agent_needs_input";
                o["message"] = WhoName(source) + " 想问你几个问题。";
            }
            o["tool_input"] = input;
            return o;
        }

        internal void HandleHookEvent(Dictionary<string, object> rawEvent, bool fromDemo = false) {
            if (rawEvent == null || (MuteHooks && !fromDemo)) return;
            string src = Events.Field(rawEvent, "_source");
            if (String.IsNullOrEmpty(src) || Array.IndexOf(Events.Sources, src) < 0) src = "claude";

            var e = AdaptEvent(rawEvent, src);
            if (e == null) return;

            string evName = Events.Field(e, "hook_event_name");
            HuskEventCount++;

            var pet = AvatarFor(src, true);
            if (pet == null) return;

            string sid = Events.Field(e, "session_id");
            AgentSession s;
            if (!sessions.TryGetValue(sid, out s)) {
                s = new AgentSession { Source = src };
                sessions[sid] = s;
            }
            s.LastAt = Clock.Now;

            string w = WhoName(src);
            string tg = SourceTag(src);
            string tool = Events.Field(e, "tool_name");
            string origTool = Events.Field(rawEvent, "tool_name");
            object inObj;
            var input = e.TryGetValue("tool_input", out inObj) ? inObj as Dictionary<string, object> : null;

            string callId = Events.Field(e, "tool_use_id");
            if (callId == "") callId = Events.Field(e, "call_id");
            if (callId == "") callId = Events.Field(e, "tool_call_id");

            switch (evName) {
                case "SessionStart": {
                    string why = Events.Field(e, "source");
                    if (why == "") why = "startup";
                    var incoming = delegations.FirstOrDefault(l => !l.Finished && l.ToSource == src && Clock.Now - l.StartedAt < 90.0);
                    if (incoming != null) {
                        pet.Play("wave", 1);
                        pet.Say("接到 " + WhoName(incoming.FromSource) + " 的委托，开工～", tg, BubbleKind.Speech, 3.0, false);
                    } else if (why == "startup" || why == "resume") {
                        pet.Play("wave", 2);
                        pet.Say(why == "resume" ? (w + " 回来了，继续干活～") : (w + " 上线啦，我来陪你写代码～"), tg, BubbleKind.Speech, 3.4, false);
                    }
                    break;
                }
                case "UserPromptSubmit": {
                    s.Busy = true;
                    s.Awaiting = null;
                    pet.Play("jump", 1);
                    string p = Calc.Cap(Events.Field(e, "prompt"), 26);
                    if (!String.IsNullOrEmpty(p)) s.LastPrompt = p;
                    pet.Say(String.IsNullOrEmpty(p) ? (w + " 接活了！") : (w + " 接活了：「" + p + "」"), tg, BubbleKind.Speech, 3.2, false);
                    break;
                }
                case "PreInvocation": {
                    string p = Calc.Cap(Events.Field(e, "prompt"), 26);
                    bool isNewTurn = !s.Busy || (!String.IsNullOrEmpty(p) && p != s.LastPrompt);
                    s.Busy = true;
                    s.Awaiting = null;
                    if (isNewTurn) {
                        if (!String.IsNullOrEmpty(p)) s.LastPrompt = p;
                        pet.Play("jump", 1);
                        pet.Say(String.IsNullOrEmpty(p) ? (w + " 接活了！") : (w + " 接活了：「" + p + "」"), tg, BubbleKind.Speech, 3.2, false);
                    }
                    break;
                }
                case "PreToolUse": {
                    s.Busy = true;
                    s.Awaiting = null;
                    s.LastTool = DescribeTool(tool, input);

                    var cu = ComputerUse.Parse(src, origTool != "" ? origTool : tool, input);
                    string callee = Events.Field(e, "_callee");
                    if (callee == "") callee = AgentCalls.Target(src, origTool != "" ? origTool : tool, input);

                    if (cu != null) {
                        s.LastTool = "操作电脑";
                        if (!String.IsNullOrEmpty(cu.App)) cuaSessionApp[sid] = cu.App;
                        string appHint = cu.App;
                        if (String.IsNullOrEmpty(appHint)) cuaSessionApp.TryGetValue(sid, out appHint);
                        RunComputerUse(pet, src, appHint, cu.Actions);
                    } else if (!String.IsNullOrEmpty(callee) && callee != src && Array.IndexOf(Events.Sources, callee) >= 0) {
                        string task = Events.Field(e, "_task");
                        if (task == "") task = AgentCalls.TaskSummary(input);
                        StartDelegation(src, sid, callId, origTool != "" ? origTool : tool, callee, task);
                    } else if (tool != "Task" && tool != "Agent") {
                        double lastCap;
                        if (!lastCaptionAt.TryGetValue(src, out lastCap) || Clock.Now - lastCap > 0.55) {
                            lastCaptionAt[src] = Clock.Now;
                            pet.Say(s.LastTool, null, BubbleKind.Caption, 2.6, false);
                        }
                    }
                    break;
                }
                case "PostToolUse": {
                    s.Awaiting = null;
                    // Tools without preceding prompt (e.g. cursor-agent -p) still mark session active
                    if (!s.Busy && !delegations.Any(l => l.FromSource == src && l.SessionId == sid)) {
                        s.Busy = true;
                        if (!String.IsNullOrEmpty(tool)) {
                            s.LastTool = DescribeTool(tool, input);
                            pet.Say(s.LastTool, null, BubbleKind.Caption, 2.2, false);
                        }
                    }
                    if (ComputerUse.MovesPointer(origTool != "" ? origTool : tool)) {
                        Effects.Ripple(Desktop.CursorDip, Palette.Pink, false);
                    }
                    object errObj, tfObj;
                    bool bad = (e.TryGetValue("_tool_failed", out tfObj) && Object.Equals(tfObj, true)) ||
                               (e.TryGetValue("error", out errObj) && errObj != null && errObj.ToString() != "" && !Object.Equals(errObj, false));
                    bool inputEmpty = e.ContainsKey("_input_empty") || input == null || input.Count == 0;
                    if (!FinishDelegation(src, sid, callId, origTool != "" ? origTool : tool, inputEmpty, bad) && bad) {
                        NotifyFailed(pet, w, tg, tool);
                    }
                    break;
                }
                case "PostToolUseFailure": {
                    s.Awaiting = null;
                    object intObj;
                    bool interrupted = e.TryGetValue("is_interrupt", out intObj) && Object.Equals(intObj, true);
                    bool inputEmpty = e.ContainsKey("_input_empty") || input == null || input.Count == 0;
                    if (!FinishDelegation(src, sid, callId, origTool != "" ? origTool : tool, inputEmpty, !interrupted) && !interrupted) {
                        NotifyFailed(pet, w, tg, tool);
                    }
                    break;
                }
                case "Notification": {
                    string ntype = Events.Field(e, "notification_type").ToLowerInvariant();
                    string msg = Events.Field(e, "message");
                    if (ntype == "permission_prompt" || msg.IndexOf("permission", StringComparison.OrdinalIgnoreCase) >= 0) {
                        string what = (src == "antigravity" && !String.IsNullOrEmpty(msg))
                            ? Calc.Cap(msg, 60)
                            : (!String.IsNullOrEmpty(s.LastTool) ? AskingPhrase(s.LastTool) : Calc.Cap(String.IsNullOrEmpty(msg) ? "执行操作" : msg, 60));
                        s.Awaiting = what;
                        RefreshPetsFromSessions();
                        pet.Say(w + " 需要你批准：" + what, tg, BubbleKind.Alert, null, true);
                        if (Prefs.Chime) PlayChime();
                        return;
                    } else if (ntype == "idle_prompt") {
                        pet.Play("waiting", 2);
                        pet.Say(w + " 在等你回话～", tg, BubbleKind.Speech, 4.0, false);
                    } else if (ntype == "agent_needs_input" || ntype == "elicitation_dialog") {
                        string q = Calc.Cap(String.IsNullOrEmpty(msg) ? "需要你回答" : msg, 60);
                        s.Awaiting = q;
                        RefreshPetsFromSessions();
                        pet.Say(w + " 有问题想问你：" + q, tg, BubbleKind.Alert, null, true);
                        if (Prefs.Chime) PlayChime();
                        return;
                    } else if (!String.IsNullOrEmpty(msg) && ntype != "auth_success") {
                        pet.Say(Calc.Cap(msg, 80), tg, BubbleKind.Speech, 4.0, false);
                    }
                    break;
                }
                case "AgentResponse": {
                    string r = Events.Field(e, "last_assistant_message");
                    if (!String.IsNullOrEmpty(r)) s.Reply = r;
                    break;
                }
                case "Stop": {
                    s.Busy = false;
                    s.Awaiting = null;
                    CancelSessionDelegations(src, sid);
                    RefreshPetsFromSessions();
                    string reply = Events.Field(e, "last_assistant_message");
                    if (reply == "") reply = s.Reply;
                    s.Reply = "";
                    if (Events.Field(e, "reason") == "user_canceled") {
                        pet.RetireClonesForSession(sid);
                        pet.Play("failed", 1);
                        pet.Say(w + " 被叫停了。", tg, BubbleKind.Speech, 4.0, false);
                    } else {
                        object idleObj;
                        bool idle = !e.TryGetValue("fully_idle", out idleObj) || !(idleObj is bool) || (bool)idleObj;
                        if (!idle && String.IsNullOrEmpty(ExtractGist(reply))) {
                            pet.Say(w + " 在等后台任务……", tg, BubbleKind.Speech, 3.5, false);
                        } else {
                            NotifyDone(pet, w, tg, reply, sid, idle);
                        }
                    }
                    return;
                }
                case "StopFailure": {
                    s.Busy = false;
                    s.Awaiting = null;
                    CancelSessionDelegations(src, sid);
                    RefreshPetsFromSessions();
                    pet.RetireClonesForSession(sid);
                    pet.Play("failed", 1);
                    pet.Say(w + " 出错停下了……", tg, BubbleKind.Speech, 4.0, false);
                    return;
                }
                case "SubagentStart": {
                    s.Busy = true;
                    string aid = Events.Field(e, "agent_id");
                    if (aid == "") aid = Guid.NewGuid().ToString("N");
                    pet.SpawnClone(aid, sid);
                    string kind = Events.Field(e, "agent_type");
                    pet.Say(String.IsNullOrEmpty(kind) ? "派出分身！" : ("派出分身：" + kind), null, BubbleKind.Caption, 2.4, false);
                    break;
                }
                case "SubagentStop": {
                    string aid = Events.Field(e, "agent_id");
                    pet.RetireClone(aid == "" ? null : aid, sid);
                    break;
                }
                case "PreCompact": {
                    pet.Glitch(1.1);
                    pet.Say("记忆压缩中……刚才的细节要模糊啦。", tg, BubbleKind.Speech, 3.2, false);
                    break;
                }
                case "SessionEnd": {
                    bool wasBusy = s.Busy;
                    string savedReply = s.Reply;
                    s.Busy = false;
                    s.Awaiting = null;
                    pet.RetireClonesForSession(sid);
                    sessions.Remove(sid);
                    cuaSessionApp.Remove(sid);
                    CancelSessionDelegations(src, sid);
                    RefreshPetsFromSessions();

                    string finalStatus = Events.Field(e, "final_status").ToLowerInvariant();
                    if (wasBusy && src == "cursor" && !String.IsNullOrEmpty(finalStatus)) {
                        if (finalStatus == "completed") {
                            NotifyDone(pet, w, tg, savedReply, sid);
                        } else {
                            pet.Play("failed", 1);
                            pet.Say(finalStatus == "aborted" ? (w + " 被叫停了。") : (w + " 出错停下了……"), tg, BubbleKind.Speech, 4.0, false);
                        }
                    } else if (!String.Equals(Events.Field(e, "reason"), "clear", StringComparison.OrdinalIgnoreCase)) {
                        pet.Play("wave", 2);
                        pet.Say(w + " 下线了，拜拜～", tg, BubbleKind.Speech, 3.0, false);
                    }
                    return;
                }
            }

            RefreshPetsFromSessions();
        }

        // Legacy 5/8/10-field tab packet fallback (for any running session using an older GLORIA-Hook.exe)
        void HandleLegacyPacket(string[] f) {
            if (MuteHooks || f == null || f.Length < 5) return;
            string src = f[0];
            if (Array.IndexOf(Events.Sources, src) < 0) return;
            string sid = src + ":" + f[1];
            string pose = f[2];
            string line = f[3];
            bool ended = f[4] == "end";
            var pet = AvatarFor(src, true);
            if (pet == null) return;

            AgentSession s;
            if (!sessions.TryGetValue(sid, out s)) {
                s = new AgentSession { Source = src };
                sessions[sid] = s;
            }
            s.LastAt = Clock.Now;

            if (ended) {
                s.Busy = false;
                s.Awaiting = null;
                CancelSessionDelegations(src, sid);
                RefreshPetsFromSessions();
                if (pose == "failed") {
                    pet.Play("failed", 1);
                    pet.Say(WhoName(src) + " 出错停下了……", SourceTag(src), BubbleKind.Speech, 3.5, false);
                } else {
                    NotifyDone(pet, WhoName(src), SourceTag(src), "", sid);
                }
                return;
            }

            if (pose == "waiting") {
                s.Awaiting = line;
                RefreshPetsFromSessions();
                pet.Say(WhoName(src) + "：" + line, SourceTag(src), BubbleKind.Alert, null, true);
                return;
            }
            s.Awaiting = null;
            if (pose == "wave" || pose == "failed") {
                pet.Play(pose, 1);
                pet.Say(WhoName(src) + " · " + line, SourceTag(src), BubbleKind.Speech, 3.0, false);
            } else {
                s.Busy = true;
                pet.Say(line, null, BubbleKind.Caption, 2.4, false);
            }
            RefreshPetsFromSessions();
        }

        static void PlayChime() {
            try { SystemSounds.Asterisk.Play(); } catch { }
        }

        void RunComputerUse(Pet pet, string src, string appHint, List<CUAction> acts) {
            var initialWin = ComputerUse.FindTargetWindow(appHint);
            string tg = SourceTag(src);
            for (int k = 0; k < acts.Count; k++) {
                var act = acts[k];
                int delayMs = k * 850;
                Action step = delegate {
                    if (!pet.IsVisible) return;
                    var winNow = ComputerUse.FindTargetWindow(appHint);
                    var targetWin = winNow.HasValue ? winNow : initialWin;
                    Point? pt = (act.Point1.HasValue && targetWin.HasValue)
                        ? (Point?)ComputerUse.ScreenPoint(act.Point1.Value, targetWin.Value)
                        : null;
                    pet.Act(act, targetWin, pt, tg);
                };
                if (delayMs == 0) step();
                else Later(delayMs, step);
            }
        }

        // MARK: Delegations (Agent calling another Agent)

        void StartDelegation(string fromSrc, string sid, string callId, string toolName, string toSrc, string task) {
            if (!String.IsNullOrEmpty(callId) && delegations.Any(d => !d.Finished && d.FromSource == fromSrc && d.SessionId == sid && d.CallId == callId)) {
                return;
            }
            var sender = AvatarFor(fromSrc, true);
            var receiver = AvatarFor(toSrc, true);
            var link = new DelegationLink {
                FromSource = fromSrc,
                SessionId = sid,
                CallId = callId,
                ToolName = (toolName ?? "").ToLowerInvariant(),
                ToSource = toSrc,
                Task = task ?? "",
                Sender = sender,
                Receiver = receiver
            };
            delegations.Add(link);
            sessions["link:" + link.Key] = new AgentSession { Source = toSrc, Busy = true, LastAt = Clock.Now };
            RefreshPetsFromSessions();

            string ask = String.IsNullOrEmpty(task)
                ? (WhoName(toSrc) + "，这个交给你啦～")
                : (WhoName(toSrc) + "，帮我：「" + task + "」");
            string got = "收到 " + WhoName(fromSrc) + " 的委托！";

            if (sender == null || receiver == null || sender == receiver) {
                link.Delivered = true;
                if (sender != null) sender.Say("请 " + WhoName(toSrc) + " 帮忙" + (String.IsNullOrEmpty(task) ? "" : ("：「" + task + "」")), null, BubbleKind.Caption, 3.0, false);
                return;
            }

            sender.PassNote(receiver, "wave", ask, SourceTag(fromSrc),
                delegate { return delegations.Contains(link) && sender.IsVisible && receiver.IsVisible; },
                delegate {
                    receiver.GotNote(sender, got, SourceTag(toSrc));
                    link.Delivered = true;
                    if (link.Finished) {
                        Later(600, delegate { SendDelegationReply(link); });
                    }
                });
        }

        bool FinishDelegation(string fromSrc, string sid, string callId, string toolName, bool inputEmpty, bool failed) {
            var sessionLinks = delegations.Where(x => !x.Finished && x.FromSource == fromSrc && x.SessionId == sid).ToList();
            if (sessionLinks.Count == 0) return false;

            DelegationLink match = null;
            if (!String.IsNullOrEmpty(callId)) {
                match = sessionLinks.FirstOrDefault(x => x.CallId == callId);
            } else {
                string lowTool = (toolName ?? "").ToLowerInvariant();
                if (inputEmpty && !String.IsNullOrEmpty(lowTool)) {
                    var byTool = sessionLinks.Where(x => x.ToolName == lowTool).ToList();
                    if (byTool.Count == 1) match = byTool[0];
                }
                if (match == null) match = sessionLinks.FirstOrDefault(x => String.IsNullOrEmpty(x.CallId));
            }
            if (match == null) return false;

            match.Finished = true;
            match.Failed = failed;
            sessions.Remove("link:" + match.Key);
            RefreshPetsFromSessions();

            if (match.Sender == match.Receiver || match.Delivered) {
                SendDelegationReply(match);
            }
            return true;
        }

        void SendDelegationReply(DelegationLink link) {
            if (!delegations.Contains(link) || link.Returning) return;
            link.Returning = true;

            var from = link.Receiver;
            var to = link.Sender;
            string say = link.Failed ? "这个没搞定……" : "做好啦，交给你～";
            string note = link.Failed ? "没关系，我来想办法。" : ("收到 " + WhoName(link.ToSource) + " 的结果，谢啦！");

            if (from == null || to == null || from == to) {
                delegations.Remove(link);
                if (to != null) {
                    to.Play(link.Failed ? "failed" : "wave", 1);
                    to.Say(note, SourceTag(link.FromSource), BubbleKind.Speech, 2.8, false);
                }
                return;
            }

            from.PassNote(to, link.Failed ? "failed" : "wave", say, SourceTag(link.ToSource),
                delegate { return delegations.Contains(link) && from.IsVisible && to.IsVisible; },
                delegate {
                    delegations.Remove(link);
                    to.GotNote(from, note, SourceTag(link.FromSource));
                });
        }

        void CancelSessionDelegations(string fromSrc, string sid) {
            foreach (var l in delegations.Where(x => x.FromSource == fromSrc && x.SessionId == sid).ToArray()) {
                delegations.Remove(l);
                sessions.Remove("link:" + l.Key);
            }
        }

        void NotifyDone(Pet pet, string w, string tg, string reply, string sid, bool retireClones = true) {
            if (retireClones) pet.RetireClonesForSession(sid);
            // If another session on this same pet is still awaiting permission, keep showing `waiting`!
            if (pet.Awaiting != null) return;
            pet.Play("review", 1);
            Later(950, delegate {
                if (pet.IsVisible && pet.Awaiting == null) pet.Play("wave", 2);
            });
            string summary = ExtractGist(reply);
            pet.Say(String.IsNullOrEmpty(summary) ? (w + " 做完啦！") : ("做完啦！" + summary), tg, BubbleKind.Speech, String.IsNullOrEmpty(summary) ? 3.2 : 6.8, false);
            pet.Sparkle("✦", Palette.Butter, 3);
        }

        void NotifyFailed(Pet pet, string w, string tg, string tool) {
            pet.Play("failed", 1);
            if (Clock.Now - lastFailSayAt > 8.0) {
                lastFailSayAt = Clock.Now;
                pet.Say(w + " 踩了个坑" + (String.IsNullOrEmpty(tool) ? "" : ("（" + tool + "）")) + "，在想办法。", tg, BubbleKind.Speech, 3.0, false);
            }
        }

        internal void RefreshPetsFromSessions() {
            foreach (var pet in pets.Values) {
                var mine = sessions.Values.Where(s => AvatarFor(s.Source, false) == pet).ToList();
                pet.Working = mine.Any(s => s.Busy);
                var wait = mine.FirstOrDefault(s => !String.IsNullOrEmpty(s.Awaiting));
                string was = pet.Awaiting;
                pet.Awaiting = wait != null ? wait.Awaiting : null;
                pet.AwaitingTag = wait != null ? SourceTag(wait.Source) : "CLAUDE CODE";
                if (was != null && pet.Awaiting == null && pet.bubble.Sticky) {
                    pet.bubble.Unstick();
                }
            }
        }

        internal void ClearHuskState() {
            sessions.Clear();
            delegations.Clear();
            cuaSessionApp.Clear();
            foreach (var p in pets.Values) p.RetireAllClones();
            RefreshPetsFromSessions();
        }

        void ExpireStaleSessions() {
            double t = Clock.Now;
            bool changed = false;
            foreach (var kv in sessions.Where(p => t - p.Value.LastAt > 20 * 60).ToArray()) {
                sessions.Remove(kv.Key);
                cuaSessionApp.Remove(kv.Key);
                foreach (var p in pets.Values) p.RetireClonesForSession(kv.Key);
                changed = true;
            }
            foreach (var l in delegations.Where(x => !x.Sender.IsVisible || !x.Receiver.IsVisible || t - x.StartedAt > 30 * 60).ToArray()) {
                delegations.Remove(l);
                sessions.Remove("link:" + l.Key);
                changed = true;
            }
            if (changed) RefreshPetsFromSessions();
        }

        static string LeafFile(object val) {
            if (val == null) return "";
            var list = val as IList;
            if (list != null) {
                var names = new List<string>();
                for (int i = 0; i < Math.Min(3, list.Count); i++) {
                    string lf = LeafFile(list[i]);
                    if (!String.IsNullOrEmpty(lf)) names.Add(lf);
                }
                return String.Join("、", names.ToArray());
            }
            string s = val.ToString().Trim();
            if (s == "") return "";
            return s.Replace('\\', '/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        }

        static string DescribeTool(string tool, Dictionary<string, object> input) {
            object fpObj = null;
            if (input != null) {
                if (!input.TryGetValue("file_path", out fpObj) || fpObj == null || fpObj.ToString() == "")
                    if (!input.TryGetValue("file_paths", out fpObj) || fpObj == null)
                        if (!input.TryGetValue("notebook_path", out fpObj) || fpObj == null)
                            input.TryGetValue("path", out fpObj);
            }
            string file = LeafFile(fpObj);

            switch (tool) {
                case "Read": return String.IsNullOrEmpty(file) ? "在看文件" : ("在看 " + file);
                case "Edit":
                case "Write":
                case "MultiEdit":
                case "NotebookEdit":
                    return String.IsNullOrEmpty(file) ? "在改文件" : ("在改 " + file);
                case "Bash": {
                    string cmd = Events.Field(input, "command");
                    return String.IsNullOrEmpty(cmd) ? "在跑命令" : ("在跑 " + Calc.Cap(cmd, 42));
                }
                case "Grep": return "在搜 " + Calc.Cap(Events.Field(input, "pattern"), 30);
                case "Glob": return "在找文件 " + Calc.Cap(Events.Field(input, "pattern"), 30);
                case "WebFetch": return "在读网页";
                case "WebSearch": return "在搜网页：" + Calc.Cap(Events.Field(input, "query"), 30);
                case "Task":
                case "Agent":
                    return "派出分身";
                case "TodoWrite": return "在排计划";
                case "update_focus": {
                    string f = Events.Field(input, "focus");
                    return String.IsNullOrEmpty(f) ? "在排计划" : Calc.Cap(f, 40);
                }
                case "generate_image": return "在画图";
                case "manage_task":
                case "command_status":
                    return "在看后台任务";
                case "run_workflow": return "在跑工作流";
                case "swarm": return "在调度蜂群";
                case "search_marketplace":
                case "list_plugin_accounts":
                    return "在查插件";
                case "list_permissions": return "在查权限";
                case "Delete": return String.IsNullOrEmpty(file) ? "在删文件" : ("在删 " + file);
                case "run_code": return "在跑一段代码";
                default:
                    if (tool.StartsWith("mcp__", StringComparison.OrdinalIgnoreCase)) {
                        string last = tool.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? tool;
                        return "在用 " + last;
                    }
                    return String.IsNullOrEmpty(tool) ? "在忙" : ("在用 " + tool);
            }
        }

        static string AskingPhrase(string caption) {
            string[,] swaps = {
                { "在跑 ", "运行 " }, { "在改 ", "修改 " }, { "在看 ", "读取 " },
                { "在搜网页：", "搜索网页：" }, { "在搜 ", "搜索 " }, { "在找文件 ", "查找文件 " },
                { "在读网页", "读取网页" }, { "在用 ", "使用 " }, { "在排计划", "更新计划" },
                { "在改文件", "修改文件" }, { "在跑命令", "运行命令" }
            };
            for (int i = 0; i < swaps.GetLength(0); i++) {
                string a = swaps[i, 0], b = swaps[i, 1];
                if (caption.StartsWith(a, StringComparison.Ordinal)) return b + caption.Substring(a.Length);
            }
            return caption;
        }

        static string ExtractGist(string text) {
            if (String.IsNullOrWhiteSpace(text)) return "";
            foreach (string rawLine in text.Split('\n')) {
                string l = rawLine.Trim().Replace("**", "").Replace("`", "");
                while (l.Length > 0 && "#>-*• ".IndexOf(l[0]) >= 0) l = l.Substring(1);
                if (l.Length >= 4) return Calc.Cap(l, 70);
            }
            return "";
        }

        // MARK: Scripted Demos (matching Sources/Husk.swift)

        internal void DemoHusk() {
            var steps = new Tuple<int, Dictionary<string, object>>[] {
                Tuple.Create(0, MakeEv("claude", "demo-husk", "SessionStart", new Dictionary<string, object> { { "source", "startup" } })),
                Tuple.Create(2500, MakeEv("claude", "demo-husk", "UserPromptSubmit", new Dictionary<string, object> { { "prompt", "优化 Windows 桌宠体验，跟 Mac 版保持一致" } })),
                Tuple.Create(4500, MakeEv("claude", "demo-husk", "PreToolUse", new Dictionary<string, object> {
                    { "tool_name", "Read" }, { "tool_input", new Dictionary<string, object> { { "file_path", "Windows/Pet.cs" } } }
                })),
                Tuple.Create(5900, MakeEv("claude", "demo-husk", "PreToolUse", new Dictionary<string, object> {
                    { "tool_name", "Edit" }, { "tool_input", new Dictionary<string, object> { { "file_path", "Windows/Music.cs" } } }
                })),
                Tuple.Create(7100, MakeEv("claude", "demo-husk", "SubagentStart", new Dictionary<string, object> {
                    { "agent_id", "demo-sub" }, { "agent_type", "Explore" }
                })),
                Tuple.Create(8300, MakeEv("claude", "demo-husk", "PreToolUse", new Dictionary<string, object> {
                    { "tool_name", "Bash" }, { "tool_input", new Dictionary<string, object> { { "command", "powershell -File Windows/build.ps1" } } }
                })),
                Tuple.Create(9200, MakeEv("claude", "demo-husk", "Notification", new Dictionary<string, object> {
                    { "notification_type", "permission_prompt" }, { "message", "Claude needs permission to run Bash" }
                })),
                Tuple.Create(13200, MakeEv("claude", "demo-husk", "PostToolUseFailure", new Dictionary<string, object> {
                    { "tool_name", "Bash" }, { "error", "exit 1" }
                })),
                Tuple.Create(15100, MakeEv("claude", "demo-husk", "SubagentStop", new Dictionary<string, object> { { "agent_id", "demo-sub" } })),
                Tuple.Create(16100, MakeEv("claude", "demo-husk", "PreCompact", new Dictionary<string, object> { { "trigger", "auto" } })),
                Tuple.Create(19000, MakeEv("claude", "demo-husk", "Stop", new Dictionary<string, object> {
                    { "last_assistant_message", "Windows 版桌宠已经跟 Mac 版完全对齐，丝滑不卡顿啦！" }
                }))
            };
            foreach (var st in steps) {
                var ev = st.Item2;
                if (st.Item1 == 0) HandleHookEvent(ev, true);
                else Later(st.Item1, delegate { HandleHookEvent(ev, true); });
            }
        }

        internal void DemoTogether() {
            var pClaude = AvatarFor("claude", true);
            var pCodex = AvatarFor("codex", true);
            if (pClaude != null && pCodex != null && pClaude == pCodex) {
                pClaude.Say("先在「Claude Code 化身」和「Codex 化身」里各选一个不同的角色，她们才能互相传话～", null, BubbleKind.Speech, 5.0, false);
                return;
            }
            var steps = new Tuple<int, Dictionary<string, object>>[] {
                Tuple.Create(0, MakeEv("claude", "demo-c", "UserPromptSubmit", new Dictionary<string, object> { { "prompt", "让 Codex 帮忙画一张角色设定图" } })),
                Tuple.Create(2000, MakeEv("claude", "demo-c", "PreToolUse", new Dictionary<string, object> {
                    { "tool_name", "Bash" }, { "tool_use_id", "demo-call" },
                    { "tool_input", new Dictionary<string, object> { { "command", "codex exec --skip-git-repo-check \"画一张 Doro 抱着欧润吉的设定图\"" } } }
                })),
                Tuple.Create(4200, MakeEv("codex", "demo-x", "SessionStart", new Dictionary<string, object> { { "source", "startup" } })),
                Tuple.Create(5400, MakeEv("codex", "demo-x", "PreToolUse", new Dictionary<string, object> {
                    { "tool_name", "Bash" },
                    { "tool_input", new Dictionary<string, object> { { "command", "python draw.py --doro" } } }
                })),
                Tuple.Create(8200, MakeEv("codex", "demo-x", "Stop", new Dictionary<string, object> { { "last_assistant_message", "设定图画好了。" } })),
                Tuple.Create(9000, MakeEv("claude", "demo-c", "PostToolUse", new Dictionary<string, object> {
                    { "tool_name", "Bash" }, { "tool_use_id", "demo-call" }
                })),
                Tuple.Create(12800, MakeEv("claude", "demo-c", "Stop", new Dictionary<string, object> {
                    { "last_assistant_message", "Codex 画好了设定图，我放进项目里了。" }
                }))
            };
            foreach (var st in steps) {
                var ev = st.Item2;
                if (st.Item1 == 0) HandleHookEvent(ev, true);
                else Later(st.Item1, delegate { HandleHookEvent(ev, true); });
            }
        }

        internal void DemoComputerUse() {
            var snap = GetDesktop(0.0);
            WinInfo? target = null;
            double bestArea = 0;
            foreach (var w in snap.Wins) {
                double area = w.Rect.Width * w.Rect.Height;
                if (w.Rect.Width > 400 && w.Rect.Height > 250 && area > bestArea) {
                    bestArea = area;
                    target = w;
                }
            }
            if (!target.HasValue && snap.Wins.Count > 0) target = snap.Wins[0];

            string owner = target.HasValue ? target.Value.Owner : "桌面窗口";
            int W = target.HasValue ? (int)target.Value.Rect.Width : 900;
            int H = target.HasValue ? (int)target.Value.Rect.Height : 600;

            var codeSteps = new Tuple<int, string>[] {
                Tuple.Create(1200, "let app = await cua.getApp('" + owner + "'); await app.getScreenshot();"),
                Tuple.Create(3000, "await app.click([" + (W * 3 / 10) + ", " + (H * 3 / 4) + "]);"),
                Tuple.Create(5200, "await app.typeText('GLORIA 在帮 Codex 打字');"),
                Tuple.Create(8000, "await app.scroll([" + (W / 2) + ", " + (H * 3 / 4) + "], {deltaY: 600});"),
                Tuple.Create(10400, "await app.drag([" + (W * 2 / 5) + ", " + (H * 4 / 5) + "], [" + (W * 7 / 10) + ", " + (H * 4 / 5) + "]);"),
                Tuple.Create(12800, "await app.pressKey('ctrl+s'); await app.getScreenshot();")
            };

            HandleHookEvent(MakeEv("codex", "demo-cua", "UserPromptSubmit", new Dictionary<string, object> {
                { "prompt", "帮我在 " + owner + " 里点一下、打几个字、截个图" }
            }), true);

            foreach (var cs in codeSteps) {
                string js = cs.Item2;
                Later(cs.Item1, delegate {
                    HandleHookEvent(MakeEv("codex", "demo-cua", "PreToolUse", new Dictionary<string, object> {
                        { "tool_name", "mcp__cua_repl__js" },
                        { "tool_input", new Dictionary<string, object> { { "code", js } } }
                    }), true);
                });
            }

            Later(16200, delegate {
                HandleHookEvent(MakeEv("codex", "demo-cua", "Stop", new Dictionary<string, object> {
                    { "last_assistant_message", "在 " + owner + " 里点了、打了字、截了图。" }
                }), true);
            });
        }

        static Dictionary<string, object> MakeEv(string source, string sid, string evName, Dictionary<string, object> extra) {
            var d = new Dictionary<string, object>(extra, StringComparer.OrdinalIgnoreCase);
            d["_source"] = source;
            d["session_id"] = sid;
            d["hook_event_name"] = evName;
            return d;
        }
    }
}
