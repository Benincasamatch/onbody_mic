namespace OnbodyMic.Platform.Windows;

using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using OnbodyMic.Audio;

/// <summary>
/// Preloads a clip fully into engine format (44.1 kHz mono float) using
/// NAudio readers + WDL resampling (P0B: the MediaFoundationResampler pull
/// mismatch corrupted the mic path; WDL held discard at 0-9 ms/s).
/// </summary>
public sealed class NAudioClipDecoder : IClipDecoder
{
    public const int MaxDurationSeconds = 60;

    public DecodedClip Decode(string path)
    {
        if (!Path.Exists(path))
        {
            throw new FileNotFoundException("clip file not found", path);
        }

        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".wav" or ".mp3"))
        {
            throw new NotSupportedException($"unsupported clip format: {extension}");
        }

        using var reader = new AudioFileReader(path);
        if (reader.TotalTime.TotalSeconds > MaxDurationSeconds)
        {
            throw new InvalidDataException($"clip exceeds {MaxDurationSeconds}s bound");
        }

        var source = reader.WaveFormat.SampleRate == EngineFormat.Rate
            ? (ISampleProvider)reader
            : new WdlResamplingSampleProvider(reader, EngineFormat.Rate);

        var channels = source.WaveFormat.Channels;
        var frames = (int)(source.WaveFormat.SampleRate * reader.TotalTime.TotalSeconds) + 1;
        var mono = new List<float>(frames);
        var interleaved = new float[8192 * channels];
        int read;
        while ((read = source.Read(interleaved, 0, interleaved.Length)) > 0)
        {
            for (var f = 0; f < read / channels; f++)
            {
                var sum = 0f;
                for (var c = 0; c < channels; c++)
                {
                    sum += interleaved[(f * channels) + c];
                }

                mono.Add(sum / channels);
            }
        }

        var samples = mono.ToArray();
        foreach (var value in samples)
        {
            if (!float.IsFinite(value))
            {
                throw new InvalidDataException("clip contains non-finite samples");
            }
        }

        return new DecodedClip(samples);
    }
}
