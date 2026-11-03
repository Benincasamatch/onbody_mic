namespace OnbodyMic.Media;

/// <summary>
/// Down-samples already-decoded engine-format mono samples into waveform
/// min/max bins for the soundboard cards (mirror of the editor's
/// <c>ReadPeaks</c> binning, but without I/O: the import pass already holds
/// the full sample array, so cards never re-read files).
/// </summary>
public static class WaveformPeaks
{
    /// <summary>Same cap as the audio editor so card and editor waveforms agree.</summary>
    public const int MaxBins = 2048;

    /// <summary>Returns <c>bins * 2</c> floats as <c>[min0, max0, min1, max1, …]</c>.</summary>
    public static float[] Compute(float[] samples, int maxBins = MaxBins)
    {
        if (samples.Length == 0) return [];
        var bins = Math.Min(maxBins, samples.Length);
        var peaks = new float[bins * 2];
        var baseWidth = samples.Length / bins;
        var remainder = samples.Length % bins;
        var bin = 0;
        var boundary = baseWidth + (remainder > 0 ? 1 : 0);
        long position = 0;
        var minimum = float.PositiveInfinity;
        var maximum = float.NegativeInfinity;
        foreach (var value in samples)
        {
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
        return peaks;
    }
}
