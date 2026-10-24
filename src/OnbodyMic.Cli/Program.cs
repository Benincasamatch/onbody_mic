namespace OnbodyMic.Cli;

using System.Text;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
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
                "devices" => ListDevices(args.Length > 1 && args[1] == "--all"),
                "tone" => PlayTone(args[1..]),
                "record" => Record(args[1..]),
                "analyze" => Analyze(args[1..]),
                "loop" => Loop(args[1..]),
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
              devices [--all]                         list audio endpoints
              tone --index <n> [--seconds s] [--freq hz] [--gain g]
                                                      play a test sine to a render endpoint
              record --index <n> --seconds s --out <file.wav>
                                                      capture an endpoint to wav
            """);
        return 0;
    }

    private static int ListDevices(bool activeOnly)
    {
        var devices = WindowsAudioDevices.List(activeOnly);
        Console.WriteLine("idx flow     state   name");
        for (var i = 0; i < devices.Count; i++)
        {
            var device = devices[i];
            Console.WriteLine($"{i,3} {device.DataFlow,-8} {device.State,-7} {device.FriendlyName}");
        }

        return 0;
    }

    private static int PlayTone(string[] rest)
    {
        var options = ParseOptions(rest);
        if (!options.TryGetValue("index", out var indexText) || !int.TryParse(indexText, out var index))
        {
            Console.Error.WriteLine("tone requires --index <n> (render endpoint index from `devices`)");
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

        using var device = WindowsAudioDevices.FindByIndex(index, capture: false);
        if (device is null)
        {
            Console.Error.WriteLine($"no active render endpoint at index {index}");
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
        if (!options.TryGetValue("index", out var indexText) || !int.TryParse(indexText, out var index))
        {
            Console.Error.WriteLine("record requires --index <n> (capture endpoint index from `devices`)");
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

        using var device = WindowsAudioDevices.FindByIndex(index, capture: true);
        if (device is null)
        {
            Console.Error.WriteLine($"no active capture endpoint at index {index}");
            return 1;
        }

        using var capture = new WasapiCapture(device);
        using var writer = new WaveFileWriter(outFile, capture.WaveFormat);
        using var finished = new ManualResetEvent(false);
        capture.DataAvailable += (_, e) => writer.Write(e.Buffer, 0, e.BytesRecorded);
        capture.RecordingStopped += (_, _) => finished.Set();
        capture.StartRecording();
        Console.WriteLine($"recording {seconds:N1}s from: {device.FriendlyName}");
        if (!finished.WaitOne(TimeSpan.FromSeconds(seconds + 5)))
        {
            Thread.Sleep(TimeSpan.FromSeconds(seconds));
        }

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

    private static double ParseDouble(Dictionary<string, string> options, string key, double fallback) =>
        double.TryParse(options.GetValueOrDefault(key), out var value) ? value : fallback;

    private static int Loop(string[] rest)
    {
        var options = ParseOptions(rest);
        if (!options.TryGetValue("render", out var renderText) || !int.TryParse(renderText, out var renderIndex) ||
            !options.TryGetValue("capture", out var captureText) || !int.TryParse(captureText, out var captureIndex) ||
            !options.TryGetValue("out", out var outFile))
        {
            Console.Error.WriteLine("loop requires --render <i> --capture <i> --out <file.wav>");
            return 1;
        }

        var seconds = ParseDouble(options, "seconds", 4);
        var frequency = ParseDouble(options, "freq", 440);
        var gain = ParseDouble(options, "gain", 0.35);
        if (seconds <= 2 || frequency <= 0 || gain is < 0 or > 1)
        {
            Console.Error.WriteLine("seconds must be > 2; freq must be positive; gain within [0,1]");
            return 1;
        }

        using var renderDevice = WindowsAudioDevices.FindByIndex(renderIndex, capture: false);
        using var captureDevice = WindowsAudioDevices.FindByIndex(captureIndex, capture: true);
        if (renderDevice is null || captureDevice is null)
        {
            Console.Error.WriteLine("render or capture index not found");
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

        using var output = new WasapiOut(renderDevice, AudioClientShareMode.Shared, false, 50);
        var sine = new SineWaveProvider(frequency, gain);
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
        Console.WriteLine($"tone_started_at_ms={(toneStartedAt - startedAt) * 1000.0 / Stopwatch.Frequency:F1}");
        _ = firstSampleTicks; // latency marking is P0 follow-up; keep the CLI deterministic for now.
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
        Console.WriteLine($"tone_noise_ratio={ratio:F2}");
        Console.WriteLine(ratio > 20 && rms > 0.0005 ? "result=tone-detected" : "result=no-tone");
        return 0;
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
}

internal sealed class SineWaveProvider(double frequency, double amplitude) : IWaveProvider
{
    private readonly WaveFormat _format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    private double _phase;

    public WaveFormat WaveFormat => _format;

    public int Read(byte[] buffer, int offset, int count)
    {
        var samples = count / sizeof(float);
        for (var i = 0; i < samples; i += 2)
        {
            var value = BitConverter.GetBytes((float)(Math.Sin(_phase) * amplitude));
            value.CopyTo(buffer, offset + (i + 0) * sizeof(float));
            value.CopyTo(buffer, offset + (i + 1) * sizeof(float));
            _phase += 2.0 * Math.PI * frequency / _format.SampleRate;
            if (_phase > 2.0 * Math.PI)
            {
                _phase -= 2.0 * Math.PI;
            }
        }

        return samples * sizeof(float);
    }
}
