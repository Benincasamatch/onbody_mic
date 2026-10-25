namespace OnbodyMic.Platform.Windows;

using System.Runtime.InteropServices;

/// <summary>
/// Shared-mode, event-driven capture pump on an MMCSS-boosted thread (P0B
/// finding: NAudio's normal-priority polling threads cost ~100 ms). Delivers
/// engine-format mono 44.1 kHz float frames; either an explicit PCM16 format
/// (the audio engine resamples/downmixes via AUTOCONVERTPCM) or the endpoint
/// mix format.
/// </summary>
public sealed class WasapiEventCapture : IDisposable
{
    private readonly IAudioClientCom _client;
    private readonly IAudioCaptureClientCom _capture;
    private readonly IntPtr _event;
    private readonly int _channels;
    private readonly bool _floatSamples;
    private readonly byte[] _byteScratch;
    private readonly float[] _floatScratch;
    private readonly float[] _monoScratch;
    private Thread? _thread;
    private volatile bool _running;

    /// <summary>Frames are valid only during the callback; copy what you keep.</summary>
    public event Action<ReadOnlySpan<float>>? Frames;

    public event Action<Exception>? Faulted;

    /// <summary>Actual negotiated format; the mix format's sample rate decides whether resampling is needed.</summary>
    public CaptureFormatInfo Format { get; private set; }

    public readonly record struct CaptureFormatInfo(int SampleRate, int Channels, bool FloatSamples);

    private WasapiEventCapture(
        IAudioClientCom client,
        IAudioCaptureClientCom capture,
        IntPtr eventHandle,
        int channels,
        bool floatSamples,
        int maxPacketBytes)
    {
        _client = client;
        _capture = capture;
        _event = eventHandle;
        _channels = channels;
        _floatSamples = floatSamples;
        _byteScratch = new byte[maxPacketBytes];
        _floatScratch = new float[maxPacketBytes / 4];
        _monoScratch = new float[_floatScratch.Length / Math.Max(1, channels)];
    }

    /// <param name="useMixFormat">true: open with the endpoint mix format (assumed float); false: explicit 44.1 kHz mono PCM16 + AUTOCONVERTPCM.</param>
    public static WasapiEventCapture Open(string endpointId, int bufferMs, bool useMixFormat)
    {
        var client = WindowsLowLevelAudio.ActivateClient(endpointId);
        var formatPtr = IntPtr.Zero;
        try
        {
            int channels;
            bool isFloat;
            int sampleRate;
            if (useMixFormat)
            {
                Marshal.ThrowExceptionForHR(client.GetMixFormat(out formatPtr));
                channels = Marshal.ReadInt16(formatPtr, 2);
                sampleRate = Marshal.ReadInt32(formatPtr, 4);
                isFloat = Marshal.ReadInt16(formatPtr, 0) == 3; // WAVE_FORMAT_IEEE_FLOAT
            }
            else
            {
                formatPtr = WindowsLowLevelAudio.CreateWaveFormatPcm16(44100, 1);
                channels = 1;
                sampleRate = 44100;
                isFloat = false;
            }

            var flags = WindowsLowLevelAudio.StreamFlagsEventCallback
                | WindowsLowLevelAudio.StreamFlagsAutoConvertPcm
                | WindowsLowLevelAudio.StreamFlagsSrcDefaultQuality;
            Marshal.ThrowExceptionForHR(client.Initialize(
                WindowsLowLevelAudio.ShareModeShared, flags, bufferMs * 10000L, 0, formatPtr, IntPtr.Zero));
            Marshal.ThrowExceptionForHR(client.GetBufferSize(out var bufferFrames));

            var captureIid = WindowsLowLevelAudio.IidIAudioCaptureClient;
            Marshal.ThrowExceptionForHR(client.GetService(ref captureIid, out var service));
            var captureClient = (IAudioCaptureClientCom)service;
            var eventHandle = WindowsLowLevelAudio.CreateEventHandle();
            Marshal.ThrowExceptionForHR(client.SetEventHandle(eventHandle));

            var blockAlign = isFloat ? channels * 4 : channels * 2;
            var instance = new WasapiEventCapture(client, captureClient, eventHandle, channels, isFloat, (int)bufferFrames * blockAlign);
            instance.Format = new CaptureFormatInfo(sampleRate, channels, isFloat);
            return instance;
        }
        finally
        {
            if (formatPtr != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(formatPtr);
            }
        }
    }

    public void Start()
    {
        if (_thread is not null)
        {
            throw new InvalidOperationException("capture already started");
        }

        _running = true;
        Marshal.ThrowExceptionForHR(_client.Start());
        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "wasapi-capture" };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(1000);
        _thread = null;
        _ = _client.Stop();
    }

    public void Dispose()
    {
        Stop();
        if (_event != IntPtr.Zero)
        {
            _ = WindowsLowLevelAudio.CloseEventHandle(_event);
        }

        _ = Marshal.ReleaseComObject(_capture);
        _ = Marshal.ReleaseComObject(_client);
    }

    private void CaptureLoop()
    {
        try
        {
            using var mmcss = new MmcssScope();
            mmcss.Acquire();
            Thread.CurrentThread.Priority = ThreadPriority.Highest;
            while (_running)
            {
                if (WindowsLowLevelAudio.WaitForEvent(_event, 2000) != 0)
                {
                    continue;
                }

                while (_running)
                {
                    Marshal.ThrowExceptionForHR(_capture.GetNextPacketSize(out var frames));
                    if (frames == 0)
                    {
                        break;
                    }

                    Marshal.ThrowExceptionForHR(_capture.GetBuffer(out var data, out frames, out var flags, out _, out _));
                    var bytes = (int)frames * (_floatSamples ? _channels * 4 : _channels * 2);
                    if (bytes > 0)
                    {
                        if ((flags & 0x2) != 0 || data == IntPtr.Zero)
                        {
                            // AUDCLNT_BUFFERFLAGS_SILENT: emit zeros in the real shape
                            var count = (int)frames * _channels;
                            _floatScratch.AsSpan(0, count).Clear();
                            Frames?.Invoke(Downmix(count));
                        }
                        else
                        {
                            Marshal.Copy(data, _byteScratch, 0, bytes);
                            Deliver(bytes);
                        }
                    }

                    Marshal.ThrowExceptionForHR(_capture.ReleaseBuffer(frames));
                }
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

    private void Deliver(int bytes)
    {
        int count;
        if (_floatSamples)
        {
            count = bytes / 4;
            Buffer.BlockCopy(_byteScratch, 0, _floatScratch, 0, bytes);
        }
        else
        {
            count = bytes / 2;
            for (var i = 0; i < count; i++)
            {
                _floatScratch[i] = BitConverter.ToInt16(_byteScratch, i * 2) / 32768f;
            }
        }

        Frames?.Invoke(Downmix(count));
    }

    /// <summary>Channel-average to engine mono into a reusable buffer; valid only during the callback.</summary>
    private ReadOnlySpan<float> Downmix(int count)
    {
        if (_channels == 1)
        {
            return _floatScratch.AsSpan(0, count);
        }

        var frameCount = count / _channels;
        for (var f = 0; f < frameCount; f++)
        {
            var sum = 0f;
            for (var c = 0; c < _channels; c++)
            {
                sum += _floatScratch[(f * _channels) + c];
            }

            _monoScratch[f] = sum / _channels;
        }

        return _monoScratch.AsSpan(0, frameCount);
    }
}
