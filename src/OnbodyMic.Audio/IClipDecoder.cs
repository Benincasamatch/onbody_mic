namespace OnbodyMic.Audio;

/// <summary>
/// Decodes an audio file fully into engine-format mono samples before playback
/// (short clips are preloaded; no file I/O on the audio path). Throws with a
/// clear message on unreadable, non-finite or oversized input.
/// </summary>
public interface IClipDecoder
{
    DecodedClip Decode(string path);
}

public readonly record struct DecodedClip(float[] Samples)
{
    public TimeSpan Duration => TimeSpan.FromSeconds((double)Samples.Length / EngineFormat.Rate);
}
