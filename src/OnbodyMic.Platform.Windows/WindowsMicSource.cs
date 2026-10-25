namespace OnbodyMic.Platform.Windows;

using OnbodyMic.Application;
using OnbodyMic.Audio;

/// <summary>Real microphone bound to one explicit capture endpoint (never the default).</summary>
public sealed class WindowsMicSource : IAudioSourceAdapter
{
    private readonly string _endpointId;
    private readonly SourceChannel _channel;
    private readonly int _bufferMs;
    private WasapiEventCapture? _capture;

    public event Action<string?>? Ended;

    public WindowsMicSource(string endpointId, SourceChannel channel, int bufferMs = 20)
    {
        _endpointId = endpointId;
        _channel = channel;
        _bufferMs = bufferMs;
    }

    public void Start()
    {
        if (_capture is not null)
        {
            return;
        }

        // explicit 44.1 kHz mono PCM16 + AUTOCONVERTPCM: the audio engine
        // converts the mic's native 48 kHz stereo so the graph never resamples
        var capture = WasapiEventCapture.Open(_endpointId, _bufferMs, useMixFormat: false);
        capture.Frames += _channel.Push;
        capture.Faulted += ex =>
        {
            _capture = null;
            DisposeCapture(capture);
            Ended?.Invoke(ex.Message);
        };
        capture.Start();
        _capture = capture;
    }

    public void Dispose() => _capture = DisposeCapture(_capture);

    private static WasapiEventCapture? DisposeCapture(WasapiEventCapture? capture)
    {
        try
        {
            capture?.Dispose();
        }
        catch
        {
            // best-effort teardown
        }

        return null;
    }
}
