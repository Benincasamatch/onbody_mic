namespace OnbodyMic.Cli;

using System.Diagnostics;
using OnbodyMic.Platform.Windows;

/// <summary>
/// FullEndpoint-monitor forensics (P3.2): plays a steady sine into the cable
/// render endpoint (exclusive, the product send path) while recording what
/// the monitor capture actually negotiates and delivers. Writes a wav plus a
/// format report so sample-rate mismatches surface as measured fact.
/// </summary>
internal static class MonitorProbe
{
    public static int Run(string[] args)
    {
        var options = PanelApp.ParseOptions(args);
        var devices = WindowsAudioDevices.List(activeOnly: true);
        var rateOnly = args.Contains("--rate-only");
        var renderId = rateOnly
            ? null
            : PanelApp.ResolveEndpoint(options, "render-id", "render", devices, capture: false, "虚拟输出（线缆 Render）");
        var captureId = PanelApp.ResolveEndpoint(options, "capture-id", "capture", devices, capture: true, "虚拟麦（线缆 Capture）");
        if (captureId is null || (renderId is null && !rateOnly))
        {
            return 1;
        }
        var seconds = double.TryParse(options.GetValueOrDefault("seconds"), out var s) ? Math.Clamp(s, 1, 30) : 5;
        var freq = double.TryParse(options.GetValueOrDefault("freq"), out var f) ? Math.Clamp(f, 20, 15000) : 440;
        var outDir = options.GetValueOrDefault("out") ?? Path.Combine(".tools", "tmp", "monprobe");
        Directory.CreateDirectory(outDir);

        // 1. negotiated capture format first (no audio yet)
        using (var probe = WasapiEventCapture.Open(captureId, bufferMs: 10, useMixFormat: true))
        {
            Console.WriteLine($"capture mix format: {probe.Format.SampleRate} Hz, {probe.Format.Channels} ch, float={probe.Format.FloatSamples}");
            if (probe.Format.SampleRate != 44100)
            {
                Console.WriteLine($"!! 采样率 != 引擎 44100 —— 现网产品路径按 44100 消费此流：速率错配 {(double)probe.Format.SampleRate / 44100:0.000}x，监听环将持续修剪/跳变（'炸'音根源）");
            }
        }

        if (rateOnly)
        {
            return MeasureCaptureRate(captureId, seconds);
        }

        // 2. play sine on the exclusive render path while capturing
        var samples = new List<float>();
        var capture = WasapiEventCapture.Open(captureId, bufferMs: 10, useMixFormat: true);
        capture.Frames += frames =>
        {
            lock (samples)
            {
                samples.AddRange(frames.ToArray());
            }
        };
        capture.Start();

        var phase = 0.0;
        var pump = new WasapiExclusiveRenderPump(renderId!);
        pump.Start(buffer =>
        {
            for (var i = 0; i < buffer.Length; i++)
            {
                buffer[i] = 0.4f * (float)Math.Sin(phase);
                phase += 2 * Math.PI * freq / 44100;
            }

            return buffer.Length;
        });

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            Thread.Sleep(100);
        }

        pump.Dispose();
        capture.Dispose();

        // 3. persist + report
        float[] captured;
        lock (samples)
        {
            captured = samples.ToArray();
        }

        var wav = Path.Combine(outDir, "monprobe_capture.wav");
        WriteWav(wav, captured, 44100);
        var peak = captured.Length == 0 ? 0 : captured.Max(Math.Abs);
        Console.WriteLine($"captured {captured.Length} frames ({(double)captured.Length / 44100:0.00}s @44100 记账), peak={peak:0.000}");
        Console.WriteLine($"wav: {wav}");
        Console.WriteLine($"分析: onbody analyze --file \"{wav}\" --freq {freq:0}");
        return 0;
    }

    /// <summary>
    /// Opens a capture session with the product parameters and measures the
    /// delivery rate against wall time. A ratio below ~0.95 means the driver
    /// is throttling this session (P0B penalty factor) — the monitor ring
    /// starves continuously and the output breaks up regardless of gain.
    /// </summary>
    private static int MeasureCaptureRate(string captureId, double seconds)
    {
        var samples = new List<float>();
        using var capture = WasapiEventCapture.Open(captureId, bufferMs: 10, useMixFormat: true);
        capture.Frames += frames =>
        {
            lock (samples)
            {
                samples.AddRange(frames.ToArray());
            }
        };
        capture.Start();
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            Thread.Sleep(50);
        }

        capture.Stop();
        sw.Stop();
        lock (samples)
        {
            var ratio = samples.Count / (44100.0 * sw.Elapsed.TotalSeconds);
            Console.WriteLine($"delivered {samples.Count} frames in {sw.Elapsed.TotalSeconds:0.00}s → {ratio:0.000}x 实时");
            Console.WriteLine(ratio < 0.95
                ? "!! 会话被驱动降速：监听环将持续饥饿/跳变（'炸'音根源）。修复方向：增大 bufferMs / 换会话参数"
                : "交付速率正常");
            return ratio < 0.95 ? 2 : 0;
        }
    }

    private static void WriteWav(string path, float[] samples, int rate)
    {
        using var writer = new System.IO.BinaryWriter(File.Create(path));
        const int channels = 1;
        writer.Write("RIFF"u8);
        writer.Write(36 + samples.Length * 2);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(rate);
        writer.Write(rate * channels * 2);
        writer.Write((short)(channels * 2));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(samples.Length * 2);
        foreach (var sample in samples)
        {
            writer.Write((short)Math.Clamp(sample, -1f, 1f) * 32767);
        }
    }
}
