namespace OnbodyMic.Platform.Windows;

using System.Diagnostics;
using OnbodyMic.Application;
using OnbodyMic.Audio;

/// <summary>Captures the verified application root and descendants, never the system mix.</summary>
public sealed class WindowsAppSource : IAudioSourceAdapter
{
    private readonly SourceChannel _channel;
    private readonly ProcessLoopbackCapture _capture = new(44100, 1);
    private readonly Process _process;
    private readonly uint _rootProcessId;
    private readonly float[] _scratch = new float[4096];
    private readonly Lock _gate = new();
    private Timer? _watcher;
    private volatile bool _ended;
    private bool _started;

    public event Action<string?>? Ended;

    public WindowsAppSource(AppBinding binding, SourceChannel channel, long? expectedStartTimeTicks = null)
    {
        _channel = channel;
        var selected = WindowsAppCatalog.ReadLive(binding.ProcessId);
        if (expectedStartTimeTicks is { } expected && selected.StartTimeTicks != expected)
            throw new InvalidOperationException("application process identity changed; bind the live instance again");
        var root = expectedStartTimeTicks is null ? WindowsAppCatalog.ResolveCaptureRoot(selected) : selected;
        _rootProcessId = root.ProcessId;
        _process = Process.GetProcessById(checked((int)root.ProcessId));
        try
        {
            // Keep this handle for the entire capture. Polling by PID can silently attach
            // to a replacement process, even one running the same executable.
            _ = _process.Handle;
            if (_process.HasExited || _process.StartTime.ToUniversalTime().Ticks != root.StartTimeTicks)
                throw new InvalidOperationException("application root exited or changed before capture started");
        }
        catch
        {
            _process.Dispose();
            throw;
        }
        _capture.DataAvailable += OnData;
        _capture.Faulted += OnFault;
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_ended, this);
            if (_started) throw new InvalidOperationException("application capture already started");
            _started = true;
            if (_process.HasExited) throw new InvalidOperationException("selected application process has exited");
            _watcher = new Timer(PollRoot, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        try
        {
            _capture.Start(_rootProcessId, includeProcessTree: true);
            lock (_gate)
            {
                if (!_ended) _watcher!.Change(TimeSpan.Zero, TimeSpan.FromMilliseconds(500));
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void PollRoot(object? state)
    {
        string? reason = null;
        lock (_gate)
        {
            if (_ended) return;
            try
            {
                if (!_process.HasExited) return;
            }
            catch (Exception ex)
            {
                reason = $"application identity watcher failed: {ex.Message} (0x{ex.HResult:X8})";
            }
        }
        RaiseEnded(reason);
    }

    private void OnFault(Exception exception)
    {
        string? reason = exception.Message;
        lock (_gate)
        {
            if (_ended) return;
            try { if (_process.HasExited) reason = null; }
            catch (Exception ex) { reason += $"; application identity check failed: {ex.Message}"; }
        }
        RaiseEnded(reason);
    }

    private void OnData(byte[] buffer, int bytes)
    {
        if (_ended) return;
        var samples = bytes / 2;
        for (var offset = 0; offset < samples; offset += _scratch.Length)
        {
            var count = Math.Min(samples - offset, _scratch.Length);
            for (var i = 0; i < count; i++)
                _scratch[i] = BitConverter.ToInt16(buffer, (offset + i) * 2) / 32768f;
            _channel.Push(_scratch.AsSpan(0, count));
        }
    }

    private void RaiseEnded(string? reason)
    {
        if (!BeginEnd()) return;
        try { _capture.Dispose(); }
        finally
        {
            _process.Dispose();
            Ended?.Invoke(reason);
        }
    }

    private bool BeginEnd()
    {
        lock (_gate)
        {
            if (_ended) return false;
            _ended = true;
            _watcher?.Dispose();
            _watcher = null;
            return true;
        }
    }

    public void Dispose()
    {
        if (!BeginEnd()) return;
        try { _capture.Dispose(); }
        finally { _process.Dispose(); }
    }
}
