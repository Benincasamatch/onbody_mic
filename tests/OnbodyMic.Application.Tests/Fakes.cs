namespace OnbodyMic.Application.Tests;

using System.Runtime.InteropServices;
using OnbodyMic.Application;
using OnbodyMic.Audio;
using OnbodyMic.Domain;

public sealed class FakeSourceAdapter : IAudioSourceAdapter
{
    public SourceChannel? Channel { get; set; }

    public bool Started { get; private set; }

    public bool Disposed { get; private set; }

    public string? StartError { get; set; }

    /// <summary>Invoked inside Start; simulates a source that already has pre-roll buffered.</summary>
    public Action? OnStart { get; set; }

    public event Action<string?>? Ended;

    public void Start()
    {
        if (StartError is not null)
        {
            throw new COMException(StartError);
        }

        OnStart?.Invoke();
        Started = true;
    }

    public void RaiseEnded(string? reason) => Ended?.Invoke(reason);

    public void Push(params float[] frames) => Channel!.Push(frames);

    public void Dispose() => Disposed = true;
}

public sealed class FakeVirtualOutput : IVirtualAudioOutput
{
    public PullFrames? Pull { get; private set; }

    public bool Disposed { get; private set; }

    public event Action<Exception>? Faulted;

    public void Start(PullFrames pull) => Pull = pull;

    public void Stop()
    {
    }

    public void RaiseFault() => Faulted?.Invoke(new COMException("device invalidated", unchecked((int)0x88890004)));

    /// <summary>Drives one pump cycle and returns the gated mix.</summary>
    public float[] Pump(int frames)
    {
        var buffer = new float[frames];
        Pull?.Invoke(buffer);
        return buffer;
    }

    public void Dispose() => Disposed = true;
}

public sealed class FakeMonitorOutput : IMonitorOutput
{
    public PullFrames? Pull { get; private set; }

    public bool Disposed { get; private set; }

    public event Action<Exception>? Faulted;

    public void Start(PullFrames pull) => Pull = pull;

    public void Stop()
    {
    }

    public void RaiseFault() => Faulted?.Invoke(new COMException("headphones gone"));

    public float[] Pump(int frames)
    {
        var buffer = new float[frames];
        Pull?.Invoke(buffer);
        return buffer;
    }

    public void Dispose() => Disposed = true;
}

public sealed class FakeMonitorCapture : IMonitorCaptureAdapter
{
    public PushFrames? Push { get; private set; }

    public bool Disposed { get; private set; }

    public event Action<Exception>? Faulted;

    public void Start(PushFrames push) => Push = push;

    public void RaiseFault() => Faulted?.Invoke(new COMException("endpoint gone"));

    public void Stop()
    {
    }

    public void Dispose() => Disposed = true;
}

public sealed class FakeDecoder : IClipDecoder
{
    public Func<string, float[]> Generator { get; set; } = _ => CreateTone(0.5f, 4410);

    public int Calls { get; private set; }

    public string? LastPath { get; private set; }

    public DecodedClip Decode(string path)
    {
        Calls++;
        LastPath = path;
        return new(Generator(path));
    }

    public static float[] CreateTone(float amplitude, int frames)
    {
        var samples = new float[frames];
        Array.Fill(samples, amplitude);
        return samples;
    }
}

public sealed class FakeHotkeyService : IHotkeyService
{
    public List<HotkeyBinding> Bound { get; } = [];

    public event Action<HotkeyTrigger>? Triggered;

    public IReadOnlyList<HotkeyBinding> Active => Bound;

    public CommandResult Register(HotkeyBinding binding)
    {
        if (Bound.Any(b => b.Gesture == binding.Gesture && !b.Matches(binding.Action, binding.TargetId)))
        {
            return CommandResult.Reject($"hotkey-conflict:{binding.Describe()}");
        }

        Bound.RemoveAll(b => b.Matches(binding.Action, binding.TargetId));
        Bound.Add(binding);
        return CommandResult.Ok;
    }

    public CommandResult Unregister(HotkeyAction action, string? targetId)
    {
        Bound.RemoveAll(b => b.Matches(action, targetId));
        return CommandResult.Ok;
    }

    /// <summary>Fires a bound activation synchronously on the test thread.</summary>
    public void Fire(HotkeyBinding binding) => Triggered?.Invoke(new HotkeyTrigger(binding, Environment.TickCount64));

    public void Dispose()
    {
    }
}

public sealed class FakeSampleStore : IClipSampleStore
{
    public Dictionary<string, float[]> Stored { get; } = [];

    public List<string> StoredIds { get; } = [];

    public bool TryGet(string clipId, out float[] samples)
    {
        if (Stored.TryGetValue(clipId, out samples!))
        {
            return true;
        }

        samples = [];
        return false;
    }

    public void Store(string clipId, float[] samples)
    {
        Stored[clipId] = samples;
        StoredIds.Add(clipId);
    }

    public void Remove(string clipId) => Stored.Remove(clipId);
}

