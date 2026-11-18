using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Gloria {
    // Core Audio COM interfaces (WASAPI session enumeration + loopback beat tracking, zero external dependencies).
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint dwStateMask, out IntPtr ppDevices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice {
        [PreserveSig] int Activate(ref Guid iid, uint dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioMeterInformation {
        [PreserveSig] int GetPeakValue(out float pfPeak);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionManager2 {
        [PreserveSig] int GetAudioSessionControl(IntPtr AudioSessionGuid, uint StreamFlags, out IntPtr SessionControl);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr AudioSessionGuid, uint StreamFlags, out IntPtr AudioVolume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator SessionEnum);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionEnumerator {
        [PreserveSig] int GetCount(out int SessionCount);
        [PreserveSig] int GetSession(int SessionCount, [MarshalAs(UnmanagedType.IUnknown)] out object Session);
    }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl2 {
        [PreserveSig] int GetState(out int pRetVal);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string Value, ref Guid EventContext);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string Value, ref Guid EventContext);
        [PreserveSig] int GetGroupingParam(out Guid pRetVal);
        [PreserveSig] int SetGroupingParam(ref Guid Override, ref Guid EventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr NewNotifications);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr NewNotifications);
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] int GetProcessId(out uint pRetVal);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference(bool optOut);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClient {
        [PreserveSig] int Initialize(int ShareMode, uint StreamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr pFormat, IntPtr AudioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint pNumBufferFrames);
        [PreserveSig] int GetStreamLatency(out long phnsLatency);
        [PreserveSig] int GetCurrentPadding(out uint pNumPaddingFrames);
        [PreserveSig] int IsFormatSupported(int ShareMode, IntPtr pFormat, out IntPtr ppClosestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr ppDeviceFormat);
        [PreserveSig] int GetDevicePeriod(out long phnsDefaultDevicePeriod, out long phnsMinimumDevicePeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioCaptureClient {
        [PreserveSig] int GetBuffer(out IntPtr ppData, out uint pNumFramesToRead, out uint pdwFlags, out ulong pu64DevicePosition, out ulong pu64QPCPosition);
        [PreserveSig] int ReleaseBuffer(uint NumFramesRead);
        [PreserveSig] int GetNextPacketSize(out uint pNumFramesInNextPacket);
    }

    internal struct BeatInfo {
        internal int Index;
        internal double Phase;
        internal double Period;
        internal double Energy;
        internal bool Live;
    }

    // Exact port of Sources/Music.swift BeatTracker (160 Hz low-band onset flux -> 65..182 BPM autocorrelation + periodicity).
    internal sealed class BeatTracker {
        internal struct SnapshotState {
            internal double Bpm;
            internal double Confidence;
            internal double LastBeat;
            internal double Period;
            internal int Beats;
            internal double Level;
            internal double LastSound;
            internal double LastData;
        }

        readonly object gate = new object();
        SnapshotState pub = new SnapshotState { Period = 0.52 };

        float lp;
        float lowAcc, fullAcc;
        int sampleCount;
        float prevLog = -100f;
        const int RingSize = 600; // 6 s at 100 hops/s
        readonly float[] flux = new float[RingSize];
        int hops;
        double nextBeat = -1;
        double period = 0.52;
        double confidence;
        double lastOnset = -10;
        double level;
        double stable;
        double lastEstimate;
        readonly object ingestLock = new object();

        internal SnapshotState Snapshot() {
            lock (gate) return pub;
        }

        internal void IngestMonoBlock(float[] mono, int frames, double sampleRate) {
            if (frames <= 0 || sampleRate <= 1000) return;
            lock (ingestLock) {
                int hop = Math.Max(64, (int)(sampleRate / 100.0));
                float alpha = (float)(1.0 - Math.Exp(-2.0 * Math.PI * 160.0 / sampleRate));
                double t0 = Clock.Now;
                bool silent = true;
                for (int i = 0; i < frames; i++) {
                    float x = mono[i];
                    lp += alpha * (x - lp);
                    lowAcc += lp * lp;
                    fullAcc += x * x;
                    sampleCount++;
                    if (sampleCount >= hop) EndHop(t0);
                    if (Math.Abs(x) > 1e-4f) silent = false;
                }
                lock (gate) {
                    pub.LastData = t0;
                    if (!silent) pub.LastSound = t0;
                }
            }
        }

        void EndHop(double t0) {
            float low = lowAcc / sampleCount;
            float full = fullAcc / sampleCount;
            lowAcc = 0; fullAcc = 0; sampleCount = 0;

            float lg = (float)(10.0 * Math.Log10(low + 1e-10));
            float f = Math.Max(0f, lg - prevLog);
            prevLog = lg;
            flux[hops % RingSize] = f > 30f ? 0f : f;
            hops++;

            double t = hops / 100.0;
            double rms = Math.Sqrt(full);
            double targetLvl = Math.Min(1.0, rms * 4.0);
            level += (targetLvl - level) * (targetLvl > level ? 0.25 : 0.02);

            if (hops % 50 == 0 && hops >= 300) EstimateTempo();

            const int recent = 100;
            if (hops > recent) {
                float mean = 0f, sq = 0f;
                for (int k = 1; k <= recent; k++) {
                    float v = flux[(hops - 1 - k + RingSize * 4) % RingSize];
                    mean += v; sq += v * v;
                }
                mean /= recent;
                float sd = (float)Math.Sqrt(Math.Max(0f, sq / recent - mean * mean));
                if (f > mean + 1.6f * sd && f > 1.2f && t - lastOnset > 0.18) {
                    lastOnset = t;
                    if (confidence > 0.25 && nextBeat > 0) {
                        double err = t - nextBeat;
                        if (err < -period * 0.5) err += period;
                        if (err > period * 0.5) err -= period;
                        if (Math.Abs(err) < period * 0.3) nextBeat += err * 0.22;
                    } else if (nextBeat < 0 || t - nextBeat > period * 2) {
                        nextBeat = t;
                    }
                }
            }

            if (nextBeat > 0 && t >= nextBeat) {
                double late = t - nextBeat;
                nextBeat += period;
                if (t - nextBeat > period * 4) nextBeat = t + period;
                lock (gate) {
                    pub.Beats++;
                    pub.LastBeat = t0 - late;
                    pub.Period = period;
                    pub.Bpm = 60.0 / period;
                    pub.Confidence = confidence;
                    pub.Level = level;
                }
            } else if (hops % 10 == 0) {
                lock (gate) {
                    pub.Level = level;
                    pub.Confidence = confidence;
                }
            }
        }

        void EstimateTempo() {
            const int span = 400;
            float[] x = new float[span];
            float mean = 0f;
            for (int i = 0; i < span; i++) {
                float v = flux[(hops - span + i + RingSize * 4) % RingSize];
                x[i] = v; mean += v;
            }
            mean /= span;
            float energy = 0f;
            for (int i = 0; i < span; i++) {
                x[i] -= mean;
                energy += x[i] * x[i];
            }
            if (energy <= 1e-3f) { confidence *= 0.7; return; }

            float best = Single.MinValue;
            int bestLag = 0;
            float[] scores = new float[100];
            for (int lag = 33; lag <= 92; lag++) {
                float s = 0f;
                for (int i = lag; i < span; i++) s += x[i] * x[i - lag];
                double bpm = 6000.0 / lag;
                double logRatio = Math.Log(bpm / 118.0) / Math.Log(2.0) / 0.75;
                float prior = (float)Math.Exp(-0.5 * logRatio * logRatio);
                s *= (0.55f + 0.45f * prior);
                scores[lag] = s;
                if (s > best) { best = s; bestLag = lag; }
            }
            if (bestLag <= 33 || bestLag >= 92) return;

            float a = scores[bestLag - 1], b = scores[bestLag], c = scores[bestLag + 1];
            float denom = a - 2f * b + c;
            double shift = denom != 0f ? 0.5 * (a - c) / denom : 0.0;
            double p = (bestLag + Calc.Clamp(shift, -0.5, 0.5)) / 100.0;

            float r1 = Corr(x, span, energy, bestLag);
            float r2 = Corr(x, span, energy, bestLag * 2);
            double periodic = Math.Max(0f, r1) * 0.5 + Math.Max(0f, r2) * 0.5;
            if (Math.Abs(p - lastEstimate) / p < 0.04) stable = Math.Min(1.0, stable + 0.25);
            else stable = Math.Max(0.0, stable - 0.4);
            lastEstimate = p;

            double target = Calc.Clamp((periodic - 0.06) * 5.0, 0.0, 1.0) * (0.35 + 0.65 * stable);
            confidence = confidence * 0.55 + target * 0.45;
            if (Math.Abs(p - period) / period < 0.06) period = period * 0.7 + p * 0.3;
            else if (confidence > 0.3 || nextBeat < 0 || stable > 0.5) period = p;
            if (nextBeat < 0) nextBeat = hops / 100.0 + period;
        }

        static float Corr(float[] x, int span, float energy, int lag) {
            if (lag >= span - 40) return 0f;
            float s = 0f;
            for (int i = lag; i < span; i++) s += x[i] * x[i - lag];
            return s / energy;
        }
    }

    internal sealed class MusicWatch : IDisposable {
        static readonly string[] MusicHints = {
            "cloudmusic", "qqmusic", "kugou", "kuwo", "spotify", "applemusic", "itunes",
            "musicbee", "foobar", "aimp", "yesplaymusic", "lx-music", "cider", "tidal",
            "deezer", "plexamp", "soundcloud", "audirvana", "roon", "potplayer", "vlc", "wmplayer", "music.ui"
        };

        static readonly string[] BrowserHints = {
            "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "arc", "thebrowser", "bilibili"
        };

        static readonly Dictionary<string, string> FriendlyAppNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            { "cloudmusic", "网易云音乐" }, { "qqmusic", "QQ音乐" }, { "kugou", "酷狗音乐" },
            { "kuwo", "酷我音乐" }, { "spotify", "Spotify" }, { "applemusic", "Apple Music" },
            { "itunes", "iTunes" }, { "foobar2000", "foobar2000" }, { "musicbee", "MusicBee" },
            { "potplayer", "PotPlayer" }, { "potplayermini64", "PotPlayer" }, { "vlc", "VLC" },
            { "chrome", "Chrome" }, { "msedge", "Edge" }, { "firefox", "Firefox" },
            { "brave", "Brave" }, { "arc", "Arc" }, { "bilibili", "哔哩哔哩" }
        };

        internal readonly BeatTracker Tracker = new BeatTracker();
        readonly Preferences prefs;
        readonly uint selfPid = (uint)Process.GetCurrentProcess().Id;
        readonly double synthOrigin = Clock.Now;

        sealed class TapSession {
            internal volatile bool StopRequested;
            internal Thread Worker;
        }

        volatile bool disposed;
        volatile bool polling;
        readonly object tapLock = new object();
        TapSession activeTap;

        bool IsTapRunning {
            get { lock (tapLock) return activeTap != null && !activeTap.StopRequested; }
        }

        uint activePid;
        bool activeIsMusic;
        bool activeIsBrowser;
        double? seenSince;
        double lastSeen;
        bool groove;

        internal string PlayerName { get; private set; }
        internal bool Active { get; private set; }
        internal string TapNote { get; private set; }
        internal double ForcedUntil { get; set; }
        internal bool Forced { get { return Clock.Now < ForcedUntil; } }

        internal MusicWatch(Preferences prefs) {
            this.prefs = prefs;
            PlayerName = "";
            TapNote = "待机";
        }

        internal bool TapLive {
            get {
                var s = Tracker.Snapshot();
                return IsTapRunning && Clock.Now - s.LastSound < 1.5;
            }
        }

        internal bool TapConfident {
            get { return TapLive && Tracker.Snapshot().Confidence > 0.3; }
        }

        internal bool Dancing {
            get {
                if (Clock.Now < ForcedUntil) return true;
                if (!prefs.Music || !Active) return false;
                if (activeIsMusic) return true;
                if (activeIsBrowser) return prefs.BrowserMusic && (prefs.BeatTap ? groove : true);
                // Other non-system apps with steady musical rhythm
                return prefs.BrowserMusic && groove;
            }
        }

        static bool ContainsAny(string proc, string[] hints) {
            if (String.IsNullOrEmpty(proc)) return false;
            string low = proc.ToLowerInvariant();
            for (int i = 0; i < hints.Length; i++) {
                if (low.Contains(hints[i])) return true;
            }
            return false;
        }

        static string FriendlyName(string proc) {
            string mapped;
            if (FriendlyAppNames.TryGetValue(proc, out mapped)) return mapped;
            return proc;
        }

        internal void Poll() {
            if (disposed || polling) return;
            polling = true;
            ThreadPool.QueueUserWorkItem(delegate {
                try {
                    PollWorker();
                } catch { } finally {
                    polling = false;
                }
            });
        }

        void PollWorker() {
            uint foundPid = 0;
            string foundProc = "";
            bool foundMusic = false, foundBrowser = false;

            IMMDeviceEnumerator enumerator = null;
            IMMDevice device = null;
            IAudioSessionManager2 mgr = null;
            IAudioSessionEnumerator sessions = null;
            try {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                if (enumerator.GetDefaultAudioEndpoint(0, 1, out device) == 0 && device != null) {
                    Guid iidMgr = typeof(IAudioSessionManager2).GUID;
                    object mgrObj;
                    if (device.Activate(ref iidMgr, 23, IntPtr.Zero, out mgrObj) == 0 && mgrObj != null) {
                        mgr = (IAudioSessionManager2)mgrObj;
                        if (mgr.GetSessionEnumerator(out sessions) == 0 && sessions != null) {
                            int count;
                            if (sessions.GetCount(out count) == 0) {
                                for (int i = 0; i < count; i++) {
                                    object sessObj = null;
                                    try {
                                        if (sessions.GetSession(i, out sessObj) != 0 || sessObj == null) continue;
                                        var ctl = sessObj as IAudioSessionControl2;
                                        var meter = sessObj as IAudioMeterInformation;
                                        if (ctl == null || meter == null) continue;
                                        if (ctl.IsSystemSoundsSession() == 0) continue; // Ignore Windows notification sounds
                                        uint pid;
                                        if (ctl.GetProcessId(out pid) < 0 || pid == 0 || pid == selfPid) continue; // Accept S_OK (0) & AUDCLNT_S_NO_SINGLE_PROCESS (>0)
                                        float peak;
                                        if (meter.GetPeakValue(out peak) != 0 || peak < 0.008f) continue;

                                        string pName = Desktop.OwnerName(pid);
                                        bool isMus = ContainsAny(pName, MusicHints);
                                        bool isBrw = !isMus && ContainsAny(pName, BrowserHints);
                                        if (isMus) {
                                            foundPid = pid; foundProc = pName; foundMusic = true; foundBrowser = false;
                                            break; // Dedicated music app wins immediately
                                        }
                                        if (foundPid == 0 || (isBrw && !foundBrowser)) {
                                            foundPid = pid; foundProc = pName; foundMusic = false; foundBrowser = isBrw;
                                        }
                                    } finally {
                                        if (sessObj != null && Marshal.IsComObject(sessObj)) Marshal.ReleaseComObject(sessObj);
                                    }
                                }
                            }
                        }
                    }
                }
            } catch {
            } finally {
                if (sessions != null) Marshal.ReleaseComObject(sessions);
                if (mgr != null) Marshal.ReleaseComObject(mgr);
                if (device != null) Marshal.ReleaseComObject(device);
                if (enumerator != null) Marshal.ReleaseComObject(enumerator);
            }

            double t = Clock.Now;
            if (foundPid != 0) {
                if (activePid != foundPid) {
                    activePid = foundPid;
                    PlayerName = FriendlyName(foundProc);
                    activeIsMusic = foundMusic;
                    activeIsBrowser = foundBrowser;
                }
                if (!seenSince.HasValue) seenSince = t;
                lastSeen = t;
                if (t - seenSince.Value > 0.8) Active = true;
            } else if (t - lastSeen > 2.5) {
                Active = false;
                seenSince = null;
                activePid = 0;
                PlayerName = "";
            }

            bool wantTap = prefs.Music && prefs.BeatTap && (Active || t < ForcedUntil);
            bool runningNow = IsTapRunning;
            if (wantTap && !runningNow) StartTap();
            else if (!wantTap && runningNow && t - lastSeen > 12.0 && t > ForcedUntil) StopTap();

            var snap = Tracker.Snapshot();
            if (TapLive && snap.Confidence > 0.38) groove = true;
            else if (!TapLive || snap.Confidence < 0.18) groove = false;

            if (!prefs.BeatTap) TapNote = "未开启";
            else if (IsTapRunning && t - snap.LastSound < 1.5) {
                TapNote = snap.Confidence > 0.3
                    ? String.Format("跟拍中 {0:0} BPM", snap.Bpm)
                    : "在听节拍…";
            } else {
                TapNote = "待机";
            }
        }

        void StartTap() {
            lock (tapLock) {
                if (disposed || (activeTap != null && !activeTap.StopRequested)) return;
                var prev = activeTap;
                if (prev != null) prev.StopRequested = true;
                var sess = new TapSession();
                var th = new Thread(delegate() { TapLoop(sess); }) { IsBackground = true, Name = "GLORIA.BeatTap" };
                th.SetApartmentState(ApartmentState.MTA);
                sess.Worker = th;
                activeTap = sess;
                th.Start();
            }
        }

        internal void StopTap() {
            Thread joinWorker = null;
            lock (tapLock) {
                if (activeTap != null) {
                    activeTap.StopRequested = true;
                    joinWorker = activeTap.Worker;
                    activeTap = null;
                }
            }
            if (joinWorker != null && joinWorker != Thread.CurrentThread) {
                try { joinWorker.Join(200); } catch { }
            }
        }

        void TapLoop(TapSession session) {
            IMMDeviceEnumerator enumerator = null;
            IMMDevice device = null;
            IAudioClient client = null;
            IAudioCaptureClient capture = null;
            IntPtr pwfx = IntPtr.Zero;
            try {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                if (enumerator.GetDefaultAudioEndpoint(0, 1, out device) != 0 || device == null) return;
                Guid iidClient = typeof(IAudioClient).GUID;
                object clientObj;
                if (device.Activate(ref iidClient, 23, IntPtr.Zero, out clientObj) != 0 || clientObj == null) return;
                client = (IAudioClient)clientObj;
                if (client.GetMixFormat(out pwfx) != 0 || pwfx == IntPtr.Zero) return;

                // WAVEFORMATEX header: wFormatTag (2), nChannels (2), nSamplesPerSec (4), nAvgBytesPerSec (4), nBlockAlign (2), wBitsPerSample (2), cbSize (2)
                ushort formatTag = (ushort)Marshal.ReadInt16(pwfx, 0);
                int channels = Marshal.ReadInt16(pwfx, 2);
                int sampleRate = Marshal.ReadInt32(pwfx, 4);
                int blockAlign = Marshal.ReadInt16(pwfx, 12);
                int bitsPerSample = Marshal.ReadInt16(pwfx, 14);
                bool isFloat = formatTag == 3; // WAVE_FORMAT_IEEE_FLOAT
                if (formatTag == 0xFFFE && Marshal.ReadInt16(pwfx, 16) >= 22) {
                    // WAVEFORMATEXTENSIBLE SubFormat GUID starts at offset 24; IEEE_FLOAT starts with 0x00000003
                    int subTag = Marshal.ReadInt32(pwfx, 24);
                    isFloat = subTag == 3;
                }
                if ((!isFloat && bitsPerSample != 16) || channels < 1 || sampleRate < 8000) return;

                const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
                if (client.Initialize(0, AUDCLNT_STREAMFLAGS_LOOPBACK, 500000 /* 50ms */, 0, pwfx, IntPtr.Zero) != 0) return;
                Guid iidCapture = typeof(IAudioCaptureClient).GUID;
                object capObj;
                if (client.GetService(ref iidCapture, out capObj) != 0 || capObj == null) return;
                capture = (IAudioCaptureClient)capObj;
                if (client.Start() != 0) return;

                float[] mono = new float[4096];
                byte[] raw = new byte[16384];

                while (!session.StopRequested && !disposed) {
                    uint packetFrames;
                    if (capture.GetNextPacketSize(out packetFrames) != 0) break;
                    if (packetFrames == 0) {
                        Thread.Sleep(10);
                        continue;
                    }
                    while (packetFrames > 0 && !session.StopRequested && !disposed) {
                        IntPtr dataPtr;
                        uint framesToRead, flags;
                        ulong devPos, qpcPos;
                        if (capture.GetBuffer(out dataPtr, out framesToRead, out flags, out devPos, out qpcPos) != 0) break;
                        try {
                            int nFrames = (int)framesToRead;
                            if (nFrames > 0) {
                                if (mono.Length < nFrames) mono = new float[nFrames * 2];
                                if ((flags & 2 /* AUDCLNT_BUFFERFLAGS_SILENT */) != 0 || dataPtr == IntPtr.Zero) {
                                    Array.Clear(mono, 0, nFrames);
                                } else {
                                    int bytes = nFrames * blockAlign;
                                    if (raw.Length < bytes) raw = new byte[bytes * 2];
                                    Marshal.Copy(dataPtr, raw, 0, bytes);
                                    if (isFloat && bitsPerSample == 32) {
                                        float invCh = 1f / channels;
                                        for (int f = 0; f < nFrames; f++) {
                                            int baseOff = f * blockAlign;
                                            float sum = 0f;
                                            for (int c = 0; c < channels; c++) sum += BitConverter.ToSingle(raw, baseOff + c * 4);
                                            mono[f] = sum * invCh;
                                        }
                                    } else if (bitsPerSample == 16) {
                                        float inv = 1f / (32768f * channels);
                                        for (int f = 0; f < nFrames; f++) {
                                            int baseOff = f * blockAlign;
                                            int sum = 0;
                                            for (int c = 0; c < channels; c++) sum += BitConverter.ToInt16(raw, baseOff + c * 2);
                                            mono[f] = sum * inv;
                                        }
                                    }
                                }
                                Tracker.IngestMonoBlock(mono, nFrames, sampleRate);
                            }
                        } finally {
                            capture.ReleaseBuffer(framesToRead);
                        }
                        if (capture.GetNextPacketSize(out packetFrames) != 0) break;
                    }
                }
                client.Stop();
            } catch {
            } finally {
                lock (tapLock) {
                    if (activeTap == session) activeTap = null;
                }
                if (pwfx != IntPtr.Zero) Marshal.FreeCoTaskMem(pwfx);
                if (capture != null) Marshal.ReleaseComObject(capture);
                if (client != null) Marshal.ReleaseComObject(client);
                if (device != null) Marshal.ReleaseComObject(device);
                if (enumerator != null) Marshal.ReleaseComObject(enumerator);
            }
        }

        internal BeatInfo Beat() {
            double t = Clock.Now;
            var s = Tracker.Snapshot();
            if (TapConfident && t - s.LastBeat < s.Period * 3.0 && s.Period > 0.1) {
                double ph = (t - s.LastBeat) / s.Period;
                double frac = ph - Math.Floor(ph);
                return new BeatInfo {
                    Index = s.Beats + (int)Math.Floor(ph),
                    Phase = frac,
                    Period = s.Period,
                    Energy = Calc.Clamp(s.Level * 1.6, 0.35, 1.0),
                    Live = true
                };
            }
            const double defaultPeriod = 60.0 / 116.0;
            double x = (t - synthOrigin) / defaultPeriod;
            return new BeatInfo {
                Index = (int)Math.Floor(x),
                Phase = x - Math.Floor(x),
                Period = defaultPeriod,
                Energy = TapLive ? Calc.Clamp(s.Level * 1.6, 0.35, 1.0) : 0.75,
                Live = false
            };
        }

        public void Dispose() {
            disposed = true;
            StopTap();
        }
    }
}
