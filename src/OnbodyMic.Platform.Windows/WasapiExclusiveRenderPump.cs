namespace OnbodyMic.Platform.Windows;

using System.Runtime.InteropServices;
using OnbodyMic.Application;

/// <summary>
/// Exclusive-mode, event-driven render pump for the virtual cable render
/// endpoint, on an MMCSS "Pro Audio" thread at highest priority.
/// Driver contract from P0B forensics: 44.1 kHz/16-bit/stereo, ≥20 ms
/// period, prefill the whole buffer and write exactly bufferFrames per
/// event — under-filling gets throttled to ~0.65x realtime by the Steam
/// driver, and CurrentPadding-based sizing is unreliable here.
/// </summary>
public sealed class WasapiExclusiveRenderPump : IVirtualAudioOutput
{
    private readonly string _endpointId;
    private readonly int _rate;
    private readonly int _channels;
    private readonly int _periodMs;
    private readonly float[] _monoScratch;
    private IAudioClientCom? _client;
    private IAudioRenderClientCom? _render;
    private IntPtr _event;
    private Thread? _thread;
    private volatile bool _running;

    public event Action<Exception>? Faulted;

    public WasapiExclusiveRenderPump(string endpointId, int rate = 44100, int channels = 2, int periodMs = 20)
    {
        if (periodMs < 20)
        {
            throw new ArgumentOutOfRangeException(nameof(periodMs), "Steam driver exclusive minimum stable period is 20 ms (P0B forensics)");
        }

        _endpointId = endpointId;
        _rate = rate;
        _channels = channels;
        _periodMs = periodMs;
        _monoScratch = new float[(rate * periodMs / 1000) + 1];
    }

    public void Start(PullFrames pull)
    {
        if (_thread is not null)
        {
            throw new InvalidOperationException("pump already started");
        }

        var client = WindowsLowLevelAudio.ActivateClient(_endpointId);
        var formatPtr = WindowsLowLevelAudio.CreateWaveFormatPcm16(_rate, _channels);
        try
        {
            var hns = _periodMs * 10000L;
            Marshal.ThrowExceptionForHR(client.Initialize(
                WindowsLowLevelAudio.ShareModeExclusive, WindowsLowLevelAudio.StreamFlagsEventCallback,
                hns, hns, formatPtr, IntPtr.Zero));
            Marshal.ThrowExceptionForHR(client.GetBufferSize(out var bufferFrames));
            var renderIid = WindowsLowLevelAudio.IidIAudioRenderClient;
            Marshal.ThrowExceptionForHR(client.GetService(ref renderIid, out var service));
            _render = (IAudioRenderClientCom)service;
            _event = WindowsLowLevelAudio.CreateEventHandle();
            Marshal.ThrowExceptionForHR(client.SetEventHandle(_event));

            _client = client;
            _running = true;
            _thread = new Thread(() => PumpLoop(pull, (int)bufferFrames))
            {
                IsBackground = true,
                Name = "exclusive-render",
            };
            _thread.Start();
        }
        finally
        {
            Marshal.FreeHGlobal(formatPtr);
        }
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(1000);
        _thread = null;
        _ = _client?.Stop();
    }

    public void Dispose()
    {
        Stop();
        if (_event != IntPtr.Zero)
        {
            _ = WindowsLowLevelAudio.CloseEventHandle(_event);
            _event = IntPtr.Zero;
        }

        if (_render is not null)
        {
            _ = Marshal.ReleaseComObject(_render);
            _render = null;
        }

        if (_client is not null)
        {
            _ = Marshal.ReleaseComObject(_client);
            _client = null;
        }
    }

    private void PumpLoop(PullFrames pull, int bufferFrames)
    {
        try
        {
            using var mmcss = new MmcssScope();
            mmcss.Acquire();
            Thread.CurrentThread.Priority = ThreadPriority.Highest;

            // exclusive event-mode contract: prefill the whole buffer; from
            // then on one event equals exactly one full buffer period
            Fill(pull, bufferFrames);
            Marshal.ThrowExceptionForHR(_client!.Start());
            while (_running)
            {
                if (WindowsLowLevelAudio.WaitForEvent(_event, 2000) != 0)
                {
                    continue;
                }

                Fill(pull, bufferFrames);
            }
        }
        catch (Exception ex)
        {
            if (_running)
            {
                Faulted?.Invoke(ex);
            }
        }
    }

    private void Fill(PullFrames pull, int frames)
    {
        Marshal.ThrowExceptionForHR(_render!.GetBuffer(frames, out var data));
        var taken = 0;
        try
        {
            var span = _monoScratch.AsSpan(0, frames);
            var filled = pull(span);
            taken = Math.Clamp(filled, 0, frames);
            for (var f = 0; f < frames; f++)
            {
                var value = f < taken ? span[f] : 0f;
                var pcm = (short)Math.Clamp(Math.Round(value * 32767f), short.MinValue, short.MaxValue);
                Marshal.WriteInt16(data, (f * _channels) * sizeof(short), pcm);
                for (var c = 1; c < _channels; c++)
                {
                    Marshal.WriteInt16(data, ((f * _channels) + c) * sizeof(short), pcm);
                }
            }
        }
        finally
        {
            // the whole buffer must be released even if the pull failed partway
            Marshal.ThrowExceptionForHR(_render.ReleaseBuffer(frames, 0));
        }
    }
}
