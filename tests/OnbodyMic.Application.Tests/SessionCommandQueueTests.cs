namespace OnbodyMic.Application.Tests;

using OnbodyMic.Application;
using Xunit;

public sealed class SessionCommandQueueTests
{
    [Fact]
    public void Value_command_preserves_original_failure_and_queue_remains_usable()
    {
        using var queue = new SessionCommandQueue();
        var failure = Assert.Throws<IOException>(() =>
            queue.SubmitValue<int>("failing-status", () => throw new IOException("device scan")));
        Assert.Equal("device scan", failure.Message);
        Assert.Equal(42, queue.SubmitValue("status", () => 42));
    }

    [Fact]
    public void Event_post_after_disposal_is_ignored()
    {
        using var queue = new SessionCommandQueue();
        queue.Dispose();
        Assert.Null(Record.Exception(() => queue.Post("late-device-event", () => { })));
    }
}
