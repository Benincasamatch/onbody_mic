namespace OnbodyMic.Platform.Windows;

using OnbodyMic.Application;
using OnbodyMic.Audio;

/// <summary>FullEndpoint monitoring: reads the actual virtual capture endpoint (what KOOK reads).</summary>
public sealed class WindowsMonitorCapture : IMonitorCaptureAdapter
{
    private readonly string _endpointId;
    private WasapiEventCapture? _capture;

    public event Action<Exception>? Faulted;

    public WindowsMonitorCapture(string endpointId) => _endpointId = endpointId;

    public void Start(PushFrames push)
    {
        if (_capture is not null)
        {
            return;
        }

        // explicit 44.1 kHz mono PCM16 + AUTOCONVERTPCM (the mic channel's
        // proven path): the AUX endpoint's mixFormat session has been proven
        // to misreport/deliver mismatched sample shapes (P3.2 forensics:
        // mixFormat claimed PCM16 while delivering float — full-scale noise)
        var capture = WasapiEventCapture.Open(_endpointId, bufferMs: 10, useMixFormat: false);
        capture.Frames += frames => push(frames);
        capture.Faulted += ex =>
        {
            _capture = null;
            DisposeCapture(capture);
            Faulted?.Invoke(ex);
        };
        capture.Start();
        _capture = capture;
    }

    public void Stop() => _capture = DisposeCapture(_capture);

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
