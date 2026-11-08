import AppKit
import Darwin

// Code / Husk mode, modelled on Capri's: Claude Code and Codex call `GLORIA --hook claude|codex` from their own hooks.
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
                       "source", "reason", "trigger", "stop_hook_active"]
    static let inputKeep = ["file_path", "file_paths", "path", "command", "cmd", "pattern", "url", "query", "description",
                            "subagent_type", "notebook_path", "questions"]

    /// `GLORIA --hook claude|codex`: forward stdin's event and exit 0 quickly, printing nothing.
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
            o["tool_input"] = i
        }
        if let r = e["last_assistant_message"] as? String { o["last_assistant_message"] = String(r.prefix(1200)) }
        return o
    }

    /// Codex names things differently; translate to Claude's shapes (after Capri's adapter).
    static func adapt(_ e: [String: Any], source: String) -> [String: Any]? {
        guard e["hook_event_name"] is String else { return nil }
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

/// Writes or removes GLORIA's entries in ~/.claude/settings.json and ~/.codex/hooks.json, keeping everything else.
enum HookInstaller {
    static let claudeEvents = ["SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "PostToolUseFailure", "Notification",
                               "Stop", "StopFailure", "SubagentStart", "SubagentStop", "PreCompact", "SessionEnd"]
    static let codexEvents = ["SessionStart", "UserPromptSubmit", "PreToolUse", "PermissionRequest", "PostToolUse", "Stop",
                              "SessionEnd", "PreCompact", "SubagentStart", "SubagentStop"]

    static func file(_ source: String) -> URL {
        let env = ProcessInfo.processInfo.environment
        if source == "codex", let dir = env["CODEX_HOME"], !dir.isEmpty { return URL(fileURLWithPath: dir).appendingPathComponent("hooks.json") }
        if source == "claude", let dir = env["CLAUDE_CONFIG_DIR"], !dir.isEmpty { return URL(fileURLWithPath: dir).appendingPathComponent("settings.json") }
        let home = env["GLORIA_HOOK_HOME"].map { URL(fileURLWithPath: $0) } ?? FileManager.default.homeDirectoryForCurrentUser
        return source == "codex" ? home.appendingPathComponent(".codex/hooks.json") : home.appendingPathComponent(".claude/settings.json")
    }

    static var exe: String { Bundle.main.executablePath ?? CommandLine.arguments[0] }

    static func command(_ source: String) -> String {
        "GLORIA_HUSK=1 '" + exe.replacingOccurrences(of: "'", with: "'\\''") + "' --hook " + source
    }

    static func ours(_ cmd: String, _ source: String) -> Bool {
        cmd.hasPrefix("GLORIA_HUSK=1 ") && cmd.hasSuffix("--hook " + source)
    }

    /// true when every event has exactly our current command.
    static func installed(_ source: String) -> Bool {
        guard let root = read(source).root, let hooks = root["hooks"] as? [String: Any] else { return false }
        let want = command(source)
        for name in source == "codex" ? codexEvents : claudeEvents {
            let groups = hooks[name] as? [[String: Any]] ?? []
            let found = groups.contains { g in ((g["hooks"] as? [[String: Any]]) ?? []).contains { ($0["command"] as? String) == want } }
            if !found { return false }
        }
        return true
    }

    static func anyOurs(_ source: String) -> Bool {
        guard let root = read(source).root, let hooks = root["hooks"] as? [String: Any] else { return false }
        for (_, v) in hooks {
            for g in v as? [[String: Any]] ?? [] {
                for h in g["hooks"] as? [[String: Any]] ?? [] where ours(h["command"] as? String ?? "", source) { return true }
            }
        }
        return false
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

        for (name, value) in hooks {
            guard let groups = value as? [[String: Any]] else { throw Failure.invalid("\(f.path) 的 \(name) 格式不对，没有改动。") }
            var kept: [[String: Any]] = []
            var removed = false
            for g in groups {
                let entries = g["hooks"] as? [[String: Any]] ?? []
                let other = entries.filter { !ours($0["command"] as? String ?? "", source) }
                if other.count == entries.count { kept.append(g); continue }
                removed = true
                if !other.isEmpty { var r = g; r["hooks"] = other; kept.append(r) }
            }
            if removed { if kept.isEmpty { hooks.removeValue(forKey: name) } else { hooks[name] = kept } }
        }
        if on {
            for name in source == "codex" ? codexEvents : claudeEvents {
                var groups = hooks[name] as? [[String: Any]] ?? []
                var entry: [String: Any] = ["type": "command", "command": command(source), "timeout": source == "codex" ? 3 : 10]
                if source == "claude" { entry["async"] = true }
                groups.append(["hooks": [entry]])
                hooks[name] = groups
            }
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
    weak var pet: Pet?
    struct Session { var source: String; var busy = false; var awaiting: String?; var lastTool = ""; var lastAt = now() }
    private(set) var sessions: [String: Session] = [:]
    private var lastFailSay: Double = 0
    private var lastCaption: Double = 0
    private(set) var events = 0

    init(pet: Pet) {
        self.pet = pet
        Timer.scheduledTimer(withTimeInterval: 30, repeats: true) { [weak self] _ in self?.expire() }
    }

    private func who(_ src: String) -> String { src == "codex" ? "Codex" : "Claude" }
    private func tag(_ src: String) -> String { src == "codex" ? "CODEX" : "CLAUDE CODE" }

    func handle(_ e: [String: Any]) {
        guard let pet, let name = e["hook_event_name"] as? String else { return }
        events += 1
        let src = e["_source"] as? String ?? "claude"
        let sid = e["session_id"] as? String ?? "default"
        var s = sessions[sid] ?? Session(source: src)
        s.lastAt = now()
        let w = who(src), tg = tag(src)
        let tool = e["tool_name"] as? String ?? ""
        let input = e["tool_input"] as? [String: Any] ?? [:]

        switch name {
        case "SessionStart":
            let why = e["source"] as? String ?? "startup"
            if why == "startup" || why == "resume" {
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
            if tool != "Task" && tool != "Agent" && now() - lastCaption > 0.6 {
                lastCaption = now()
                pet.say(s.lastTool, kind: .caption, duration: 2.6)
            }
        case "PostToolUse":
            s.awaiting = nil
            if let err = e["error"], !(err is NSNull), "\(err)".count > 0 { failed(pet, w, tg, tool) }
        case "PostToolUseFailure":
            s.awaiting = nil
            if (e["is_interrupt"] as? Bool) != true { failed(pet, w, tg, tool) }
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
        case "Stop":
            s.busy = false
            s.awaiting = nil
            pet.retireClone(nil)
            pet.play(.review)
            DispatchQueue.main.asyncAfter(deadline: .now() + 1.1) { [weak pet] in pet?.play(.wave, loops: 2) }
            let summary = gist(e["last_assistant_message"] as? String ?? "")
            pet.say(summary.isEmpty ? "\(w) 做完啦！" : "做完啦！\(summary)", tag: tg, duration: summary.isEmpty ? 3 : 7)
            pet.sparkle("✦", color: Palette.butter, count: 3)
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
            s.busy = false
            s.awaiting = nil
            pet.retireClone(nil)
            sessions.removeValue(forKey: sid)
            refresh()
            if (e["reason"] as? String) != "clear" { pet.play(.wave, loops: 2); pet.say("\(w) 下线了，拜拜～", tag: tg, duration: 3) }
            return
        default: break
        }
        sessions[sid] = s
        refresh()
    }

    private func failed(_ pet: Pet, _ w: String, _ tg: String, _ tool: String) {
        pet.play(.failed)
        if now() - lastFailSay > 8 {
            lastFailSay = now()
            pet.say("\(w) 踩了个坑\(tool.isEmpty ? "" : "（\(tool)）")，在想办法。", tag: tg, duration: 3)
        }
    }

    func resync() { refresh() }
    func clear() { sessions.removeAll(); refresh() }

    private func refresh() {
        guard let pet else { return }
        pet.working = sessions.values.contains { $0.busy }
        let wait = sessions.values.first { $0.awaiting != nil }
        let was = pet.awaiting
        pet.awaiting = wait?.awaiting
        pet.awaitingTag = wait.map { tag($0.source) } ?? "CLAUDE CODE"
        if was != nil && pet.awaiting == nil && pet.bubble.sticky { pet.bubble.unstick() }
    }

    private func expire() {
        let t = now()
        for (k, s) in sessions where t - s.lastAt > 20 * 60 { sessions.removeValue(forKey: k) }
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
        let busy = sessions.values.filter { $0.busy }
        if let b = busy.first { return "\(who(b.source)) 工作中" + (busy.count > 1 ? " ×\(busy.count)" : "") }
        return sessions.isEmpty ? "空闲" : "待命"
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
