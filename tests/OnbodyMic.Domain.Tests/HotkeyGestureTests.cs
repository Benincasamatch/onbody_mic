namespace OnbodyMic.Domain.Tests;

using OnbodyMic.Domain;
using Xunit;

public sealed class HotkeyGestureTests
{
    [Theory]
    [InlineData("Ctrl+Alt+F7", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x76, "Ctrl+Alt+F7")]
    [InlineData("a", HotkeyModifiers.None, 0x41, "A")] // letters carry Win32 VK codes 0x41..0x5A
    [InlineData("5", HotkeyModifiers.None, 0x35, "5")]
    [InlineData("Control+Shift+Num1", HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x61, "Ctrl+Shift+Num1")]
    [InlineData("Win+F24", HotkeyModifiers.Win, 0x87, "Win+F24")]
    [InlineData("ctrl+alt+grave", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0xC0, "Ctrl+Alt+Grave")]
    public void Parse_round_trips_through_display_text(string text, HotkeyModifiers modifiers, int key, string display)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));
        Assert.Equal(modifiers, gesture.Modifiers);
        Assert.Equal(key, gesture.Key);
        Assert.Equal(display, gesture.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Empty_text_yields_the_unbound_gesture(string? text)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));
        Assert.True(gesture.IsUnbound);
        Assert.Equal(string.Empty, gesture.ToString());
    }

    [Theory]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Nonsense")]
    [InlineData("VolumeKnob+F5")]
    [InlineData("Ctrl+Alt")]
    public void Unknown_tokens_fail_to_parse(string text)
    {
        Assert.False(HotkeyGesture.TryParse(text, out _));
    }

    [Fact]
    public void Duplicate_modifiers_are_tolerated_as_flags()
    {
        Assert.True(HotkeyGesture.TryParse("Ctrl+Ctrl+A", out var gesture));
        Assert.Equal(HotkeyModifiers.Control, gesture.Modifiers);
        Assert.Equal(0x41, gesture.Key);
    }

    [Fact]
    public void Binding_describe_and_match_are_target_scoped()
    {
        var play = new HotkeyBinding(HotkeyAction.PlayClip, "clip-1", default);
        Assert.Equal("play-clip:clip-1", play.Describe());
        Assert.True(play.Matches(HotkeyAction.PlayClip, "clip-1"));
        Assert.False(play.Matches(HotkeyAction.PlayClip, "clip-2"));

        Assert.Equal("emergency-mute", new HotkeyBinding(HotkeyAction.EmergencyMute, null, default).Describe());
    }

    [Fact]
    public void Action_names_round_trip()
    {
        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            Assert.True(HotkeyActionNames.TryParse(HotkeyActionNames.ToName(action), out var parsed));
            Assert.Equal(action, parsed);
        }

        Assert.False(HotkeyActionNames.TryParse("playPreview", out _));
    }
}
