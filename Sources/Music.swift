import AppKit
import CoreAudio
import AudioToolbox

// Music detection without any permission: Core Audio lists which apps are sending sound to an output right now.
// Beat tracking (optional): a private Core Audio process tap on system output, analysed in memory and thrown away.

enum AudioProcs {
    struct Proc { let pid: pid_t; let bundle: String }

    private static func addr(_ sel: AudioObjectPropertySelector) -> AudioObjectPropertyAddress {
        AudioObjectPropertyAddress(mSelector: sel, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
    }

    static func u32(_ obj: AudioObjectID, _ sel: AudioObjectPropertySelector) -> UInt32? {
        var a = addr(sel)
        var v: UInt32 = 0
        var size = UInt32(MemoryLayout<UInt32>.size)
        return AudioObjectGetPropertyData(obj, &a, 0, nil, &size, &v) == noErr ? v : nil
    }

    static func string(_ obj: AudioObjectID, _ sel: AudioObjectPropertySelector) -> String? {
        var a = addr(sel)
        var ref: Unmanaged<CFString>? = nil
        var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        guard AudioObjectGetPropertyData(obj, &a, 0, nil, &size, &ref) == noErr, let r = ref else { return nil }
        return r.takeRetainedValue() as String
    }

    static func outputting() -> [Proc] {
        var a = addr(kAudioHardwarePropertyProcessObjectList)
        var size: UInt32 = 0
        let sys = AudioObjectID(kAudioObjectSystemObject)
        guard AudioObjectGetPropertyDataSize(sys, &a, 0, nil, &size) == noErr, size > 0 else { return [] }
        var ids = [AudioObjectID](repeating: 0, count: Int(size) / MemoryLayout<AudioObjectID>.size)
        guard AudioObjectGetPropertyData(sys, &a, 0, nil, &size, &ids) == noErr else { return [] }
        var out: [Proc] = []
        let me = getpid()
        for id in ids {
            guard u32(id, kAudioProcessPropertyIsRunningOutput) == 1 else { continue }
            var pidAddr = addr(kAudioProcessPropertyPID)
            var pid: pid_t = 0
            var ps = UInt32(MemoryLayout<pid_t>.size)
            AudioObjectGetPropertyData(id, &pidAddr, 0, nil, &ps, &pid)
            if pid == me { continue }
            out.append(Proc(pid: pid, bundle: string(id, kAudioProcessPropertyBundleID) ?? ""))
        }
        return out
    }

    static func defaultOutputUID() -> String? {
        var a = addr(kAudioHardwarePropertyDefaultOutputDevice)
        var dev = AudioObjectID(0)
        var size = UInt32(MemoryLayout<AudioObjectID>.size)
        guard AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &a, 0, nil, &size, &dev) == noErr else { return nil }
        return string(dev, kAudioDevicePropertyDeviceUID)
    }
}

/// Low-band onset flux → tempo by autocorrelation → a beat clock nudged by onsets. Runs on the tap's queue.
final class BeatTracker {
    struct State {
        var bpm: Double = 0
        var confidence: Double = 0
        var lastBeat: Double = 0
        var period: Double = 0.52
        var beats = 0
        var level: Double = 0          // smoothed loudness, 0…1-ish
        var lastSound: Double = 0      // last time anything non-silent arrived
        var lastData: Double = 0       // last time the tap delivered a buffer at all
    }

    var sampleRate: Double = 48000
    private let lock = NSLock()
    private var pub = State()

    private var lp: Float = 0
    private var lowAcc: Float = 0, fullAcc: Float = 0, n = 0
    private var prevLog: Float = -100
    private static let ring = 600                 // 6 s at 100 hops/s
    private var flux = [Float](repeating: 0, count: BeatTracker.ring)
    private var hops = 0
    private var nextBeat: Double = -1             // tracker time, seconds
    private var period: Double = 0.52
    private var confidence: Double = 0
    private var lastOnset: Double = -10
    private var level: Double = 0
    private var stable: Double = 0
    private var lastEstimate: Double = 0
    var debug: (Float, Float, Float) = (0, 0, 0)

    func snapshot() -> State { lock.lock(); defer { lock.unlock() }; return pub }

    func ingest(_ list: UnsafePointer<AudioBufferList>) {
        let abl = UnsafeMutableAudioBufferListPointer(UnsafeMutablePointer(mutating: list))
        guard let first = abl.first, let data = first.mData else { return }
        let hop = max(64, Int(sampleRate / 100))
        let alpha = Float(1 - exp(-2 * Double.pi * 160 / sampleRate))
        let t0 = CACurrentMediaTime()
        var silent = true
        if abl.count >= 2, first.mNumberChannels == 1, let d2 = abl[1].mData {
            let a = data.assumingMemoryBound(to: Float.self), b = d2.assumingMemoryBound(to: Float.self)
            let frames = Int(first.mDataByteSize) / 4
            for i in 0..<frames { if feed((a[i] + b[i]) * 0.5, hop: hop, alpha: alpha, t0: t0) { silent = false } }
        } else {
            let ch = max(1, Int(first.mNumberChannels))
            let p = data.assumingMemoryBound(to: Float.self)
            let frames = Int(first.mDataByteSize) / 4 / ch
            for i in 0..<frames {
                var s: Float = 0
                for c in 0..<ch { s += p[i * ch + c] }
                if feed(s / Float(ch), hop: hop, alpha: alpha, t0: t0) { silent = false }
            }
        }
        lock.lock()
        pub.lastData = t0
        if !silent { pub.lastSound = t0 }
        lock.unlock()
    }

    /// Returns true when the sample is audible.
    private func feed(_ x: Float, hop: Int, alpha: Float, t0: Double) -> Bool {
        lp += alpha * (x - lp)
        lowAcc += lp * lp
        fullAcc += x * x
        n += 1
        if n >= hop { endHop(t0: t0, hop: hop) }
        return abs(x) > 1e-4
    }

    private func endHop(t0: Double, hop: Int) {
        let low = lowAcc / Float(n), full = fullAcc / Float(n)
        lowAcc = 0; fullAcc = 0; n = 0
        let lg = 10 * log10f(low + 1e-10)
        let f = max(0, lg - prevLog)
        prevLog = lg
        let idx = hops % BeatTracker.ring
        flux[idx] = f > 30 ? 0 : f                    // first sound after silence isn't an onset
        hops += 1
        let t = Double(hops) / 100
        let rms = Double(sqrt(full))
        level += (min(1, rms * 4) - level) * (rms * 4 > level ? 0.25 : 0.02)

        if hops % 50 == 0 && hops >= 300 { estimateTempo() }

        // Onset: local flux peak well above the recent average.
        let recent = 100
        if hops > recent {
            var mean: Float = 0, sq: Float = 0
            for k in 1...recent { let v = flux[(hops - 1 - k + BeatTracker.ring * 4) % BeatTracker.ring]; mean += v; sq += v * v }
            mean /= Float(recent)
            let sd = sqrt(max(0, sq / Float(recent) - mean * mean))
            if f > mean + 1.6 * sd && f > 1.2 && t - lastOnset > 0.18 {
                lastOnset = t
                if confidence > 0.25 && nextBeat > 0 {
                    // Pull the clock toward onsets that land near a predicted beat.
                    var err = t - nextBeat
                    if err < -period / 2 { err += period }
                    if err > period / 2 { err -= period }
                    if abs(err) < period * 0.3 { nextBeat += err * 0.22 }
                } else if nextBeat < 0 || t - nextBeat > period * 2 {
                    nextBeat = t
                }
            }
        }

        if nextBeat > 0 && t >= nextBeat {
            let late = t - nextBeat
            nextBeat += period
            if t - nextBeat > period * 4 { nextBeat = t + period }
            lock.lock()
            pub.beats += 1
            pub.lastBeat = t0 - late
            pub.period = period
            pub.bpm = 60 / period
            pub.confidence = confidence
            pub.level = level
            lock.unlock()
        } else if hops % 10 == 0 {
            lock.lock(); pub.level = level; pub.confidence = confidence; lock.unlock()
        }
    }

    private func estimateTempo() {
        let span = 400
        var x = [Float](repeating: 0, count: span)
        var mean: Float = 0
        for i in 0..<span { let v = flux[(hops - span + i + BeatTracker.ring * 4) % BeatTracker.ring]; x[i] = v; mean += v }
        mean /= Float(span)
        for i in 0..<span { x[i] -= mean }
        var energy: Float = 0
        for v in x { energy += v * v }
        guard energy > 1e-3 else { confidence *= 0.7; return }
        var best = -Float.greatestFiniteMagnitude, bestLag = 0
        var scores = [Float](repeating: 0, count: 100)
        for lag in 33...92 {                          // 182…65 BPM
            var s: Float = 0
            for i in lag..<span { s += x[i] * x[i - lag] }
            let bpm = 6000 / Double(lag)
            let prior = Float(exp(-0.5 * pow(log2(bpm / 118) / 0.75, 2)))
            s *= (0.55 + 0.45 * prior)
            scores[lag] = s
            if s > best { best = s; bestLag = lag }
        }
        guard bestLag > 33 && bestLag < 92 else { return }
        let a = scores[bestLag - 1], b = scores[bestLag], c = scores[bestLag + 1]
        let denom = a - 2 * b + c
        let shift = denom != 0 ? Double(0.5 * (a - c) / denom) : 0
        let p = (Double(bestLag) + clamp(shift, -0.5, 0.5)) / 100
        // Periodicity: a real beat repeats at one and two periods; speech doesn't.
        func r(_ lag: Int) -> Float {
            guard lag < span - 40 else { return 0 }
            var s: Float = 0
            for i in lag..<span { s += x[i] * x[i - lag] }
            return s / energy
        }
        let r1 = r(bestLag), r2 = r(bestLag * 2)
        let periodic = Double(max(0, r1) * 0.5 + max(0, r2) * 0.5)
        if abs(p - lastEstimate) / p < 0.04 { stable = min(1, stable + 0.25) } else { stable = max(0, stable - 0.4) }
        lastEstimate = p
        debug = (r1, r2, Float(stable))
        let target = clamp((periodic - 0.06) * 5, 0, 1) * (0.35 + 0.65 * stable)
        confidence = confidence * 0.55 + target * 0.45
        if abs(p - period) / period < 0.06 { period = period * 0.7 + p * 0.3 } else if confidence > 0.3 || nextBeat < 0 || stable > 0.5 { period = p }
        if nextBeat < 0 { nextBeat = Double(hops) / 100 + period }
    }
}

@available(macOS 14.2, *)
final class SystemTap {
    private var tapID = AudioObjectID(kAudioObjectUnknown)
    private var aggID = AudioObjectID(kAudioObjectUnknown)
    private var procID: AudioDeviceIOProcID?
    private let queue = DispatchQueue(label: "studio.seasnake.gloria.tap", qos: .userInitiated)
    let tracker: BeatTracker
    private(set) var error: String?

    init(tracker: BeatTracker) { self.tracker = tracker }

    func start() -> Bool {
        let desc = CATapDescription(stereoGlobalTapButExcludeProcesses: [])
        desc.uuid = UUID()
        desc.muteBehavior = .unmuted
        desc.isPrivate = true
        desc.name = "GLORIA beat tap"
        var tap = AudioObjectID(kAudioObjectUnknown)
        var err = AudioHardwareCreateProcessTap(desc, &tap)
        guard err == noErr else { error = "tap \(err)"; return false }
        tapID = tap

        var fa = AudioObjectPropertyAddress(mSelector: kAudioTapPropertyFormat, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        var asbd = AudioStreamBasicDescription()
        var size = UInt32(MemoryLayout<AudioStreamBasicDescription>.size)
        if AudioObjectGetPropertyData(tapID, &fa, 0, nil, &size, &asbd) == noErr, asbd.mSampleRate > 0 { tracker.sampleRate = asbd.mSampleRate }

        guard let out = AudioProcs.defaultOutputUID() else { error = "no output"; stop(); return false }
        let dict: [String: Any] = [
            kAudioAggregateDeviceNameKey: "GLORIA Beat",
            kAudioAggregateDeviceUIDKey: UUID().uuidString,
            kAudioAggregateDeviceMainSubDeviceKey: out,
            kAudioAggregateDeviceIsPrivateKey: true,
            kAudioAggregateDeviceIsStackedKey: false,
            kAudioAggregateDeviceTapAutoStartKey: true,
            kAudioAggregateDeviceSubDeviceListKey: [[kAudioSubDeviceUIDKey: out]],
            kAudioAggregateDeviceTapListKey: [[kAudioSubTapDriftCompensationKey: true, kAudioSubTapUIDKey: desc.uuid.uuidString]],
        ]
        var agg = AudioObjectID(kAudioObjectUnknown)
        err = AudioHardwareCreateAggregateDevice(dict as CFDictionary, &agg)
        guard err == noErr else { error = "aggregate \(err)"; stop(); return false }
        aggID = agg
        let tracker = self.tracker
        err = AudioDeviceCreateIOProcIDWithBlock(&procID, aggID, queue) { _, input, _, _, _ in tracker.ingest(input) }
        guard err == noErr, let p = procID else { error = "ioproc \(err)"; stop(); return false }
        err = AudioDeviceStart(aggID, p)
        guard err == noErr else { error = "start \(err)"; stop(); return false }
        return true
    }

    func stop() {
        if let p = procID, aggID != kAudioObjectUnknown { AudioDeviceStop(aggID, p); AudioDeviceDestroyIOProcID(aggID, p) }
        procID = nil
        if aggID != kAudioObjectUnknown { AudioHardwareDestroyAggregateDevice(aggID); aggID = AudioObjectID(kAudioObjectUnknown) }
        if tapID != kAudioObjectUnknown { AudioHardwareDestroyProcessTap(tapID); tapID = AudioObjectID(kAudioObjectUnknown) }
    }
}

/// Decides whether she should be dancing, and gives the dance its clock.
final class MusicWatch {
    struct Beat { var index: Int; var phase: Double; var period: Double; var energy: Double; var live: Bool }

    private(set) var player: AudioProcs.Proc?
    private(set) var playerName = ""
    private(set) var active = false
    private var seenSince: Double?
    private var lastSeen: Double = 0
    let tracker = BeatTracker()
    private var tapBox: AnyObject?
    private var tapStarted: Double = 0
    private(set) var tapNote = "未开启"
    var forcedUntil: Double = 0
    private var synthOrigin = now()
    var forced: Bool { now() < forcedUntil }

    static let musicHints = ["music", "spotify", "163music", "netease", "qqmusic", "kugou", "kuwo", "tidal", "deezer",
                             "cider", "vox", "swinsian", "doppler", "plexamp", "soundcloud", "foobar", "audirvana", "roon", "xiami"]
    static let browserHints = ["chrome", "safari", "webkit", "firefox", "edgemac", "thebrowser", "arc", "opera", "brave",
                               "vivaldi", "bilibili", "youtube"]

    static func isMusic(_ b: String) -> Bool { let l = b.lowercased(); return musicHints.contains { l.contains($0) } && !l.contains("musickit") }
    static func isBrowser(_ b: String) -> Bool { let l = b.lowercased(); return browserHints.contains { l.contains($0) } }

    var tapLive: Bool { let s = tracker.snapshot(); return tapBox != nil && now() - s.lastSound < 1.5 }
    var tapConfident: Bool { tapLive && tracker.snapshot().confidence > 0.3 }
    private(set) var groove = false        // browser audio with a steady beat (with hysteresis)

    var dancing: Bool {
        if now() < forcedUntil { return true }
        guard Prefs.dance, active, let p = player else { return false }
        if MusicWatch.isMusic(p.bundle) { return true }
        return Prefs.browserMusic && groove   // browser audio counts only when it has a steady beat
    }

    private let audioQueue = DispatchQueue(label: "studio.seasnake.gloria.audio", qos: .utility)
    private var polling = false
    private var tapStarting = false

    /// Called on the main thread; the Core Audio queries and tap setup run on a background queue.
    func poll() {
        guard !polling else { return }
        polling = true
        audioQueue.async { [weak self] in
            let procs = AudioProcs.outputting()
            DispatchQueue.main.async { self?.apply(procs) }
        }
    }

    private func apply(_ procs: [AudioProcs.Proc]) {
        polling = false
        let t = now()
        let music = procs.first { MusicWatch.isMusic($0.bundle) }
        let browser = procs.first { MusicWatch.isBrowser($0.bundle) }
        if let p = music ?? browser {
            if player?.pid != p.pid {
                player = p
                playerName = NSRunningApplication(processIdentifier: p.pid)?.localizedName ?? p.bundle
                if MusicWatch.isBrowser(p.bundle), let host = NSWorkspace.shared.runningApplications.first(where: { app in
                    guard let id = app.bundleIdentifier?.lowercased() else { return false }
                    return p.bundle.lowercased().hasPrefix(id) && app.activationPolicy == .regular
                }) { playerName = host.localizedName ?? playerName }
            }
            if seenSince == nil { seenSince = t }
            lastSeen = t
            if t - (seenSince ?? t) > 0.8 { active = true }
        } else if t - lastSeen > 2.5 {
            active = false
            seenSince = nil
            player = nil
        }

        let wantTap = Prefs.beatTap && (active || t < forcedUntil)
        if wantTap && tapBox == nil { startTap() }
        if !wantTap && tapBox != nil && t - lastSeen > 15 && t > forcedUntil { stopTap() }
        let conf = tracker.snapshot().confidence
        if tapLive && conf > 0.42 { groove = true } else if !tapLive || conf < 0.2 { groove = false }
        if tapBox != nil {
            let s = tracker.snapshot()
            if t - tapStarted > 4 && t - s.lastSound > 4 && active { tapNote = "没收到声音（可能没给“系统录音”权限）" }
            else if t - s.lastSound < 1.5 { tapNote = s.confidence > 0.3 ? String(format: "跟拍中 %.0f BPM", s.bpm) : "在听节拍…" }
        }
    }

    private func startTap() {
        guard !tapStarting else { return }
        guard #available(macOS 14.2, *) else { tapNote = "需要 macOS 14.2"; return }
        tapStarting = true
        tapNote = "正在打开…"
        let tracker = self.tracker
        audioQueue.async { [weak self] in
            // The first call can wait on the system-audio permission prompt, so never on the main thread.
            let tap = SystemTap(tracker: tracker)
            let ok = tap.start()
            DispatchQueue.main.async {
                guard let self else { return }
                self.tapStarting = false
                if ok && Prefs.beatTap { self.tapBox = tap; self.tapStarted = now(); self.tapNote = "在听节拍…" }
                else if ok { self.audioQueue.async { tap.stop() } }
                else { self.tapNote = "开不了：\(tap.error ?? "?")" }
            }
        }
    }

    func stopTap() {
        if #available(macOS 14.2, *), let tap = tapBox as? SystemTap { audioQueue.async { tap.stop() } }
        tapBox = nil
        tapNote = Prefs.beatTap ? "待机" : "未开启"
    }

    /// The dance clock: the tap's beats when it has a steady tempo, otherwise a gentle 116 BPM.
    func beat() -> Beat {
        let t = now()
        let s = tracker.snapshot()
        if tapConfident && t - s.lastBeat < s.period * 3 {
            let ph = (t - s.lastBeat) / s.period
            return Beat(index: s.beats + Int(ph), phase: ph.truncatingRemainder(dividingBy: 1), period: s.period,
                        energy: clamp(s.level * 1.6, 0.35, 1), live: true)
        }
        let period = 60.0 / 116
        let x = (t - synthOrigin) / period
        return Beat(index: Int(x), phase: x - floor(x), period: period, energy: tapLive ? clamp(s.level * 1.6, 0.35, 1) : 0.75, live: false)
    }
}
