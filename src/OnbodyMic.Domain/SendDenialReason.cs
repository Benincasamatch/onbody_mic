namespace OnbodyMic.Domain;

public enum SendDenialReason
{
    None = 0,
    EmergencyLatched = 1,
    StaleSessionEpoch = 2,
    MicSendDisabled = 3,
    ExtrasSendDisabled = 4,
    AppSendDisabled = 5,
    ClipNotSending = 6,
    StalePlaybackEpoch = 7,
    SourceNotAuthorized = 8,
}
