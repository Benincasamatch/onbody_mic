namespace OnbodyMic.Application;

/// <summary>
/// Composes the safety-critical audio graph: capture sources -> gated send
/// mixer -> virtual output, plus the local monitor router. Every mutation of
/// engine state runs on the serialized command thread; the output pump reads
/// an immutable runtime snapshot, so route/gain/mute changes are atomic for
/// consumers (docs/03 P1 task 3).
/// </summary>
public sealed class AudioSession : IDisposable
{
    private readonly AudioSessionOptions _options;
    private readonly SessionCommandQueue _commands;
    private readonly SourceChannel _micChannel;
    private readonly SourceChannel _appChannel;
    private readonly ClipPlayer _clipPlayer;
    private readonly ClipFeed _clipFeed;
    private readonly SendMixer _mixer;
    private readonly MonitorRouter _monitorRouter;
    private readonly IClipDecoder _decoder;
    private readonly SemaphoreSlim _decodeSlot = new(1, 1);

    private IVirtualAudioOutput? _output;
    private IAudioSourceAdapter? _micAdapter;
    private IAudioSourceAdapter? _appAdapter;
    private IMonitorOutput? _monitorOutput;
    private IMonitorCaptureAdapter? _monitorCapture;
    private AppBinding? _appBinding;
    private RouteGains _gains;
    private PresetDeviceConfig? _devices;
    private volatile SessionRuntimeState _runtime;
    private volatile bool _disposed;
    private long _playRequestId;
    private bool _pendingClipSend;
    private bool _clipLoading;

    /// <summary>Notices raised by faults and completions; never contains paths or device ids.</summary>
    public event Action<SessionNotice>? Noticed;

    public AudioSession(AudioSessionOptions options)
    {
        _options = options;
        _gains = options.InitialGains;
        _devices = options.InitialDevices;
        _decoder = options.ClipDecoder;
        _commands = new SessionCommandQueue();
        _micChannel = new SourceChannel("mic", AudioKind.Microphone, options.MicQueueFrames);
        _appChannel = new SourceChannel("app", AudioKind.Application, options.AppQueueFrames);
        if (options.AdaptiveRate)
        {
            _micChannel.EnableAdaptiveRate(options.MicAdaptiveTargetFrames);
            _appChannel.EnableAdaptiveRate(options.AppAdaptiveTargetFrames);
        }
        _runtime = BuildRuntime();
        _clipPlayer = new ClipPlayer(options.ClipBoundaryFadeFrames);
        _clipFeed = new ClipFeed(_clipPlayer, options.MonitorRingFrames, options.MonitorHighWaterFrames);
        _clipPlayer.PlaybackCompleted += clip =>
            _commands.Post("clip-completed", () => HandleClipCompleted(clip));

        _mixer = new SendMixer(options.MaxPeriodFrames)
        {
            Gate = Engine.Gate,
            SnapshotProvider = () => _runtime.GateSnapshot,
            GainsProvider = () => _runtime.Gains,
        };
        _mixer.Add(_micChannel);
        _mixer.Add(_appChannel);
        _mixer.Add(_clipFeed);

        _monitorRouter = new MonitorRouter(_clipFeed, options.MonitorRingFrames, options.MonitorHighWaterFrames)
        {
            StateProvider = () => _runtime.Monitor,
            GainsProvider = () => _runtime.Gains,
        };


    }

    private sealed record SessionRuntimeState(
        SendGateSnapshot GateSnapshot,
        RouteGains Gains,
        MonitorRouteState Monitor);

    public SessionEngine Engine { get; } = new();

    // ---- public command surface (serialized; safe from any thread) ----

    /// <summary>Attaches (or re-attaches after a fault) the virtual output for the configured render endpoint.</summary>
    public CommandResult AttachOutput() => _commands.Submit("attach-output", () =>
    {
        if (_devices is null)
        {
            return CommandResult.Reject("no-device-config");
        }

        _output = SafeDispose(_output);
        try
        {
            StartOutput();
            _ = Engine.ValidateDevices();
            PublishState();
            return CommandResult.Ok;
        }
        catch (Exception ex)
        {
            _output = SafeDispose(_output);
            PublishState();
            Notice(SessionNoticeKind.OutputFaulted, $"output start failed: {ex.Message}");
            return CommandResult.Reject($"output-start-failed: {ex.Message}");
        }
    });

    /// <summary>
    /// Applies a preset atomically (docs/06 §5.3.5): stop clips, stop capture
    /// and monitor paths, abort the session epoch, validate, then commit the
    /// new device config, gains and monitor preference and re-attach the
    /// output. Sending and monitoring stay off; enable remains explicit.
    /// </summary>
    public PresetApplyResult ApplyPreset(PresetConfig preset) => _commands.SubmitValue("apply-preset", () =>
    {
        if (!PresetConfigRules.IsValidId(preset.PresetId))
        {
            return PresetApplyResult.Reject("invalid-preset-id");
        }

        var gainsValid = PresetConfigRules.IsValidGain(preset.MicGain, PresetConfig.GainLimits)
            && PresetConfigRules.IsValidGain(preset.AppGain, PresetConfig.GainLimits)
            && PresetConfigRules.IsValidGain(preset.ClipGain, PresetConfig.GainLimits)
            && PresetConfigRules.IsValidGain(preset.Monitor.Gain, PresetConfig.GainLimits);
        if (!gainsValid)
        {
            // structural garbage never reaches the runtime; the attempt still
            // stops everything, the old selection stays (B06)
            AbortForFailedSwitch();
            return PresetApplyResult.Reject("invalid-gain");
        }

        TeardownAudioPaths();
        Engine.AbortSession();
        _appBinding = null;
        PublishState();

        var issues = new List<RouteIssue>();
        if (_options.DeviceValidator is { } validate)
        {
            try { issues.AddRange(validate(preset.Devices)); }
            catch (Exception ex)
            {
                return PresetApplyResult.Reject($"device-validation-failed: {ex.Message}");
            }
        }

        // commit: config swap, gains, disabled monitor preference, preset id
        _devices = preset.Devices;
        _gains = new RouteGains(preset.MicGain, preset.AppGain, preset.ClipGain, preset.Monitor.Gain);
        Engine.ConfigureMonitorPreference(preset.Monitor.Mode);
        _ = Engine.SwitchPreset(preset.PresetId);

        // fresh output for the (possibly different) render endpoint; a failed
        // attach leaves sending closed with a visible issue, not a rollback
        string? attachRejection = null;
        _output = SafeDispose(_output);
        try
        {
            StartOutput();
            _ = Engine.ValidateDevices();
        }
        catch (Exception ex)
        {
            _output = SafeDispose(_output);
            attachRejection = ex.Message;
            issues.Add(new RouteIssue(PresetRoute.VirtualOutput, "attach-failed"));
            Notice(SessionNoticeKind.OutputFaulted, $"output attach failed after preset switch: {ex.Message}");
        }

        PublishState();
        return new PresetApplyResult(true, null, issues, attachRejection);
    });

    /// <summary>Stops every audio path and aborts the session without changing the selected preset (B06 failure path, T38 delete-active).</summary>
    public CommandResult TeardownForAbort() => _commands.Submit("abort-session", () =>
    {
        AbortForFailedSwitch();
        return CommandResult.Ok;
    });

    /// <summary>Deletes the active configuration, including its output, live binding and every local/send path.</summary>
    public CommandResult ClearConfiguration() => _commands.Submit("clear-configuration", () =>
    {
        TeardownAudioPaths();
        var result = Engine.ClearConfiguration();
        _appBinding = null;
        _devices = null;
        _gains = RouteGains.Unity;
        _output = SafeDispose(_output);
        PublishState();
        return result;
    });

    public CommandResult EnableMic(bool enabled) => _commands.Submit($"mic-{(enabled ? "on" : "off")}", () => DoEnableMic(enabled));

    public CommandResult EnableExtras(bool enabled) => _commands.Submit($"extras-{(enabled ? "on" : "off")}", () => DoEnableExtras(enabled));

    public CommandResult EnableApp(bool enabled) => _commands.Submit($"app-{(enabled ? "on" : "off")}", () => DoEnableApp(enabled));

    /// <summary>Sets the target application binding; refuses while the app route is sending (T08: re-binding is explicit).</summary>
    public CommandResult BindApplication(AppBinding binding, AppSelection? selection = null) => _commands.Submit("bind-app", () =>
    {
        if (Engine.Switches.AppSendEnabled)
        {
            return CommandResult.Reject("app-send-active");
        }

        StopAppCapture();
        _appBinding = binding;
        if (selection is not null && _devices is not null)
        {
            _devices = _devices with { Application = selection };
        }
        PublishState();
        return CommandResult.Ok;
    });

    public CommandResult PlayClip(string path) => PlayClipAsync(path).GetAwaiter().GetResult();

    public Task<CommandResult> PlayClipAsync(string path) => StartClipAsync(path, send: true);

    public CommandResult PreviewClip(string path) => PreviewClipAsync(path).GetAwaiter().GetResult();

    public Task<CommandResult> PreviewClipAsync(string path) => StartClipAsync(path, send: false);

    public CommandResult StopClip() => _commands.Submit("stop-clip", () =>
    {
        var cancelledLoading = _clipLoading;
        _clipLoading = false;
        _playRequestId++;
        var result = Engine.StopClip();
        _clipPlayer.Discard();
        _clipFeed.SetTicket(null);
        PublishState();
        ReleaseIdleMonitor();
        return cancelledLoading ? CommandResult.Ok : result;
    });

    public CommandResult SetMonitor(bool enabled, MonitorMode mode) => _commands.Submit("set-monitor", () => DoSetMonitor(enabled, mode));

    public CommandResult EmergencyMute() => _commands.Submit("emergency-mute", () =>
    {
        _clipLoading = false;
        _playRequestId++;
        var result = Engine.EmergencyMute();
        _clipPlayer.Discard();
        _clipFeed.SetTicket(null);
        StopMonitorDevices();
        _monitorRouter.Clear();
        PublishState();
        return result;
    }, urgent: true);

    public CommandResult UnlockEmergency() => _commands.Submit("unlock-emergency", () =>
    {
        var result = Engine.UnlockEmergency();
        PublishState();
        return result;
    }, urgent: true);

    /// <summary>Runtime gain change; affects subsequent frames only, never touches safety state.</summary>
    public CommandResult SetGains(RouteGains gains) => _commands.Submit("set-gains", () =>
    {
        _gains = new RouteGains(
            ClampGain(gains.Mic), ClampGain(gains.App), ClampGain(gains.Clip), ClampGain(gains.Monitor));
        PublishState();
        return CommandResult.Ok;
    });

    public SessionStatus Status() => _commands.SubmitValue("status", () => new SessionStatus(
        Engine.Phase,
        Engine.Switches,
        Engine.EmergencyLatched,
        Engine.OutputLost,
        Engine.DevicesValidated,
        Engine.LastPresetId,
        _appBinding,
        Engine.Clip,
        Engine.MonitorSuspendedForPreview,
        Engine.MonitorMode,
        _gains,
        _devices,
        _clipLoading));

    /// <summary>Desensitized counters; components are safe to read from any thread.</summary>
    public SessionMetrics Metrics() => new(
        _micChannel.Stats(),
        _appChannel.Stats(),
        _clipPlayer.Stats(),
        _monitorRouter.Stats(),
        _mixer.FramesMixed,
        _mixer.ClippedSamples,
        _mixer.UnderrunFrames,
        _mixer.DeniedPulls,
        _mixer.HardSteps,
        _mixer.Peak,
        _micChannel.Ratio,
        _appChannel.Ratio);

    /// <summary>Consumes recent peaks for the UI; lifetime diagnostics remain unchanged.</summary>
    public SessionLevels TakeLevels() => new(
        _mixer.MicLevel.Take(), _mixer.AppLevel.Take(),
        Math.Max(_mixer.ClipLevel.Take(), _monitorRouter.PreviewLevel.Take()), _mixer.MixLevel.Take());

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _commands.Dispose();
        _clipPlayer.Discard();
        _micAdapter = SafeDispose(_micAdapter);
        _appAdapter = SafeDispose(_appAdapter);
        _monitorOutput = SafeDispose(_monitorOutput);
        _monitorCapture = SafeDispose(_monitorCapture);
        _output = SafeDispose(_output);
    }

    // ---- command handlers (session thread only) ----

    private CommandResult DoEnableMic(bool enabled)
    {
        if (!enabled)
        {
            var result = Engine.SetMicSend(false);
            PublishState();
            return result;
        }

        if (Engine.EmergencyLatched)
        {
            return CommandResult.Reject("emergency-latched");
        }

        if (Engine.OutputLost)
        {
            return CommandResult.Reject("output-lost");
        }

        if (_devices is null)
        {
            return CommandResult.Reject("no-device-config");
        }
        var authorized = Engine.AuthorizeSource("mic", AudioKind.Microphone);
        if (!authorized.Accepted)
        {
            return authorized;
        }

        // ticket first so capture frames buffer from the very first callback;
        // the send switch only flips after a short pre-roll, so the mixer
        // never starts a live source from an empty queue (startup pop)
        _micChannel.SetTicket(Engine.CreateTicket("mic", AudioKind.Microphone));
        if (_micAdapter is null)
        {
            try
            {
                var adapter = _options.MicAdapterFactory(_devices!.MicDeviceId, _micChannel);
                _micAdapter = adapter;
                adapter.Ended += reason => _commands.Post("mic-ended", () =>
                {
                    if (ReferenceEquals(_micAdapter, adapter)) HandleSourceEnded("mic", reason);
                }, urgent: true);
                _micAdapter.Start();
            }
            catch (Exception ex)
            {
                _micAdapter = SafeDispose(_micAdapter);
                _micChannel.SetTicket(null);
                _ = Engine.SourceFailed("mic");
                PublishState();
                Notice(SessionNoticeKind.SourceFaulted, $"mic start failed: {ex.Message}");
                return CommandResult.Reject($"mic-start-failed: {ex.Message}");
            }
        }

        WaitForPreroll(_micChannel);
        var setResult = Engine.SetMicSend(true);
        PublishState();
        return setResult;
    }

    private CommandResult DoEnableExtras(bool enabled)
    {
        var result = Engine.SetExtrasSend(enabled);
        if (result.Accepted && !enabled)
        {
            if (_pendingClipSend) { _playRequestId++; _clipLoading = false; }
            // docs/06 §1.1: closing extras stops app capture, clears the app and
            // clip send queues and stops the outbound clip; mic and pure-local
            // preview are untouched.
            StopAppCapture();
            if (_clipPlayer.Active is { Mode: ClipPlaybackMode.Send })
            {
                _clipPlayer.Discard();
            }

            _clipFeed.SetTicket(null);
        }

        PublishState();
        return result;
    }

    private CommandResult DoEnableApp(bool enabled)
    {
        if (!enabled)
        {
            var result = Engine.SetAppSend(false);
            StopAppCapture();
            PublishState();
            return result;
        }

        // mirror the engine's enable rules without flipping the switch early;
        // the switch only flips after pre-roll so the app feed starts full
        if (Engine.EmergencyLatched)
        {
            return CommandResult.Reject("emergency-latched");
        }

        if (!Engine.Switches.ExtrasSendEnabled)
        {
            return CommandResult.Reject("extras-disabled");
        }

        if (_appBinding is null)
        {
            return CommandResult.Reject("no-app-bound");
        }

        var authorized = Engine.AuthorizeSource("app", AudioKind.Application);
        if (!authorized.Accepted)
        {
            return authorized;
        }

        _appChannel.SetTicket(Engine.CreateTicket("app", AudioKind.Application));
        if (_appAdapter is null)
        {
            try
            {
                var adapter = _options.AppAdapterFactory(_appChannel, _appBinding.Value);
                _appAdapter = adapter;
                adapter.Ended += reason => _commands.Post("app-ended", () =>
                {
                    if (ReferenceEquals(_appAdapter, adapter)) HandleSourceEnded("app", reason);
                }, urgent: true);
                _appAdapter.Start();
            }
            catch (Exception ex)
            {
                _appAdapter = SafeDispose(_appAdapter);
                _appChannel.SetTicket(null);
                _ = Engine.SourceFailed("app");
                PublishState();
                Notice(SessionNoticeKind.SourceFaulted, $"app capture start failed: {ex.Message}");
                return CommandResult.Reject($"app-start-failed: {ex.Message}");
            }
        }

        WaitForPreroll(_appChannel);
        var setResult = Engine.SetAppSend(true);
        PublishState();
        return setResult;
    }

    private CommandResult CheckClipStart(bool send)
    {
        // mirror the engine's preconditions before any file I/O: a rejected
        // trigger must not pay for a decode (docs/01 §5.4 — reject, never queue)
        if (send && (Engine.EmergencyLatched || !Engine.Switches.ExtrasSendEnabled))
        {
            return CommandResult.Reject("extras-disabled-or-emergency");
        }

        if (!send && Engine.EmergencyLatched)
        {
            return CommandResult.Reject("emergency-latched");
        }

        if (send && Engine.OutputLost) return CommandResult.Reject("output-lost");
        return CommandResult.Ok;
    }

    private Task<CommandResult> StartClipAsync(string clipId, bool send)
    {
        long requestId = 0;
        var prepared = _commands.Submit("prepare-clip", () =>
        {
            var check = CheckClipStart(send);
            if (!check.Accepted) return check;
            requestId = ++_playRequestId;
            _pendingClipSend = send;
            _clipLoading = true;
            return CommandResult.Ok;
        });
        if (!prepared.Accepted) return Task.FromResult(prepared);

        return Task.Run(async () =>
        {
            await _decodeSlot.WaitAsync().ConfigureAwait(false);
            try
            {
                // Bound decoder memory use and skip superseded requests before I/O.
                var current = _commands.Submit("check-clip-request", () => requestId == _playRequestId
                    ? CommandResult.Ok : CommandResult.Reject("clip-start-cancelled"));
                return current.Accepted ? DecodeAndStartClip(clipId, send, requestId) : current;
            }
            finally
            {
                _decodeSlot.Release();
                _commands.Submit("clip-load-finished", () =>
                {
                    if (requestId == _playRequestId) _clipLoading = false;
                    return CommandResult.Ok;
                });
            }
        });
    }

    private CommandResult DecodeAndStartClip(string clipId, bool send, long requestId)
    {
        // Decode on the caller's worker, never the session command thread.
        // Mute, stop, status and preset changes remain available during I/O.
        ClipResolution resolution;
        try { resolution = _options.ClipResolver?.Invoke(clipId) ?? ClipResolution.Unknown; }
        catch (Exception ex) { return CommandResult.Reject($"decode-failed: {ex.Message}"); }
        if (resolution.Known && !resolution.Available)
        {
            return CommandResult.Reject($"clip-unavailable:{clipId}");
        }

        float[] samples;
        if (_options.SampleStore is { } store && store.TryGet(clipId, out var cached))
        {
            samples = cached;
        }
        else
        {
            DecodedClip decoded;
            try
            {
                decoded = _decoder.Decode(resolution.Path ?? clipId);
            }
            catch (Exception ex)
            {
                return CommandResult.Reject($"decode-failed: {ex.Message}");
            }

            samples = decoded.Samples;
            if (resolution.Known)
            {
                // only stable library ids enter the cache; ad-hoc raw paths
                // must not evict managed clips
                _options.SampleStore?.Store(clipId, samples);
            }
        }

        return _commands.Submit("start-decoded-clip", () =>
        {
            if (requestId != _playRequestId) return CommandResult.Reject("clip-start-cancelled");
            var check = CheckClipStart(send);
            if (!check.Accepted) return check;
            return StartDecodedClip(clipId, send, samples, resolution.Gain);
        });
    }

    private CommandResult StartDecodedClip(string clipId, bool send, float[] samples, float gain)
    {
        if (!send)
        {
            if (string.IsNullOrWhiteSpace(_devices?.MonitorDeviceId))
                return CommandResult.Reject("monitor-device-unconfigured");

            try { EnsureMonitorOutput(); }
            catch (Exception ex)
            {
                _monitorOutput = SafeDispose(_monitorOutput);
                return CommandResult.Reject($"monitor-start-failed: {ex.Message}");
            }
        }

        var result = send ? Engine.PlayClip(clipId) : Engine.PreviewClip(clipId);
        if (!result.Accepted)
        {
            return result;
        }

        var active = Engine.Clip!;
        _clipPlayer.Start(active.ClipId, samples, active.PlaybackEpoch, active.Mode, gain);
        // preview deliberately carries no send ticket: the mixer denies and
        // drains it, so auditions can never reach the virtual output
        _clipFeed.SetTicket(active.Mode == ClipPlaybackMode.Send
            ? new SourceTicket("clip", AudioKind.Clip, Engine.SessionEpoch, active.PlaybackEpoch)
            : null);
        PublishState();
        ReleaseIdleMonitor();
        return CommandResult.Ok;
    }

    private CommandResult DoSetMonitor(bool enabled, MonitorMode mode)
    {
        if (enabled && mode == MonitorMode.FullEndpoint
            && (_options.MonitorCaptureFactory is null || _devices?.VirtualCaptureDeviceId is null))
        {
            return CommandResult.Reject("full-endpoint-unavailable");
        }

        if (enabled && _devices is null)
        {
            return CommandResult.Reject("no-device-config");
        }

        var result = Engine.SetMonitor(enabled, mode);
        if (!result.Accepted)
        {
            return result;
        }

        try
        {
            if (enabled)
            {
                if (mode == MonitorMode.FullEndpoint && _monitorCapture is null)
                {
                    var capture = _options.MonitorCaptureFactory!(_devices!.VirtualCaptureDeviceId!, _monitorRouter);
                    _monitorCapture = capture;
                    capture.Faulted += ex => _commands.Post("monitor-faulted", () =>
                    {
                        if (ReferenceEquals(_monitorCapture, capture)) HandleMonitorFaulted(ex);
                    }, urgent: true);
                    _monitorCapture.Start(_monitorRouter.PushFullEndpoint);
                }
                else if (mode != MonitorMode.FullEndpoint)
                {
                    _monitorCapture = SafeDispose(_monitorCapture);
                }

                EnsureMonitorOutput();
            }
            else
            {
                _monitorCapture = SafeDispose(_monitorCapture);
                ReleaseIdleMonitor();
            }
        }
        catch (Exception ex)
        {
            StopMonitorDevices();
            _ = Engine.SetMonitor(false, mode);
            PublishState();
            Notice(SessionNoticeKind.MonitorFaulted, $"monitor start failed: {ex.Message}");
            return CommandResult.Reject($"monitor-start-failed: {ex.Message}");
        }

        _monitorRouter.Clear();
        PublishState();
        return CommandResult.Ok;
    }

    // ---- fault and completion handlers ----

    private void HandleSourceEnded(string sourceId, string? reason)
    {
        if (_disposed)
        {
            return;
        }

        _ = Engine.SourceFailed(sourceId);
        if (sourceId == "mic")
        {
            _ = Engine.SetMicSend(false);
            _micChannel.SetTicket(null);
            _micAdapter = SafeDispose(_micAdapter);
        }
        else
        {
            _ = Engine.SetAppSend(false);
            _appChannel.SetTicket(null);
            _appAdapter = SafeDispose(_appAdapter);
        }

        PublishState();
        Notice(reason is null ? SessionNoticeKind.SourceEnded : SessionNoticeKind.SourceFaulted,
            reason is null ? $"{sourceId} ended" : $"{sourceId} faulted: {reason}");
    }

    private void HandleOutputFaulted(Exception exception)
    {
        if (_disposed)
        {
            return;
        }

        _ = Engine.OutputFailed();
        if (_pendingClipSend) { _playRequestId++; _clipLoading = false; }
        if (_clipPlayer.Active is { Mode: ClipPlaybackMode.Send }) _clipPlayer.Discard();
        _clipFeed.SetTicket(null);
        _output = SafeDispose(_output);
        PublishState();
        Notice(SessionNoticeKind.OutputFaulted, $"virtual output faulted: {exception.Message}");
    }

    private void HandleMonitorFaulted(Exception exception)
    {
        if (_disposed)
        {
            return;
        }

        _ = Engine.SetMonitor(false, Engine.MonitorMode);
        if (Engine.Clip is { Mode: ClipPlaybackMode.Preview })
        {
            Engine.StopClip();
            _clipPlayer.Discard();
        }
        StopMonitorDevices();
        PublishState();
        Notice(SessionNoticeKind.MonitorFaulted, $"monitor faulted: {exception.Message}");
    }

    private void HandleClipCompleted(ActiveClip clip)
    {
        if (_disposed || Engine.Clip != clip)
        {
            return;
        }

        var result = Engine.CompleteClip();
        if (clip.Mode == ClipPlaybackMode.Send)
        {
            _clipFeed.SetTicket(null);
        }

        if (result.Accepted)
        {
            Notice(SessionNoticeKind.ClipCompleted, "clip finished");
        }

        PublishState();
        ReleaseIdleMonitor();
    }

    // ---- helpers ----

    private int MixPull(Span<float> destination)
    {
        _mixer.Read(destination);
        return destination.Length;
    }

    /// <summary>
    /// Blocks (bounded) until the source produced its first frames. The queue
    /// itself cannot accumulate pre-switch: the pump keeps draining while the
    /// gate is closed (T09 — pre-enable frames must never be sent), so the
    /// meaningful precondition is that capture is actually flowing.
    /// </summary>
    private void WaitForPreroll(SourceChannel channel)
    {
        var pushedAtStart = channel.Stats().PushedFrames;
        var deadline = Environment.TickCount64 + _options.SourcePrerollTimeoutMs;
        while (channel.Stats().PushedFrames == pushedAtStart && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(5);
        }
    }

    /// <summary>Creates and starts the output pump for the currently configured render endpoint; caller owns error handling.</summary>
    private void StartOutput()
    {
        var output = _options.OutputFactory(_devices!.VirtualRenderDeviceId);
        _output = output;
        output.Faulted += ex => _commands.Post("output-faulted", () =>
        {
            if (ReferenceEquals(_output, output)) HandleOutputFaulted(ex);
        }, urgent: true);
        _output.Start(MixPull);
    }

    private void EnsureMonitorOutput()
    {
        if (_monitorOutput is not null) return;
        var output = _options.MonitorOutputFactory(_devices!.MonitorDeviceId);
        _monitorOutput = output;
        output.Faulted += ex => _commands.Post("monitor-faulted", () =>
        {
            if (ReferenceEquals(_monitorOutput, output)) HandleMonitorFaulted(ex);
        }, urgent: true);
        output.Start(_monitorRouter.Read);
    }

    private void ReleaseIdleMonitor()
    {
        if (!Engine.Switches.MonitorEnabled && Engine.Clip is not { Mode: ClipPlaybackMode.Preview })
            StopMonitorDevices();
    }

    /// <summary>Stops every live audio path except the output pump; engine state changes happen in the engine.</summary>
    private void TeardownAudioPaths()
    {
        _clipLoading = false;
        _playRequestId++;
        _clipPlayer.Discard();
        _clipFeed.SetTicket(null);
        _micChannel.SetTicket(null);
        _micAdapter = SafeDispose(_micAdapter);
        StopAppCapture();
        StopMonitorDevices();
        _monitorRouter.Clear();
    }

    private void AbortForFailedSwitch()
    {
        TeardownAudioPaths();
        Engine.AbortSession();
        if (_output is not null)
        {
            _ = Engine.ValidateDevices();
        }

        PublishState();
    }

    private void StopAppCapture()
    {
        _appChannel.SetTicket(null);
        _appAdapter = SafeDispose(_appAdapter);
    }

    private void StopMonitorDevices()
    {
        _monitorOutput = SafeDispose(_monitorOutput);
        _monitorCapture = SafeDispose(_monitorCapture);
    }

    private static float ClampGain(float value) => float.IsFinite(value) ? Math.Clamp(value, 0f, 4f) : 0f;

    private SessionRuntimeState BuildRuntime() => new(
        Engine.Snapshot(),
        _gains,
        new MonitorRouteState(Engine.Switches.MonitorEnabled, Engine.MonitorMode));

    /// <summary>Publishes an immutable snapshot for the pump threads (command thread is the only writer).</summary>
    private void PublishState() => _runtime = BuildRuntime();

    private void Notice(SessionNoticeKind kind, string message) => Noticed?.Invoke(new SessionNotice(kind, message));

    private static T? SafeDispose<T>(T? disposable)
        where T : class, IDisposable
    {
        try
        {
            disposable?.Dispose();
        }
        catch
        {
            // teardown best effort: a failed release must not break the next attach
        }

        return null;
    }
}
