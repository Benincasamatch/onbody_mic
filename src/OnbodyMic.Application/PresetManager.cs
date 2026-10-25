namespace OnbodyMic.Application;

using OnbodyMic.Domain;

/// <summary>
/// Preset use cases (P3): create/duplicate/rename/delete/save/apply plus
/// startup restore. Applies ride the session's serialized command thread and
/// its transactional teardown; only a successful apply updates the persisted
/// last-preset id (docs/06 §5.3.5, T23/T24/T38).
/// </summary>
public sealed class PresetManager
{
    private readonly AudioSession _session;
    private readonly IPresetStore _store;
    private readonly ILastPresetSink _lastPreset;

    public PresetManager(AudioSession session, IPresetStore store, ILastPresetSink lastPreset)
    {
        _session = session;
        _store = store;
        _lastPreset = lastPreset;
    }

    public event Action<string>? Noticed;

    public IPresetStore Store => _store;

    public string? ActivePresetId => _session.Status().LastPresetId;

    public IReadOnlyList<PresetSummary> List() => _store.List();

    public bool Exists(string presetId) => _store.Exists(presetId);

    /// <summary>Captures the current session configuration plus the caller's library view as a new preset.</summary>
    public CreateResult Create(string name, IReadOnlyList<string> clipLayout, bool favoritesOnly, string? groupId)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new CreateResult(CommandResult.Reject("name-required"), null);
        }

        var config = Snapshot(_store.NewId(), name.Trim(), clipLayout, favoritesOnly, groupId);
        var saved = _store.Save(config);
        if (!saved.Accepted)
        {
            return new CreateResult(saved, null);
        }

        Notice($"已创建预设 {config.PresetId}（{config.Name}）");
        return new CreateResult(CommandResult.Ok, config);
    }

    /// <summary>Overwrites the stored preset with the current session configuration and view.</summary>
    public CommandResult Save(string presetId, IReadOnlyList<string> clipLayout, bool favoritesOnly, string? groupId)
    {
        var load = _store.Load(presetId);
        if (load.Status != PresetLoadStatus.Loaded)
        {
            return load.Status == PresetLoadStatus.NotFound
                ? CommandResult.Reject("preset-not-found")
                : CommandResult.Reject($"preset-corrupt:{load.Reason}");
        }

        var config = Snapshot(presetId, load.Config!.Name, clipLayout, favoritesOnly, groupId);
        var result = _store.Save(config);
        if (result.Accepted)
        {
            Notice($"已保存预设 {presetId}（发送/监听状态不受影响）");
        }

        return result;
    }

    /// <summary>
    /// Settings-page save: writes new endpoints and monitor preference into
    /// the stored preset, then re-applies it through the same safe switch
    /// transaction (everything turns off; enabling stays explicit).
    /// </summary>
    public PresetApplyResult UpdateDeviceSettings(
        string presetId,
        PresetDeviceConfig devices,
        MonitorPreferences monitor)
    {
        var load = _store.Load(presetId);
        if (load.Status != PresetLoadStatus.Loaded)
        {
            return PresetApplyResult.Reject(load.Status == PresetLoadStatus.NotFound
                ? "preset-not-found"
                : $"preset-corrupt:{load.Reason}");
        }

        var config = load.Config! with { Devices = devices, Monitor = monitor };
        var saved = _store.Save(config);
        if (!saved.Accepted)
        {
            return PresetApplyResult.Reject($"save-failed:{saved.Rejection}");
        }

        Notice($"已更新预设 {presetId} 的设备与监听偏好（正在重新应用，全部发送/监听将关闭）");
        return Apply(presetId);
    }
    public CommandResult Duplicate(string presetId, string newName)
    {
        var load = _store.Load(presetId);
        if (load.Status != PresetLoadStatus.Loaded)
        {
            return load.Status == PresetLoadStatus.NotFound
                ? CommandResult.Reject("preset-not-found")
                : CommandResult.Reject($"preset-corrupt:{load.Reason}");
        }

        if (string.IsNullOrWhiteSpace(newName))
        {
            return CommandResult.Reject("name-required");
        }

        var copy = load.Config! with { PresetId = _store.NewId(), Name = newName.Trim() };
        var result = _store.Save(copy);
        if (result.Accepted)
        {
            Notice($"已复制 {presetId} → {copy.PresetId}（{copy.Name}）");
        }

        return result;
    }

    public CommandResult Rename(string presetId, string newName) => WithNotice(_store.Rename(presetId, newName), $"已重命名 {presetId}");

    /// <summary>
    /// Deletes a preset. Deleting the active one first tears the session down
    /// and clears the last-preset id; no other preset auto-activates (T38).
    /// </summary>
    public CommandResult Delete(string presetId)
    {
        if (!_store.Exists(presetId))
        {
            return CommandResult.Reject("preset-not-found");
        }

        if (ActivePresetId == presetId)
        {
            var teardown = _session.TeardownForAbort();
            if (!teardown.Accepted)
            {
                return teardown;
            }
        }

        var result = _store.Delete(presetId);
        if (!result.Accepted)
        {
            return result;
        }

        if (_lastPreset.LastPresetId == presetId)
        {
            _lastPreset.SetLastPreset(null);
        }

        Notice($"已删除预设 {presetId}" + (ActivePresetId == presetId ? "（原活动预设已安全关闭，请重新选择）" : string.Empty));
        return CommandResult.Ok;
    }

    /// <summary>
    /// Applies a preset: an unreadable-but-present preset still stops the
    /// session and keeps the old selection muted (B06); only a successful
    /// apply commits the new id as the last preset.
    /// </summary>
    public PresetApplyResult Apply(string presetId)
    {
        if (!PresetConfigRules.IsValidId(presetId))
        {
            return PresetApplyResult.Reject("invalid-preset-id");
        }

        var load = _store.Load(presetId);
        if (load.Status == PresetLoadStatus.NotFound)
        {
            // an id that never existed is an invalid command, not a switch attempt
            return PresetApplyResult.Reject("preset-not-found");
        }

        if (load.Status == PresetLoadStatus.Corrupt)
        {
            var teardown = _session.TeardownForAbort();
            if (!teardown.Accepted)
            {
                return PresetApplyResult.Reject($"teardown-failed:{teardown.Rejection}");
            }

            Notice($"预设损坏，已安全关闭；保留旧选择: {load.Reason}");
            return PresetApplyResult.Reject($"preset-corrupt:{load.Reason}");
        }

        var result = _session.ApplyPreset(load.Config!);
        if (result.Accepted)
        {
            _lastPreset.SetLastPreset(presetId);
            var issues = result.Issues.Count == 0
                ? string.Empty
                : "；失效: " + string.Join(", ", result.Issues.Select(i => $"{i.Route}:{i.Reason}"));
            var attach = result.OutputAttachRejection is null
                ? string.Empty
                : $"；输出接入失败: {result.OutputAttachRejection}";
            Notice($"已应用预设 {load.Config!.Name}（发送/监听全部关闭，需手动开启）{issues}{attach}");
        }

        return result;
    }

    /// <summary>Startup restore (T01/T24): config only, every send/monitor switch stays off.</summary>
    public CommandResult RestoreStartup()
    {
        var id = _lastPreset.LastPresetId;
        if (id is null)
        {
            return CommandResult.Reject("no-last-preset");
        }

        var result = Apply(id);
        return result.Accepted ? CommandResult.Ok : CommandResult.Reject(result.Rejection ?? "apply-failed");
    }

    public CommandResult RestoreBackup(string presetId) => WithNotice(_store.RestoreBackup(presetId), $"已从备份恢复 {presetId}");

    /// <summary>Presets whose layout references the clip; used before deleting a library entry (docs/01 §5.3).</summary>
    public IReadOnlyList<string> PresetsReferencingClip(string clipId) =>
        _store.List()
            .Where(s => !s.Corrupt)
            .Where(s => _store.Load(s.PresetId).Config?.ClipLayout.Contains(clipId) == true)
            .Select(s => s.Name)
            .ToList();

    /// <summary>Snapshots the live session configuration; the view fields come from the caller.</summary>
    public PresetConfig Snapshot(
        string presetId,
        string name,
        IReadOnlyList<string> clipLayout,
        bool favoritesOnly,
        string? groupId)
    {
        var status = _session.Status();
        var devices = status.Devices ?? new PresetDeviceConfig("", "", null, "", null);
        return new PresetConfig(
            presetId,
            name,
            devices,
            status.Gains.Mic,
            status.Gains.App,
            status.Gains.Clip,
            new MonitorPreferences(status.MonitorMode, status.Gains.Monitor),
            clipLayout,
            favoritesOnly,
            groupId);
    }

    public readonly record struct CreateResult(CommandResult Result, PresetConfig? Config);

    private CommandResult WithNotice(CommandResult result, string message)
    {
        if (result.Accepted)
        {
            Notice(message);
        }

        return result;
    }

    private void Notice(string message) => Noticed?.Invoke(message);
}
