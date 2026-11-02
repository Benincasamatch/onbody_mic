namespace OnbodyMic.Platform.Windows;

using System.Security.Cryptography;
using System.Text;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using OnbodyMic.Audio;

public sealed record AudioFileInspection(
    string SourcePath, long FrameCount, int SampleRate, int Channels, float[] Peaks, string SourceVersion)
{
    public double DurationMs => FrameCount * (1000d / EngineFormat.Rate);
}

/// <summary>Reads source audio without duration-sized buffers and writes only a selected engine-format region.</summary>
public sealed class AudioFileEditor
{
    public const int MaxWaveformBins = 2048;
    public const int MaxSelectionMs = NAudioClipDecoder.MaxDurationSeconds * 1000;
    private const int BufferFrames = 8192;
    private const int MaxSelectionFrames = NAudioClipDecoder.MaxDurationSeconds * EngineFormat.Rate;

    public AudioFileInspection Inspect(string path, CancellationToken cancellationToken = default)
    {
        using var source = OpenSource(path, cancellationToken);
        var buffer = new float[BufferFrames];
        var (samples, blockFrames) = EngineSource(source.Reader, cancellationToken);
        var frames = CountFrames(samples, buffer, blockFrames, cancellationToken);
        if (frames == 0) throw new InvalidDataException("audio source is empty");

        // A decoder's advertised duration can be an estimate (notably VBR MP3).
        // Count actual output first, then collect equally timed bins on a fresh pass.
        source.Reader.Position = 0;
        (samples, blockFrames) = EngineSource(source.Reader, cancellationToken);
        var peaks = ReadPeaks(samples, buffer, blockFrames, frames, cancellationToken);
        return new AudioFileInspection(source.Path, frames, source.Reader.WaveFormat.SampleRate,
            source.Reader.WaveFormat.Channels, peaks, source.Version);
    }

    public static void ValidateRange(double startMs, double endMs)
    {
        if (!double.IsFinite(startMs) || !double.IsFinite(endMs) || startMs < 0 || endMs <= startMs
            || endMs - startMs > MaxSelectionMs)
            throw new ArgumentException($"selection must be a finite, positive range of at most {MaxSelectionMs}ms");
    }

    public void ExportSelection(string path, string sourceVersion, double startMs, double endMs,
        string destinationPath, CancellationToken cancellationToken = default, AudioFileInspection? inspected = null)
    {
        ValidateRange(startMs, endMs);
        using var source = OpenSource(path, cancellationToken);
        if (!string.Equals(source.Version, sourceVersion, StringComparison.Ordinal))
            throw new InvalidDataException("audio source changed; inspect it again before editing");
        var destination = Path.GetFullPath(destinationPath);
        if (string.Equals(destination, source.Path, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("selection destination must not be the original audio source");

        var buffer = new float[BufferFrames];
        var (samples, blockFrames) = EngineSource(source.Reader, cancellationToken);
        long totalFrames;
        if (inspected is not null && inspected.SourceVersion == source.Version
            && string.Equals(inspected.SourcePath, source.Path, StringComparison.OrdinalIgnoreCase)
            && inspected.SampleRate == source.Reader.WaveFormat.SampleRate
            && inspected.Channels == source.Reader.WaveFormat.Channels)
        {
            // Only reuse a server-side inspection after rehashing the locked original.
            totalFrames = inspected.FrameCount;
        }
        else
        {
            totalFrames = CountFrames(samples, buffer, blockFrames, cancellationToken);
            source.Reader.Position = 0;
            (samples, blockFrames) = EngineSource(source.Reader, cancellationToken);
        }
        if (endMs > totalFrames * (1000d / EngineFormat.Rate))
            throw new ArgumentException("selection is outside the audio source");

        var startFrame = MillisecondsToFrame(startMs);
        var endFrame = MillisecondsToFrame(endMs);
        if (endFrame <= startFrame || endFrame - startFrame > MaxSelectionFrames || endFrame > totalFrames)
            throw new ArgumentException("selection must contain samples and remain within the audio source and 60s bound");

        var created = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
            created = true;
            using var writer = new WaveFileWriter(output, WaveFormat.CreateIeeeFloatWaveFormat(EngineFormat.Rate, EngineFormat.Channels));
            long position = 0;
            while (position < endFrame)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = samples.Read(buffer, 0, blockFrames);
                cancellationToken.ThrowIfCancellationRequested();
                if (read == 0) throw new InvalidDataException("audio source ended before the selected boundary");
                var from = (int)Math.Clamp(startFrame - position, 0L, read);
                var through = (int)Math.Clamp(endFrame - position, 0L, read);
                for (var index = from; index < through; index++)
                    if (!float.IsFinite(buffer[index])) throw new InvalidDataException("selection contains non-finite samples");
                if (through > from) writer.WriteSamples(buffer, from, through - from);
                position = checked(position + read);
            }
            cancellationToken.ThrowIfCancellationRequested();
            writer.Flush();
        }
        catch
        {
            if (created) File.Delete(destination);
            throw;
        }
    }

    private static long MillisecondsToFrame(double milliseconds)
    {
        var frames = Math.Round(milliseconds * (EngineFormat.Rate / 1000d), MidpointRounding.AwayFromZero);
        if (frames < 0 || frames >= long.MaxValue) throw new ArgumentException("selection time is too large");
        return checked((long)frames);
    }

    private static long CountFrames(ISampleProvider source, float[] buffer, int blockFrames, CancellationToken cancellationToken)
    {
        long frames = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = source.Read(buffer, 0, blockFrames);
            cancellationToken.ThrowIfCancellationRequested();
            if (read == 0) return frames;
            frames = checked(frames + read);
        }
    }

    private static float[] ReadPeaks(ISampleProvider source, float[] buffer, int blockFrames,
        long frames, CancellationToken cancellationToken)
    {
        var bins = (int)Math.Min(MaxWaveformBins, frames);
        var peaks = new float[bins * 2];
        var baseWidth = frames / bins;
        var remainder = frames % bins;
        var bin = 0;
        var boundary = baseWidth + (remainder > 0 ? 1 : 0);
        long position = 0;
        var minimum = float.PositiveInfinity;
        var maximum = float.NegativeInfinity;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = source.Read(buffer, 0, blockFrames);
            cancellationToken.ThrowIfCancellationRequested();
            if (read == 0) break;
            for (var index = 0; index < read; index++)
            {
                if (position >= frames) throw new InvalidDataException("audio decoded duration changed between passes");
                var value = buffer[index];
                if (!float.IsFinite(value)) throw new InvalidDataException("audio source contains non-finite samples");
                minimum = Math.Min(minimum, value);
                maximum = Math.Max(maximum, value);
                position++;
                if (position != boundary) continue;
                peaks[bin * 2] = Math.Clamp(minimum, -1f, 1f);
                peaks[bin * 2 + 1] = Math.Clamp(maximum, -1f, 1f);
                bin++;
                boundary += baseWidth + (bin < remainder ? 1 : 0);
                minimum = float.PositiveInfinity;
                maximum = float.NegativeInfinity;
            }
        }
        if (position != frames) throw new InvalidDataException("audio decoded duration changed between passes");
        return peaks;
    }

    private static (ISampleProvider Samples, int BlockFrames) EngineSource(WaveStream reader, CancellationToken cancellationToken)
    {
        ISampleProvider mono = new MonoSource(reader.ToSampleProvider(), cancellationToken);
        var sampleRate = reader.WaveFormat.SampleRate;
        if (sampleRate == EngineFormat.Rate) return (mono, BufferFrames);
        // Bound WDL's input request as well as our output buffer, even for high-rate WAV sources.
        var blockFrames = (int)Math.Max(1L, Math.Min(BufferFrames, (long)BufferFrames * EngineFormat.Rate / sampleRate));
        return (new WdlResamplingSampleProvider(mono, EngineFormat.Rate), blockFrames);
    }

    private static SourceFile OpenSource(string path, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var extension = Path.GetExtension(fullPath).ToLowerInvariant();
        if (extension is not (".wav" or ".mp3"))
            throw new NotSupportedException($"unsupported audio format: {extension}");
        cancellationToken.ThrowIfCancellationRequested();
        // Keep a read-only share lock for hashing AND decoding: a source cannot be
        // overwritten or replaced between its fingerprint check and the selection read.
        var file = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        WaveStream? reader = null;
        try
        {
            var version = Fingerprint(fullPath, file, cancellationToken);
            reader = extension == ".mp3"
                ? new StreamMediaFoundationReader(file, new MediaFoundationReader.MediaFoundationReaderSettings { SingleReaderObject = true })
                : new AudioFileReader(fullPath);
            if (reader.WaveFormat.SampleRate <= 0 || reader.WaveFormat.Channels <= 0 || reader.WaveFormat.Channels > ushort.MaxValue)
                throw new InvalidDataException("invalid audio sample rate or channel count");
            cancellationToken.ThrowIfCancellationRequested();
            return new SourceFile(fullPath, file, reader, version);
        }
        catch
        {
            try { reader?.Dispose(); }
            finally { file.Dispose(); }
            throw;
        }
    }

    private static string Fingerprint(string path, FileStream file, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var identity = FormattableString.Invariant($"audio-editor-v1\n{path.ToUpperInvariant()}\n{file.Length}\n{File.GetCreationTimeUtc(path).Ticks}\n{File.GetLastWriteTimeUtc(path).Ticks}\n");
        hash.AppendData(Encoding.UTF8.GetBytes(identity));
        var buffer = new byte[65536];
        int read;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            read = file.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            hash.AppendData(buffer, 0, read);
        }
        cancellationToken.ThrowIfCancellationRequested();
        file.Position = 0;
        return "audio-v1:" + Convert.ToHexString(hash.GetHashAndReset());
    }

    private sealed class SourceFile(string path, FileStream file, WaveStream reader, string version) : IDisposable
    {
        public string Path { get; } = path;
        public WaveStream Reader { get; } = reader;
        public string Version { get; } = version;

        public void Dispose()
        {
            try { Reader.Dispose(); }
            finally { file.Dispose(); }
        }
    }

    private sealed class MonoSource : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly CancellationToken _cancellationToken;
        private readonly int _channels;
        private readonly float[] _interleaved;

        public MonoSource(ISampleProvider source, CancellationToken cancellationToken)
        {
            _source = source;
            _cancellationToken = cancellationToken;
            _channels = source.WaveFormat.Channels;
            _interleaved = new float[Math.Max(1, BufferFrames / _channels) * _channels];
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, EngineFormat.Channels);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            var produced = 0;
            while (produced < count)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var requestedFrames = Math.Min(count - produced, _interleaved.Length / _channels);
                var read = _source.Read(_interleaved, 0, requestedFrames * _channels);
                _cancellationToken.ThrowIfCancellationRequested();
                if (read % _channels != 0) throw new InvalidDataException("audio source ended within a sample frame");
                var frames = read / _channels;
                if (frames == 0) break;
                for (var frame = 0; frame < frames; frame++)
                {
                    var sum = 0f;
                    for (var channel = 0; channel < _channels; channel++)
                        sum += _interleaved[frame * _channels + channel];
                    buffer[offset + produced + frame] = sum / _channels;
                }
                produced += frames;
            }
            return produced;
        }
    }
}
