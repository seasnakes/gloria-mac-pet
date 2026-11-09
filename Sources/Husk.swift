import AppKit
import Darwin

// Code / Husk mode, modelled on Capri's: Claude Code, Codex, Cursor and DSH call `GLORIA --hook <agent>` from their own hooks.
// The hook forwards a slimmed copy of the event over a private Unix socket; GLORIA only watches and never answers for them.

enum Wire {
    static func connect() -> Int32? {
        let fd = socket(AF_UNIX, SOCK_STREAM, 0)
        guard fd >= 0 else { return nil }
        var addr = sockaddr_un()
        addr.sun_family = sa_family_t(AF_UNIX)
        let path = Paths.socket
        guard path.utf8.count < MemoryLayout.size(ofValue: addr.sun_path) else { close(fd); return nil }
        withUnsafeMutablePointer(to: &addr.sun_path) { p in
            p.withMemoryRebound(to: CChar.self, capacity: 104) { strncpy($0, path, 103) }
        }
        let ok = withUnsafePointer(to: &addr) { p in
            p.withMemoryRebound(to: sockaddr.self, capacity: 1) { Darwin.connect(fd, $0, socklen_t(MemoryLayout<sockaddr_un>.size)) }
        }
        if ok != 0 { close(fd); return nil }
        var tv = timeval(tv_sec: 3, tv_usec: 0)
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &tv, socklen_t(MemoryLayout<timeval>.size))
        setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &tv, socklen_t(MemoryLayout<timeval>.size))
        var one: Int32 = 1
        setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &one, socklen_t(MemoryLayout<Int32>.size))
        return fd
    }

    static func writeAll(_ fd: Int32, _ data: Data) -> Bool {
        data.withUnsafeBytes { buf -> Bool in
            var off = 0
            while off < buf.count {
                let n = write(fd, buf.baseAddress!.advanced(by: off), buf.count - off)
                if n <= 0 { return false }
                off += n
            }
            return true
        }
    }

    static func readAll(_ fd: Int32, limit: Int) -> Data? {
        var out = Data()
        var chunk = [UInt8](repeating: 0, count: 16384)
        while true {
            let n = read(fd, &chunk, chunk.count)
            if n < 0 && errno == EINTR { continue }
            if n < 0 { return out.isEmpty ? nil : out }
            if n == 0 { return out }
            out.append(chunk, count: n)
            if out.count > limit { return nil }
        }
    }
}

enum HookClient {
    static let keep = ["hook_event_name", "session_id", "cwd", "tool_name", "tool_input", "tool_use_id", "error", "is_interrupt",
                       "prompt", "message", "title", "notification_type", "last_assistant_message", "agent_id", "agent_type",
                       "source", "reason", "trigger", "stop_hook_active", "final_status"]
    static let inputKeep = ["file_path", "file_paths", "path", "command", "cmd", "pattern", "url", "query", "description",
                            "subagent_type", "notebook_path", "questions",
                            // computer use: Codex's cua_repl code, Claude's action / coordinates (typed text: only its length)
                            "code", "title", "action", "coordinate", "start_coordinate", "scroll_direction", "key", "app"]

    /// `GLORIA --hook claude|codex|cursor|dsh`: forward stdin's event and exit 0 quickly, printing nothing.
    static func run(source: String) {
        signal(SIGALRM) { _ in exit(0) }
        alarm(3)
        signal(SIGPIPE, SIG_IGN)
        let data = FileHandle.standardInput.readDataToEndOfFile()
        guard data.count < 8 * 1024 * 1024,
              let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              var evt = adapt(obj, source: source) else { return }
        evt = slim(evt)
        evt["_source"] = source
        guard let body = try? JSONSerialization.data(withJSONObject: evt), let fd = Wire.connect() else { return }
        _ = Wire.writeAll(fd, body)
        shutdown(fd, SHUT_WR)
        close(fd)
    }

    /// `GLORIA --ctl '{"ctl":"status"}'`: talk to the running app, print its reply.
    static func control(_ json: String) -> Int32 {
        guard let fd = Wire.connect() else { FileHandle.standardError.write("GLORIA 没在运行\n".data(using: .utf8)!); return 1 }
        _ = Wire.writeAll(fd, json.data(using: .utf8)!)
        shutdown(fd, SHUT_WR)
        let reply = Wire.readAll(fd, limit: 1 << 20) ?? Data()
        close(fd)
        FileHandle.standardOutput.write(reply)
        FileHandle.standardOutput.write("\n".data(using: .utf8)!)
        return 0
    }

    static func trim(_ v: Any, depth: Int) -> Any? {
        if let s = v as? String { return cap(s, 300) }
        if let a = v as? [Any] { return depth > 2 ? nil : a.prefix(8).compactMap { trim($0, depth: depth + 1) } }
        if let m = v as? [String: Any] {
            if depth > 2 { return nil }
            var o: [String: Any] = [:]
            for (k, x) in m where !["content", "old_string", "new_string", "new_source", "edits"].contains(k) {
                if let t = trim(x, depth: depth + 1) { o[k] = t }
            }
            return o
        }
        return v
    }

    static func slim(_ e: [String: Any]) -> [String: Any] {
        var o: [String: Any] = [:]
        for k in keep { if let v = e[k], let t = trim(v, depth: 0) { o[k] = t } }
        if let input = e["tool_input"] as? [String: Any] {
            var i: [String: Any] = [:]
            for k in inputKeep { if let v = input[k], let t = trim(v, depth: 1) { i[k] = t } }
            if let code = input["code"] as? String { i["code"] = String(code.prefix(2400)) }
            if let text = input["text"] as? String {
                if (input["action"] as? String) == "key" { i["text"] = cap(text, 24) } else { i["text_len"] = text.count }
            }
            if let acts = input["actions"] as? [[String: Any]] {
                i["actions"] = acts.prefix(16).map { a -> [String: Any] in
                    ["name": a["name"] as? String ?? "", "action": (a["input"] as? [String: Any])?["action"] as? String ?? ""]
                }
            }
            o["tool_input"] = i
        }
        if let r = e["last_assistant_message"] as? String { o["last_assistant_message"] = String(r.prefix(1200)) }
        return o
    }

    /// Cursor's tool names → Claude's (seen from cursor-agent's own hooks).
    static let cursorTools = ["Shell": "Bash", "StrReplace": "Edit", "MultiEdit": "Edit", "EditNotebook": "NotebookEdit", "Delete": "Delete",
                              "LS": "Glob", "ReadLints": "Read", "SemanticSearch": "Grep", "Task": "Agent", "TodoWrite": "TodoWrite"]
    /// DeepSeek Harness's lowercase tools → Claude's (the names dsh-pet's tool table knows, plus the obvious shells).
    static let dshTools = ["pwsh": "Bash", "bash": "Bash", "shell": "Bash", "exec": "Bash", "read": "Read", "read_image": "Read",
                           "write": "Write", "edit": "Edit", "str_replace_editor": "Edit", "glob": "Glob", "grep": "Grep",
                           "web_search": "WebSearch", "web_fetch": "WebFetch", "subagent": "Agent", "subagent_fork": "Agent",
                           "workflow": "TodoWrite", "todo": "TodoWrite"]

    /// Every agent's event in Claude Code's shape (Codex after Capri's adapter; DSH already speaks it through its hooks bridge).
    static func adapt(_ e: [String: Any], source: String) -> [String: Any]? {
        guard e["hook_event_name"] is String else { return nil }
        if source == "cursor" { return cursor(e) }
        if source == "dsh" {
            var o = e
            o["session_id"] = "dsh:" + ((e["session_id"] as? String) ?? "default")
            let tool = e["tool_name"] as? String ?? ""
            if let t = dshTools[tool.lowercased()] { o["tool_name"] = t }
            if var i = e["tool_input"] as? [String: Any] {
                if i["file_path"] == nil { i["file_path"] = i["filePath"] ?? i["path"] }
                if i["query"] == nil, let q = (i["queries"] as? [Any])?.first { i["query"] = q }
                o["tool_input"] = i
            }
            return o
        }
        guard source == "codex" else { return e }
        var o = e
        o["session_id"] = "codex:" + ((e["session_id"] as? String) ?? "default")
        let name = e["hook_event_name"] as? String ?? ""
        let tool = e["tool_name"] as? String ?? ""
        let input = e["tool_input"] as? [String: Any] ?? [:]
        if name == "PermissionRequest" {
            o["hook_event_name"] = "Notification"
            o["notification_type"] = "permission_prompt"
            o["message"] = (input["description"] as? String) ?? ""
        }
        if tool == "apply_patch" {
            let cmd = input["command"] as? String ?? ""
            var files: [String] = []
            for line in cmd.split(separator: "\n") {
                for p in ["*** Update File: ", "*** Add File: ", "*** Delete File: "] where line.hasPrefix(p) {
                    files.append(String(line.dropFirst(p.count)).trimmingCharacters(in: .whitespaces))
                }
            }
            o["tool_name"] = "Edit"
            o["tool_input"] = ["file_paths": files, "file_path": files.first ?? ""]
        }
        if tool == "exec_command" || tool == "shell_command" {
            o["tool_name"] = "Bash"
            var i = input
            i["command"] = input["cmd"] ?? input["command"]
            o["tool_input"] = i
        }
        if name == "PreToolUse" && tool == "request_user_input" {
            o["hook_event_name"] = "Notification"
            o["notification_type"] = "agent_needs_input"
            o["message"] = "Codex 想问你几个问题。"
        }
        return o
    }

    /// Cursor (the editor and cursor-agent): camelCase events, its own tool names. cursor-agent -p only sends tool events
    /// and sessionEnd, so that one also has to say how the job went.
    static func cursor(_ e: [String: Any]) -> [String: Any]? {
        let names = ["sessionStart": "SessionStart", "sessionEnd": "SessionEnd", "beforeSubmitPrompt": "UserPromptSubmit",
                     "preToolUse": "PreToolUse", "postToolUse": "PostToolUse", "postToolUseFailure": "PostToolUseFailure",
                     "subagentStart": "SubagentStart", "subagentStop": "SubagentStop", "stop": "Stop",
                     "afterAgentResponse": "AgentResponse", "preCompact": "PreCompact"]
        guard let name = names[e["hook_event_name"] as? String ?? ""] else { return nil }
        var o = e
        o["hook_event_name"] = name
        o["session_id"] = "cursor:" + ((e["session_id"] as? String) ?? (e["conversation_id"] as? String) ?? "default")
        if ((o["cwd"] as? String) ?? "").isEmpty { o["cwd"] = (e["workspace_roots"] as? [String])?.first }
        if let t = e["tool_name"] as? String {
            o["tool_name"] = cursorTools[t] ?? (t.hasPrefix("MCP:") ? "mcp__" + t.dropFirst(4).replacingOccurrences(of: ":", with: "__") : t)
        }
        switch name {
        case "PostToolUseFailure": o["error"] = e["error_message"] ?? "failed"
        case "SubagentStart": o["agent_id"] = e["subagent_id"]; o["agent_type"] = e["subagent_type"]
        case "SubagentStop": o["agent_type"] = e["subagent_type"]
        case "Stop" where (e["status"] as? String) == "error": o["hook_event_name"] = "StopFailure"
        case "AgentResponse": o["last_assistant_message"] = e["text"]
        default: break
        }
        return o
    }
}

/// Listens on the Unix socket for hook events and control commands.
final class HuskServer {
    private var fd: Int32 = -1
    private var source: DispatchSourceRead?
    private let queue = DispatchQueue(label: "studio.seasnake.gloria.socket")
    var onEvent: (([String: Any]) -> Void)?
    var onControl: (([String: Any]) -> [String: Any])?
    private(set) var listening = false

    func start() {
        let path = Paths.socket
        unlink(path)
        fd = socket(AF_UNIX, SOCK_STREAM, 0)
        guard fd >= 0 else { return }
        var addr = sockaddr_un()
        addr.sun_family = sa_family_t(AF_UNIX)
        withUnsafeMutablePointer(to: &addr.sun_path) { p in
            p.withMemoryRebound(to: CChar.self, capacity: 104) { strncpy($0, path, 103) }
        }
        let ok = withUnsafePointer(to: &addr) { p in
            p.withMemoryRebound(to: sockaddr.self, capacity: 1) { bind(fd, $0, socklen_t(MemoryLayout<sockaddr_un>.size)) }
        }
        guard ok == 0 else { close(fd); fd = -1; return }
        chmod(path, 0o600)
        listen(fd, 16)
        let src = DispatchSource.makeReadSource(fileDescriptor: fd, queue: queue)
        src.setEventHandler { [weak self] in self?.accept() }
        src.resume()
        source = src
        listening = true
    }

    func stop() {
        source?.cancel()
        if fd >= 0 { close(fd) }
        unlink(Paths.socket)
    }

    private func accept() {
        let c = Darwin.accept(fd, nil, nil)
        guard c >= 0 else { return }
        // Accepted sockets inherit the listener's O_NONBLOCK; read this one blocking (with a timeout).
        let flags = fcntl(c, F_GETFL)
        if flags >= 0 { _ = fcntl(c, F_SETFL, flags & ~O_NONBLOCK) }
        var tv = timeval(tv_sec: 2, tv_usec: 0)
        setsockopt(c, SOL_SOCKET, SO_RCVTIMEO, &tv, socklen_t(MemoryLayout<timeval>.size))
        var one: Int32 = 1
        setsockopt(c, SOL_SOCKET, SO_NOSIGPIPE, &one, socklen_t(MemoryLayout<Int32>.size))
        let data = Wire.readAll(c, limit: 2 << 20)
        guard let d = data, let obj = try? JSONSerialization.jsonObject(with: d) as? [String: Any] else { close(c); return }
        if obj["ctl"] != nil {
            var reply: [String: Any] = [:]
            DispatchQueue.main.sync { reply = self.onControl?(obj) ?? [:] }
            let out = (try? JSONSerialization.data(withJSONObject: reply, options: [.prettyPrinted, .sortedKeys])) ?? Data("{}".utf8)
            _ = Wire.writeAll(c, out)
            close(c)
        } else {
            close(c)
            DispatchQueue.main.async { self.onEvent?(obj) }
        }
    }
}

/// Writes or removes GLORIA's entries in each agent's hook config, keeping everything else:
///   Claude Code  ~/.claude/settings.json   ($CLAUDE_CONFIG_DIR)
///   Codex        ~/.codex/hooks.json       ($CODEX_HOME; Codex asks once whether to trust them)
///   Cursor       ~/.cursor/hooks.json      (the editor and cursor-agent; its own flat format, "version": 1)
///   DSH          ~/.dsh/hooks.json         ($DSH_HOME; Claude Code's format, read by DSH's hooks bridge plugin)
enum HookInstaller {
    static let claudeEvents = ["SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "PostToolUseFailure", "Notification",
                               "Stop", "StopFailure", "SubagentStart", "SubagentStop", "PreCompact", "SessionEnd"]
    static let codexEvents = ["SessionStart", "UserPromptSubmit", "PreToolUse", "PermissionRequest", "PostToolUse", "Stop",
                              "SessionEnd", "PreCompact", "SubagentStart", "SubagentStop"]
    static let cursorEvents = ["sessionStart", "sessionEnd", "beforeSubmitPrompt", "preToolUse", "postToolUse", "postToolUseFailure",
                               "subagentStart", "subagentStop", "stop", "afterAgentResponse", "preCompact"]
    /// What DSH's bridges run (the official dsh-hooks-claude-code takes the first seven; the rest are ignored where unsupported).
    static let dshEvents = ["SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "Stop", "SubagentStart", "SubagentStop",
                            "PostToolUseFailure", "SessionEnd"]

    static func events(_ source: String) -> [String] {
        ["codex": codexEvents, "cursor": cursorEvents, "dsh": dshEvents][source] ?? claudeEvents
    }
    /// Cursor lists hooks per event directly; the others wrap them in matcher groups ({"hooks": [...]}).
    private static func flat(_ source: String) -> Bool { source == "cursor" }

    static func file(_ source: String) -> URL {
        let env = ProcessInfo.processInfo.environment
        func dir(_ k: String) -> URL? { env[k].flatMap { $0.isEmpty ? nil : URL(fileURLWithPath: $0) } }
        if source == "codex", let d = dir("CODEX_HOME") { return d.appendingPathComponent("hooks.json") }
        if source == "claude", let d = dir("CLAUDE_CONFIG_DIR") { return d.appendingPathComponent("settings.json") }
        if source == "dsh", let d = dir("DSH_HOME") { return d.appendingPathComponent("hooks.json") }
        let home = dir("GLORIA_HOOK_HOME") ?? FileManager.default.homeDirectoryForCurrentUser
        switch source {
        case "codex": return home.appendingPathComponent(".codex/hooks.json")
        case "cursor": return home.appendingPathComponent(".cursor/hooks.json")
        case "dsh": return home.appendingPathComponent(".dsh/hooks.json")
        default: return home.appendingPathComponent(".claude/settings.json")
        }
    }

    static var exe: String { Bundle.main.executablePath ?? CommandLine.arguments[0] }

    static func command(_ source: String) -> String {
        "GLORIA_HUSK=1 '" + exe.replacingOccurrences(of: "'", with: "'\\''") + "' --hook " + source
    }

    static func ours(_ cmd: String, _ source: String) -> Bool {
        cmd.hasPrefix("GLORIA_HUSK=1 ") && cmd.hasSuffix("--hook " + source)
    }

    /// The hook entries under one event, whichever format the file uses.
    private static func entries(_ value: Any?, _ source: String) -> [[String: Any]] {
        let list = value as? [[String: Any]] ?? []
        return flat(source) ? list : list.flatMap { ($0["hooks"] as? [[String: Any]]) ?? [] }
    }

    /// true when every event has exactly our current command.
    static func installed(_ source: String) -> Bool {
        guard let root = read(source).root, let hooks = root["hooks"] as? [String: Any] else { return false }
        let want = command(source)
        return events(source).allSatisfy { name in entries(hooks[name], source).contains { ($0["command"] as? String) == want } }
    }

    static func anyOurs(_ source: String) -> Bool {
        guard let root = read(source).root, let hooks = root["hooks"] as? [String: Any] else { return false }
        return hooks.values.contains { v in entries(v, source).contains { ours($0["command"] as? String ?? "", source) } }
    }

    private static func read(_ source: String) -> (root: [String: Any]?, raw: Data?) {
        let f = file(source)
        guard let d = try? Data(contentsOf: f) else { return ([:], nil) }
        if d.isEmpty { return ([:], d) }
        return ((try? JSONSerialization.jsonObject(with: d)) as? [String: Any], d)
    }

    enum Failure: Error, CustomStringConvertible {
        case invalid(String)
        var description: String { if case .invalid(let s) = self { return s }; return "" }
    }

    static func set(_ source: String, on: Bool) throws {
        if on && exe.contains("/AppTranslocation/") {
            throw Failure.invalid("请先把 GLORIA.app 移到“应用程序”或固定的文件夹里再打开，再开启化身。")
        }
        let f = file(source)
        let (rootOpt, raw) = read(source)
        guard var root = rootOpt else { throw Failure.invalid("\(f.path) 不是有效的 JSON，没有改动。") }
        if raw == nil && !on { return }
        if root["hooks"] != nil && !(root["hooks"] is [String: Any]) { throw Failure.invalid("\(f.path) 的 hooks 格式不对，没有改动。") }
        var hooks = root["hooks"] as? [String: Any] ?? [:]
        let before = (try? JSONSerialization.data(withJSONObject: root, options: [.sortedKeys])) ?? Data()

        // Take our old entries out (wherever the app was when it wrote them), leave everyone else's alone.
        for (name, value) in hooks {
            guard let list = value as? [[String: Any]] else { throw Failure.invalid("\(f.path) 的 \(name) 格式不对，没有改动。") }
            var kept: [[String: Any]] = []
            var removed = false
            for g in list {
                if flat(source) {
                    if ours(g["command"] as? String ?? "", source) { removed = true } else { kept.append(g) }
                    continue
                }
                let inner = g["hooks"] as? [[String: Any]] ?? []
                let other = inner.filter { !ours($0["command"] as? String ?? "", source) }
                if other.count == inner.count { kept.append(g); continue }
                removed = true
                if !other.isEmpty { var r = g; r["hooks"] = other; kept.append(r) }
            }
            if removed { if kept.isEmpty { hooks.removeValue(forKey: name) } else { hooks[name] = kept } }
        }
        if on {
            for name in events(source) {
                var list = hooks[name] as? [[String: Any]] ?? []
                var entry: [String: Any] = ["command": command(source), "timeout": source == "claude" ? 10 : 3]
                if source == "claude" { entry["async"] = true }
                if flat(source) {
                    list.append(entry)
                } else {
                    entry["type"] = "command"
                    list.append(["hooks": [entry]])
                }
                hooks[name] = list
            }
            if flat(source) && root["version"] == nil { root["version"] = 1 }
        }
        if hooks.isEmpty { root.removeValue(forKey: "hooks") } else { root["hooks"] = hooks }
        let after = (try? JSONSerialization.data(withJSONObject: root, options: [.sortedKeys])) ?? Data()
        if after == before { return }

        if let raw {
            let stamp = ISO8601DateFormatter().string(from: Date()).replacingOccurrences(of: ":", with: "-")
            try raw.write(to: Paths.backups.appendingPathComponent("\(source)-\(f.lastPathComponent).\(stamp)"))
        }
        try FileManager.default.createDirectory(at: f.deletingLastPathComponent(), withIntermediateDirectories: true)
        var out = try JSONSerialization.data(withJSONObject: root, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes])
        out.append(0x0A)
        try out.write(to: f, options: .atomic)
    }
}

/// Turns hook events into what she does and says.
final class HuskBrain {
    /// The pet standing in for an agent (see Agents), and every pet on screen.
    var petFor: (String) -> Pet? = { _ in nil }
    var pets: () -> [Pet] = { [] }
    struct Session { var source: String; var busy = false; var awaiting: String?; var lastTool = ""; var lastAt = now(); var reply = "" }
    private(set) var sessions: [String: Session] = [:]
    /// One agent waiting on another (Claude running `codex exec`, …), keyed by the caller's tool call.
    struct Link { let from: String; let to: String; let task: String; let at: Double }
    private var links: [String: Link] = [:]
    private var lastFailSay: Double = 0
    /// The app a computer-use session last picked with getApp(); later calls in the same REPL reuse it.
    private var cuaApp: [String: String] = [:]
    private var lastCaption: [String: Double] = [:]
    private(set) var events = 0

    init() {
        Timer.scheduledTimer(withTimeInterval: 30, repeats: true) { [weak self] _ in self?.expire() }
    }

    private func who(_ src: String) -> String { src == "claude" ? "Claude" : Agents.name(src) }
    private func tag(_ src: String) -> String { Agents.tag(src) }

    func handle(_ e: [String: Any]) {
        guard let name = e["hook_event_name"] as? String else { return }
        events += 1
        let src = e["_source"] as? String ?? "claude"
        guard let pet = petFor(src) else { return }
        let sid = e["session_id"] as? String ?? "default"
        var s = sessions[sid] ?? Session(source: src)
        s.lastAt = now()
        let w = who(src), tg = tag(src)
        let tool = e["tool_name"] as? String ?? ""
        let input = e["tool_input"] as? [String: Any] ?? [:]

        switch name {
        case "SessionStart":
            let why = e["source"] as? String ?? "startup"
            if let l = links.values.first(where: { $0.to == src && now() - $0.at < 90 }) {
                pet.play(.wave)
                pet.say("接到 \(who(l.from)) 的委托，开工～", tag: tg, duration: 3)
            } else if why == "startup" || why == "resume" {
                pet.play(.wave, loops: 2)
                pet.say(why == "resume" ? "\(w) 回来了，继续干活～" : "\(w) 上线啦，我来陪你写代码～", tag: tg, duration: 3.5)
            }
        case "UserPromptSubmit":
            s.busy = true
            s.awaiting = nil
            pet.play(.jump)
            let p = (e["prompt"] as? String).map { cap($0, 26) } ?? ""
            pet.say(p.isEmpty ? "\(w) 接活了！" : "\(w) 接活了：「\(p)」", tag: tg, duration: 3.2)
        case "PreToolUse":
            s.busy = true
            s.lastTool = describe(tool, input)
            if let cu = ComputerUse.parse(source: src, tool: tool, input: input) {
                s.lastTool = "操作电脑"
                if let a = cu.app { cuaApp[sid] = a }
                computerUse(pet, src, cu.app ?? cuaApp[sid], cu.acts)
            } else if let to = AgentCalls.callee(from: src, tool: tool, input: input) {
                link(from: src, to: to, id: e["tool_use_id"] as? String ?? "\(sid):\(now())", task: AgentCalls.task(input))
            } else if tool != "Task" && tool != "Agent" && now() - (lastCaption[src] ?? 0) > 0.6 {
                lastCaption[src] = now()
                pet.say(s.lastTool, kind: .caption, duration: 2.6)
            }
        case "PostToolUse":
            s.awaiting = nil
            // Desktop computer use moved the real pointer: the click landed where it is now.
            if ComputerUse.movesPointer(tool) { Effects.ripple(at: NSEvent.mouseLocation) }
            let bad = e["error"].map { !($0 is NSNull) && "\($0)".count > 0 } ?? false
            if !unlink(src, e["tool_use_id"] as? String, failed: bad) && bad { failed(pet, w, tg, tool) }
        case "PostToolUseFailure":
            s.awaiting = nil
            let interrupted = (e["is_interrupt"] as? Bool) == true
            if !unlink(src, e["tool_use_id"] as? String, failed: !interrupted) && !interrupted { failed(pet, w, tg, tool) }
        case "Notification":
            let type = e["notification_type"] as? String ?? ""
            let msg = e["message"] as? String ?? ""
            if type == "permission_prompt" || msg.lowercased().contains("permission") {
                let what = s.lastTool.isEmpty ? cap(msg, 60) : asking(s.lastTool)
                s.awaiting = what
                pet.say("\(w) 需要你批准：\(what)", tag: tg, kind: .alert, sticky: true)
                if Prefs.chime { NSSound(named: "Glass")?.play() }
            } else if type == "idle_prompt" {
                pet.play(.waiting, loops: 2)
                pet.say("\(w) 在等你回话～", tag: tg, duration: 4)
            } else if type == "agent_needs_input" || type == "elicitation_dialog" {
                s.awaiting = cap(msg.isEmpty ? "需要你回答" : msg, 60)
                pet.say("\(w) 有问题想问你：\(s.awaiting!)", tag: tg, kind: .alert, sticky: true)
                if Prefs.chime { NSSound(named: "Glass")?.play() }
            } else if !msg.isEmpty && type != "auth_success" {
                pet.say(cap(msg, 80), tag: tg, duration: 4)
            }
        case "AgentResponse":
            // Cursor sends the reply on its own, before stop.
            s.reply = e["last_assistant_message"] as? String ?? s.reply
        case "Stop":
            s.busy = false
            s.awaiting = nil
            done(pet, w, tg, e["last_assistant_message"] as? String ?? s.reply)
            s.reply = ""
        case "StopFailure":
            s.busy = false
            s.awaiting = nil
            pet.retireClone(nil)
            pet.play(.failed)
            pet.say("\(w) 出错停下了……", tag: tg, duration: 4)
        case "SubagentStart":
            let id = e["agent_id"] as? String ?? UUID().uuidString
            pet.spawnClone(id)
            let kind = e["agent_type"] as? String ?? ""
            pet.say(kind.isEmpty ? "派出分身！" : "派出分身：\(kind)", kind: .caption, duration: 2.4)
        case "SubagentStop":
            pet.retireClone(e["agent_id"] as? String)
        case "PreCompact":
            pet.glitch(1.1)
            pet.say("记忆压缩中……刚才的细节要模糊啦。", tag: tg, duration: 3.2)
        case "SessionEnd":
            let wasBusy = s.busy
            s.busy = false
            s.awaiting = nil
            pet.retireClone(nil)
            sessions.removeValue(forKey: sid)
            cuaApp.removeValue(forKey: sid)
            links = links.filter { $0.value.from != src || now() - $0.value.at < 5 }
            refresh()
            // cursor-agent -p sends no stop: the end of a busy session is the end of the job.
            let status = (e["final_status"] as? String) ?? ""
            if wasBusy && src == "cursor" && !status.isEmpty {
                if status == "completed" { done(pet, w, tg, s.reply) } else {
                    pet.play(.failed); pet.say(status == "aborted" ? "\(w) 被叫停了。" : "\(w) 出错停下了……", tag: tg, duration: 4)
                }
            } else if (e["reason"] as? String) != "clear" { pet.play(.wave, loops: 2); pet.say("\(w) 下线了，拜拜～", tag: tg, duration: 3) }
            return
        default: break
        }
        sessions[sid] = s
        refresh()
    }

    // MARK: Computer use

    /// Play each step of a computer-use call in turn, on the window it works in.
    private func computerUse(_ pet: Pet, _ src: String, _ app: String?, _ acts: [CUAction]) {
        let w = ComputerUse.window(for: app)
        for (k, a) in acts.enumerated() {
            DispatchQueue.main.asyncAfter(deadline: .now() + Double(k) * 0.85) { [weak pet] in
                guard let pet else { return }
                let p = a.point.flatMap { pt in w.map { ComputerUse.screenPoint(pt, in: $0) } }
                pet.act(a, window: w, at: p, tag: self.tag(src))
            }
        }
    }

    /// Codex drives the biggest window on screen through cua_repl: look, click, type, scroll, drag, press a key.
    func demoComputerUse() {
        let wins = Desktop.scan(excluding: getpid()).wins
        guard let w = wins.filter({ $0.rect.width > 500 && $0.rect.height > 300 }).max(by: { $0.rect.width * $0.rect.height < $1.rect.width * $1.rect.height }) ?? wins.first,
              let app = NSRunningApplication(processIdentifier: w.pid) else { return }
        let id = app.bundleIdentifier ?? app.bundleURL?.path ?? w.owner
        let W = Int(w.rect.width), H = Int(w.rect.height)
        let code: [(Double, String)] = [
            (0.0, "let app = await cua.getApp('\(id)'); await app.getScreenshot();"),
            (1.8, "await app.click([\(W * 3 / 10), \(H * 3 / 4)]); await app.getAXState();"),
            (4.2, "await app.typeText('GLORIA 在帮 Codex 打字');"),
            (7.0, "await app.scroll([\(W / 2), \(H * 3 / 4)], {deltaY: 600});"),
            (9.4, "await app.drag([\(W * 2 / 5), \(H * 4 / 5)], [\(W * 7 / 10), \(H * 4 / 5)]);"),
            (11.8, "await app.pressKey('super+s'); await app.getScreenshot();"),
        ]
        var steps: [(Double, [String: Any])] = [(0, ["hook_event_name": "UserPromptSubmit", "prompt": "帮我在 \(w.owner) 里点一下、打几个字、截个图"])]
        for (at, c) in code {
            steps.append((at + 1.2, ["hook_event_name": "PreToolUse", "tool_name": "mcp__cua_repl__js", "tool_input": ["code": c]]))
        }
        steps.append((15.6, ["hook_event_name": "Stop", "last_assistant_message": "在 \(w.owner) 里点了、打了字、截了图。"]))
        for (at, e) in steps {
            var ev = e
            ev["session_id"] = "codex:demo-cua"
            ev["_source"] = "codex"
            DispatchQueue.main.asyncAfter(deadline: .now() + at) { [weak self] in self?.handle(ev) }
        }
    }

    // MARK: Agents calling agents

    private func link(from: String, to: String, id: String, task: String) {
        links[id] = Link(from: from, to: to, task: task, at: now())
        // The callee looks busy for as long as the caller waits on it, even if its own hooks aren't installed.
        sessions["link:\(id)"] = Session(source: to, busy: true)
        let ask = task.isEmpty ? "\(who(to))，这个交给你啦～" : "\(who(to))，帮我：「\(task)」"
        guard let a = petFor(from), let b = petFor(to), a !== b else {
            petFor(from)?.say("请 \(who(to)) 帮忙\(task.isEmpty ? "" : "：「\(task)」")", kind: .caption, duration: 3)
            return
        }
        a.passNote(to: b, say: ask, tag: tag(from), note: "收到 \(who(from)) 的委托！", noteTag: tag(to))
    }

    /// The caller's tool call came back: the callee hands the result over. Returns false when it wasn't a link.
    @discardableResult
    private func unlink(_ src: String, _ id: String?, failed: Bool) -> Bool {
        // Match by the tool call's id; only an event without one falls back to this agent's latest call.
        let key = id.map { links[$0] != nil ? $0 : nil }
            ?? links.filter { $0.value.from == src }.max { $0.value.at < $1.value.at }?.key
        guard let key, let l = links.removeValue(forKey: key) else { return false }
        sessions.removeValue(forKey: "link:\(key)")
        refresh()
        guard let a = petFor(l.from), let b = petFor(l.to), a !== b else { return true }
        b.passNote(to: a, say: failed ? "这个没搞定……" : "做好啦，交给你～", tag: tag(l.to),
                   note: failed ? "没关系，我来想办法。" : "收到 \(who(l.to)) 的结果，谢啦！", noteTag: tag(l.from))
        if failed { b.play(.failed) }
        return true
    }

    /// The job is done: look it over, wave, and say what came of it.
    private func done(_ pet: Pet, _ w: String, _ tg: String, _ reply: String) {
        pet.retireClone(nil)
        pet.play(.review)
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.1) { [weak pet] in pet?.play(.wave, loops: 2) }
        let summary = gist(reply)
        pet.say(summary.isEmpty ? "\(w) 做完啦！" : "做完啦！\(summary)", tag: tg, duration: summary.isEmpty ? 3 : 7)
        pet.sparkle("✦", color: Palette.butter, count: 3)
    }

    private func failed(_ pet: Pet, _ w: String, _ tg: String, _ tool: String) {
        pet.play(.failed)
        if now() - lastFailSay > 8 {
            lastFailSay = now()
            pet.say("\(w) 踩了个坑\(tool.isEmpty ? "" : "（\(tool)）")，在想办法。", tag: tg, duration: 3)
        }
    }

    func resync() { refresh() }
    func clear() { sessions.removeAll(); links.removeAll(); refresh() }

    private func refresh() {
        for pet in pets() {
            let mine = sessions.values.filter { petFor($0.source) === pet }
            pet.working = mine.contains { $0.busy }
            let wait = mine.first { $0.awaiting != nil }
            let was = pet.awaiting
            pet.awaiting = wait?.awaiting
            pet.awaitingTag = wait.map { tag($0.source) } ?? "CLAUDE CODE"
            if was != nil && pet.awaiting == nil && pet.bubble.sticky { pet.bubble.unstick() }
        }
    }

    private func expire() {
        let t = now()
        for (k, s) in sessions where t - s.lastAt > 20 * 60 { sessions.removeValue(forKey: k) }
        for (k, l) in links where t - l.at > 30 * 60 { links.removeValue(forKey: k); sessions.removeValue(forKey: "link:\(k)") }
        refresh()
    }

    func describe(_ tool: String, _ input: [String: Any]) -> String {
        func leaf(_ p: Any?) -> String {
            if let a = p as? [Any] { return a.prefix(3).map { leaf($0) }.joined(separator: "、") }
            return ((p as? String) ?? "").split(separator: "/").last.map(String.init) ?? ""
        }
        let file = leaf(input["file_path"] ?? input["file_paths"] ?? input["notebook_path"] ?? input["path"])
        switch tool {
        case "Read": return file.isEmpty ? "在看文件" : "在看 \(file)"
        case "Edit", "Write", "MultiEdit", "NotebookEdit": return file.isEmpty ? "在改文件" : "在改 \(file)"
        case "Bash": return "在跑 \(cap((input["command"] as? String) ?? "命令", 44))"
        case "Grep": return "在搜 \(cap((input["pattern"] as? String) ?? "", 30))"
        case "Glob": return "在找文件 \(cap((input["pattern"] as? String) ?? "", 30))"
        case "WebFetch": return "在读网页"
        case "WebSearch": return "在搜网页：\(cap((input["query"] as? String) ?? "", 30))"
        case "Task", "Agent": return "派出分身"
        case "TodoWrite": return "在排计划"
        case "Delete": return file.isEmpty ? "在删文件" : "在删 \(file)"
        case "run_code": return "在跑一段代码"
        default:
            if tool.hasPrefix("mcp__") { return "在用 \(tool.split(separator: "_").last.map(String.init) ?? tool)" }
            return tool.isEmpty ? "在忙" : "在用 \(tool)"
        }
    }

    /// "在跑 npm test" → "运行 npm test", for approval prompts.
    func asking(_ caption: String) -> String {
        let swaps = [("在跑 ", "运行 "), ("在改 ", "修改 "), ("在看 ", "读取 "), ("在搜网页：", "搜索网页："), ("在搜 ", "搜索 "),
                     ("在找文件 ", "查找文件 "), ("在读网页", "读取网页"), ("在用 ", "使用 "), ("在排计划", "更新计划"), ("在改文件", "修改文件")]
        for (a, b) in swaps where caption.hasPrefix(a) { return b + caption.dropFirst(a.count) }
        return caption
    }

    /// First meaningful line of the reply, without markdown.
    func gist(_ text: String) -> String {
        for raw in text.split(separator: "\n") {
            var l = raw.trimmingCharacters(in: .whitespaces)
            l = l.replacingOccurrences(of: "**", with: "").replacingOccurrences(of: "`", with: "")
            while let f = l.first, "#>-*• ".contains(f) { l.removeFirst() }
            if l.count >= 4 { return cap(l, 70) }
        }
        return ""
    }

    var summary: String {
        if let s = sessions.values.first(where: { $0.awaiting != nil }) { return "\(who(s.source)) 等你批准" }
        // "Claude、Cursor 工作中", with ×n when one agent runs several sessions at once.
        let busy = sessions.values.filter { $0.busy }.map(\.source)
        if !busy.isEmpty {
            let parts = Agents.ids.compactMap { id -> String? in
                let n = busy.filter { $0 == id }.count
                return n == 0 ? nil : who(id) + (n > 1 ? " ×\(n)" : "")
            }
            return parts.joined(separator: "、") + " 工作中"
        }
        return sessions.isEmpty ? "空闲" : "待命"
    }

    /// Claude hands a job to Codex and gets the answer back, so you can see the two pets talk.
    func demoTogether() {
        let steps: [(Double, [String: Any])] = [
            (0.0, ["hook_event_name": "UserPromptSubmit", "prompt": "让 Codex 帮忙画一张角色设定图", "_source": "claude", "session_id": "demo-c"]),
            (2.2, ["hook_event_name": "PreToolUse", "tool_name": "Bash", "tool_use_id": "demo-call",
                   "tool_input": ["command": "codex exec --skip-git-repo-check \"画一张 Doro 抱着欧润吉的设定图\""], "_source": "claude", "session_id": "demo-c"]),
            (4.4, ["hook_event_name": "SessionStart", "source": "startup", "_source": "codex", "session_id": "codex:demo-x"]),
            (5.6, ["hook_event_name": "PreToolUse", "tool_name": "Bash", "tool_input": ["command": "python3 draw.py --doro"], "_source": "codex", "session_id": "codex:demo-x"]),
            (8.6, ["hook_event_name": "Stop", "last_assistant_message": "设定图画好了。", "_source": "codex", "session_id": "codex:demo-x"]),
            (9.4, ["hook_event_name": "PostToolUse", "tool_name": "Bash", "tool_use_id": "demo-call", "_source": "claude", "session_id": "demo-c"]),
            (13.5, ["hook_event_name": "Stop", "last_assistant_message": "Codex 画好了设定图，我放进项目里了。", "_source": "claude", "session_id": "demo-c"]),
        ]
        for (at, ev) in steps {
            DispatchQueue.main.asyncAfter(deadline: .now() + at) { [weak self] in self?.handle(ev) }
        }
    }

    /// A scripted run so you can see every reaction without a real session.
    func demo() {
        let steps: [(Double, [String: Any])] = [
            (0.0, ["hook_event_name": "SessionStart", "source": "startup"]),
            (2.6, ["hook_event_name": "UserPromptSubmit", "prompt": "把桌宠做成 mac 版，能坐在窗口上跟着音乐跳舞"]),
            (4.6, ["hook_event_name": "PreToolUse", "tool_name": "Read", "tool_input": ["file_path": "/repo/Sources/Pet.swift"]]),
            (6.0, ["hook_event_name": "PreToolUse", "tool_name": "Edit", "tool_input": ["file_path": "/repo/Sources/Music.swift"]]),
            (7.2, ["hook_event_name": "SubagentStart", "agent_id": "demo-a", "agent_type": "Explore"]),
            (8.4, ["hook_event_name": "PreToolUse", "tool_name": "Bash", "tool_input": ["command": "bash build.sh"]]),
            (9.2, ["hook_event_name": "Notification", "notification_type": "permission_prompt", "message": "Claude needs your permission to use Bash"]),
            (13.5, ["hook_event_name": "PostToolUseFailure", "tool_name": "Bash", "error": "exit 1"]),
            (15.5, ["hook_event_name": "SubagentStop", "agent_id": "demo-a"]),
            (16.5, ["hook_event_name": "PreCompact", "trigger": "auto"]),
            (19.5, ["hook_event_name": "Stop", "last_assistant_message": "## 完成\n桌宠已经能坐在窗口上、跟着音乐跳舞，还会替 Claude 喊你批准。"]),
        ]
        for (at, e) in steps {
            var ev = e
            ev["session_id"] = "demo"
            ev["_source"] = "claude"
            DispatchQueue.main.asyncAfter(deadline: .now() + at) { [weak self] in self?.handle(ev) }
        }
    }
}
