namespace OnbodyMic.Platform.Windows;

using System.Runtime.InteropServices;
using OnbodyMic.Application;

/// <summary>
/// Local monitor output: shared-mode event-driven render pump (44.1 kHz stereo
/// PCM16 via AUTOCONVERTPCM) on an MMCSS thread, pulling engine-format mono
/// frames. Avoids NAudio's MMDeviceEnumerator entirely: once this process has
/// activated the device enumerator through our own interop, NAudio's coclass
/// cast fails on the cached RCW (observed in P1 smoke E).
/// </summary>
public sealed class WindowsMonitorOutput : IMonitorOutput
{
    private readonly string _endpointId;
    private readonly int _bufferMs;
    private readonly float[] _monoScratch = new float[8192];
    private IAudioClientCom? _client;
    private IAudioRenderClientCom? _render;
    private IntPtr _event;
    private Thread? _thread;
    private volatile bool _running;

    public event Action<Exception>? Faulted;

    public WindowsMonitorOutput(string endpointId, int bufferMs = 50)
    {
        _endpointId = endpointId;
        _bufferMs = bufferMs;
    }

    public void Start(PullFrames pull)
    {
        if (_thread is not null)
        {
            return;
        }

        var client = WindowsLowLevelAudio.ActivateClient(_endpointId);
        var formatPtr = WindowsLowLevelAudio.CreateWaveFormatPcm16(44100, 2);
        try
        {
            var flags = WindowsLowLevelAudio.StreamFlagsEventCallback
                | WindowsLowLevelAudio.StreamFlagsAutoConvertPcm
                | WindowsLowLevelAudio.StreamFlagsSrcDefaultQuality;
            Marshal.ThrowExceptionForHR(client.Initialize(
                WindowsLowLevelAudio.ShareModeShared, flags, _bufferMs * 10000L, 0, formatPtr, IntPtr.Zero));
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
                Name = "monitor-render",
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
            Marshal.ThrowExceptionForHR(_client!.Start());
            while (_running)
            {
                if (WindowsLowLevelAudio.WaitForEvent(_event, 2000) != 0)
                {
                    continue;
                }

                // shared mode is padding-aware; underfilling is fine here
                Marshal.ThrowExceptionForHR(_client.GetCurrentPadding(out var padding));
                var toWrite = (int)(bufferFrames - padding);
                if (toWrite <= 0)
                {
                    continue;
                }

                Marshal.ThrowExceptionForHR(_render!.GetBuffer(toWrite, out var data));
                var written = 0;
                while (written < toWrite)
                {
                    var chunk = Math.Min(_monoScratch.Length, toWrite - written);
                    var taken = pull(_monoScratch.AsSpan(0, chunk));
                    if (taken <= 0)
                    {
                        break;
                    }

                    for (var f = 0; f < taken; f++)
                    {
                        var pcm = (short)Math.Clamp(Math.Round(_monoScratch[f] * 32767f), short.MinValue, short.MaxValue);
                        var baseIndex = ((written + f) * 2) * sizeof(short);
                        Marshal.WriteInt16(data, baseIndex, pcm);
                        Marshal.WriteInt16(data, baseIndex + 2, pcm);
                    }

                    written += taken;
                    if (taken < chunk)
                    {
                        break;
                    }
                }

                for (var f = written; f < toWrite; f++)
                {
                    var baseIndex = (f * 2) * sizeof(short);
                    Marshal.WriteInt16(data, baseIndex, 0);
                    Marshal.WriteInt16(data, baseIndex + 2, 0);
                }

                Marshal.ThrowExceptionForHR(_render.ReleaseBuffer(toWrite, 0));
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
}
