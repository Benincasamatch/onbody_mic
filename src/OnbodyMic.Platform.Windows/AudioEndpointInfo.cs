namespace OnbodyMic.Platform.Windows;

public sealed record AudioEndpointInfo(
    string Id,
    string FriendlyName,
    string DataFlow,
    string State);
