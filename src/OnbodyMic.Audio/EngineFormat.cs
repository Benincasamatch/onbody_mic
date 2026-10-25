namespace OnbodyMic.Audio;

/// <summary>
/// The single internal engine format: every channel in the graph carries
/// 44.1 kHz mono float samples, matching the Steam virtual cable capture pin
/// (P0B evidence). All format/rate/channel conversion happens in platform
/// adapters before samples enter a channel.
/// </summary>
public static class EngineFormat
{
    public const int Rate = 44100;
    public const int Channels = 1;

    public static int TimeToFrames(TimeSpan time) => (int)(time.TotalSeconds * Rate);

    public static TimeSpan FramesToTime(int frames) => TimeSpan.FromSeconds((double)frames / Rate);
}
