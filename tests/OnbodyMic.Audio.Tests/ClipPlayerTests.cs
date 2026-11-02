namespace OnbodyMic.Audio.Tests;

using OnbodyMic.Domain;
using Xunit;

public sealed class ClipPlayerTests
{
    [Fact]
    public void Stop_does_not_resume_at_full_volume_in_a_large_audio_period()
    {
        var player = new ClipPlayer(fadeFrames: 4);
        player.Start("old", Filled(64, 1), 1, ClipPlaybackMode.Send);
        player.Read(new float[8], 8);
        player.Stop();
        var output = new float[16];
        Assert.Equal(4, player.Read(output, output.Length));
        Assert.All(output[4..], sample => Assert.Equal(0, sample));
    }

    [Fact]
    public void Crossfade_preserves_the_previous_material_gain()
    {
        var player = new ClipPlayer(fadeFrames: 4);
        player.Start("quiet", Filled(64, 1), 1, ClipPlaybackMode.Send, gain: .1f);
        player.Read(new float[8], 8);
        player.Start("silent", new float[64], 2, ClipPlaybackMode.Send);
        var output = new float[8];
        player.Read(output, output.Length);
        Assert.All(output, sample => Assert.InRange(sample, 0, .1f));
    }

    [Fact]
    public void Stale_send_ticket_cannot_consume_a_preview_or_replacement()
    {
        var player = new ClipPlayer(fadeFrames: 0);
        var feed = new ClipFeed(player, 64, 32);
        feed.SetTicket(new SourceTicket("clip", AudioKind.Clip, 0, 1));
        player.Start("private", Filled(64, .8f), 1, ClipPlaybackMode.Preview);
        Assert.Equal(0, feed.Pull(new float[8], 8));
        player.Start("new", Filled(64, .8f), 2, ClipPlaybackMode.Send);
        Assert.Equal(0, feed.Pull(new float[8], 8));
        Assert.Equal(0, player.Stats().PositionFrames);
    }

    [Fact]
    public void Preview_tail_cannot_crossfade_into_a_send_playback()
    {
        var player = new ClipPlayer(fadeFrames: 4);
        player.Start("private-preview", Filled(64, 0.8f), 1, ClipPlaybackMode.Preview);
        player.Read(new float[8], 8);
        player.Start("silent-send", new float[64], 2, ClipPlaybackMode.Send);
        var sent = new float[8];
        player.Read(sent, sent.Length);
        Assert.All(sent, sample => Assert.Equal(0, sample));
    }

    [Fact]
    public void Stopped_material_cannot_reappear_when_a_new_clip_starts()
    {
        var player = new ClipPlayer(fadeFrames: 4);
        player.Start("old", Filled(64, 0.8f), 1, ClipPlaybackMode.Send);
        player.Read(new float[8], 8);
        player.Stop(); // no subsequent pull while the send gate is closed
        player.Start("silent-new", new float[64], 2, ClipPlaybackMode.Send);
        var sent = new float[8];
        player.Read(sent, sent.Length);
        Assert.All(sent, sample => Assert.Equal(0, sample));
    }

    [Fact]
    public void New_target_replaces_old_and_retrigger_restarts()
    {
        var player = new ClipPlayer(fadeFrames: 0);
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
        var player = new ClipPlayer(fadeFrames: 0);
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
        var player = new ClipPlayer(fadeFrames: 0);
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

    [Fact]
    public void Start_fades_in_and_natural_end_fades_out()
    {
        var player = new ClipPlayer(fadeFrames: 4);
        player.Start("clip-a", Filled(8, 1f), playbackEpoch: 1, ClipPlaybackMode.Send);

        var buffer = new float[8];
        var taken = player.Read(buffer, 8);

        Assert.Equal(8, taken);
        Assert.Equal(new float[] { 0f, 0.25f, 0.5f, 0.75f, 1f, 0.75f, 0.5f, 0.25f }, buffer);
        Assert.Null(player.Active); // natural completion cleared the slot
    }

    [Fact]
    public void Stop_fades_out_instead_of_stepping_to_silence()
    {
        var player = new ClipPlayer(fadeFrames: 4);
        var completions = 0;
        player.Start("clip-a", Filled(100, 1f), playbackEpoch: 1, ClipPlaybackMode.Send);

        var warmup = new float[8];
        player.Read(warmup, 8); // through the fade-in, into steady material
        Assert.All(warmup[4..], value => Assert.Equal(1f, value));


        player.Stop();
        Assert.NotNull(player.Active); // still fading: not cleared yet

        var buffer = new float[4];
        var taken = player.Read(buffer, 4);
        Assert.Equal(4, taken);
        Assert.Equal(new float[] { 1f, 0.75f, 0.5f, 0.25f }, buffer);

        var after = new float[4];
        Assert.Equal(0, player.Read(after, 4));
        Assert.All(after, value => Assert.Equal(0f, value));
        Assert.Equal(0, completions); // a stop never fires the completion event
    }

    [Fact]
    public void Replace_crossfades_old_material_into_the_new_clip()
    {
        var player = new ClipPlayer(fadeFrames: 4);
        var completions = 0;
        player.PlaybackCompleted += _ => completions++;
        player.Start("clip-a", Filled(16, 1f), playbackEpoch: 1, ClipPlaybackMode.Send);

        var warmup = new float[8];
        player.Read(warmup, 8); // through the fade-in, mid-clip
        Assert.All(warmup[4..], value => Assert.Equal(1f, value));

        player.Start("clip-b", new float[] { 2f, 2f }, playbackEpoch: 2, ClipPlaybackMode.Send);
        Assert.Equal(1, player.Stats().ReplacedPlaybacks);

        var first = new float[2];
        player.Read(first, 2);
        // new clip: fade-in x natural-end ramp on a clip shorter than the
        // fade window (0, 0.25*0.25*2=0.125) while old decays on top (1, 0.75)
        Assert.Equal(new float[] { 1f, 0.875f }, first);

        var tail = new float[4];
        var tailTaken = player.Read(tail, 4);
        Assert.Equal(2, tailTaken); // crossfade remainder after the new clip ended
        Assert.Equal(new float[] { 0.5f, 0.25f, 0f, 0f }, tail);
        Assert.Equal(1, completions); // clip-b completed naturally
    }

    [Fact]
    public void Default_fades_keep_abrupt_material_step_below_audibility()
    {
        // the P2 pop regression guard: an abrupt clip (first sample at full
        // amplitude) must ramp, never step
        var player = new ClipPlayer(); // default 128-frame fades
        player.Start("clip-a", Filled(1000, 0.9f), playbackEpoch: 1, ClipPlaybackMode.Send);

        var buffer = new float[200];
        player.Read(buffer, 200);

        var maxStep = 0f;
        for (var i = 1; i < buffer.Length; i++)
        {
            maxStep = Math.Max(maxStep, Math.Abs(buffer[i] - buffer[i - 1]));
        }

        Assert.True(maxStep < 0.02f, $"inter-sample step {maxStep} exceeds the ramp bound");
    }

    private static float[] Filled(int count, float value)
    {
        var samples = new float[count];
        Array.Fill(samples, value);
        return samples;
    }
}
