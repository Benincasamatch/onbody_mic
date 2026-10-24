namespace OnbodyMic.Domain;

public sealed record ActiveClip(
    string ClipId,
    long PlaybackEpoch,
    ClipPlaybackMode Mode);
