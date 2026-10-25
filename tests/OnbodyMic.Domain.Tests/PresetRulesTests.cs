using OnbodyMic.Domain;
using Xunit;

namespace OnbodyMic.Domain.Tests;

public sealed class PresetRulesTests
{
    [Theory]
    [InlineData("preset-abc123", true)]
    [InlineData("p", true)]
    [InlineData("", false)]
    [InlineData("../escape", false)]
    [InlineData("id with space", false)]
    [InlineData("中文预设", false)] // ids become file names; ASCII tokens only
    public void Preset_ids_must_be_ascii_file_safe_tokens(string id, bool expected)
    {
        Assert.Equal(expected, PresetConfigRules.IsValidId(id));
    }

    [Theory]
    [InlineData(0f, true)]
    [InlineData(4f, true)]
    [InlineData(-0.01f, false)]
    [InlineData(4.01f, false)]
    [InlineData(float.NaN, false)]
    [InlineData(float.PositiveInfinity, false)]
    public void Gains_must_be_finite_and_within_limits(float gain, bool expected)
    {
        Assert.Equal(expected, PresetConfigRules.IsValidGain(gain, PresetConfig.GainLimits));
    }

    [Fact]
    public void Validate_flags_dangling_layout_entries_and_bad_fields()
    {
        var config = new PresetConfig(
            "preset-x",
            "x",
            new PresetDeviceConfig("m", "r", null, "h", null),
            1f, 1f, 1f,
            new MonitorPreferences(MonitorMode.ClipOnly, 1f),
            ["clip-ok", "clip-gone"],
            false,
            "group;bad");

        var validation = PresetConfigRules.Validate(config, clipId => clipId == "clip-ok");

        Assert.Contains("clipLayout:clip-gone", validation.Issues);
        Assert.Contains("libraryGroupId", validation.Issues);
    }

    [Fact]
    public void SelectPreset_action_name_round_trips()
    {
        Assert.True(HotkeyActionNames.TryParse("selectPreset", out var action));
        Assert.True(HotkeyGesture.TryParse("Ctrl+Alt+F9", out var gesture));
        var binding = new HotkeyBinding(HotkeyAction.SelectPreset, "preset-a", gesture);
        Assert.Equal("selectPreset", HotkeyActionNames.ToName(HotkeyAction.SelectPreset));
        Assert.Equal("select-preset:preset-a", binding.Describe());
    }
}
