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

    public event Action<string?>? Ended;

    public void Start()
    {
        if (StartError is not null)
        {
            throw new COMException(StartError);
        }

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

    public DecodedClip Decode(string path) => new(Generator(path));

    public static float[] CreateTone(float amplitude, int frames)
    {
        var samples = new float[frames];
        Array.Fill(samples, amplitude);
        return samples;
    }
}
