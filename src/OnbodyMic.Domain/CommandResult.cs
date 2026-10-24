namespace OnbodyMic.Domain;

public readonly record struct CommandResult(bool Accepted, string? Rejection = null)
{
    public static CommandResult Ok { get; } = new(true, null);

    public static CommandResult Reject(string reason) => new(false, reason);
}
