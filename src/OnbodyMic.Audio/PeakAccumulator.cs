namespace OnbodyMic.Audio;

/// <summary>Audio callbacks record peaks; the display consumes each polling window once.</summary>
public sealed class PeakAccumulator
{
    private float _peak;

    public void Record(float magnitude)
    {
        if (!float.IsFinite(magnitude)) return;
        var previous = Volatile.Read(ref _peak);
        while (magnitude > previous)
        {
            var observed = Interlocked.CompareExchange(ref _peak, magnitude, previous);
            if (observed == previous) return;
            previous = observed;
        }
    }

    public float Take() => Interlocked.Exchange(ref _peak, 0f);
}
