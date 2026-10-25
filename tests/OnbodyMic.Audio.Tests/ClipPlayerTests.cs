namespace OnbodyMic.Audio.Tests;

using OnbodyMic.Domain;
using Xunit;

public sealed class ClipPlayerTests
{
    [Fact]
    public void New_target_replaces_old_and_retrigger_restarts()
    {
        var player = new ClipPlayer();
        player.Start("clip-a", new float[] { 1, 2, 3, 4 }, playbackEpoch: 1, ClipPlaybackMode.Send);

        var buffer = new float[2];
        player.Read(buffer, 2);
        Assert.Equal(new float[] { 1, 2 }, buffer);

        // same clip triggered again -> restart from the beginning
        player.Start("clip-a", new float[] { 9, 9 }, playbackEpoch: 2, ClipPlaybackMode.Send);
        Assert.Equal(0, player.Stats().PositionFrames);

        player.Read(buffer, 2);
        Assert.Equal(new float[] { 9, 9 }, buffer);
    }

    [Fact]
    public void Natural_completion_fires_once_and_clears_slot()
    {
        var player = new ClipPlayer();
        ActiveClip? completed = null;
        player.PlaybackCompleted += clip => completed = clip;
        player.Start("clip-a", new float[] { 1, 2 }, playbackEpoch: 5, ClipPlaybackMode.Send);

        var buffer = new float[4];
        var taken = player.Read(buffer, 4);

        Assert.Equal(2, taken);
        Assert.Equal(new float[] { 1, 2, 0, 0 }, buffer);
        Assert.NotNull(completed);
        Assert.Equal(5, completed!.PlaybackEpoch);
        Assert.Null(player.Active);

        // idle reads stay silent and fire no further completions
        completed = null;
        player.Read(buffer, 4);
        Assert.Null(completed);
    }

    [Fact]
    public void Stop_clears_without_completion_event()
    {
        var player = new ClipPlayer();
        var completions = 0;
        player.PlaybackCompleted += _ => completions++;
        player.Start("clip-a", new float[] { 1, 2, 3 }, playbackEpoch: 1, ClipPlaybackMode.Preview);

        player.Stop();

        Assert.Null(player.Active);
        Assert.Equal(0, completions);
        var buffer = new float[2];
        Assert.Equal(0, player.Read(buffer, 2));
        Assert.All(buffer, value => Assert.Equal(0f, value));
    }
}
