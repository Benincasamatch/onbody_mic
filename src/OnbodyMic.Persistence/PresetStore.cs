namespace OnbodyMic.Persistence;

using System.Text.Json;
using System.Text.Json.Serialization;
using OnbodyMic.Application;
using OnbodyMic.Domain;

/// <summary>
/// One JSON file per preset under <c>presets/</c> (docs/02 §10). Structural
/// garbage is refused with the file left untouched; saves are atomic with a
/// recoverable backup. Field names follow docs/06 §4.2.
/// </summary>
public sealed class JsonPresetStore : IPresetStore
{
    private const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _directory;

    public JsonPresetStore(string directory) => _directory = directory;

    public IReadOnlyList<PresetSummary> List()
    {
        var result = new List<PresetSummary>();
        if (!Directory.Exists(_directory))
        {
            return result;
        }

        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            var load = Load(id);
            result.Add(new PresetSummary(
                id,
                load.Config?.Name is { Length: > 0 } name ? name : id,
                load.Status == PresetLoadStatus.Corrupt));
        }

        result.Sort((a, b) => string.CompareOrdinal(a.PresetId, b.PresetId));
        return result;
    }

    public PresetLoadResult Load(string presetId)
    {
        if (!PresetConfigRules.IsValidId(presetId))
        {
            return PresetLoadResult.Corrupt($"invalid preset id '{presetId}'");
        }

        var path = PathFor(presetId);
        if (!File.Exists(path))
        {
            return PresetLoadResult.NotFound();
        }

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            return PresetLoadResult.Corrupt($"unreadable: {ex.Message}");
        }

        return Parse(presetId, json);
    }

    public CommandResult Save(PresetConfig config)
    {
        var structural = ValidateStructure(config);
        if (structural is not null)
        {
            return CommandResult.Reject(structural);
        }

        Directory.CreateDirectory(_directory);
        AtomicFile.WriteText(PathFor(config.PresetId), JsonSerializer.Serialize(ToFileData(config), JsonOptions));
        return CommandResult.Ok;
    }

    public bool Exists(string presetId) =>
        PresetConfigRules.IsValidId(presetId) && File.Exists(PathFor(presetId));

    public CommandResult Delete(string presetId)
    {
        if (!Exists(presetId))
        {
            return CommandResult.Reject("preset-not-found");
        }

        var path = PathFor(presetId);
        foreach (var side in new[] { path + ".tmp", path + ".bak" })
        {
            if (File.Exists(side))
            {
                File.Delete(side);
            }
        }

        File.Delete(path);
        return CommandResult.Ok;
    }

    public CommandResult Rename(string presetId, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
        {
            return CommandResult.Reject("name-required");
        }

        var load = Load(presetId);
        return load.Status switch
        {
            PresetLoadStatus.NotFound => CommandResult.Reject("preset-not-found"),
            PresetLoadStatus.Corrupt => CommandResult.Reject($"preset-corrupt:{load.Reason}"),
            _ => Save(load.Config! with { Name = newName.Trim() }),
        };
    }

    /// <summary>
    /// Restores the backup after re-validating it (docs/06 §7: verified
    /// recovery). The current (broken) file becomes the new backup, so the
    /// operation stays reversible; an invalid backup changes nothing.
    /// </summary>
    public CommandResult RestoreBackup(string presetId)
    {
        if (!PresetConfigRules.IsValidId(presetId))
        {
            return CommandResult.Reject("invalid-preset-id");
        }

        var backup = PathFor(presetId) + ".bak";
        if (!File.Exists(backup))
        {
            return CommandResult.Reject("no-backup");
        }

        string json;
        try
        {
            json = File.ReadAllText(backup);
        }
        catch (Exception ex)
        {
            return CommandResult.Reject($"backup-unreadable: {ex.Message}");
        }

        if (Parse(presetId, json).Status != PresetLoadStatus.Loaded)
        {
            return CommandResult.Reject("backup-invalid");
        }

        AtomicFile.WriteText(PathFor(presetId), json);
        return CommandResult.Ok;
    }

    public string NewId()
    {
        while (true)
        {
            var id = "preset-" + Random.Shared.Next(0x1000000, int.MaxValue).ToString("x8");
            if (!Exists(id))
            {
                return id;
            }
        }
    }

    private string PathFor(string presetId) => Path.Combine(_directory, presetId + ".json");

    internal static PresetLoadResult Parse(string presetId, string json)
    {
        PresetFileData data;
        try
        {
            data = JsonSerializer.Deserialize<PresetFileData>(json, JsonOptions)
                ?? throw new JsonException("empty preset document");
        }
        catch (JsonException ex)
        {
            return PresetLoadResult.Corrupt($"json invalid: {ex.Message}");
        }

        if (data.SchemaVersion != SchemaVersion)
        {
            return PresetLoadResult.Corrupt($"unsupported schemaVersion: {data.SchemaVersion}");
        }

        if (!string.IsNullOrEmpty(data.PresetId) && !string.Equals(data.PresetId, presetId, StringComparison.Ordinal))
        {
            return PresetLoadResult.Corrupt($"presetId mismatch: file says '{data.PresetId}', expected '{presetId}'");
        }

        var config = ToConfig(presetId, data);
        var structural = ValidateStructure(config);
        return structural is null
            ? PresetLoadResult.Loaded(config)
            : PresetLoadResult.Corrupt(structural);
    }

    /// <summary>Load-time structural rules (docs/06 §7); null means loadable.</summary>
    private static string? ValidateStructure(PresetConfig config)
    {
        if (!PresetConfigRules.IsValidId(config.PresetId))
        {
            return $"invalid preset id '{config.PresetId}'";
        }

        if (string.IsNullOrWhiteSpace(config.Name))
        {
            return "name-required";
        }

        if (string.IsNullOrWhiteSpace(config.Devices.MicDeviceId)
            || string.IsNullOrWhiteSpace(config.Devices.VirtualRenderDeviceId)
            || string.IsNullOrWhiteSpace(config.Devices.MonitorDeviceId))
        {
            return "device-id-required";
        }

        if (!PresetConfigRules.IsValidGain(config.MicGain, PresetConfig.GainLimits)
            || !PresetConfigRules.IsValidGain(config.AppGain, PresetConfig.GainLimits)
            || !PresetConfigRules.IsValidGain(config.ClipGain, PresetConfig.GainLimits)
            || !PresetConfigRules.IsValidGain(config.Monitor.Gain, PresetConfig.GainLimits))
        {
            return "gain-out-of-range";
        }

        return null;
    }

    private static PresetConfig ToConfig(string presetId, PresetFileData data) => new(
        presetId,
        data.Name ?? "",
        new PresetDeviceConfig(
            data.Microphone?.DeviceStableId ?? "",
            data.VirtualOutput?.DeviceStableId ?? "",
            data.VirtualOutput?.CaptureDeviceStableId,
            data.Monitor?.DeviceStableId ?? "",
            data.Application?.Selection is null
                ? null
                : new AppSelection(data.Application.Selection.ExecutablePath, data.Application.Selection.DisplayName)),
        data.Microphone?.Gain ?? 1f,
        data.AppGain ?? 1f,
        data.ClipBus?.Gain ?? 1f,
        new MonitorPreferences(ParseMonitorMode(data.Monitor?.Mode), data.Monitor?.Gain ?? 1f),
        data.ClipLayout ?? [],
        data.LibraryView?.FavoritesOnly ?? false,
        data.LibraryView?.GroupId);

    private static PresetFileData ToFileData(PresetConfig config) => new()
    {
        SchemaVersion = SchemaVersion,
        PresetId = config.PresetId,
        Name = config.Name,
        Application = config.Devices.Application is { IsBound: true } app
            ? new ApplicationSelectionData { Selection = new() { ExecutablePath = app.ExecutablePath, DisplayName = app.DisplayName } }
            : null,
        Microphone = new() { DeviceStableId = config.Devices.MicDeviceId, Gain = config.MicGain },
        AppGain = config.AppGain,
        ClipBus = new() { Gain = config.ClipGain },
        ClipLayout = [.. config.ClipLayout],
        LibraryView = new() { FavoritesOnly = config.LibraryFavoritesOnly, GroupId = config.LibraryGroupId },
        VirtualOutput = new()
        {
            DeviceStableId = config.Devices.VirtualRenderDeviceId,
            CaptureDeviceStableId = config.Devices.VirtualCaptureDeviceId,
        },
        Monitor = new()
        {
            Mode = config.Monitor.Mode == MonitorMode.FullEndpoint ? "FullEndpoint" : "ClipOnly",
            DeviceStableId = config.Devices.MonitorDeviceId,
            Gain = config.Monitor.Gain,
        },
    };

    private static MonitorMode ParseMonitorMode(string? mode) =>
        mode == "FullEndpoint" ? MonitorMode.FullEndpoint : MonitorMode.ClipOnly;

    private sealed class PresetFileData
    {
        public int SchemaVersion { get; set; } = 1;

        public string? PresetId { get; set; }

        public string? Name { get; set; }

        public ApplicationSelectionData? Application { get; set; }

        public MicrophoneData? Microphone { get; set; }

        public float? AppGain { get; set; }

        public ClipBusData? ClipBus { get; set; }

        public List<string>? ClipLayout { get; set; }

        public LibraryViewData? LibraryView { get; set; }

        public VirtualOutputData? VirtualOutput { get; set; }

        public MonitorData? Monitor { get; set; }
    }

    private sealed class ApplicationSelectionData
    {
        public SelectionData? Selection { get; set; }
    }

    private sealed class SelectionData
    {
        public string? ExecutablePath { get; set; }

        public string? DisplayName { get; set; }
    }

    private sealed class MicrophoneData
    {
        public string? DeviceStableId { get; set; }

        public float? Gain { get; set; }
    }

    private sealed class ClipBusData
    {
        public float? Gain { get; set; }
    }

    private sealed class LibraryViewData
    {
        public bool FavoritesOnly { get; set; }

        public string? GroupId { get; set; }
    }

    private sealed class VirtualOutputData
    {
        public string? DeviceStableId { get; set; }

        public string? CaptureDeviceStableId { get; set; }
    }

    private sealed class MonitorData
    {
        public string? Mode { get; set; }

        public string? DeviceStableId { get; set; }

        public float? Gain { get; set; }
    }
}
