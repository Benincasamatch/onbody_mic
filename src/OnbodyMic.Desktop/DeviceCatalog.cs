namespace OnbodyMic.Desktop;

using OnbodyMic.Platform.Windows;

/// <summary>
/// Shared, cached audio-endpoint list. Enumerating WASAPI endpoints costs tens
/// of milliseconds and must never run on the UI thread inside a 500 ms poll
/// (§19: 设备扫描不能阻塞 UI 线程), so scans run on a pool thread and the
/// result is published back on the caller's synchronization context.
///
/// If the cross-thread call fails (the enumerator is created on the UI
/// apartment at startup, devlog 2026-10-26 P1 constraint), the scan retries
/// once on the calling thread instead of silently showing an empty list.
/// </summary>
internal sealed class DeviceCatalog
{
    private readonly Action<string> _log;
    private IReadOnlyList<AudioEndpointInfo> _devices = [];
    private string? _lastError;
    private int _scanning;

    public DeviceCatalog(Action<string> log) => _log = log;

    public IReadOnlyList<AudioEndpointInfo> Devices => _devices;

    public string? LastError => _lastError;

    public bool Scanning => Volatile.Read(ref _scanning) == 1;

    /// <summary>Raised on the UI thread after a successful scan.</summary>
    public event Action? Refreshed;

    /// <summary>Starts a background scan; a second call while scanning is a no-op.</summary>
    public void RefreshAsync()
    {
        if (Interlocked.Exchange(ref _scanning, 1) == 1)
        {
            return;
        }

        var context = SynchronizationContext.Current;
        Task.Run(() =>
        {
            try
            {
                return WindowsAudioDevices.List(activeOnly: true);
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _log($"[devices] 后台枚举失败，改用 UI 线程重试: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }).ContinueWith(
            task =>
            {
                var list = task.Result;
                if (list is null)
                {
                    try
                    {
                        list = WindowsAudioDevices.List(activeOnly: true);
                    }
                    catch (Exception ex)
                    {
                        _lastError = ex.Message;
                        _log($"[devices] 设备枚举失败: {ex.Message}");
                    }
                }

                if (list is not null)
                {
                    _devices = list;
                    _lastError = null;
                }

                Interlocked.Exchange(ref _scanning, 0);
                if (context is null)
                {
                    Refreshed?.Invoke();
                }
                else
                {
                    context.Post(_ => Refreshed?.Invoke(), null);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            context is null ? TaskScheduler.Default : TaskScheduler.FromCurrentSynchronizationContext());
    }

    public IReadOnlyList<AudioEndpointInfo> ForFlow(bool capture) =>
        _devices.Where(d => d.DataFlow == (capture ? "Capture" : "Render")).ToList();

    public string? FriendlyName(string? id) =>
        id is null ? null : _devices.FirstOrDefault(d => d.Id == id)?.FriendlyName;
}
