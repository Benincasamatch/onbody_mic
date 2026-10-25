namespace OnbodyMic.Domain;

/// <summary>
/// The application a preset captures (docs/02 §8.3): a stable executable
/// path, never a PID. Applying a preset stores the selection; binding to a
/// live process instance stays an explicit user action.
/// </summary>
public sealed record AppSelection(string? ExecutablePath, string? DisplayName)
{
    public static AppSelection None { get; } = new(null, null);

    public bool IsBound => !string.IsNullOrWhiteSpace(ExecutablePath);
}

/// <summary>
/// Device endpoints and app selection a preset configures. Ids are opaque
/// stable endpoint identifiers; a missing device keeps the configured id and
/// marks the route invalid instead of substituting a default (B05).
/// </summary>
public sealed record PresetDeviceConfig(
    string MicDeviceId,
    string VirtualRenderDeviceId,
    string? VirtualCaptureDeviceId,
    string MonitorDeviceId,
    AppSelection? Application);

/// <summary>Monitor preference only: the enabled state is runtime and never restores (docs/06 §1.1).</summary>
public sealed record MonitorPreferences(MonitorMode Mode, float Gain);
/// <summary>
/// A named, versioned routing configuration (docs/06 §4.2). Describes what
/// to configure; the running session stays authoritative for what is
/// currently sending.
/// </summary>
public sealed record PresetConfig(
    string PresetId,
    string Name,
    PresetDeviceConfig Devices,
    float MicGain,
    float AppGain,
    float ClipGain,
    MonitorPreferences Monitor,
    IReadOnlyList<string> ClipLayout,
    bool LibraryFavoritesOnly,
    string? LibraryGroupId)
{
    public static PresetGainLimits GainLimits { get; } = new(0f, 4f);
}

public readonly record struct PresetGainLimits(float Min, float Max);

/// <summary>Load-time validation outcome: config data plus non-fatal issues for the UI to surface.</summary>
public readonly record struct PresetValidation(IReadOnlyList<string> Issues)
{
    public static PresetValidation Clean { get; } = new([]);

    public bool HasIssues => Issues.Count > 0;
}

public static class PresetConfigRules
{
    /// <summary>
    /// Validates structure and ranges (docs/06 §7): finite gains within
    /// limits, non-empty required ids, safe id charset for file storage.
    /// Structural failures reject the preset; dangling layout ids are
    /// reported as issues for the caller to filter.
    /// </summary>
    public static bool IsValidId(string presetId) => ManagedIdRules.IsValidToken(presetId);

    public static bool IsValidGain(float gain, PresetGainLimits limits) =>
        float.IsFinite(gain) && gain >= limits.Min && gain <= limits.Max;

    /// <summary>Returns non-fatal issues for an otherwise loadable config; empty when fully consistent.</summary>
    public static PresetValidation Validate(PresetConfig config, Func<string, bool> clipExists)
    {
        var issues = new List<string>();
        if (!IsValidGain(config.MicGain, PresetConfig.GainLimits))
        {
            issues.Add("micGain");
        }

        if (!IsValidGain(config.AppGain, PresetConfig.GainLimits))
        {
            issues.Add("appGain");
        }

        if (!IsValidGain(config.ClipGain, PresetConfig.GainLimits))
        {
            issues.Add("clipGain");
        }

        if (!string.IsNullOrWhiteSpace(config.LibraryGroupId)
            && !ManagedIdRules.IsValidToken(config.LibraryGroupId))
        {
            issues.Add("libraryGroupId");
        }

        foreach (var clipId in config.ClipLayout)
        {
            if (!clipExists(clipId))
            {
                issues.Add($"clipLayout:{clipId}");
            }
        }

        return new PresetValidation(issues);
    }
}


/// <summary>Shared token charset for ids that become part of managed file names (presets, groups, clips).</summary>
public static class ManagedIdRules
{
    public static bool IsValidToken(string token) =>
        token.Length > 0
        && token.Length <= 64
        && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
