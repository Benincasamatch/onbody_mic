namespace OnbodyMic.Cli;

using OnbodyMic.Application;
using OnbodyMic.Domain;
using OnbodyMic.Platform.Windows;

/// <summary>
/// T30 continuous-run gate (docs/03 §2 P4 task 2): drives the real adapter
/// chain end to end (mic capture -> safety gates -> mixer -> exclusive
/// virtual output, plus the FullEndpoint monitor ring) with periodic clip
/// plays, sampling memory and desensitized counters. The verdict lives in
/// the test report, not here — this tool prints the raw growth evidence.
/// </summary>
internal static class Soak
{
    public static int Run(string[] args)
    {
        var options = Program.ParseOptions(args);
        var minutes = Program.ParseDouble(options, "minutes", 60);
        var periodS = Program.ParseDouble(options, "period-s", 5);
        var toneS = Program.ParseDouble(options, "tone-s", 2);
        if (minutes <= 0 || periodS < 1 || toneS is < 1 or > 10)
        {
            Console.Error.WriteLine("soak requires minutes > 0, period-s >= 1, tone-s within [1,10]");
            return 1;
        }

        var micId = ResolveId(options, "mic-id", "mic", capture: true);
        var renderId = ResolveId(options, "render-id", "render", capture: false);
        var cableId = ResolveId(options, "capture-id", "capture", capture: true);
        var monitorId = ResolveId(options, "monitor-id", "monitor", capture: false);
        if (renderId is null || cableId is null)
        {
            Console.Error.WriteLine("soak requires (--render <i>|--render-id <id>) and (--capture <i>|--capture-id <id>)");
            return 1;
        }

        var root = options.GetValueOrDefault("root");
        var workDir = Path.Combine(string.IsNullOrEmpty(root)
            ? Path.Combine(Path.GetTempPath(), "onbody-soak")
            : Path.Combine(root, "tmp", "soak"));
        Directory.CreateDirectory(workDir);
        var tonePath = Path.Combine(workDir, $"soak-tone-{toneS:F0}s-440hz.wav");
        if (!File.Exists(tonePath))
        {
            WriteToneWav(tonePath, 440, 0.3, toneS);
        }

        Console.WriteLine($"soak: tone={tonePath} minutes={minutes} period={periodS}s");
        using var session = new AudioSession(new AudioSessionOptions
        {
            ClipDecoder = new NAudioClipDecoder(),
            MicAdapterFactory = (micDeviceId, channel) => new WindowsMicSource(micDeviceId, channel),
            AppAdapterFactory = (channel, binding) => new WindowsAppSource(binding, channel),
            OutputFactory = renderDeviceId => new WasapiExclusiveRenderPump(renderDeviceId),
            MonitorOutputFactory = monitorDeviceId => new WindowsMonitorOutput(monitorDeviceId),
            MonitorCaptureFactory = (cableDeviceId, _) => new WindowsMonitorCapture(cableDeviceId),
            InitialDevices = new PresetDeviceConfig(micId ?? "soak-no-mic", renderId, cableId, monitorId ?? cableId, null),
        });
        session.Noticed += notice => Console.WriteLine($"[notice] {notice.Kind}: {notice.Message}");

        var attach = session.AttachOutput();
        if (!attach.Accepted)
        {
            Console.Error.WriteLine($"soak: attach failed: {attach.Rejection}");
            return 1;
        }

        if (micId is not null)
        {
            var mic = session.EnableMic(true);
            Console.WriteLine(mic.Accepted ? "soak: mic send on" : $"soak: mic unavailable ({mic.Rejection}) — continuing extras-only");
        }

        Accept(session.EnableExtras(true));
        if (monitorId is not null)
        {
            var monitor = session.SetMonitor(true, MonitorMode.FullEndpoint);
            Console.WriteLine(monitor.Accepted ? "soak: full-endpoint monitor on" : $"soak: monitor unavailable ({monitor.Rejection})");
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var total = TimeSpan.FromMinutes(minutes);
        var nextPlay = TimeSpan.Zero;
        var nextSample = TimeSpan.Zero;
        var plays = 0;
        var playFailures = 0;
        Sample? first = null;
        Sample? last = null;
        var head = "t_min,ws_mb,gc_mb,mixed,underrun,hard_steps,clipped,peak,monitor_starved,monitor_queued,mic_ratio,app_ratio,clip_done";
        Console.WriteLine(head);

        while (sw.Elapsed < total)
        {
            if (sw.Elapsed >= nextPlay)
            {
                var play = session.PlayClip(tonePath);
                if (play.Accepted)
                {
                    plays++;
                }
                else
                {
                    playFailures++;
                    Console.WriteLine($"[soak] play rejected: {play.Rejection}");
                }

                nextPlay = sw.Elapsed + TimeSpan.FromSeconds(periodS);
            }

            if (sw.Elapsed >= nextSample)
            {
                var sample = SampleNow(session, sw.Elapsed.TotalMinutes);
                first ??= sample;
                last = sample;
                Console.WriteLine(sample.Line);
                nextSample = sw.Elapsed + TimeSpan.FromSeconds(10);
            }

            Thread.Sleep(250);
        }

        var spanMin = Math.Max(last!.TMin - first!.TMin, 1e-6);
        var wsDeltaMb = (last.WsBytes - first.WsBytes) / 1024.0 / 1024.0;
        var gcDeltaMb = (last.GcBytes - first.GcBytes) / 1024.0 / 1024.0;
        Console.WriteLine();
        Console.WriteLine($"soak-summary: duration_min={sw.Elapsed.TotalMinutes:F1} plays={plays} play_failures={playFailures}");
        Console.WriteLine($"soak-mem: ws_delta_mb={wsDeltaMb:F1} gc_delta_mb={gcDeltaMb:F1}");
        Console.WriteLine($"soak-rate: underrun_per_min={(last.Underrun - first.Underrun) / spanMin:F1} hard_steps_total={last.HardSteps - first.HardSteps} clipped_total={last.Clipped - first.Clipped}");
        Console.WriteLine($"soak-monitor: starved_total={last.MonitorStarved - first.MonitorStarved} queued_last={last.MonitorQueued}");
        Console.WriteLine($"soak-clip: completed={last.ClipCompleted - first.ClipCompleted} plays_accepted={plays}");
        return 0;
    }

    private static void Accept(CommandResult result)
    {
        if (!result.Accepted)
        {
            Console.Error.WriteLine($"soak: setup failed: {result.Rejection}");
            Environment.Exit(1);
        }
    }

    private static string? ResolveId(Dictionary<string, string> options, string idKey, string indexKey, bool capture)
    {
        if (options.TryGetValue(idKey, out var id))
        {
            return id;
        }

        if (!int.TryParse(options.GetValueOrDefault(indexKey), out var index))
        {
            return null;
        }

        using var device = WindowsAudioDevices.FindByIndex(index, capture);
        return device?.ID;
    }

    private static Sample SampleNow(AudioSession session, double tMin)
    {
        var m = session.Metrics();
        return new Sample(
            tMin,
            Environment.WorkingSet,
            GC.GetTotalMemory(forceFullCollection: false),
            m.MixerFramesMixed,
            m.MixerUnderrunFrames,
            m.MixerHardSteps,
            m.MixerClippedSamples,
            m.MixerPeak,
            m.Monitor.FullEndpointStarvedFrames,
            m.Monitor.QueuedFrames,
            m.Mic.Ratio,
            m.App.Ratio,
            m.Clip.CompletedPlaybacks);
    }

    private static void WriteToneWav(string path, double hz, double amplitude, double seconds)
    {
        const int rate = 44100;
        var frames = (int)(rate * seconds);
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        var dataBytes = frames * 2;
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write((short)1); // mono
        writer.Write(rate);
        writer.Write(rate * 2); // byte rate
        writer.Write((short)2); // block align
        writer.Write((short)16); // bits
        writer.Write("data"u8);
        writer.Write(dataBytes);
        for (var i = 0; i < frames; i++)
        {
            var value = amplitude * Math.Sin(2 * Math.PI * hz * i / rate);
            writer.Write((short)Math.Clamp(value * 32767, short.MinValue, short.MaxValue));
        }
    }

    private sealed record Sample(
        double TMin,
        long WsBytes,
        long GcBytes,
        long Mixed,
        long Underrun,
        long HardSteps,
        long Clipped,
        float Peak,
        long MonitorStarved,
        int MonitorQueued,
        double MicRatio,
        double AppRatio,
        long ClipCompleted)
    {
        public string Line =>
            $"{TMin:F2},{WsBytes / 1024.0 / 1024.0:F1},{GcBytes / 1024.0 / 1024.0:F1},{Mixed},{Underrun},{HardSteps},{Clipped},{Peak:F3},{MonitorStarved},{MonitorQueued},{MicRatio:F4},{AppRatio:F4},{ClipCompleted}";
    }
}
