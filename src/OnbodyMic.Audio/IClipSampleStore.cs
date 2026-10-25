namespace OnbodyMic.Audio;

/// <summary>
/// Bounded pre-decoded sample cache keyed by stable clip id (docs/01 §5.3:
/// frequently used short clips preload under a total capacity cap; playback
/// never waits on file I/O). Implementations must be safe to call from the
/// session command thread.
/// </summary>
public interface IClipSampleStore
{
    /// <summary>Cache hit: true and the engine-format samples; false on miss.</summary>
    bool TryGet(string clipId, out float[] samples);

    /// <summary>Caches decoded samples; may decline or evict to respect the capacity bound.</summary>
    void Store(string clipId, float[] samples);

    /// <summary>Drops a cached clip (e.g. the library entry was deleted).</summary>
    void Remove(string clipId);
}
