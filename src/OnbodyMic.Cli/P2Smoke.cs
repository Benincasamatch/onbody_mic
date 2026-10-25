namespace OnbodyMic.Cli;

using System.Diagnostics;
using System.Runtime.InteropServices;
using OnbodyMic.Application;
using OnbodyMic.Audio;
using OnbodyMic.Domain;
using OnbodyMic.Media;
using OnbodyMic.Platform.Windows;

/// <summary>
/// P2 gate smoke (automatable half): registers real global hotkeys, drives
/// them with SendInput while a borderless-fullscreen window is foreground,
/// and verifies delivery on the cable capture side with digital evidence:
/// trigger latency (docs/03 §5.1 hotkey target P95 ≤ 80 ms), replace/restart,
/// hot stop, extras-closed rejection (no queue), emergency latch, and mic
/// continuity. Real-game/elevated matrix (T17) remains a user-side item.
/// </summary>
internal static class P2Smoke
{
    private const string PlayGesture = "Ctrl+Alt+F7";
    private const string StopGesture = "Ctrl+Alt+F8";
    private const string ExtrasGesture = "Ctrl+Alt+F9";
    private const string MuteGesture = "Ctrl+Alt+F10";
    private const double ToneHz = 700.0;
    private const int ToneMs = 500;
    private const int Rate = 44100;

    private sealed record RecordingChunk(long Tick, float[] Samples);

    private sealed class Recorder
    {
        private readonly object _gate = new();
        private readonly List<RecordingChunk> _chunks = [];
        private WasapiEventCapture? _capture;

        public void Start(string captureEndpointId)
        {
            _capture = WasapiEventCapture.Open(captureEndpointId, bufferMs: 10, useMixFormat: true);
            _capture.Frames += frames =>
            {
                var copy = new float[frames.Length];
                frames.CopyTo(copy);
                lock (_gate)
                {
                    _chunks.Add(new RecordingChunk(Environment.TickCount64, copy));
                }
            };
            _capture.Start();
        }

        public List<RecordingChunk> Stop()
        {
            _capture?.Dispose();
            _capture = null;
            lock (_gate)
            {
                return [.. _chunks];
            }
        }
    }

    public static int Run(string[] args)
    {
        var options = PanelApp.ParseOptions(args);
        var devices = WindowsAudioDevices.List(activeOnly: true);
        var renderId = PanelApp.ResolveEndpoint(options, "render-id", "render", devices, capture: false, "虚拟输出（线缆 Render）");
        var captureId = PanelApp.ResolveEndpoint(options, "capture-id", "capture", devices, capture: true, "取证读取（线缆 Capture）");
        var micId = options.TryGetValue("mic-id", out var micIdText) && devices.Any(d => d.Id == micIdText) ? micIdText : null;
        if (renderId is null || captureId is null)
        {
            return 1;
        }

        var presses = options.TryGetValue("presses", out var pressText) && int.TryParse(pressText, out var n) ? Math.Clamp(n, 5, 100) : 20;
        var outDir = options.GetValueOrDefault("out") ?? Path.Combine(".tools", "tmp", "p2smoke");
        Directory.CreateDirectory(outDir);
        var root = Path.Combine(outDir, "run");
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        Directory.CreateDirectory(root);
        Console.WriteLine($"p2smoke: presses={presses} mic={(micId is null ? "off" : "on")} out={Path.GetFullPath(outDir)}");

        // 1. managed library in an isolated root: import + prefill (hot cache)
        var sourceWav = WriteToneWav(Path.Combine(root, "sfx700.wav"));
        var decoder = new NAudioClipDecoder();
        using var library = ClipLibrary.Open(Path.Combine(root, "lib"), decoder);
        var imported = library.Import(sourceWav, displayName: "SmokeBeep");
        var clipId = imported.Item.ClipId;
        var cache = new ClipCache(ClipCache.DefaultCapacityFrames);
        var prefilled = cache.Prefill(library, decoder);
        Console.WriteLine($"library: clip={clipId} duration={imported.Item.DurationMs}ms prefill={prefilled} cacheFrames={cache.Stats().TotalFrames}");
        if (prefilled != 1)
        {
            Console.WriteLine("FAIL: prefill did not cache the clip");
            return 1;
        }

        var failures = new List<string>();

        // 2. real session on the exclusive cable render pump
        ClipResolution Resolve(string id)
        {
            var item = library.Find(id);
            if (item is null)
            {
                return ClipResolution.Unknown;
            }

            var path = library.AssetAbsolutePath(item);
            return new ClipResolution(true, File.Exists(path), path, item.Gain);
        }

        using var session = new AudioSession(new AudioSessionOptions
        {
            ClipDecoder = decoder,
            SampleStore = cache,
            ClipResolver = Resolve,
            MicAdapterFactory = channel => micId is null
                ? throw new NotSupportedException("mic not configured for this smoke run")
                : new WindowsMicSource(micId, channel),
            AppAdapterFactory = (channel, binding) => new WindowsAppSource(binding, channel),
            OutputFactory = () => new WasapiExclusiveRenderPump(renderId),
            MonitorOutputFactory = () => new WindowsMonitorOutput(renderId),
        });
        var attach = session.AttachOutput();
        Console.WriteLine(attach.Accepted ? "session: 输出已接入（发送/监听全部关闭）" : $"FAIL: attach {attach.Rejection}");
        if (!attach.Accepted)
        {
            return 1;
        }

        // 3. hotkey backend. The injection probe runs at the service level
        // BEFORE the dispatcher exists, so probing has zero dispatch side
        // effects and cannot disturb the scenario bindings registered below.
        using var hotkeys = new WindowsHotkeyService();
        var triggerLog = new List<(HotkeyAction Action, long Tick)>();
        hotkeys.Triggered += trigger =>
        {
            lock (triggerLog)
            {
                triggerLog.Add((trigger.Binding.Action, trigger.TimestampTick));
            }
        };

        // Injection capability probe: SendInput must reach the system input
        // stream for keys mode. The agent harness blocks it entirely (proven
        // with a WH_KEYBOARD_LL hook); some machines own individual gestures
        // (graphics drivers commonly take Ctrl+Alt+F-keys), so every
        // candidate reports its own outcome instead of failing silently.
        var keysMode = ProbeInjection(hotkeys, triggerLog);
        Console.WriteLine(keysMode
            ? "drive: SendInput 注入有效（keys 模式，测热键全链路）"
            : "drive: 注入不可达（direct 模式：直驱会话命令=分派器同一路径；热键触发用面板+物理按键验证）");

        using var dispatcher = new HotkeyDispatcher(session, hotkeys, Resolve);

        foreach (var (name, gestureText, action, target) in new[]
        {
            ("play", PlayGesture, HotkeyAction.PlayClip, clipId),
            ("stop", StopGesture, HotkeyAction.StopClip, (string?)null),
            ("extras", ExtrasGesture, HotkeyAction.ToggleExtrasSend, (string?)null),
            ("mute", MuteGesture, HotkeyAction.EmergencyMute, (string?)null),
        })
        {
            var bound = dispatcher.Bind(new HotkeyBinding(action, target, ParseGesture(gestureText)));
            Console.WriteLine(bound.Accepted ? $"hotkey: {name} = {gestureText}" : $"FAIL: bind {name}: {bound.Rejection}");
            if (!bound.Accepted)
            {
                return 1;
            }
        }


        void Drive(string action)
        {
            if (keysMode)
            {
                PressCombo(action switch
                {
                    "play" => PlayGesture,
                    "stop" => StopGesture,
                    "extras" => ExtrasGesture,
                    _ => MuteGesture,
                });
                return;
            }

            var tick = Environment.TickCount64;
            lock (triggerLog)
            {
                triggerLog.Add((action switch
                {
                    "play" => HotkeyAction.PlayClip,
                    "stop" => HotkeyAction.StopClip,
                    "extras" => HotkeyAction.ToggleExtrasSend,
                    _ => HotkeyAction.EmergencyMute,
                }, tick));
            }

            var result = action switch
            {
                "play" => session.PlayClip(clipId),
                "stop" => session.StopClip(),
                "extras" => session.EnableExtras(!session.Status().Switches.ExtrasSendEnabled),
                _ => session.EmergencyMute(),
            };
            if (!result.Accepted && action == "play" && session.Status().Switches.ExtrasSendEnabled)
            {
                Console.WriteLine($"  [direct] play rejected: {result.Rejection}");
            }
        }

        long playStart = 0, replaceStart = 0, stopAt = 0, gateAt = 0, micStartTick = 0, micEndTick = 0;
        SessionPhase phaseAfterMute = default;
        ActiveClip? clipDuringGate = null, clipAfterRegain = null, clipAfterMute = null;
        bool? micStayedOn = null;

        // 4. capture-side recorder (KOOK-equivalent view of the virtual mic)
        var recorder = new Recorder();
        recorder.Start(captureId);
        Thread.Sleep(400); // pre-roll baseline silence

        var extrasOn = session.EnableExtras(true);
        if (!extrasOn.Accepted)
        {
            Console.WriteLine($"FAIL: extras on: {extrasOn.Rejection}");
            return 1;
        }

        if (keysMode)
        {
            using (var foreground = FullscreenWindow.Claim())
            {
                Console.WriteLine($"foreground: fullscreen surrogate claimed={foreground.Claimed}");
                RunScenario();
            }
        }
        else
        {
            RunScenario();
        }


        void RunScenario()
        {
            // 5. latency matrix: N presses, ~0.65 s apart (500 ms tone + gap)
            playStart = Environment.TickCount64;
            for (var i = 0; i < presses; i++)
            {
                Drive("play");
                Thread.Sleep(650);
                if ((i + 1) % 5 == 0)
                {
                    Console.WriteLine($"  pressed {i + 1}/{presses}");
                }
            }

            // 6. T13 replace/restart: retrigger mid-play
            replaceStart = Environment.TickCount64;
            Drive("play");
            Thread.Sleep(150);
            Drive("play");
            Thread.Sleep(800);

            // 7. T12 hot stop while playing
            stopAt = Environment.TickCount64;
            Drive("play");
            Thread.Sleep(120);
            Drive("stop");
            Thread.Sleep(600);

            // 8. gate: extras off, then play must not sound
            Drive("extras");
            Thread.Sleep(150);
            gateAt = Environment.TickCount64;
            Drive("play");
            Thread.Sleep(700);
            clipDuringGate = session.Status().Clip;
            Drive("extras"); // extras back on: nothing queued may play
            Thread.Sleep(500);
            clipAfterRegain = session.Status().Clip;

            // 9. emergency latch; play rejected
            Drive("mute");
            Thread.Sleep(150);
            phaseAfterMute = session.Status().Phase;
            Drive("play");
            Thread.Sleep(300);
            clipAfterMute = session.Status().Clip;
            _ = session.UnlockEmergency();

            // 10. mic continuity: mic on, clip plays, stop leaves mic on
            if (micId is not null)
            {
                _ = session.EnableExtras(true);
                var micOn = session.EnableMic(true);
                micStartTick = Environment.TickCount64;
                if (!micOn.Accepted)
                {
                    lock (failures)
                    {
                        failures.Add($"mic enable rejected: {micOn.Rejection}");
                    }
                }
                else
                {
                    Thread.Sleep(300);
                    Drive("play");
                    Thread.Sleep(300);
                    Drive("stop");
                    Thread.Sleep(200);
                    micStayedOn = session.Status().Switches.MicSendEnabled;
                    micEndTick = Environment.TickCount64;
                    _ = session.EnableMic(false);
                }
            }
        }

        Thread.Sleep(300);
        var chunks = recorder.Stop();

        // ---- analysis ----
        var total = chunks.Sum(c => c.Samples.Length);
        Console.WriteLine($"capture: {chunks.Count} chunks, {total} frames ({total / (double)Rate:F2}s)");
        var flat = chunks.SelectMany(c => c.Samples).ToArray();

        var playTriggers = TicksOf(triggerLog, HotkeyAction.PlayClip, skipBefore: playStart - 1000);
        var latencies = new List<long>();
        foreach (var tick in playTriggers)
        {
            var onset = FindOnset(chunks, tick, windowMs: 300);
            if (onset is { } value)
            {
                latencies.Add(value - tick);
            }
        }

        var ordered = latencies.OrderBy(v => v).ToList();
        var p95 = Percentile(ordered, 0.95);
        Console.WriteLine(
            $"{(keysMode ? "latency(hotkey trigger→capture onset)" : "latency(command submit→capture onset, direct 模式)")}: n={latencies.Count}/{presses} P50={Percentile(ordered, 0.50):F0}ms P95={p95:F0}ms max={(ordered.Count == 0 ? double.NaN : ordered[^1]):F0}ms");
        if (latencies.Count < presses * 8 / 10)
        {
            failures.Add($"only {latencies.Count}/{presses} presses detected on the capture side");
        }

        if (double.IsNaN(p95) || (keysMode && p95 > 80))
        {
            // in keys mode this is the docs/03 §5.1 hotkey target; in direct
            // mode only a sanity bound applies (the figure is engine-path)
            failures.Add($"{(keysMode ? "hotkey" : "engine-path")} P95 {p95:F0}ms exceeds {(keysMode ? "the 80 ms target (docs/03 §5.1)" : "the 150 ms sanity bound")}");
        }
        else if (!keysMode && p95 > 150)
        {
            failures.Add($"engine-path P95 {p95:F0}ms exceeds the 150 ms sanity bound");
        }

        var toneRatio = GoertzelRatio(flat, ToneHz);
        Console.WriteLine($"tone 700Hz ratio={toneRatio:F0} rms={Rms(flat):F4}");
        if (toneRatio < 1000)
        {
            failures.Add($"700Hz tone not detected on the cable capture (ratio {toneRatio:F0})");
        }

        // T13 replace continuity: no silent gap beyond the bound inside the double-press window
        var replaceWindow = ChunksBetween(chunks, replaceStart, replaceStart + 750);
        if (!AssertContinuity(replaceWindow, maxSilentMs: 120, out var gap))
        {
            failures.Add($"replace/restart produced a {gap}ms silent gap (expected seamless replace)");
            Console.WriteLine("  replace-window 50ms energy bins: "
                + string.Join(" ", EnergyBins(replaceWindow, 50)));
        }
        else
        {
            Console.WriteLine($"replace/restart: max silent gap {gap}ms（无级断裂）");
        }

        // T12 hot stop: silence shortly after the stop trigger
        var stopTrigger = TicksOf(triggerLog, HotkeyAction.StopClip, skipBefore: stopAt - 1000, until: stopAt + 500).FirstOrDefault();
        if (stopTrigger > 0)
        {
            var tail = ChunksBetween(chunks, stopTrigger + 150, stopTrigger + 600);
            var energy = MaxAbs(tail);
            Console.WriteLine($"stop-tail energy (150-600ms after stop) = {energy:F3}");
            if (energy > 0.05)
            {
                failures.Add($"clip still audible after stop (tail energy {energy:F3})");
            }
        }
        else
        {
            failures.Add("stop hotkey trigger not recorded");
        }

        // gate: extras closed → no sound after the play press
        var gatePlayTrigger = TicksOf(triggerLog, HotkeyAction.PlayClip, skipBefore: gateAt - 200, until: gateAt + 400).FirstOrDefault();
        if (gatePlayTrigger > 0)
        {
            var gateWindow = ChunksBetween(chunks, gatePlayTrigger, gatePlayTrigger + 700);
            var energy = MaxAbs(gateWindow);
            Console.WriteLine($"gate-window energy (extras off, 700ms) = {energy:F3}");
            if (energy > 0.05)
            {
                failures.Add($"sound leaked while extras was closed (energy {energy:F3})");
            }
        }
        else
        {
            failures.Add("play hotkey trigger during extras-off not recorded");
        }

        if (clipDuringGate is not null)
        {
            failures.Add("session accepted a clip while extras was closed");
        }

        if (clipAfterRegain is not null)
        {
            failures.Add("a queued clip played after extras was re-enabled (queueing is forbidden)");
        }

        if (phaseAfterMute != SessionPhase.EmergencyMuted)
        {
            failures.Add($"emergency hotkey did not latch (phase {phaseAfterMute})");
        }

        if (clipAfterMute is not null)
        {
            failures.Add("clip played while emergency latched");
        }

        if (micStayedOn == false)
        {
            failures.Add("stop-clip hotkey turned the microphone off (T12 violation)");
        }

        if (micId is not null && micStayedOn is not null)
        {
            var micFrames = session.Metrics().Mic.PushedFrames;
            Console.WriteLine($"mic-continuity: pushedFrames={micFrames} stayedOn={micStayedOn}");
            if (micFrames == 0)
            {
                failures.Add("mic capture produced no frames during the continuity window");
            }
        }

        var evidence = Path.GetFullPath(Path.Combine(outDir, "p2smoke_capture.wav"));
        WriteWav16(evidence, flat);
        Console.WriteLine();
        Console.WriteLine(failures.Count == 0
            ? $"P2SMOKE PASS（取证 WAV: {evidence}）"
            : $"P2SMOKE FAIL ({failures.Count} 项):");
        foreach (var failure in failures)
        {
            Console.WriteLine($"  - {failure}");
        }

        return failures.Count == 0 ? 0 : 1;
    }
    /// <summary>
    /// Tries several probe gestures at the service level; reports each
    /// outcome (bind conflict vs. silent) so a taken gesture is never
    /// mistaken for blocked injection.
    /// </summary>
    private static bool ProbeInjection(WindowsHotkeyService hotkeys, List<(HotkeyAction Action, long Tick)> log)
    {
        var report = new List<string>();
        foreach (var gestureText in (string[])["Ctrl+Alt+U", "Ctrl+Alt+P", "Ctrl+Alt+Num9", "Ctrl+Alt+F6"])
        {
            var bind = hotkeys.Register(new HotkeyBinding(HotkeyAction.ToggleMonitor, null, ParseGesture(gestureText)));
            if (!bind.Accepted)
            {
                report.Add($"{gestureText}:被占用({bind.Rejection})");
                continue;
            }

            int before;
            lock (log)
            {
                before = log.Count;
            }

            PressCombo(gestureText);
            var fired = false;
            for (var i = 0; i < 8 && !fired; i++)
            {
                Thread.Sleep(100);
                lock (log)
                {
                    fired = log.Count > before && log[log.Count - 1].Action == HotkeyAction.ToggleMonitor;
                }
            }

            _ = hotkeys.Unregister(HotkeyAction.ToggleMonitor, null);
            if (fired)
            {
                Console.WriteLine($"injection probe: {gestureText} 触发有效（其余候选: {(report.Count == 0 ? "无" : string.Join("; ", report))}）");
                return true;
            }

            report.Add($"{gestureText}:注入无响应");
        }

        Console.WriteLine($"injection probe: 全部候选未触发（{string.Join("; ", report)}）");
        return false;
    }
    private static List<long> TicksOf(List<(HotkeyAction Action, long Tick)> log, HotkeyAction action, long skipBefore = long.MinValue, long until = long.MaxValue)
    {
        lock (log)
        {
            return log.Where(t => t.Action == action && t.Tick >= skipBefore && t.Tick <= until).Select(t => t.Tick).ToList();
        }
    }

    private static long? FindOnset(List<RecordingChunk> chunks, long fromTick, int windowMs)
    {
        var deadline = fromTick + windowMs;
        foreach (var chunk in chunks)
        {
            if (chunk.Tick < fromTick || chunk.Tick > deadline)
            {
                continue;
            }

            foreach (var value in chunk.Samples)
            {
                if (Math.Abs(value) > 0.05)
                {
                    return chunk.Tick;
                }
            }
        }

        return null;
    }

    /// <summary>Binned peak amplitude relative to the first chunk's tick, for gap forensics.</summary>
    private static List<string> EnergyBins(List<RecordingChunk> window, int binMs)
    {
        if (window.Count == 0)
        {
            return ["(empty)"];
        }

        var bins = new List<string>();
        var start = window[0].Tick;
        foreach (var group in window.GroupBy(c => (c.Tick - start) / binMs).OrderBy(g => g.Key))
        {
            var peak = group.Max(c => c.Samples.Length == 0 ? 0 : c.Samples.Max(Math.Abs));
            bins.Add($"{group.Key * binMs}:{peak:F2}");
        }

        return bins;
    }

    private static List<RecordingChunk> ChunksBetween(List<RecordingChunk> chunks, long fromTick, long toTick) =>
        [.. chunks.Where(c => c.Tick >= fromTick && c.Tick <= toTick)];

    private static float MaxAbs(List<RecordingChunk> chunks) =>
        chunks.Count == 0 ? 0 : chunks.Max(c => c.Samples.Length == 0 ? 0 : c.Samples.Max(Math.Abs));

    /// <summary>Coarse continuity check: between signal-bearing chunks the silence must stay under the bound.</summary>
    private static bool AssertContinuity(List<RecordingChunk> window, int maxSilentMs, out int worstGapMs)
    {
        worstGapMs = 0;
        RecordingChunk? previous = null;
        foreach (var chunk in window)
        {
            if (!chunk.Samples.Any(s => Math.Abs(s) > 0.05))
            {
                continue;
            }

            if (previous is { } prev)
            {
                var gap = (int)(chunk.Tick - prev.Tick) - (prev.Samples.Length * 1000 / Rate);
                worstGapMs = Math.Max(worstGapMs, Math.Max(0, gap));
            }

            previous = chunk;
        }

        return worstGapMs <= maxSilentMs;
    }

    private static double Percentile(List<long> sorted, double q)
    {
        if (sorted.Count == 0)
        {
            return double.NaN;
        }

        var index = (int)Math.Clamp(Math.Ceiling(q * sorted.Count) - 1, 0, sorted.Count - 1);
        return sorted[index];
    }

    private static double Rms(float[] samples) =>
        samples.Length == 0 ? 0 : Math.Sqrt(samples.Average(s => (double)s * s));

    private static double GoertzelRatio(float[] samples, double frequency)
    {
        if (samples.Length < 4096)
        {
            return 0;
        }

        var tone = GoertzelPower(samples, frequency);
        var noise = (GoertzelPower(samples, frequency * 0.7) + GoertzelPower(samples, frequency * 1.3)) / 2;
        return noise > 0 ? tone / noise : double.PositiveInfinity;
    }

    private static double GoertzelPower(float[] samples, double frequency)
    {
        var k = (int)Math.Round(frequency * samples.Length / Rate);
        var omega = 2.0 * Math.PI * k / samples.Length;
        var coeff = 2.0 * Math.Cos(omega);
        double q0 = 0, q1 = 0, q2 = 0;
        foreach (var value in samples)
        {
            q0 = value + (coeff * q1) - q2;
            q2 = q1;
            q1 = q0;
        }

        return (q1 * q1) + (q2 * q2) - (coeff * q1 * q2);
    }

    private static HotkeyGesture ParseGesture(string text) =>
        HotkeyGesture.TryParse(text, out var gesture) ? gesture : throw new InvalidOperationException($"bad gesture {text}");

    private static string WriteToneWav(string path)
    {
        var frames = Rate * ToneMs / 1000;
        var samples = new float[frames];
        var ramp = Rate * 5 / 1000;
        for (var i = 0; i < frames; i++)
        {
            var tone = 0.4 * Math.Sin(2.0 * Math.PI * ToneHz * i / Rate);
            double envelope = 1;
            if (i < ramp)
            {
                envelope = (double)i / ramp;
            }

            if (i > frames - ramp)
            {
                envelope = (double)(frames - i) / ramp;
            }

            samples[i] = (float)(tone * envelope);
        }

        WriteWav16(path, samples);
        return path;
    }

    private static void WriteWav16(string path, float[] mono)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8);
        writer.Write(36 + (mono.Length * 2));
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write((short)1); // mono
        writer.Write(Rate);
        writer.Write(Rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(mono.Length * 2);
        foreach (var value in mono)
        {
            writer.Write((short)Math.Clamp(value * short.MaxValue, short.MinValue, short.MaxValue));
        }
    }

    // ---- input synthesis and foreground claim (Win32) ----

    private static void PressCombo(string gestureText)
    {
        var gesture = ParseGesture(gestureText);
        var modifiers = new List<ushort>();
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            modifiers.Add(0x11);
        }

        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            modifiers.Add(0x12);
        }

        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            modifiers.Add(0x10);
        }

        foreach (var vk in modifiers)
        {
            SendKey(vk, down: true);
            Thread.Sleep(15);
        }

        SendKey((ushort)gesture.Key, down: true);
        Thread.Sleep(25);
        SendKey((ushort)gesture.Key, down: false);
        Thread.Sleep(15);
        for (var i = modifiers.Count - 1; i >= 0; i--)
        {
            SendKey(modifiers[i], down: false);
            Thread.Sleep(15);
        }
    }

    private static void SendKey(ushort vk, bool down)
    {
        var input = new NativeInput
        {
            Type = 1, // INPUT_KEYBOARD
            Union = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = vk,
                    Flags = (uint)(down ? 0 : 2), // KEYEVENTF_KEYUP
                },
            },
        };
        var sent = SendInput(1, [input], Marshal.SizeOf<NativeInput>());
        if (sent != 1)
        {
            Console.WriteLine($"WARN: SendInput rejected err={Marshal.GetLastWin32Error()}");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        // the union must size to MOUSEINPUT (32 B) so INPUT marshals to the
        // OS layout (40 B on x64); a keyboard-only union makes SendInput fail
        // with ERROR_INVALID_PARAMETER (87) — proven on the real machine
        [FieldOffset(0)]
        public KeyboardInput Keyboard;

        [FieldOffset(0)]
        public MouseInput Mouse;
    }

    /// <summary>Borderless topmost window that grabs the foreground, simulating a fullscreen game (focus evidence for T16/T17).</summary>
    private sealed class FullscreenWindow : IDisposable
    {
        private IntPtr _hwnd;
        private ushort _classAtom;
        private readonly IntPtr _defProc = GetProcAddress(GetModuleHandle("user32.dll"), "DefWindowProcW");

        public bool Claimed { get; private set; }

        public static FullscreenWindow Claim()
        {
            var window = new FullscreenWindow();
            try
            {
                window.Create();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"foreground window failed: {ex.Message}");
            }

            return window;
        }

        private void Create()
        {
            var className = "onbodymic_p2smoke";
            var wc = new WndClass
            {
                WndProc = _defProc,
                HInstance = GetModuleHandle(null),
                LpClassName = className,
            };
            _classAtom = RegisterClass(ref wc);
            _hwnd = CreateWindowEx(
                0x00000008, // WS_EX_TOPMOST
                className,
                "p2smoke fullscreen surrogate",
                unchecked((int)(0x80000000 | 0x10000000)), // WS_POPUP | WS_VISIBLE
                0, 0, GetSystemMetrics(0), GetSystemMetrics(1),
                IntPtr.Zero, IntPtr.Zero, wc.HInstance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                return;
            }

            ShowWindow(_hwnd, 3); // SW_MAXIMIZE
            Claimed = SetForegroundWindow(_hwnd);
            Thread.Sleep(100);
        }

        public void Dispose()
        {
            if (_hwnd != IntPtr.Zero)
            {
                _ = DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WndClass
    {
        public uint Style;
        public nint WndProc;
        public int ClsExtra;
        public int WndExtra;
        public nint HInstance;
        public nint HIcon;
        public nint HCursor;
        public nint HBackground;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string LpClassName;
    }


    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        public uint Type;
        public InputUnion Union;
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, NativeInput[] inputs, int size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClass(ref WndClass wndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr module, string procName);
}
