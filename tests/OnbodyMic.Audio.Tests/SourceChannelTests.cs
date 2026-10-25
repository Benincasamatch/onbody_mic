namespace OnbodyMic.Audio.Tests;

using OnbodyMic.Domain;
using Xunit;

public sealed class SourceChannelTests
{
    [Fact]
    public void Push_then_pull_is_fifo()
    {
        var channel = new SourceChannel("mic", AudioKind.Microphone, 100);
        channel.SetTicket(new SourceTicket("mic", AudioKind.Microphone, 1, 1));
        channel.Push(new float[] { 1, 2, 3, 4 });

        var buffer = new float[10];
        var taken = channel.Pull(buffer, 4);

        Assert.Equal(4, taken);
        Assert.Equal(new float[] { 1, 2, 3, 4 }, buffer[..4]);
        Assert.Equal(0, channel.Pull(buffer, 4));
    }

    [Fact]
    public void Overflow_drops_oldest_and_counts()
    {
        var channel = new SourceChannel("app", AudioKind.Application, 4);
        channel.SetTicket(new SourceTicket("app", AudioKind.Application, 1, 1));
        channel.Push(new float[] { 1, 2, 3, 4, 5, 6 });

        var stats = channel.Stats();
        Assert.Equal(2, stats.DroppedFrames);
        Assert.Equal(4, channel.QueuedFrames);

        var buffer = new float[4];
        channel.Pull(buffer, 4);
        Assert.Equal(new float[] { 3, 4, 5, 6 }, buffer);
    }

    [Fact]
    public void Unauthorized_push_is_dropped_not_buffered()
    {
        var channel = new SourceChannel("mic", AudioKind.Microphone, 100);
        channel.Push(new float[] { 1, 2, 3 });

        var stats = channel.Stats();
        Assert.False(stats.Authorized);
        Assert.Equal(3, stats.DroppedFrames);
        Assert.Equal(0, channel.QueuedFrames);
    }

    [Fact]
    public void Revoking_ticket_discards_buffered_frames()
    {
        var channel = new SourceChannel("mic", AudioKind.Microphone, 100);
        channel.SetTicket(new SourceTicket("mic", AudioKind.Microphone, 1, 1));
        channel.Push(new float[] { 1, 2, 3 });
        channel.SetTicket(null);

        var buffer = new float[3];
        Assert.Equal(0, channel.Pull(buffer, 3));
        Assert.Equal(3, channel.Stats().DroppedFrames);
    }

    [Fact]
    public void Drain_discards_without_emitting()
    {
        var channel = new SourceChannel("app", AudioKind.Application, 100);
        channel.SetTicket(new SourceTicket("app", AudioKind.Application, 1, 1));
        channel.Push(new float[] { 1, 2, 3, 4, 5 });

        Assert.Equal(3, channel.Drain(3));

        var buffer = new float[2];
        channel.Pull(buffer, 2);
        Assert.Equal(new float[] { 4, 5 }, buffer);
        Assert.Equal(3, channel.Stats().DeniedFrames);
    }
}
