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

    [Fact]
    public void Adaptive_rate_absorbs_slow_producer_without_underrun()
    {
        // producer clock 0.23% slow (real drift observed in P1 KOOK testing)
        var channel = new SourceChannel("mic", AudioKind.Microphone, 44100);
        channel.SetTicket(new SourceTicket("mic", AudioKind.Microphone, 1, 1));
        channel.EnableAdaptiveRate(targetQueuedFrames: 2646); // 60 ms
        channel.Push(new float[2646]); // start at the watermark

        var output = new float[882];
        var underruns = 0;
        for (var period = 0; period < 600; period++)
        {
            channel.Push(new float[880]);
            if (channel.Pull(output, 882) < 882)
            {
                underruns++;
            }
        }

        Assert.Equal(0, underruns);
        Assert.True(channel.QueuedFrames > 0);
        Assert.True(channel.Ratio < 1.0); // consumption slowed to match the producer
    }

    [Fact]
    public void Adaptive_rate_absorbs_fast_producer_without_overflow()
    {
        var channel = new SourceChannel("app", AudioKind.Application, 44100);
        channel.SetTicket(new SourceTicket("app", AudioKind.Application, 1, 1));
        channel.EnableAdaptiveRate(targetQueuedFrames: 2646);
        channel.Push(new float[2646]);

        var output = new float[882];
        for (var period = 0; period < 600; period++)
        {
            channel.Push(new float[884]); // producer 0.23% fast
            channel.Pull(output, 882);
        }

        Assert.Equal(0, channel.Stats().DroppedFrames); // nothing evicted
        Assert.True(channel.Ratio > 1.0);
        Assert.True(channel.QueuedFrames < 44100);
    }
}
