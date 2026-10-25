namespace OnbodyMic.Platform.Windows;

using System.Diagnostics;
using OnbodyMic.Application;
using OnbodyMic.Audio;

/// <summary>
/// Per-process loopback capture of the bound application's process tree
/// (include mode). Process exit is reported as a normal end (null reason);
/// capture errors are faults.
/// </summary>
public sealed class WindowsAppSource : IAudioSourceAdapter
{
    private readonly AppBinding _binding;
    private readonly SourceChannel _channel;
    private readonly ProcessLoopbackCapture _capture = new(44100, 1);
    private readonly Lock _gate = new();
    private Timer? _watcher;
    private bool _ended;

    public event Action<string?>? Ended;

    public WindowsAppSource(AppBinding binding, SourceChannel channel)
    {
        _binding = binding;
        _channel = channel;
    }

    public void Start()
    {
        _capture.DataAvailable += OnData;
        _capture.Faulted += ex =>
        {
            RaiseEnded(ex.Message);
        };
        _capture.Start(_binding.ProcessId, includeProcessTree: true);

        // poll the root process: loopback keeps delivering zeros after exit,
        // so the watcher is what actually ends this source (T08)
        _watcher = new Timer(_ =>
        {
            try
            {
                using var process = Process.GetProcessById((int)_binding.ProcessId);
                _ = process.HasExited;
            }
            catch (ArgumentException)
            {
                RaiseEnded(null); // process gone: normal end
            }
        }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    private void OnData(byte[] buffer, int bytes)
    {
        var samples = bytes / 2;
        var floats = new float[samples];
        for (var i = 0; i < samples; i++)
        {
            floats[i] = BitConverter.ToInt16(buffer, i * 2) / 32768f;
        }

        _channel.Push(floats);
    }

    private void RaiseEnded(string? reason)
    {
        lock (_gate)
        {
            if (_ended)
            {
                return;
            }

            _ended = true;
        }

        _watcher?.Dispose();
        _watcher = null;
        try
        {
            _capture.Dispose();
        }
        catch
        {
            // best-effort teardown
        }

        Ended?.Invoke(reason);
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
        try
        {
            _capture.Dispose();
        }
        catch
        {
            // best-effort teardown
        }
    }
}
