namespace OnbodyMic.Cli;

using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using OnbodyMic.Platform.Windows;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
            return args.Length == 0 ? Usage() : args[0] switch
            {
                "devices" => ListDevices(args.Skip(1).Contains("--all"), args.Skip(1).Contains("--ids")),
                "enable" => SetEnabled(args[1..], true),
                "disable" => SetEnabled(args[1..], false),
                "setformat" => SetFormat(args[1..]),
                "sessions" => ListSessions(args[1..]),
                "tone" => PlayTone(args[1..]),
                "record" => Record(args[1..]),
                "analyze" => Analyze(args[1..]),
                "loop" => Loop(args[1..]),
                "mixloop" => MixLoop(args[1..]),
                "latency" => LatencyProbe(args[1..]),
                "proloop" => ProcLoop(args[1..]),
                "panel" => PanelApp.Run(args[1..]),
                _ => Usage(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static int Usage()
    {
        Console.WriteLine("""
            onbody <command>

            commands:
              devices [--all] [--ids]                list audio endpoints
              tone (--index <n> | --id <id>) [--seconds s] [--freq hz] [--gain g]
                                                      play a test sine to a render endpoint
              record (--index <n> | --id <id>) --seconds s --out <file.wav>
                                                      capture an endpoint to wav
              analyze --file <f.wav> [--freq hz]     goertzel tone detection on a wav
              loop (--render <i> | --render-id <id>) (--capture <i> | --capture-id <id>)
                    --out <file.wav> [--seconds s] [--freq hz] [--gain g]
                                                      same-process play+record loopback probe
              mixloop (--mic .. | --mic-id ..) (--render .. | --render-id ..) (--capture .. | --capture-id ..)
                    --out <file.wav> [--freq hz] [--gain g] [--micgain g] [--rate hz] [--seconds s]
                                                      mic+sfx mixed into the cable, verified at the capture side
                    [--monitor local|cable] [--render-latency ms] [--monitor-latency ms]
              analyze --file <f.wav> [--freq hz] [--peaks] [--onset <thr>]
              proloop --pid <n> --out <file.wav> [--seconds s] [--mode include|exclude]
                                                      per-process loopback capture to wav (T04/T05)
              latmatrix (--render <i> | --render-id <id>) (--capture <i> | --capture-id <id>)
                    [--bursts n] [--period-ms n] [--burst-ms n] [--engine mmcss|naudio]
                                                      200-burst engine->virtual-input latency matrix
              enable --id <id> | disable --id <id>    toggle a disabled MMDevice endpoint (IPolicyConfig)
              setformat --id <id> --rate <hz> [--channels c]
              panel [--mic-id id] [--render-id id] [--capture-id id] [--monitor-id id] [--app-pid n]
                                                      interactive P1 control panel (real session)
              setformat --id <id> --rate <hz> [--channels c]
                                                      set endpoint shared-mode format to float32 (IPolicyConfig)
            """);
        return 0;
    }

    private static int ListDevices(bool activeOnly, bool showIds)
    {
        var devices = WindowsAudioDevices.List(activeOnly);
        var defaultRender = WindowsAudioDevices.DefaultDeviceId(capture: false);
        var defaultCapture = WindowsAudioDevices.DefaultDeviceId(capture: true);
        Console.WriteLine("idx flow     state   name (* = default)");
        for (var i = 0; i < devices.Count; i++)
        {
            var device = devices[i];
            var marker = device.Id == (device.DataFlow == "Capture" ? defaultCapture : defaultRender) ? "*" : " ";
            Console.WriteLine($"{i,3} {device.DataFlow,-8} {device.State,-7}{marker}{device.FriendlyName}");
            if (showIds)
            {
                Console.WriteLine($"      id={device.Id}");
            }
        }

        return 0;
    }

    private static int SetEnabled(string[] rest, bool enabled)
    {
        var options = ParseOptions(rest);
        if (!options.TryGetValue("id", out var id))
        {
            Console.Error.WriteLine("enable/disable requires --id <endpointId>");
            return 1;
        }

        WindowsEndpointControl.SetEnabled(id, enabled);
        Console.WriteLine($"{(enabled ? "enabled" : "disabled")}: {id}");
        return 0;
    }

    private static int SetFormat(string[] rest)
    {
        var options = ParseOptions(rest);
        if (!options.TryGetValue("id", out var id) || !options.ContainsKey("rate"))
        {
            Console.Error.WriteLine("setformat requires --id <endpointId> --rate <hz> [--channels c]");
            return 1;
        }

        var rate = (int)ParseDouble(options, "rate", 0);
        var channels = (int)ParseDouble(options, "channels", 1);
        WindowsEndpointControl.SetDeviceFormat(id, rate, channels);
        Console.WriteLine($"set format: {id} -> {rate} Hz, {channels} ch, float32");
        return 0;
    }

    private static int ListSessions(string[] rest)
    {
        var options = ParseOptions(rest);
        if (!options.TryGetValue("id", out var id))
        {
            Console.Error.WriteLine("sessions requires --id <endpointId>");
            return 1;
        }

        using var device = WindowsAudioDevices.OpenById(id);
        Console.WriteLine($"endpoint={device.FriendlyName}");
        var sessions = WindowsEndpointSessions.List(id);
        for (var i = 0; i < sessions.Count; i++)
        {
            Console.WriteLine($"session[{i}] pid={sessions[i].ProcessId} process={sessions[i].ProcessName} state={sessions[i].State}");
        }

        Console.WriteLine($"total={sessions.Count}");
        return 0;
    }
    private static int PlayTone(string[] rest)
    {
        Console.Error.WriteLine($"pid={Environment.ProcessId}");
        var options = ParseOptions(rest);
        if (!options.ContainsKey("id") && !options.ContainsKey("index"))
        {
            Console.Error.WriteLine("tone requires --index <n> or --id <endpointId> (render endpoint from `devices`)");
            return 1;
        }

        var seconds = ParseDouble(options, "seconds", 5);
        var frequency = ParseDouble(options, "freq", 440);
        var gain = ParseDouble(options, "gain", 0.4);
        if (seconds <= 0 || frequency <= 0 || gain is < 0 or > 1)
        {
            Console.Error.WriteLine("seconds/freq must be positive and gain must be within [0,1]");
            return 1;
        }

        using var device = ResolveDevice(options, "index", "id", capture: false);
        if (device is null)
        {
            Console.Error.WriteLine("render endpoint not found");
            return 1;
        }

        using var output = new WasapiOut(device, AudioClientShareMode.Shared, false, 50);
        var sine = new SineWaveProvider(frequency, gain);
        output.Init(sine);
        output.Play();
        Console.WriteLine($"playing {frequency:N0} Hz for {seconds:N1}s to: {device.FriendlyName}");
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        output.Stop();
        return 0;
    }

    private static int Record(string[] rest)
    {
        var options = ParseOptions(rest);
        if (!options.ContainsKey("id") && !options.ContainsKey("index"))
        {
            Console.Error.WriteLine("record requires --index <n> or --id <endpointId> (capture endpoint from `devices`)");
            return 1;
        }

        if (!options.TryGetValue("out", out var outFile))
        {
            Console.Error.WriteLine("record requires --out <file.wav>");
            return 1;
        }

        var seconds = ParseDouble(options, "seconds", 5);
        if (seconds <= 0)
        {
            Console.Error.WriteLine("seconds must be positive");
            return 1;
        }

        using var device = ResolveDevice(options, "index", "id", capture: true);
        if (device is null)
        {
            Console.Error.WriteLine("capture endpoint not found");
            return 1;
        }

        using var capture = new WasapiCapture(device);
        using var writer = new WaveFileWriter(outFile, capture.WaveFormat);
        using var finished = new ManualResetEvent(false);
        capture.DataAvailable += (_, e) => writer.Write(e.Buffer, 0, e.BytesRecorded);
        capture.RecordingStopped += (_, _) => finished.Set();
        capture.StartRecording();
        Console.WriteLine($"recording {seconds:N1}s from: {device.FriendlyName}");
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        capture.StopRecording();
        finished.WaitOne(TimeSpan.FromSeconds(2));
        writer.Flush();
        return 0;
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                result[args[i][2..]] = args[i + 1];
            }
        }

        return result;
    }
    private static MMDevice? ResolveDevice(Dictionary<string, string> options, string indexKey, string idKey, bool capture)
    {
        if (options.TryGetValue(idKey, out var id))
        {
            try
            {
                return WindowsAudioDevices.OpenById(id);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"endpoint id lookup failed: {ex.Message}");
                return null;
            }
        }

        return int.TryParse(options.GetValueOrDefault(indexKey), out var index)
            ? WindowsAudioDevices.FindByIndex(index, capture)
            : null;
    }

    private static double ParseDouble(Dictionary<string, string> options, string key, double fallback) =>
        double.TryParse(options.GetValueOrDefault(key), out var value) ? value : fallback;

    private static int Loop(string[] rest)
    {
        var options = ParseOptions(rest);
        var hasOut = options.TryGetValue("out", out var outFile);
        var hasRender = options.ContainsKey("render") || options.ContainsKey("render-id");
        var hasCapture = options.ContainsKey("capture") || options.ContainsKey("capture-id");
        if (!hasOut || !hasRender || !hasCapture)
        {
            Console.Error.WriteLine("loop requires (--render <i> | --render-id <id>) (--capture <i> | --capture-id <id>) --out <file.wav>");
            return 1;
        }

        var seconds = ParseDouble(options, "seconds", 4);
        var frequency = ParseDouble(options, "freq", 440);
        var gain = ParseDouble(options, "gain", 0.35);
        var exclusive = "exclusive".Equals(options.GetValueOrDefault("share", "shared"), StringComparison.OrdinalIgnoreCase);
        var rate = (int)ParseDouble(options, "rate", 0);
        var channels = (int)ParseDouble(options, "channels", 2);
        var pcm16 = (int)ParseDouble(options, "bits", 32) == 16;
        if (seconds <= 2 || frequency <= 0 || gain is < 0 or > 1)
        {
            Console.Error.WriteLine("seconds must be > 2; freq must be positive; gain within [0,1]");
            return 1;
        }

        using var renderDevice = ResolveDevice(options, "render", "render-id", capture: false);
        using var captureDevice = ResolveDevice(options, "capture", "capture-id", capture: true);
        if (renderDevice is null || captureDevice is null)
        {
            Console.Error.WriteLine("render or capture endpoint not found");
            return 1;
        }

        using var capture = new WasapiCapture(captureDevice);
        using var writer = new WaveFileWriter(outFile, capture.WaveFormat);
        var firstSampleTicks = 0L;
        var startedAt = Stopwatch.GetTimestamp();
        capture.DataAvailable += (_, e) =>
        {
            if (e.BytesRecorded > 0)
            {
                writer.Write(e.Buffer, 0, e.BytesRecorded);
            }
        };

        capture.StartRecording();
        Thread.Sleep(500);
        var toneStartedAt = Stopwatch.GetTimestamp();

        var sineFormat = rate > 0
            ? (pcm16 ? new WaveFormat(rate, 16, channels) : WaveFormat.CreateIeeeFloatWaveFormat(rate, channels))
            : null;
        using var output = new WasapiOut(renderDevice, exclusive ? AudioClientShareMode.Exclusive : AudioClientShareMode.Shared, false, 50);
        var sine = new SineWaveProvider(frequency, gain, sineFormat);
        output.Init(sine);
        output.Play();
        Thread.Sleep(TimeSpan.FromSeconds(seconds - 1));
        output.Stop();
        Thread.Sleep(250);
        capture.StopRecording();
        Thread.Sleep(250);
        writer.Flush();

        Console.WriteLine($"render={renderDevice.FriendlyName}");
        Console.WriteLine($"capture={captureDevice.FriendlyName}");
        Console.WriteLine($"capture_format={capture.WaveFormat}");
        Console.WriteLine($"sine_format={sine.WaveFormat} share={(exclusive ? "exclusive" : "shared")}");
        Console.WriteLine($"tone_started_at_ms={(toneStartedAt - startedAt) * 1000.0 / Stopwatch.Frequency:F1}");
        _ = firstSampleTicks; // latency marking is P0 follow-up; keep the CLI deterministic for now.
        return 0;
    }

    private static int MixLoop(string[] rest)
    {
        var options = ParseOptions(rest);
        var hasMic = options.ContainsKey("mic") || options.ContainsKey("mic-id");
        var hasRender = options.ContainsKey("render") || options.ContainsKey("render-id");
        var hasCable = options.ContainsKey("capture") || options.ContainsKey("capture-id");
        if (!hasMic || !hasRender || !hasCable || !options.TryGetValue("out", out var outFile))
        {
            Console.Error.WriteLine("mixloop requires (--mic <i> | --mic-id <id>) (--render <i> | --render-id <id>) (--capture <i> | --capture-id <id>) --out <file.wav>");
            return 1;
        }

        var seconds = ParseDouble(options, "seconds", 6);
        var frequency = ParseDouble(options, "freq", 440);
        var gain = ParseDouble(options, "gain", 0.3);
        var micGain = ParseDouble(options, "micgain", 1.0);
        var rate = (int)ParseDouble(options, "rate", 44100);
        var monitorMode = options.GetValueOrDefault("monitor", "");

        var renderLatencyMs = (int)ParseDouble(options, "render-latency", 50);
        var monitorLatencyMs = (int)ParseDouble(options, "monitor-latency", 80);
        if (seconds <= 2 || frequency <= 0 || gain is < 0 or > 1 || micGain < 0)
        {
            Console.Error.WriteLine("seconds>2, freq>0, gain within [0,1], micgain>=0");
            return 1;
        }

        using var micDevice = ResolveDevice(options, "mic", "mic-id", capture: true);
        using var renderDevice = ResolveDevice(options, "render", "render-id", capture: false);
        using var cableDevice = ResolveDevice(options, "capture", "capture-id", capture: true);
        if (micDevice is null || renderDevice is null || cableDevice is null)
        {
            Console.Error.WriteLine("mic, render or cable capture endpoint not found");
            return 1;
        }

        var startedAt = Stopwatch.GetTimestamp();

        // verification capture: exactly what KOOK would hear from the cable
        using var cableCapture = new WasapiCapture(cableDevice, true, 20);
        using var writer = new WaveFileWriter(outFile, cableCapture.WaveFormat);
        var monitorBuffer = monitorMode == "cable"
            ? new BufferedWaveProvider(cableCapture.WaveFormat) { DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromSeconds(2) }
            : null;
        cableCapture.DataAvailable += (_, e) =>
        {
            writer.Write(e.Buffer, 0, e.BytesRecorded);
            monitorBuffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);
        };
        cableCapture.StartRecording();

        // live mic buffered, then resampled into the cable's exclusive format
        using var micCapture = new WasapiCapture(micDevice, true, 20);
        var micBuffer = new BufferedWaveProvider(micCapture.WaveFormat)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(2),
        };
        micCapture.DataAvailable += (_, e) => micBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
        micCapture.StartRecording();
        Thread.Sleep(300);

        var cableFloat = WaveFormat.CreateIeeeFloatWaveFormat(rate, 2);
        var micResampled = new WdlResamplingSampleProvider(micBuffer.ToSampleProvider(), rate);
        var micScaled = new VolumeSampleProvider(micResampled) { Volume = (float)micGain };
        var beepHz = ParseDouble(options, "beep", 0);
        var sine = new SineWaveProvider(frequency, gain, cableFloat, beepHz).ToSampleProvider();
        var mixer = new MixingSampleProvider(cableFloat) { ReadFully = true };
        mixer.AddMixerInput(micScaled);
        mixer.AddMixerInput(sine);

        using var output = new WasapiOut(renderDevice, AudioClientShareMode.Exclusive, true, renderLatencyMs);
        var mixStream = new SampleToWaveProvider16(mixer);
        BroadcastWaveProvider? tap = null;
        if (monitorMode == "local")
        {
            tap = new BroadcastWaveProvider(mixStream);
            output.Init(tap);
        }
        else
        {
            output.Init(mixStream);
        }

        output.Play();
        WasapiOut? monitorOut = null;
        if (monitorMode == "local")
        {
            monitorOut = new WasapiOut(AudioClientShareMode.Shared, monitorLatencyMs);
            monitorOut.Init(tap!.AddSink());
            monitorOut.Play();
        }
        else if (monitorMode == "cable" && monitorBuffer is not null)
        {
            monitorOut = new WasapiOut(AudioClientShareMode.Shared, monitorLatencyMs);
            monitorOut.Init(new SampleToWaveProvider(new MonoToStereoSampleProvider(monitorBuffer.ToSampleProvider())));
            monitorOut.Play();
        }

        // jitter control: elastic buffers must hold a few periods, never a standing backlog.
        // Startup bursts can fill them to the cap (the reported ~1s self-voice delay); trim
        // continuously and print depths so the bound stays observable.
        var trimStop = new ManualResetEvent(false);
        var micScratch = new byte[16 * 1024];
        var trimThread = new Thread(() =>
        {
            var statsAt = Stopwatch.GetTimestamp();
            var micDiscardedMs = 0.0;
            while (!trimStop.WaitOne(40))
            {
                micDiscardedMs += TrimTo(micBuffer, TimeSpan.FromMilliseconds(80), micScratch);
                tap?.TrimSinks(TimeSpan.FromMilliseconds(60));
                var now = Stopwatch.GetTimestamp();
                if ((now - statsAt) * 1000.0 / Stopwatch.Frequency >= 1000)
                {
                    Console.WriteLine($"buffers_ms mic={micBuffer.BufferedDuration.TotalMilliseconds:F0} monitor={(tap?.SinkDepthMs() ?? -1):F0} discard_ms_per_s={micDiscardedMs:F1}");
                    micDiscardedMs = 0;
                    statsAt = now;
                }
            }
        })
        { IsBackground = true };
        trimThread.Start();

        Console.WriteLine($"mixing mic + {frequency:N0} Hz -> {renderDevice.FriendlyName} (exclusive {rate} Hz)");
        Console.WriteLine($"verifying from cable capture: {cableDevice.FriendlyName}");
        if (monitorOut is not null)
        {
            Console.WriteLine($"monitor={monitorMode} -> 默认输出设备 (local=混音直通低延迟; cable=实际线缆内容, 含你自己的声音)");
        }

        Console.WriteLine("speak into the mic during this run ...");
        Thread.Sleep(TimeSpan.FromSeconds(seconds - 1));
        trimStop.Set();
        trimThread.Join(500);
        output.Stop();
        monitorOut?.Stop();
        monitorOut?.Dispose();
        micCapture.StopRecording();
        Thread.Sleep(300);
        cableCapture.StopRecording();
        Thread.Sleep(300);
        writer.Flush();

        Console.WriteLine($"elapsed_ms={(Stopwatch.GetTimestamp() - startedAt) * 1000.0 / Stopwatch.Frequency:F1}");
        return 0;
    }

    private static int LatMatrix(string[] rest)
    {
        var options = ParseOptions(rest);
        var hasRender = options.ContainsKey("render") || options.ContainsKey("render-id");
        var hasCable = options.ContainsKey("capture") || options.ContainsKey("capture-id");
        if (!hasRender || !hasCable)
        {
            Console.Error.WriteLine("latmatrix requires (--render <i> | --render-id <id>) (--capture <i> | --capture-id <id>)");
            return 1;
        }

        var burstCount = (int)ParseDouble(options, "bursts", 200);
        var periodMs = (int)ParseDouble(options, "period-ms", 250);
        var burstMs = (int)ParseDouble(options, "burst-ms", 30);
        var rate = 44100;
        if (burstCount < 10 || periodMs < 100 || burstMs is < 5 or > 100)
        {
            Console.Error.WriteLine("bursts>=10, period-ms>=100, burst-ms 5..100");
            return 1;
        }

        using var renderDevice = ResolveDevice(options, "render", "render-id", capture: false);
        using var cableDevice = ResolveDevice(options, "capture", "capture-id", capture: true);
        if (renderDevice is null || cableDevice is null)
        {
            Console.Error.WriteLine("render or cable capture endpoint not found");
            return 1;
        }

        var engine = options.GetValueOrDefault("engine", "mmcss");
        if (engine.Equals("mmcss", StringComparison.OrdinalIgnoreCase))
        {
            return LatMatrixMmcss(renderDevice.ID, cableDevice.ID, burstCount, periodMs, burstMs,
                (int)ParseDouble(options, "render-latency", 10), (int)ParseDouble(options, "capture-latency", 10));
        }

        var provider = new BurstWaveProvider(rate, 2, 440, 0.35, burstMs, periodMs);
        var onsets = new List<long>();
        long totalSamples = 0;
        long lastOnsetSample = -2 * (long)(0.08 * rate);
        float maxAbs = 0;
        var quietGap = (long)(0.08 * rate);
        var threshold = 0.1f;

        var captureBufferMs = (int)ParseDouble(options, "capture-latency", 20);
        using var cableCapture = new WasapiCapture(cableDevice, true, captureBufferMs);
        cableCapture.DataAvailable += (_, e) =>
        {
            var samples = e.BytesRecorded / sizeof(float);
            var tick = Stopwatch.GetTimestamp();
            for (var i = 0; i < samples; i++)
            {
                var value = Math.Abs(BitConverter.ToSingle(e.Buffer, i * sizeof(float)));
                maxAbs = Math.Max(maxAbs, value);
                var position = totalSamples + i;
                if (value > threshold && position - lastOnsetSample >= quietGap)
                {
                    onsets.Add(tick);
                    lastOnsetSample = position;
                }
            }

            totalSamples += samples;
        };
        cableCapture.StartRecording();
        Thread.Sleep(500);

        var renderEventSync = options.GetValueOrDefault("render-event", "on").Equals("on", StringComparison.OrdinalIgnoreCase);
        var renderBufferMs = (int)ParseDouble(options, "render-latency", 20);
        using var output = new WasapiOut(renderDevice, AudioClientShareMode.Exclusive, renderEventSync, renderBufferMs);
        output.Init(provider);
        output.Play();

        var expectedSeconds = burstCount * periodMs / 1000.0 + 1;
        Thread.Sleep(TimeSpan.FromSeconds(expectedSeconds));
        output.Stop();
        Thread.Sleep(400);
        cableCapture.StopRecording();

        var generated = provider.BurstTicks;
        var usable = Math.Min(generated.Count, onsets.Count) - 5; // drop warmup bursts
        if (usable < 10)
        {
            Console.Error.WriteLine($"insufficient samples: generated={generated.Count} detected={onsets.Count}");
            return 2;
        }

        var latencies = new List<double>();
        for (var i = 5; i < 5 + usable; i++)
        {
            latencies.Add((onsets[i] - generated[i]) * 1000.0 / Stopwatch.Frequency);
        }

        latencies.Sort();
        double Percentile(int p) => latencies[Math.Min(latencies.Count - 1, latencies.Count * p / 100)];
        Console.WriteLine($"capture_samples={totalSamples} max_abs={maxAbs:F3}");
        Console.WriteLine($"render={renderDevice.FriendlyName} capture={cableDevice.FriendlyName}");
        Console.WriteLine($"bursts generated={generated.Count} detected={onsets.Count} matched={latencies.Count} missed={generated.Count - onsets.Count}");
        Console.WriteLine($"latency_ms p50={Percentile(50):F1} p95={Percentile(95):F1} p99={Percentile(99):F1} max={latencies[^1]:F1} min={latencies[0]:F1}");
        return 0;
    }


    /// <summary>
    /// Latency matrix on own MMCSS-boosted event-driven pumps: exclusive render + shared
    /// capture, both on Highest-priority threads registered as Pro Audio. This is the seed
    /// of the P1 audio engine; NAudio's normal-priority threads cannot hold 10ms deadlines.
    /// </summary>
    private static int LatMatrixMmcss(string renderId, string cableId, int burstCount, int periodMs, int burstMs, int renderBufferMs, int captureBufferMs)
    {
        const int rate = 44100;
        var burstFrames = rate * burstMs / 1000;
        var periodFrames = rate * periodMs / 1000;
        var generated = new List<long>();
        var onsets = new List<long>();
        long totalFrames = 0;
        long lastOnsetFrame = -2 * (long)(0.08 * rate);
        long quietGap = (long)(0.08 * rate);
        const float threshold = 0.1f;
        var maxAbs = 0f;
        var running = true;

        var renderClient = WindowsLowLevelAudio.ActivateClient(renderId);
        var formatPtr = WindowsLowLevelAudio.CreateWaveFormatPcm16(rate, 2);
        var renderHns = renderBufferMs * 10000L;
        Marshal.ThrowExceptionForHR(renderClient.Initialize(
            WindowsLowLevelAudio.ShareModeExclusive, WindowsLowLevelAudio.StreamFlagsEventCallback,
            renderHns, renderHns, formatPtr, IntPtr.Zero));
        Marshal.ThrowExceptionForHR(renderClient.GetBufferSize(out var bufferFrames));
        var renderIid = WindowsLowLevelAudio.IidIAudioRenderClient;
        Marshal.ThrowExceptionForHR(renderClient.GetService(ref renderIid, out var renderService));
        var render = (IAudioRenderClientCom)renderService;
        var renderEvent = WindowsLowLevelAudio.CreateEventHandle();
        Marshal.ThrowExceptionForHR(renderClient.SetEventHandle(renderEvent));

        var captureClient = WindowsLowLevelAudio.ActivateClient(cableId);
        Marshal.ThrowExceptionForHR(captureClient.GetMixFormat(out var mixFormatPtr));
        var channels = Marshal.ReadInt16(mixFormatPtr, 2);
        var captureRate = Marshal.ReadInt32(mixFormatPtr, 4);
        var captureIid = WindowsLowLevelAudio.IidIAudioCaptureClient;
        Marshal.ThrowExceptionForHR(captureClient.Initialize(
            WindowsLowLevelAudio.ShareModeShared, WindowsLowLevelAudio.StreamFlagsEventCallback,
            captureBufferMs * 10000L, 0, mixFormatPtr, IntPtr.Zero));
        Marshal.ThrowExceptionForHR(captureClient.GetService(ref captureIid, out var captureService));
        var capture = (IAudioCaptureClientCom)captureService;
        var captureEvent = WindowsLowLevelAudio.CreateEventHandle();
        Marshal.ThrowExceptionForHR(captureClient.SetEventHandle(captureEvent));

        var captureThread = new Thread(() =>
        {
            using var mmcss = new MmcssScope();
            mmcss.Acquire();
            Thread.CurrentThread.Priority = ThreadPriority.Highest;
            Marshal.ThrowExceptionForHR(captureClient.Start());
            while (running)
            {
                if (WindowsLowLevelAudio.WaitForEvent(captureEvent, 2000) != 0)
                {
                    continue;
                }

                while (true)
                {
                    Marshal.ThrowExceptionForHR(capture.GetNextPacketSize(out var frames));
                    if (frames == 0)
                    {
                        break;
                    }

                    Marshal.ThrowExceptionForHR(capture.GetBuffer(out var data, out frames, out _, out _, out _));
                    var tick = Stopwatch.GetTimestamp();
                    var floatCount = (int)frames * channels;
                    for (var i = 0; i < floatCount; i += channels)
                    {
                        var value = Math.Abs(BitConverter.Int32BitsToSingle(Marshal.ReadInt32(data, i * sizeof(float))));
                        maxAbs = Math.Max(maxAbs, value);
                        var framePosition = totalFrames + (i / channels);
                        if (value > threshold && framePosition - lastOnsetFrame >= quietGap)
                        {
                            onsets.Add(tick);
                            lastOnsetFrame = framePosition;
                        }
                    }

                    totalFrames += frames;
                    Marshal.ThrowExceptionForHR(capture.ReleaseBuffer(frames));
                }
            }
        })
        { IsBackground = true, Name = "mmcss-capture" };

        long renderWritten = 0;
        long renderEvents = 0;
        long renderOffered = 0;
        long lastPadding = -1;
        var renderFault = string.Empty;
        var renderThread = new Thread(() =>
        {
        try
        {
            using var mmcss = new MmcssScope();
            mmcss.Acquire();
            Thread.CurrentThread.Priority = ThreadPriority.Highest;
            var phase = 0.0;
            long written = 0;
            long nextBoundary = 0;
            void FillRender(int frames)
            {
                Marshal.ThrowExceptionForHR(render.GetBuffer(frames, out var data));
                for (var f = 0; f < frames; f++)
                {
                    if (written >= nextBoundary)
                    {
                        generated.Add(Stopwatch.GetTimestamp());
                        nextBoundary += periodFrames;
                    }

                    var inBurst = (written % periodFrames) < burstFrames;
                    var value = inBurst ? Math.Sin(phase) * 0.35 * short.MaxValue : 0;
                    Marshal.WriteInt16(data, (f * 2) * sizeof(short), (short)value);
                    Marshal.WriteInt16(data, ((f * 2) + 1) * sizeof(short), (short)value);
                    phase += 2.0 * Math.PI * 440 / rate;
                    if (phase > 2.0 * Math.PI)
                    {
                        phase -= 2.0 * Math.PI;
                    }

                    written++;
                }

                renderWritten = written;
                Marshal.ThrowExceptionForHR(render.ReleaseBuffer(frames, 0));
            }

            // exclusive event-mode contract: prefill the whole buffer, then one event
            // equals one full buffer period. CurrentPadding is unreliable here (the Steam
            // driver mis-paces under-filled streams at ~0.65x realtime) — NAudio writes
            // full bufferFrames per event for the same reason.
            FillRender((int)bufferFrames);
            Marshal.ThrowExceptionForHR(renderClient.Start());
            while (running && generated.Count < burstCount + 5)
            {
                if (WindowsLowLevelAudio.WaitForEvent(renderEvent, 2000) != 0)
                {
                    continue;
                }

                renderEvents++;
                renderOffered += bufferFrames;
                FillRender((int)bufferFrames);
            }

        }
        catch (Exception ex)
        {
            renderFault = ex.ToString();
        }
        })
        { IsBackground = true, Name = "mmcss-render" };

        captureThread.Start();
        Thread.Sleep(500);
        renderThread.Start();
        Thread.Sleep(TimeSpan.FromSeconds(burstCount * periodMs / 1000.0 + 1));
        running = false;
        renderThread.Join(1000);
        captureThread.Join(1000);
        _ = renderClient.Stop();
        _ = captureClient.Stop();
        Console.WriteLine($"render_diag events={renderEvents} offered={renderOffered} written={renderWritten} buffer_frames={bufferFrames} last_padding={lastPadding} fault={(string.IsNullOrEmpty(renderFault) ? "none" : renderFault)}");
        WindowsLowLevelAudio.CloseEventHandle(renderEvent);
        WindowsLowLevelAudio.CloseEventHandle(captureEvent);
        Marshal.FreeHGlobal(formatPtr);
        Marshal.FreeCoTaskMem(mixFormatPtr);
        _ = Marshal.ReleaseComObject(renderClient);
        _ = Marshal.ReleaseComObject(captureClient);

        var usable = Math.Min(generated.Count, onsets.Count) - 5;
        if (usable < 10)
        {
            Console.Error.WriteLine($"insufficient samples: generated={generated.Count} detected={onsets.Count} render_written={renderWritten} fault={(string.IsNullOrEmpty(renderFault) ? "none" : renderFault)}");
            return 2;
        }

        var latencies = new List<double>();
        for (var i = 5; i < 5 + usable; i++)
        {
            latencies.Add((onsets[i] - generated[i]) * 1000.0 / Stopwatch.Frequency);
        }

        latencies.Sort();
        double Percentile(int p) => latencies[Math.Min(latencies.Count - 1, latencies.Count * p / 100)];
        Console.WriteLine($"engine=mmcss render={renderBufferMs}ms capture={captureBufferMs}ms cable_rate={captureRate}");
        Console.WriteLine($"capture_frames={totalFrames} max_abs={maxAbs:F3}");
        Console.WriteLine($"bursts generated={generated.Count} detected={onsets.Count} matched={latencies.Count} missed={generated.Count - onsets.Count}");
        Console.WriteLine($"latency_ms p50={Percentile(50):F1} p95={Percentile(95):F1} p99={Percentile(99):F1} max={latencies[^1]:F1} min={latencies[0]:F1}");
        return 0;
    }
    private static int ProcLoop(string[] rest)
    {
        var options = ParseOptions(rest);
        if (!options.TryGetValue("pid", out var pidText) || !uint.TryParse(pidText, out var pid) ||
            !options.TryGetValue("out", out var outFile))
        {
            Console.Error.WriteLine("proloop requires --pid <n> --out <file.wav> [--seconds s] [--mode include|exclude]");
            return 1;
        }

        var seconds = ParseDouble(options, "seconds", 6);
        if (seconds <= 0)
        {
            Console.Error.WriteLine("seconds must be positive");
            return 1;
        }

        var include = !options.GetValueOrDefault("mode", "include").Equals("exclude", StringComparison.OrdinalIgnoreCase);
        using var capture = new ProcessLoopbackCapture();
        using var writer = new WaveFileWriter(outFile, capture.WaveFormat);
        capture.DataAvailable += (buffer, count) => writer.Write(buffer, 0, count);
        capture.Start(pid, include);
        Console.WriteLine($"capturing pid={pid} mode={(include ? "include-tree" : "exclude-tree")} format={capture.WaveFormat}");
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        capture.Stop();
        writer.Flush();
        return 0;
    }

    private static int Analyze(string[] rest)
    {
        var options = ParseOptions(rest);
        if (!options.TryGetValue("file", out var file) || !File.Exists(file))
        {
            Console.Error.WriteLine("analyze requires --file <existing.wav>");
            return 1;
        }

        var frequency = ParseDouble(options, "freq", 440);
        using var reader = new WaveFileReader(file);
        var sampleRate = reader.WaveFormat.SampleRate;
        var channels = reader.WaveFormat.Channels;
        var samples = new List<float>();
        var buffer = new byte[4096];
        while (reader.Position < reader.Length)
        {
            var bytesRead = reader.Read(buffer, 0, buffer.Length);
            if (bytesRead <= 0)
            {
                break;
            }

            var bytesPerSample = reader.WaveFormat.BitsPerSample switch
            {
                16 => 2,
                24 => 3,
                32 => 4,
                _ => throw new NotSupportedException($"unsupported bits per sample: {reader.WaveFormat.BitsPerSample}"),
            };

            var frameBytes = bytesPerSample * channels;
            for (var offset = 0; offset + frameBytes <= bytesRead; offset += frameBytes)
            {
                var sum = 0f;
                for (var c = 0; c < channels; c++)
                {
                    var at = offset + (c * bytesPerSample);
                    sum += bytesPerSample switch
                    {
                        2 => BitConverter.ToInt16(buffer, at) / 32768f,
                        3 => ReadInt24(buffer, at) / 8388608f,
                        _ => BitConverter.ToSingle(buffer, at),
                    };
                }

                samples.Add(sum / channels);
            }
        }

        if (samples.Count < sampleRate / 2)
        {
            Console.WriteLine("result=too-short");
            return 2;
        }

        var rms = Math.Sqrt(samples.Average(s => (double)s * s));
        var tonePower = GoertzelPower(samples, sampleRate, frequency);
        var noisePower = (GoertzelPower(samples, sampleRate, frequency * 0.7) +
                          GoertzelPower(samples, sampleRate, frequency * 1.3)) / 2;
        var ratio = noisePower > 0 ? tonePower / noisePower : double.PositiveInfinity;
        Console.WriteLine($"seconds={samples.Count / (double)sampleRate:F2}");
        Console.WriteLine($"rms={rms:F6}");
        Console.WriteLine($"tone_power={tonePower:F2}");
        Console.WriteLine($"noise_power={noisePower:F2}");
        if (options.ContainsKey("onset"))
        {
            var threshold = ParseDouble(options, "onset", 0.02);
            var onset = samples.FindIndex(s => Math.Abs(s) > threshold);
            Console.WriteLine(onset < 0 ? "onset=none" : $"onset_ms={onset * 1000.0 / sampleRate:F0}");
        }
        Console.WriteLine($"tone_noise_ratio={ratio:F2}");

        if (options.ContainsKey("peaks"))
        {
            Console.WriteLine("top spectral peaks (goertzel scan 40..8000 Hz, step 2 Hz):");
            var energies = new List<(double Hz, double Power)>();
            for (var f = 40.0; f <= 8000.0; f += 2.0)
            {
                energies.Add((f, GoertzelPower(samples, sampleRate, f)));
            }

            foreach (var (hz, power) in energies.OrderByDescending(x => x.Power).Take(8))
            {
                Console.WriteLine($"  {hz,7:F0} Hz  power={power:F2}");
            }
        }

        Console.WriteLine(ratio > 20 && rms > 0.0005 ? "result=tone-detected" : "result=no-tone");
        return 0;
    }


    private static double TrimTo(BufferedWaveProvider buffer, TimeSpan target, byte[] scratch)
    {
        var bytesPerMs = buffer.WaveFormat.AverageBytesPerSecond / 1000.0;
        var targetBytes = (int)(target.TotalSeconds * buffer.WaveFormat.AverageBytesPerSecond);
        var discarded = 0;
        while (buffer.BufferedBytes > targetBytes)
        {
            var take = Math.Min(scratch.Length, buffer.BufferedBytes - targetBytes);
            var read = buffer.Read(scratch, 0, take);
            if (read <= 0)
            {
                break;
            }

            discarded += read;
        }

        return discarded / bytesPerMs;
    }
    private static double GoertzelPower(List<float> samples, int sampleRate, double frequency)
    {
        var k = (int)Math.Round(frequency * samples.Count / sampleRate);
        var omega = 2.0 * Math.PI * k / samples.Count;
        var coeff = 2.0 * Math.Cos(omega);
        double s0 = 0, s1 = 0, s2 = 0;
        foreach (var sample in samples)
        {
            s0 = sample + coeff * s1 - s2;
            s2 = s1;
            s1 = s0;
        }

        return s1 * s1 + s2 * s2 - coeff * s1 * s2;
    }

    private static int ReadInt24(byte[] buffer, int offset) =>
        buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16);

    /// <summary>
    /// Plays a continuous sine into the cable render while recording the cable capture (A)
    /// and a loopback of the default render (B, the monitor path). Cross-correlates 1ms
    /// envelopes to report the monitor-path latency precisely; absolute legs stay approximate
    /// because capture start offsets are engine-side.
    /// </summary>
    private static int LatencyProbe(string[] rest)
    {
        var options = ParseOptions(rest);
        var hasRender = options.ContainsKey("render") || options.ContainsKey("render-id");
        var hasCable = options.ContainsKey("capture") || options.ContainsKey("capture-id");
        if (!hasRender || !hasCable || !options.TryGetValue("outa", out var outA) || !options.TryGetValue("outb", out var outB))
        {
            Console.Error.WriteLine("latency requires (--render <i> | --render-id <id>) (--capture <i> | --capture-id <id>) --outa <cable.wav> --outb <loopback.wav>");
            return 1;
        }

        var frequency = ParseDouble(options, "freq", 440);
        var gain = ParseDouble(options, "gain", 0.4);
        var renderLatencyMs = (int)ParseDouble(options, "render-latency", 50);
        using var renderDevice = ResolveDevice(options, "render", "render-id", capture: false);
        using var cableDevice = ResolveDevice(options, "capture", "capture-id", capture: true);
        if (renderDevice is null || cableDevice is null)
        {
            Console.Error.WriteLine("render or cable capture endpoint not found");
            return 1;
        }

        using var loopback = new WasapiLoopbackCapture();
        using var writerB = new WaveFileWriter(outB, loopback.WaveFormat);
        loopback.DataAvailable += (_, e) => writerB.Write(e.Buffer, 0, e.BytesRecorded);

        using var cableCapture = new WasapiCapture(cableDevice);
        using var writerA = new WaveFileWriter(outA, cableCapture.WaveFormat);
        cableCapture.DataAvailable += (_, e) => writerA.Write(e.Buffer, 0, e.BytesRecorded);

        loopback.StartRecording();
        cableCapture.StartRecording();
        Thread.Sleep(500);

        using var output = new WasapiOut(renderDevice, AudioClientShareMode.Exclusive, true, renderLatencyMs);
        output.Init(new SineWaveProvider(frequency, gain, new WaveFormat(44100, 16, 2)));
        output.Play();
        Thread.Sleep(2500);
        output.Stop();
        Thread.Sleep(400);
        loopback.StopRecording();
        cableCapture.StopRecording();
        Thread.Sleep(300);
        writerA.Flush();
        writerB.Flush();
        writerA.Dispose();
        writerB.Dispose();

        Console.WriteLine($"render={renderDevice.FriendlyName} (exclusive 44100Hz, buffer {renderLatencyMs}ms)");
        var envA = LoadEnvelopeMs(outA);
        var envB = LoadEnvelopeMs(outB);
        var bestLag = int.MinValue;
        double bestScore = double.NegativeInfinity;
        for (var lag = -600; lag <= 600; lag++)
        {
            double score = 0;
            var count = 0;
            for (var i = 0; i + lag >= 0 && i < envA.Length && i + lag < envB.Length; i++)
            {
                score += envA[i] * envB[i + lag];
                count++;
            }

            if (count > 100 && score / count > bestScore)
            {
                bestScore = score / count;
                bestLag = lag;
            }
        }

        Console.WriteLine(bestLag == int.MinValue
            ? "lag=unresolved (no overlap)"
 : $"lag_b_minus_a_ms={bestLag} (score={bestScore:F5}) -> 线缆->监听输出 延迟");
        return 0;
    }

    private static float[] LoadEnvelopeMs(string path)
    {
        using var reader = new WaveFileReader(path);
        var format = reader.WaveFormat;
        if (format.Encoding != WaveFormatEncoding.IeeeFloat)
        {
            throw new NotSupportedException($"expected float wav, got {format.Encoding}");
        }

        var framesPerMs = format.SampleRate / 1000.0;
        var samples = new List<float>();
        var buffer = new byte[16384];
        while (reader.Position < reader.Length)
        {
            var bytesRead = reader.Read(buffer, 0, buffer.Length);
            if (bytesRead <= 0)
            {
                break;
            }

            var frameBytes = sizeof(float) * format.Channels;
            for (var at = 0; at + frameBytes <= bytesRead; at += frameBytes)
            {
                var sum = 0f;
                for (var c = 0; c < format.Channels; c++)
                {
                    sum += BitConverter.ToSingle(buffer, at + (c * sizeof(float)));
                }

                samples.Add(sum / format.Channels);
            }
        }

        var bins = (int)(samples.Count / framesPerMs);
        var envelope = new float[bins];
        for (var bin = 0; bin < bins; bin++)
        {
            var from = (int)(bin * framesPerMs);
            var to = (int)((bin + 1) * framesPerMs);
            var acc = 0f;
            for (var i = from; i < to; i++)
            {
                acc += Math.Abs(samples[i]);
            }

            envelope[bin] = acc / Math.Max(1, to - from);
        }

        return envelope;
    }
}

internal sealed class SineWaveProvider(double frequency, double amplitude, WaveFormat? format = null, double beepHz = 0) : IWaveProvider
{
    private readonly WaveFormat _format = format ?? WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    private double _phase;
    private double _modPhase;


    public WaveFormat WaveFormat => _format;

    public int Read(byte[] buffer, int offset, int count)
    {
        var bytesPerSample = _format.BitsPerSample / 8;
        var frameBytes = bytesPerSample * _format.Channels;
        var frames = count / frameBytes;
        for (var i = 0; i < frames; i++)
        {
            var envelope = beepHz <= 0 ? 1.0 : (Math.Sin(_modPhase) >= 0 ? 1.0 : 0.0);
            var value = Math.Sin(_phase) * amplitude * envelope;
            for (var c = 0; c < _format.Channels; c++)
            {
                var at = offset + (i * frameBytes) + (c * bytesPerSample);
                if (_format.Encoding == WaveFormatEncoding.IeeeFloat)
                {
                    BitConverter.GetBytes((float)value).CopyTo(buffer, at);
                }
                else
                {
                    BitConverter.GetBytes((short)Math.Clamp(value * short.MaxValue, short.MinValue, short.MaxValue)).CopyTo(buffer, at);
                }
            }

            _phase += 2.0 * Math.PI * frequency / _format.SampleRate;
            if (_phase > 2.0 * Math.PI)
            {
                _phase -= 2.0 * Math.PI;
            }
            _modPhase += 2.0 * Math.PI * beepHz / _format.SampleRate;
            if (_modPhase > 2.0 * Math.PI)
            {
                _modPhase -= 2.0 * Math.PI;
            }
        }

        return frames * frameBytes;
    }
}

/// <summary>
/// One pull-driven source fanned out to independent sinks: the main render consumes the
/// source, every consumed block is also appended to each sink's elastic buffer so a second
/// consumer (local monitor) can drain at its own pace.
/// </summary>
internal sealed class BroadcastWaveProvider(IWaveProvider source) : IWaveProvider
{
    private readonly List<BufferedWaveProvider> _sinks = [];

    public WaveFormat WaveFormat => source.WaveFormat;

    public BufferedWaveProvider AddSink()
    {
        var sink = new BufferedWaveProvider(source.WaveFormat)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(1),
        };
        _sinks.Add(sink);
        return sink;
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        var read = source.Read(buffer, offset, count);
        if (read > 0)
        {
            foreach (var sink in _sinks)
            {
                sink.AddSamples(buffer, offset, read);
            }
        }

        return read;
    }

    public void TrimSinks(TimeSpan target)
    {
        foreach (var sink in _sinks)
        {
            var targetBytes = (int)(target.TotalSeconds * sink.WaveFormat.AverageBytesPerSecond);
            var scratch = new byte[16 * 1024];
            while (sink.BufferedBytes > targetBytes)
            {
                var take = Math.Min(scratch.Length, sink.BufferedBytes - targetBytes);
                if (sink.Read(scratch, 0, take) <= 0)
                {
                    break;
                }
            }
        }
    }

    public double SinkDepthMs() => _sinks.Count == 0
        ? 0
        : _sinks.Average(s => s.BufferedBytes * 1000.0 / s.WaveFormat.AverageBytesPerSecond);
}

/// <summary>
/// Periodic short marker bursts for the latency matrix: burstMs of sine every periodMs.
/// Records a Stopwatch tick when the first sample of each burst is pulled by the renderer,
/// pairing with capture-side onset ticks gives per-burst engine-to-virtual-input latency.
/// </summary>
internal sealed class BurstWaveProvider(int rate, int channels, int frequency, double amplitude, int burstMs, int periodMs) : IWaveProvider
{
    private readonly WaveFormat _format = new(rate, 16, channels);
    private readonly int _burstFrames = rate * burstMs / 1000;
    private readonly int _periodFrames = rate * periodMs / 1000;
    private long _frame;
    private double _phase;

    public List<long> BurstTicks { get; } = [];

    public WaveFormat WaveFormat => _format;

    public int Read(byte[] buffer, int offset, int count)
    {
        var frameBytes = sizeof(short) * channels;
        var frames = count / frameBytes;
        for (var i = 0; i < frames; i++)
        {
            var positionInPeriod = (int)(_frame % _periodFrames);
            if (positionInPeriod == 0)
            {
                BurstTicks.Add(Stopwatch.GetTimestamp());
            }

            var value = positionInPeriod < _burstFrames
                ? Math.Sin(_phase) * amplitude * short.MaxValue
                : 0;
            for (var c = 0; c < channels; c++)
            {
                BitConverter.GetBytes((short)value).CopyTo(buffer, offset + ((i * channels) + c) * sizeof(short));
            }

            _phase += 2.0 * Math.PI * frequency / rate;
            if (_phase > 2.0 * Math.PI)
            {
                _phase -= 2.0 * Math.PI;
            }

            _frame++;
        }

        return frames * frameBytes;
    }
}
