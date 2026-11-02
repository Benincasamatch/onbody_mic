namespace OnbodyMic.EngineHost;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using OnbodyMic.Application;
using OnbodyMic.Audio;
using OnbodyMic.Domain;
using OnbodyMic.Media;
using OnbodyMic.Platform.Windows;

internal sealed class EngineRuntime : IAsyncDisposable
{
    private sealed record ManagedEntry(ClipItem Item, string ManagedPath);
    private sealed record LibraryView(LibraryData Data, Dictionary<string, ManagedEntry> Entries);
    private sealed class LibrarySlot(string root)
    {
        public string Root { get; } = root;
        public ClipLibrary? Library { get; set; }
        public string? Error { get; set; }
        public Action? Changed { get; set; }
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly string _root;
    private readonly NdjsonWriter _writer;
    private readonly SessionCommandQueue _controls = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly OwnedClipDecoder _decoder;
    private readonly ManagedClipSampleStore _samples;
    private readonly object _deviceGate = new();
    private readonly object _workerGate = new();
    private readonly object _shutdownGate = new();
    private readonly HashSet<Task> _workers = [];
    private readonly List<LibrarySlot> _libraries = [];
    private readonly HashSet<LibrarySlot> _prefilling = [];
    private readonly ConcurrentDictionary<string, byte> _deletingClips = new(StringComparer.Ordinal);
    private Dictionary<string, PresetConfig> _presets = new(StringComparer.Ordinal);
    private volatile LibraryView? _libraryView;
    private volatile WindowsAppCatalog.AppInstance? _boundApplication;
    private volatile string? _virtualRenderDeviceId;
    private LibrarySlot? _librarySlot;
    private SettingsData? _settings;
    private AudioSession? _session;
    private QueuedHotkeyService? _hotkeys;
    private HotkeyDispatcher? _dispatcher;
    private HotkeyData _hotkeyData = new([], [], HotkeyBindings.Actions);
    private RuntimeIssueData[] _issues = [];
    private PresetConfig? _activePresetConfig;
    private RuntimeStateData? _lastState;
    private Timer? _heartbeat;
    private Task _libraryTail = Task.CompletedTask;
    private Task _deviceTail = Task.CompletedTask;
    private Task? _shutdown;
    private long _revision;
    private long _playIntent;
    private string? _pendingClipId;
    private bool _pendingClipIsSend;
    private bool _initialized;
    private int _stopping;

    public EngineRuntime(string root, NdjsonWriter writer)
    {
        _root = root;
        _writer = writer;
        _decoder = new OwnedClipDecoder(_lifetime.Token);
        _samples = new ManagedClipSampleStore(_decoder);
    }

    public Task<object?> SubmitAsync(string operation, JsonElement arguments)
    {
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _controls.Post(operation, () =>
            {
                if (Volatile.Read(ref _stopping) != 0)
                {
                    completion.TrySetException(HostCommandException.Rejected("engine-stopping"));
                    return;
                }
                try
                {
                    var pending = ExecuteAsync(operation, arguments);
                    if (pending.IsCompleted)
                    {
                        CompleteRequest(operation, completion, pending.GetAwaiter().GetResult(), null);
                    }
                    else
                    {
                        // Preparing playback is synchronous and ordered. Only its decode/completion leaves this lane.
                        if (operation == "play") PublishState();
                        Track(CompleteRequestAsync(operation, pending, completion));
                    }
                }
                catch (Exception ex) { CompleteRequest(operation, completion, null, ex); }
            });
        }
        catch (Exception ex) { completion.TrySetException(ex); }
        return completion.Task;
    }

    private async Task CompleteRequestAsync(string operation, Task<object?> pending, TaskCompletionSource<object?> completion)
    {
        object? result = null;
        Exception? error = null;
        try { result = await pending.ConfigureAwait(false); }
        catch (Exception ex) { error = ex; }
        try
        {
            await OnControlsAsync(() =>
            {
                CompleteRequest(operation, completion, result, error);
                return true;
            }, allowStopping: true).ConfigureAwait(false);
        }
        catch (Exception ex) { completion.TrySetException(ex); }
    }

    private void CompleteRequest(string operation, TaskCompletionSource<object?> completion, object? result, Exception? error)
    {
        if (Volatile.Read(ref _stopping) != 0)
            error ??= HostCommandException.Rejected("engine-stopping");
        if (operation != "snapshot") PublishState();
        if (error is not null)
        {
            completion.TrySetException(error);
            return;
        }
        if (operation is "snapshot" or "init") result = Snapshot();
        completion.TrySetResult(result);
    }

    private Task<object?> ExecuteAsync(string operation, JsonElement args)
    {
        if (operation == "init") return InitializeAsync(args);
        if (!_initialized) throw HostCommandException.Rejected("engine-not-initialized");
        var session = _session!;
        object? result;
        switch (operation)
        {
            case "snapshot":
                return SnapshotReadyAsync();
            case "settings_apply":
                return ApplySettingsAsync(CommandArguments.Object<SettingsData>(args, "settings"));
            case "set_catalog":
                _presets = ReadCatalog(args);
                if (session.Status().LastPresetId is { } active && !_presets.ContainsKey(active)) ClearPreset();
                ReapplyHotkeys();
                result = true;
                break;
            case "apply_preset":
                PresetConfig config;
                try { config = CommandArguments.Object<PresetConfigData>(args, "config").ToDomain(); }
                catch
                {
                    _pendingClipId = null;
                    _playIntent++;
                    RequireAccepted(session.TeardownForAbort());
                    throw;
                }
                var applied = ApplyConfiguration(config);
                if (!applied.Accepted) throw HostCommandException.Rejected(applied.Rejection ?? "preset-apply-rejected");
                result = applied;
                break;
            case "clear_preset":
                ClearPreset();
                result = true;
                break;
            case "devices":
                return QueueDeviceWork(() => ReadDevices());
            case "device_enable":
                var deviceId = CommandArguments.String(args, "deviceId");
                return QueueDeviceWork(() => EnableDevice(deviceId));
            case "apps":
                return Task.Run<object?>(ReadApplications, _lifetime.Token);
            case "bind_app":
                BindApplication(CommandArguments.ProcessId(args), CommandArguments.String(args, "executablePath"),
                    CommandArguments.String(args, "displayName"), CommandArguments.String(args, "startTimeTicks"));
                result = true;
                break;
            case "route":
                Route(CommandArguments.String(args, "kind"), CommandArguments.Boolean(args, "enabled"),
                    CommandArguments.OptionalString(args, "monitorMode"));
                result = true;
                break;
            case "gains":
                RequireAccepted(session.SetGains(new RouteGains(CommandArguments.Gain(args, "mic"), CommandArguments.Gain(args, "app"),
                    CommandArguments.Gain(args, "clip"), CommandArguments.Gain(args, "monitor"))));
                result = true;
                break;
            case "play":
                return BeginPlayback(CommandArguments.String(args, "clipId"), CommandArguments.String(args, "mode"));
            case "stop":
                _pendingClipId = null;
                _playIntent++;
                RequireAccepted(session.StopClip());
                result = true;
                break;
            case "mute":
                _pendingClipId = null;
                _playIntent++;
                RequireAccepted(session.EmergencyMute());
                result = true;
                break;
            case "unlock":
                RequireAccepted(session.UnlockEmergency());
                result = true;
                break;
            case "attach_output":
                _pendingClipId = null;
                _playIntent++;
                RequireAccepted(session.TeardownForAbort());
                RequireAccepted(session.AttachOutput());
                ClearIssue(PresetRoute.VirtualOutput);
                result = true;
                break;
            case "prepare_folder_import":
                var folderPaths = CommandArguments.Required(args, "paths");
                if (folderPaths.ValueKind != JsonValueKind.Array ||
                    folderPaths.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())))
                    throw HostCommandException.Invalid("paths must be an array of non-empty strings");
                var importPaths = folderPaths.EnumerateArray().Select(value => value.GetString()!).ToArray();
                return QueueLibraryWork(library => FolderImportData.FromDomain(library.PrepareFolderImport(importPaths)));
            case "import_clip":
                var path = CommandArguments.String(args, "path");
                var name = CommandArguments.OptionalString(args, "displayName");
                if (name is not null && string.IsNullOrWhiteSpace(name))
                    throw HostCommandException.Invalid("displayName must not be empty");
                var allowDuplicate = CommandArguments.OptionalBoolean(args, "allowDuplicate");
                var importGroupId = CommandArguments.OptionalString(args, "groupId");
                return QueueLibraryWork(library =>
                {
                    var imported = library.Import(path, name, allowDuplicate, importGroupId);
                    return new ImportResultData(imported.Outcome.ToString(), ClipItemData.FromDomain(imported.Item, library),
                        imported.ExistingDuplicate is { } duplicate ? ClipItemData.FromDomain(duplicate, library) : null);
                });
            case "clip_rename":
                var renameId = CommandArguments.String(args, "clipId");
                var clipName = CommandArguments.String(args, "name");
                return QueueLibraryWork(library =>
                {
                    RequireClip(library, renameId);
                    if (!library.Rename(renameId, clipName)) throw HostCommandException.Rejected("clip-rename-rejected");
                    return true;
                });
            case "clip_favorite":
                var favoriteId = CommandArguments.String(args, "clipId");
                var favorite = CommandArguments.Boolean(args, "value");
                return QueueLibraryWork(library =>
                {
                    RequireClip(library, favoriteId);
                    if (!library.SetFavorite(favoriteId, favorite)) throw HostCommandException.Rejected("clip-favorite-rejected");
                    return true;
                });
            case "clip_gain":
                var gainId = CommandArguments.String(args, "clipId");
                var gain = CommandArguments.Gain(args, "gain");
                return QueueLibraryWork(library =>
                {
                    RequireClip(library, gainId);
                    if (!library.SetGain(gainId, gain)) throw HostCommandException.Rejected("clip-gain-rejected");
                    return true;
                });
            case "clip_group":
                var groupClipId = CommandArguments.String(args, "clipId");
                _ = CommandArguments.Required(args, "groupId");
                var groupId = CommandArguments.OptionalString(args, "groupId");
                return QueueLibraryWork(library =>
                {
                    RequireClip(library, groupClipId);
                    if (groupId is not null) RequireGroup(library, groupId);
                    if (!library.SetGroup(groupClipId, groupId)) throw HostCommandException.Rejected("clip-group-rejected");
                    return true;
                });
            case "clip_delete":
                return DeleteClipAsync(CommandArguments.String(args, "clipId"));
            case "group_create":
                var groupName = CommandArguments.String(args, "name");
                var parentGroupId = CommandArguments.OptionalString(args, "parentGroupId");
                return QueueLibraryWork(library =>
                {
                    if (parentGroupId is not null) RequireGroup(library, parentGroupId);
                    return library.CreateGroup(groupName, parentGroupId)
                        ?? throw HostCommandException.Rejected("group name already exists");
                });
            case "group_rename":
                var renameGroupId = CommandArguments.String(args, "groupId");
                var renameGroupName = CommandArguments.String(args, "name");
                return QueueLibraryWork(library =>
                {
                    RequireGroup(library, renameGroupId);
                    if (!library.RenameGroup(renameGroupId, renameGroupName))
                        throw HostCommandException.Rejected("group name already exists");
                    return true;
                });
            case "group_delete":
                var deleteGroupId = CommandArguments.String(args, "groupId");
                return QueueLibraryWork(library =>
                {
                    RequireGroup(library, deleteGroupId);
                    return library.DeleteGroup(deleteGroupId);
                });
            case "clip_path":
                var pathId = CommandArguments.String(args, "clipId");
                return QueueLibraryWork(library =>
                {
                    var item = RequireClip(library, pathId);
                    return new { managedPath = library.AssetAbsolutePath(item), originalPath = item.OriginalImportPath };
                });
            case "hotkeys_suspend":
                if (CommandArguments.Boolean(args, "value")) _dispatcher!.Suspend();
                else _dispatcher!.Resume();
                result = true;
                break;
            case "exit":
                StopAudioPaths();
                result = true;
                break;
            default:
                throw HostCommandException.Invalid($"unknown operation: {operation}");
        }
        return Task.FromResult<object?>(result);
    }

    private Task<object?> InitializeAsync(JsonElement args)
    {
        if (_session is not null) throw HostCommandException.Rejected("engine-already-initialized");
        var settings = CommandArguments.Object<SettingsData>(args, "settings");
        settings.Validate();
        var catalog = ReadCatalog(args);
        var libraryRoot = ResolveLibraryRoot(settings);
        // Establish NAudio's process-wide enumerator before any raw COM output/capture pump can activate it.
        lock (_deviceGate) _ = WindowsAudioDevices.List(activeOnly: false);
        _settings = settings;
        _presets = catalog;
        _session = new AudioSession(new AudioSessionOptions
        {
            ClipDecoder = _decoder,
            SampleStore = _samples,
            ClipResolver = id =>
            {
                var resolution = ResolveClip(id);
                // AudioSession's raw-path API is intentionally unavailable at the IPC boundary, including deletion races.
                if (!resolution.Known) throw new FileNotFoundException($"unknown managed clip: {id}");
                return resolution;
            },
            MicAdapterFactory = (id, channel) => new WindowsMicSource(id, channel),
            AppAdapterFactory = (channel, binding) =>
            {
                var expected = _boundApplication ?? throw new InvalidOperationException("no verified application binding");
                var actual = ReadLiveApplication(binding.ProcessId);
                if (expected.ProcessId != binding.ProcessId || expected.StartTimeTicks != actual.StartTimeTicks
                    || !string.Equals(expected.ExecutablePath, actual.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("application process identity changed; bind the live instance again");
                return new WindowsAppSource(binding, channel, expected.StartTimeTicks);
            },
            OutputFactory = id => new WasapiExclusiveRenderPump(id),
            MonitorOutputFactory = id =>
            {
                if (string.Equals(id, _virtualRenderDeviceId, StringComparison.Ordinal))
                    throw new InvalidOperationException("monitor output cannot be the virtual render endpoint");
                return new WindowsMonitorOutput(id);
            },
            MonitorCaptureFactory = (id, _) => new WindowsMonitorCapture(id),
            DeviceValidator = ValidateDevices,
        });
        _session.Noticed += OnNotice;
        _hotkeys = new QueuedHotkeyService(EnqueueHotkey);
        _dispatcher = new HotkeyDispatcher(_session, _hotkeys, ResolveClip, id => _presets.ContainsKey(id), ApplyHotkeyPreset);
        _dispatcher.Noticed += OnNotice;
        var opening = StartLibraryOpen(libraryRoot);
        return FinishInitializationAsync(opening, settings.LastPresetId);
    }

    private async Task<object?> FinishInitializationAsync(Task<object?> opening, string? lastPresetId)
    {
        await opening.ConfigureAwait(false);
        return await OnControlsAsync<object?>(() =>
        {
            if (!string.IsNullOrWhiteSpace(lastPresetId))
            {
                if (_presets.TryGetValue(lastPresetId, out var preset))
                {
                    var applied = ApplyConfiguration(preset);
                    if (!applied.Accepted) Notice("Info", $"startup preset was not restored: {applied.Rejection}");
                }
                else Notice("Info", $"startup preset is unavailable: {lastPresetId}");
            }
            _initialized = true;
            ReapplyHotkeys();
            _heartbeat = new Timer(_ =>
            {
                if (Volatile.Read(ref _stopping) != 0) return;
                try { _controls.Post("state-tick", PublishState); }
                catch (ObjectDisposedException) { }
            }, null, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));
            StartPrefill(_librarySlot!);
            return true;
        }).ConfigureAwait(false);
    }

    private static Dictionary<string, PresetConfig> ReadCatalog(JsonElement args)
    {
        var data = CommandArguments.Object<PresetConfigData[]>(args, "presets");
        var catalog = new Dictionary<string, PresetConfig>(StringComparer.Ordinal);
        foreach (var item in data)
        {
            if (item is null) throw HostCommandException.Invalid("presets must not contain null");
            var config = item.ToDomain();
            if (!catalog.TryAdd(config.PresetId, config))
                throw HostCommandException.Invalid($"duplicate catalog preset: {config.PresetId}");
        }
        return catalog;
    }

    private Task<object?> ApplySettingsAsync(SettingsData settings)
    {
        settings.Validate();
        var root = ResolveLibraryRoot(settings);
        _settings = settings;
        if (_librarySlot is null || !string.Equals(_librarySlot.Root, root, StringComparison.OrdinalIgnoreCase)
            || (_librarySlot.Ready.Task.IsCompletedSuccessfully && _librarySlot.Library is null))
        {
            _pendingClipId = null;
            _playIntent++;
            _ = _session!.StopClip();
            var opening = StartLibraryOpen(root);
            ReapplyHotkeys();
            return FinishSettingsAsync(opening);
        }
        ReapplyHotkeys();
        if (!_librarySlot.Ready.Task.IsCompletedSuccessfully) return FinishSettingsAsync(_librarySlot.Ready.Task);
        return Task.FromResult<object?>(_hotkeyData);
    }

    private async Task<object?> FinishSettingsAsync(Task opening)
    {
        await opening.ConfigureAwait(false);
        return await OnControlsAsync<object?>(() =>
        {
            ReapplyHotkeys();
            return _hotkeyData;
        }).ConfigureAwait(false);
    }

    private string ResolveLibraryRoot(SettingsData settings)
    {
        if (settings.LibraryRoot is null) return Path.Combine(_root, "library");
        if (string.IsNullOrWhiteSpace(settings.LibraryRoot)) throw HostCommandException.Invalid("libraryRoot must not be empty");
        return Path.GetFullPath(settings.LibraryRoot, _root);
    }

    private Task<object?> StartLibraryOpen(string root)
    {
        var slot = new LibrarySlot(root);
        _libraries.Add(slot);
        _librarySlot = slot;
        _libraryView = null;
        _samples.SetAssets(new Dictionary<string, string>(StringComparer.Ordinal), reset: true);
        return EnqueueLibraryWork(async () =>
        {
            try
            {
                try { slot.Library = ClipLibrary.Open(root, _decoder); }
                catch (Exception ex) when (ex is ClipLibraryLoadException or IOException or UnauthorizedAccessException or JsonException)
                {
                    slot.Error = ex.Message;
                }
                if (slot.Library is { } library)
                {
                    slot.Changed = () => LibraryChanged(slot);
                    library.Changed += slot.Changed;
                }
                var view = CaptureLibrary(slot);
                await OnControlsAsync(() =>
                {
                    if (ReferenceEquals(_librarySlot, slot)) SetLibraryView(slot, view, reset: true);
                    return true;
                }).ConfigureAwait(false);
                slot.Ready.TrySetResult();
                return true;
            }
            catch (Exception ex)
            {
                slot.Ready.TrySetException(ex);
                throw;
            }
        });
    }

    private static LibraryView CaptureLibrary(LibrarySlot slot)
    {
        if (slot.Library is not { } library)
            return new LibraryView(new LibraryData([], [], slot.Error, slot.Root), new Dictionary<string, ManagedEntry>(StringComparer.Ordinal));
        var items = library.ItemsSnapshot();
        var entries = new Dictionary<string, ManagedEntry>(items.Count, StringComparer.Ordinal);
        var clips = new ClipItemData[items.Count];
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            entries.Add(item.ClipId, new ManagedEntry(item, library.AssetAbsolutePath(item)));
            clips[index] = ClipItemData.FromDomain(item, library);
        }
        return new LibraryView(new LibraryData(clips, library.GroupsSnapshot().ToArray(), null, slot.Root), entries);
    }

    private void LibraryChanged(LibrarySlot slot)
    {
        if (Volatile.Read(ref _stopping) != 0) return;
        // ClipLibrary may raise Changed while holding its lock. Capture here (reentrant), never wait for the control lane.
        var view = CaptureLibrary(slot);
        try
        {
            _controls.Post("library-changed", () =>
            {
                if (!ReferenceEquals(_librarySlot, slot) || Volatile.Read(ref _stopping) != 0) return;
                var old = _libraryView;
                SetLibraryView(slot, view, reset: false);
                if (old is null || !SameTargets(old.Data.Clips, view.Data.Clips)) ReapplyHotkeys();
                StartPrefill(slot);
            });
        }
        catch (ObjectDisposedException) { }
    }

    private void SetLibraryView(LibrarySlot slot, LibraryView view, bool reset)
    {
        var previous = _libraryView;
        _libraryView = view;
        _samples.SetAssets(view.Entries.ToDictionary(pair => pair.Key, pair => pair.Value.ManagedPath, StringComparer.Ordinal), reset);
        if (previous is null || !previous.Data.Groups.SequenceEqual(view.Data.Groups) || !SameTargets(previous.Data.Clips, view.Data.Clips))
            UpdateSelectionIssues();
        if (previous is null || previous.Data.LibraryRoot != view.Data.LibraryRoot
            || previous.Data.LibraryError != view.Data.LibraryError
            || !previous.Data.Clips.SequenceEqual(view.Data.Clips) || !previous.Data.Groups.SequenceEqual(view.Data.Groups))
            _writer.Event("library", view.Data);
        if (_initialized && ReferenceEquals(_librarySlot, slot) && reset)
        {
            ReapplyHotkeys();
            StartPrefill(slot);
        }
    }

    private static bool SameTargets(ClipItemData[] old, ClipItemData[] current)
    {
        if (old.Length != current.Length) return false;
        for (var index = 0; index < old.Length; index++)
            if (old[index].ClipId != current[index].ClipId || old[index].Available != current[index].Available) return false;
        return true;
    }

    private void RefreshAvailability()
    {
        var view = _libraryView;
        if (view is null) return;
        ClipItemData[]? changed = null;
        for (var index = 0; index < view.Data.Clips.Length; index++)
        {
            var clip = view.Data.Clips[index];
            var available = File.Exists(view.Entries[clip.ClipId].ManagedPath);
            if (available == clip.Available) continue;
            changed ??= (ClipItemData[])view.Data.Clips.Clone();
            changed[index] = clip with { Available = available };
        }
        if (changed is null) return;
        _libraryView = view with { Data = view.Data with { Clips = changed } };
        _writer.Event("library", _libraryView.Data);
        ReapplyHotkeys();
    }

    private ClipResolution ResolveClip(string id)
    {
        var view = _libraryView;
        if (_deletingClips.ContainsKey(id) || view is null || !view.Entries.TryGetValue(id, out var entry))
            return ClipResolution.Unknown;
        return new ClipResolution(true, File.Exists(entry.ManagedPath), entry.ManagedPath, entry.Item.Gain);
    }

    private Task<object?> QueueLibraryWork(Func<ClipLibrary, object?> operation, LibrarySlot? target = null)
    {
        var slot = target ?? _librarySlot ?? throw HostCommandException.Rejected("library-not-initialized");
        return EnqueueLibraryWork(async () =>
        {
            await slot.Ready.Task.ConfigureAwait(false);
            _lifetime.Token.ThrowIfCancellationRequested();
            var library = slot.Library ?? throw HostCommandException.Rejected($"library unavailable: {slot.Error}");
            return operation(library);
        });
    }

    private Task<object?> EnqueueLibraryWork(Func<Task<object?>> operation)
    {
        var previous = _libraryTail;
        var pending = Task.Run(async () =>
        {
            try { await previous.ConfigureAwait(false); }
            catch (Exception) { /* A failed user mutation must not poison subsequent library commands. */ }
            _lifetime.Token.ThrowIfCancellationRequested();
            return await operation().ConfigureAwait(false);
        });
        _libraryTail = pending;
        return pending;
    }

    private void StartPrefill(LibrarySlot slot)
    {
        if (!_initialized || slot.Library is null || Volatile.Read(ref _stopping) != 0 || !_prefilling.Add(slot)) return;
        var cache = _samples.Cache;
        var work = QueueLibraryWork(library => cache.Prefill(library, _decoder), slot);
        Track(ObservePrefillAsync(slot, work));
    }

    private async Task ObservePrefillAsync(LibrarySlot slot, Task<object?> work)
    {
        Exception? failure = null;
        try { await work.ConfigureAwait(false); }
        catch (Exception ex) { failure = ex; }
        await OnControlsAsync(() =>
        {
            _prefilling.Remove(slot);
            if (failure is not null && failure is not OperationCanceledException)
                Notice("Info", $"clip cache prefill failed: {failure.Message}");
            return true;
        }, allowStopping: true).ConfigureAwait(false);
    }

    private static ClipItem RequireClip(ClipLibrary library, string id) => library.Find(id)
        ?? throw HostCommandException.Rejected($"unknown-clip:{id}");

    private static void RequireGroup(ClipLibrary library, string id)
    {
        if (!library.GroupsSnapshot().Any(group => group.GroupId == id))
            throw HostCommandException.Rejected($"unknown-group:{id}");
    }

    private async Task<object?> DeleteClipAsync(string id)
    {
        var slot = _librarySlot ?? throw HostCommandException.Rejected("library-not-initialized");
        if (!_deletingClips.TryAdd(id, 0)) throw HostCommandException.Rejected("clip deletion is already in progress");
        try
        {
            var status = _session!.Status();
            if (_pendingClipId == id || status.Clip?.ClipId == id)
            {
                _pendingClipId = null;
                _playIntent++;
                _ = _session.StopClip();
            }
            _samples.Remove(id);
            RequireAccepted(_dispatcher!.Unbind(HotkeyAction.PlayClip, id));
            ReapplyHotkeys();
            await QueueLibraryWork(library =>
            {
                RequireClip(library, id);
                if (!library.Delete(id)) throw HostCommandException.Rejected($"unknown-clip:{id}");
                return true;
            }, slot).ConfigureAwait(false);
            return await OnControlsAsync<object?>(() =>
            {
                _deletingClips.TryRemove(id, out _);
                _settings!.ClipHotkeys.Remove(id);
                ReapplyHotkeys();
                return true;
            }).ConfigureAwait(false);
        }
        catch
        {
            await OnControlsAsync(() =>
            {
                _deletingClips.TryRemove(id, out _);
                if (_libraryView is { } view)
                    _samples.SetAssets(view.Entries.ToDictionary(pair => pair.Key, pair => pair.Value.ManagedPath, StringComparer.Ordinal), reset: false);
                ReapplyHotkeys();
                return true;
            }, allowStopping: true).ConfigureAwait(false);
            throw;
        }
    }

    private Task<object?> BeginPlayback(string id, string mode)
    {
        if (mode is not ("preview" or "send")) throw HostCommandException.Invalid("mode must be preview or send");
        if (_libraryView is null) throw HostCommandException.Rejected("library-change-in-progress");
        RefreshAvailability();
        var resolution = ResolveClip(id);
        if (!resolution.Known) throw HostCommandException.Rejected($"unknown-clip:{id}");
        if (!resolution.Available) throw HostCommandException.Rejected($"clip-unavailable:{id}");
        if (mode == "send") RequireOutputReady(_session!.Status());
        var playback = mode == "preview" ? _session!.PreviewClipAsync(id) : _session!.PlayClipAsync(id);
        var intent = ++_playIntent;
        _pendingClipId = id;
        _pendingClipIsSend = mode == "send";
        return FinishPlaybackAsync(playback, intent);
    }

    private async Task<object?> FinishPlaybackAsync(Task<CommandResult> playback, long intent)
    {
        CommandResult result;
        try { result = await playback.ConfigureAwait(false); }
        finally
        {
            await OnControlsAsync(() =>
            {
                if (_playIntent == intent) _pendingClipId = null;
                return true;
            }, allowStopping: true).ConfigureAwait(false);
        }
        RequireAccepted(result);
        return true;
    }

    private void Route(string kind, bool enabled, string? monitorMode)
    {
        var session = _session!;
        var status = session.Status();
        if (enabled && kind is ("mic" or "app" or "extras")) RequireOutputReady(status);
        CommandResult result;
        switch (kind)
        {
            case "mic": result = session.EnableMic(enabled); break;
            case "app": result = session.EnableApp(enabled); break;
            case "extras":
                result = session.EnableExtras(enabled);
                if (!enabled && _pendingClipIsSend && _pendingClipId is not null)
                {
                    _pendingClipId = null;
                    _playIntent++;
                }
                break;
            case "monitor":
                result = session.SetMonitor(enabled, monitorMode is null ? status.MonitorMode : CommandArguments.MonitorMode(monitorMode));
                break;
            default: throw HostCommandException.Invalid("kind must be mic, app, extras or monitor");
        }
        RequireAccepted(result);
        if (enabled)
        {
            if (kind == "mic") ClearIssue(PresetRoute.Microphone);
            else if (kind == "app") ClearIssue(PresetRoute.Application);
            else if (kind == "monitor")
            {
                ClearIssue(PresetRoute.Monitor);
                if (session.Status().MonitorMode == MonitorMode.FullEndpoint) ClearIssue(PresetRoute.VirtualCapture);
            }
        }
    }

    private static void RequireOutputReady(SessionStatus status)
    {
        if (status.EmergencyLatched) throw HostCommandException.Rejected("emergency-latched");
        if (status.Devices is null) throw HostCommandException.Rejected("no-device-config");
        if (status.OutputLost) throw HostCommandException.Rejected("output-lost");
        if (!status.DevicesValidated) throw HostCommandException.Rejected("output-unavailable");
    }

    private PresetApplyResult ApplyConfiguration(PresetConfig config)
    {
        _pendingClipId = null;
        _playIntent++;
        var result = _session!.ApplyPreset(config);
        _boundApplication = null;
        if (result.Accepted)
        {
            _virtualRenderDeviceId = config.Devices.VirtualRenderDeviceId;
            _activePresetConfig = config;
            if (config.Monitor.Mode == MonitorMode.FullEndpoint && string.IsNullOrWhiteSpace(config.Devices.VirtualCaptureDeviceId))
                result = result with { Issues = [.. result.Issues, new RouteIssue(PresetRoute.VirtualCapture, "endpoint-unconfigured")] };
            _issues = result.Issues.Select(RuntimeIssueData.FromDomain).ToArray();
            UpdateSelectionIssues();
        }
        return result;
    }

    private PresetApplyResult ApplyHotkeyPreset(string id)
    {
        if (!_presets.TryGetValue(id, out var config)) return PresetApplyResult.Reject($"unknown-preset:{id}");
        var result = ApplyConfiguration(config);
        if (result.Accepted)
        {
            _writer.Event("preset_applied", new { presetId = id, result });
        }
        return result;
    }

    private void ClearPreset()
    {
        _pendingClipId = null;
        _playIntent++;
        RequireAccepted(_session!.ClearConfiguration());
        _boundApplication = null;
        _virtualRenderDeviceId = null;
        _activePresetConfig = null;
        _issues = [];
    }

    private void BindApplication(uint processId, string path, string displayName, string startTimeTicks)
    {
        if (_session!.Status().Switches.AppSendEnabled) throw HostCommandException.Rejected("app-send-active");
        if (!long.TryParse(startTimeTicks, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var expectedStartTime) || expectedStartTime <= 0)
            throw HostCommandException.Invalid("startTimeTicks must be a positive decimal process creation time string");
        var selected = ReadLiveApplication(processId);
        if (selected.StartTimeTicks != expectedStartTime
            || !string.Equals(Path.GetFullPath(path), selected.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            throw HostCommandException.Rejected("application process identity changed; refresh and select the live instance again");
        WindowsAppCatalog.AppInstance actual;
        try { actual = WindowsAppCatalog.ResolveCaptureRoot(selected); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
        {
            throw HostCommandException.Rejected($"selected application root is unavailable: {ex.Message}");
        }
        RequireAccepted(_session.BindApplication(new AppBinding(actual.ProcessId, displayName),
            new AppSelection(actual.ExecutablePath, displayName)));
        _boundApplication = actual;
        ClearIssue(PresetRoute.Application);
    }

    private static WindowsAppCatalog.AppInstance ReadLiveApplication(uint processId)
    {
        try { return WindowsAppCatalog.ReadLive(processId); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception or OverflowException or NotSupportedException)
        {
            throw HostCommandException.Rejected($"selected application is unavailable: {ex.Message}");
        }
    }

    private object ReadApplications() => WindowsAppCatalog.List(_lifetime.Token)
        .Select(app => new AppInstanceData(app.ProcessId, app.ExecutablePath, app.DisplayName,
            app.Category, app.StartTimeTicks.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray();

    private Task<object?> QueueDeviceWork(Func<object?> operation)
    {
        var previous = _deviceTail;
        var pending = Task.Run(async () =>
        {
            try { await previous.ConfigureAwait(false); }
            catch (Exception) { /* Each scan/enable reports its own failure. */ }
            _lifetime.Token.ThrowIfCancellationRequested();
            lock (_deviceGate) return operation();
        });
        _deviceTail = pending;
        return pending;
    }

    private AudioDeviceData[] ReadDevices()
    {
        var devices = WindowsAudioDevices.List(activeOnly: false);
        var defaultRender = WindowsAudioDevices.DefaultDeviceId(capture: false);
        var defaultCapture = WindowsAudioDevices.DefaultDeviceId(capture: true);
        return devices.Select(device => new AudioDeviceData(device.Id, device.FriendlyName, device.DataFlow, device.State,
            device.Id == (device.DataFlow == "Capture" ? defaultCapture : defaultRender))).ToArray();
    }

    private AudioDeviceData[] EnableDevice(string id)
    {
        var selected = WindowsAudioDevices.List(activeOnly: false).FirstOrDefault(device => device.Id == id)
            ?? throw HostCommandException.Rejected("endpoint-missing");
        if (selected.State != "Disabled") throw HostCommandException.Rejected($"endpoint is not disabled: {selected.State}");
        WindowsEndpointControl.SetEnabled(id, true);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            _lifetime.Token.ThrowIfCancellationRequested();
            var refreshed = ReadDevices();
            if (refreshed.Any(device => device.Id == id && device.State == "Active")) return refreshed;
            if (_lifetime.Token.WaitHandle.WaitOne(150)) _lifetime.Token.ThrowIfCancellationRequested();
        }
        throw HostCommandException.Rejected("Windows did not confirm the selected endpoint became active");
    }

    private IReadOnlyList<RouteIssue> ValidateDevices(PresetDeviceConfig config)
    {
        IReadOnlyList<AudioEndpointInfo> endpoints;
        lock (_deviceGate) endpoints = WindowsAudioDevices.List(activeOnly: false);
        var issues = new List<RouteIssue>();
        Check(config.MicDeviceId, "Capture", PresetRoute.Microphone);
        Check(config.VirtualRenderDeviceId, "Render", PresetRoute.VirtualOutput);
        if (config.VirtualCaptureDeviceId is { Length: > 0 } capture) Check(capture, "Capture", PresetRoute.VirtualCapture);
        Check(config.MonitorDeviceId, "Render", PresetRoute.Monitor);
        if (string.Equals(config.MonitorDeviceId, config.VirtualRenderDeviceId, StringComparison.Ordinal))
            issues.Add(new RouteIssue(PresetRoute.Monitor, "monitor-output-is-virtual-output"));
        if (config.Application is { IsBound: true, ExecutablePath: { } path } && !File.Exists(path))
            issues.Add(new RouteIssue(PresetRoute.Application, "executable-missing"));
        return issues;

        void Check(string id, string flow, PresetRoute route)
        {
            var endpoint = endpoints.FirstOrDefault(device => device.Id == id);
            var reason = endpoint switch
            {
                null => "endpoint-missing",
                { State: "Disabled" } => "endpoint-disabled",
                { State: "Unplugged" } => "endpoint-unplugged",
                { State: "NotPresent" } => "endpoint-not-present",
                { State: not "Active" } => "endpoint-inactive",
                { DataFlow: var actualFlow } when actualFlow != flow => "endpoint-wrong-data-flow",
                _ => null,
            };
            if (reason is not null) issues.Add(new RouteIssue(route, reason));
        }
    }

    private void ReapplyHotkeys()
    {
        if (_dispatcher is null || _settings is null || Volatile.Read(ref _stopping) != 0) return;
        var current = HotkeyBindings.Apply(_settings, _dispatcher);
        if (!_hotkeyData.Hotkeys.SequenceEqual(current.Hotkeys) || !_hotkeyData.HotkeyIssues.SequenceEqual(current.HotkeyIssues)
            || !_hotkeyData.HotkeyActions.SequenceEqual(current.HotkeyActions))
        {
            _hotkeyData = current;
            _writer.Event("hotkeys", current);
        }
    }

    private void EnqueueHotkey(QueuedHotkeyTrigger queued)
    {
        var trigger = queued.Trigger;
        if (Volatile.Read(ref _stopping) != 0) return;
        try
        {
            _controls.Post("hotkey", () =>
            {
                if (_hotkeys is null || queued.Generation != _hotkeys.Generation) return;
                if (!_initialized || Volatile.Read(ref _stopping) != 0 || _dispatcher is null) return;
                var action = trigger.Binding.Action;
                var status = _session!.Status();
                if (!_dispatcher.Suspended)
                {
                    var needsOutput = action == HotkeyAction.PlayClip
                        || (action == HotkeyAction.ToggleMicSend && !status.Switches.MicSendEnabled)
                        || (action == HotkeyAction.ToggleExtrasSend && !status.Switches.ExtrasSendEnabled);
                    if (needsOutput)
                    {
                        try { RequireOutputReady(status); }
                        catch (HostCommandException ex)
                        {
                            Notice("Info", $"hotkey {trigger.Binding.Describe()} rejected: {ex.Message}");
                            PublishState();
                            return;
                        }
                    }
                }
                var previous = _dispatcher.LastPlayback;
                _hotkeys!.Deliver(trigger);
                if (!ReferenceEquals(previous, _dispatcher.LastPlayback))
                {
                    var intent = ++_playIntent;
                    _pendingClipId = trigger.Binding.TargetId;
                    _pendingClipIsSend = true;
                    Track(ObserveHotkeyPlaybackAsync(_dispatcher.LastPlayback, intent));
                }
                else if (action == HotkeyAction.EmergencyMute || (action == HotkeyAction.StopClip && !_dispatcher.Suspended)
                    || (action == HotkeyAction.ToggleExtrasSend && !_dispatcher.Suspended && status.Switches.ExtrasSendEnabled
                        && _pendingClipId is not null && _pendingClipIsSend))
                {
                    _pendingClipId = null;
                    _playIntent++;
                }
                RefreshAvailability();
                PublishState();
            });
        }
        catch (ObjectDisposedException) { }
    }

    private async Task ObserveHotkeyPlaybackAsync(Task playback, long intent)
    {
        try { await playback.ConfigureAwait(false); }
        catch (Exception ex) { Notice("Info", $"hotkey playback failed: {ex.Message}"); }
        await OnControlsAsync(() =>
        {
            if (_playIntent == intent) _pendingClipId = null;
            PublishState();
            return true;
        }, allowStopping: true).ConfigureAwait(false);
    }

    private void OnNotice(SessionNotice notice)
    {
        if (Volatile.Read(ref _stopping) != 0) return;
        try
        {
            _controls.Post("session-notice", () =>
            {
                if (Volatile.Read(ref _stopping) != 0) return;
                if (notice.Kind == SessionNoticeKind.OutputFaulted) SetIssue(PresetRoute.VirtualOutput, notice.Message);
                else if (notice.Kind == SessionNoticeKind.MonitorFaulted) SetIssue(PresetRoute.Monitor, notice.Message);
                else if (notice.Kind == SessionNoticeKind.SourceFaulted)
                    SetIssue(notice.Message.StartsWith("mic", StringComparison.Ordinal) ? PresetRoute.Microphone : PresetRoute.Application, notice.Message);
                Notice(notice.Kind.ToString(), notice.Message);
                RefreshAvailability();
                PublishState();
            });
        }
        catch (ObjectDisposedException) { }
    }

    private void Notice(string kind, string message) => _writer.Event("notice", new { kind, message });

    private void UpdateSelectionIssues()
    {
        var routing = _issues.Where(issue => issue.Route != "Library").ToList();
        if (_activePresetConfig is { } config)
        {
            var view = _libraryView;
            var validation = PresetConfigRules.Validate(config, id => view?.Entries.ContainsKey(id) == true);
            routing.AddRange(validation.Issues.Select(reason => new RuntimeIssueData("Library", reason)));
            if (!string.IsNullOrWhiteSpace(config.LibraryGroupId)
                && (view is null || !view.Data.Groups.Any(group => group.GroupId == config.LibraryGroupId)))
                routing.Add(new RuntimeIssueData("Library", $"libraryGroupUnavailable:{config.LibraryGroupId}"));
        }
        _issues = routing.ToArray();
    }

    private void SetIssue(PresetRoute route, string reason)
    {
        _issues = [.. _issues.Where(issue => issue.Route != route.ToString()), new RuntimeIssueData(route.ToString(), reason)];
    }

    private void ClearIssue(PresetRoute route) => _issues = _issues.Where(issue => issue.Route != route.ToString()).ToArray();

    private void PublishState()
    {
        if (_session is null) return;
        _lastState = new RuntimeStateData(++_revision, SessionStatusData.FromDomain(_session.Status()),
            _session.TakeLevels(), new RuntimeMetricsData(_session.Metrics(), _samples.Stats()), _issues);
        _writer.Event("state", _lastState);
    }

    private Task<object?> SnapshotReadyAsync()
    {
        var slot = _librarySlot!;
        if (slot.Ready.Task.IsCompletedSuccessfully)
        {
            RefreshAvailability();
            return Task.FromResult<object?>(null);
        }
        return AwaitSnapshotLibraryAsync(slot);
    }

    private async Task<object?> AwaitSnapshotLibraryAsync(LibrarySlot slot)
    {
        await slot.Ready.Task.ConfigureAwait(false);
        return await OnControlsAsync<object?>(() =>
        {
            RefreshAvailability();
            return null;
        }).ConfigureAwait(false);
    }

    private EngineSnapshotData Snapshot()
    {
        var state = _lastState ?? throw new InvalidOperationException("no runtime state has been published");
        var library = _libraryView?.Data ?? throw HostCommandException.Rejected("library-change-in-progress");
        return new EngineSnapshotData(state.Revision, state.Status, state.Levels, state.Metrics, state.Issues,
            library.Clips, library.Groups, _hotkeyData.Hotkeys, _hotkeyData.HotkeyIssues, _hotkeyData.HotkeyActions,
            library.LibraryError, library.LibraryRoot);
    }

    private static void RequireAccepted(CommandResult result)
    {
        if (!result.Accepted) throw HostCommandException.Rejected(result.Rejection ?? "command-rejected");
    }

    private Task<T> OnControlsAsync<T>(Func<T> operation, bool allowStopping = false)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _controls.Post("worker-completion", () =>
            {
                try
                {
                    if (!allowStopping && Volatile.Read(ref _stopping) != 0)
                        throw HostCommandException.Rejected("engine-stopping");
                    completion.TrySetResult(operation());
                }
                catch (Exception ex) { completion.TrySetException(ex); }
            });
        }
        catch (Exception ex) { completion.TrySetException(ex); }
        return completion.Task;
    }

    private void Track(Task task)
    {
        lock (_workerGate) _workers.Add(task);
        _ = task.ContinueWith(completed =>
        {
            _ = completed.Exception;
            lock (_workerGate) _workers.Remove(completed);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void StopAudioPaths()
    {
        _pendingClipId = null;
        _playIntent++;
        _dispatcher?.Suspend();
        if (_session is not null)
        {
            try { RequireAccepted(_session.EmergencyMute()); }
            finally { RequireAccepted(_session.ClearConfiguration()); }
        }
        _dispatcher?.Dispose();
        _hotkeys?.Dispose();
        _boundApplication = null;
        _virtualRenderDeviceId = null;
        _activePresetConfig = null;
        _issues = [];
    }

    public Task StopAsync()
    {
        lock (_shutdownGate) return _shutdown ??= ShutdownAsync();
    }

    private async Task ShutdownAsync()
    {
        Volatile.Write(ref _stopping, 1);
        _lifetime.Cancel();
        _heartbeat?.Dispose();
        var silenced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _controls.Post("shutdown", () =>
        {
            try
            {
                foreach (var slot in _libraries) slot.Ready.TrySetCanceled(_lifetime.Token);
                StopAudioPaths();
                PublishState();
                silenced.TrySetResult();
            }
            catch (Exception ex) { silenced.TrySetException(ex); }
        }, urgent: true);
        Exception? silenceFailure = null;
        try { await silenced.Task.ConfigureAwait(false); }
        catch (Exception ex) { silenceFailure = ex; }
        while (true)
        {
            Task[] workers;
            lock (_workerGate) workers = _workers.ToArray();
            if (workers.Length == 0) break;
            try { await Task.WhenAll(workers).ConfigureAwait(false); }
            catch (Exception) { /* Request/preload completion owns its error; shutdown still joins every worker. */ }
        }
        // Drain queued completions before closing the host control lane, then close the session's command lane.
        _controls.SubmitValue("shutdown-barrier", () => true);
        _session?.Dispose();
        foreach (var slot in _libraries)
        {
            if (slot.Library is not { } library) continue;
            if (slot.Changed is not null) library.Changed -= slot.Changed;
            library.Dispose();
        }
        _decoder.Dispose();
        _controls.Dispose();
        _lifetime.Dispose();
        if (silenceFailure is not null) throw silenceFailure;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
