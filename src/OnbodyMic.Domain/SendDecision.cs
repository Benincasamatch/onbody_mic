namespace OnbodyMic.Domain;

public readonly record struct SendDecision(bool Allowed, SendDenialReason Reason)
{
    public static SendDecision Allow { get; } = new(true, SendDenialReason.None);

    public static SendDecision Deny(SendDenialReason reason) => new(false, reason);
}
