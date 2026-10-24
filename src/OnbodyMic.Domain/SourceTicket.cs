namespace OnbodyMic.Domain;

public sealed record SourceTicket(
    string SourceId,
    AudioKind Kind,
    long SessionEpoch,
    long SourceEpoch);
