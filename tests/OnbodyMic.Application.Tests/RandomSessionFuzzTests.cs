namespace OnbodyMic.Application.Tests;

using OnbodyMic.Application;
using OnbodyMic.Audio;
using OnbodyMic.Domain;
using Xunit;

/// <summary>
/// T41 fuzz gate: long seeded random command sequences (switches, clips,
/// monitor, gains, source/output faults, preset applies). After every
/// command three pump cycles run — mic push, app push, quiet — so the clip
/// tone (the only self-draining source) is separable from pushed sources.
/// Every output frame must belong to the authorized subset-sum set derived
/// from the claimed SessionStatus snapshot; the emergency latch must produce
/// literal silence; no NaN, no out-of-range samples; the session stays
/// operable. Amplitudes (mic 0.08, app 0.2, clip 0.45) and gain bands are
/// chosen so authorized sums never collide with a forbidden component's
/// amplitude, keeping detection exact instead of heuristic.
/// </summary>
public sealed class RandomSessionFuzzTests
{
    private const int Period = 441; // 10 ms @ 44.1 kHz
    private const double Tol = 0.008;
    

    private const float MicAmp = 0.08f;
    private const float AppAmp = 0.2f;
    private const float ClipAmp = 0.45f;
    private const float CaptureAmp = 0.3f;

    public static IEnumerable<object[]> Seeds()
    {
        yield return [20261026];
        yield return [20261027];
        yield return [42];
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Random_command_sequences_never_leak_unauthorized_audio(int seed)
    {
        var rng = new Random(seed);
        using var env = FuzzEnv.Create();
        Accept(env.Session.AttachOutput());
        Accept(env.Session.SetGains(new RouteGains(0.7f, 0.8f, 0.7f, 0.75f)));

        for (var step = 0; step < 120; step++)
        {
            switch (rng.Next(16))
            {
                case 0: env.Session.EnableMic(rng.NextSingle() < 0.6); break;
                case 1: env.Session.EnableExtras(rng.NextSingle() < 0.6); break;
                case 2: env.Session.EnableApp(rng.NextSingle() < 0.6); break;
                case 3:
                    var extras = env.Session.EnableExtras(true);
                    Assert.True(extras.Accepted || extras.Rejection == "emergency-latched", extras.Rejection);
                    env.Session.BindApplication(new AppBinding((uint)rng.Next(1, 9999), "fuzz-app"));
                    break;
                case 4:
                    env.Session.PlayClip("fuzz.wav");
                    break;
                case 5:
                    env.Session.PreviewClip("fuzz.wav");
                    break;
                case 6:
                    env.Session.StopClip();
                    break;
                case 7:
                    var mode = rng.Next(2) == 0 ? MonitorMode.ClipOnly : MonitorMode.FullEndpoint;
                    env.Session.SetMonitor(rng.NextSingle() < 0.6, mode);
                    break;
                case 8:
                    env.Session.EmergencyMute();
                    break;
                case 9:
                    env.Session.UnlockEmergency();
                    break;
                case 10:
                    env.Session.SetGains(new RouteGains(
                        0.5f + 0.4f * rng.NextSingle(),
                        0.6f + 0.5f * rng.NextSingle(),
                        0.6f + 0.3f * rng.NextSingle(),
                        0.5f + 0.5f * rng.NextSingle()));
                    break;
                case 11:
                    env.Mic.RaiseEnded(rng.Next(2) == 0 ? "device-gone" : null);
                    break;
                case 12:
                    env.App.RaiseEnded("crash");
                    break;
                case 13:
                    env.Output.RaiseFault();
                    break;
                case 14:
                    env.Session.ApplyPreset(FuzzEnv.RandomPreset(rng));
                    // T23: any applied preset leaves everything off
                    var after = env.Session.Status();
                    Assert.Equal(SessionSwitches.AllOff, after.Switches);
                    Assert.Null(after.Clip);
                    break;
                default:
                    if (rng.NextSingle() < 0.5 && env.Mic.Channel is not null)
                    {
                        env.Mic.Push(Filled(MicAmp));
                    }

                    break;
            }

            // cycle A: mic push
            if (env.Mic.Channel is not null)
            {
                env.Mic.Push(Filled(MicAmp));
            }

            var status = env.Session.Status();
            CheckSend(env.Output.Pump(Period), status, CycleKind.Mic);

            // cycle B: app push
            if (env.App.Channel is not null)
            {
                env.App.Push(Filled(AppAmp));
            }

            status = env.Session.Status();
            CheckSend(env.Output.Pump(Period), status, CycleKind.App);

            // cycle C: quiet — the clip (if sending) is the only contribution
            status = env.Session.Status();
            CheckSend(env.Output.Pump(Period), status, CycleKind.Quiet);

            // monitor cycle
            var capturePushed = false;
            if (status.MonitorMode == MonitorMode.FullEndpoint && status.Switches.MonitorEnabled)
            {
                env.MonitorCapture.Push?.Invoke(Filled(CaptureAmp));
                capturePushed = true;
            }

            status = env.Session.Status();
            CheckMonitor(env.MonitorOutput.Pump(Period), status, capturePushed);
        }

        // session remains operable after the whole storm
        env.Session.UnlockEmergency();
        Accept(env.Session.EnableMic(false));
        Accept(env.Session.EnableExtras(false));
        Accept(env.Session.SetMonitor(false, MonitorMode.ClipOnly));
        var final = env.Session.Status();
        Assert.Equal(SessionSwitches.AllOff, final.Switches);
    }

    private enum CycleKind
    {
        Mic,
        App,
        Quiet,
    }

    private static void CheckSend(float[] sent, SessionStatus status, CycleKind cycle)
    {
        var sw = status.Switches;
        var gains = status.Gains;
        var micAuth = sw.MicSendEnabled;
        var appAuth = sw.AppSendEnabled && sw.ExtrasSendEnabled;
        var clipAuth = sw.ExtrasSendEnabled
            && status.Clip is not null
            && status.Clip.Mode == ClipPlaybackMode.Send
            && !status.MonitorSuspendedForPreview;
        var down = status.EmergencyLatched || status.OutputLost || !sw.AnySendEnabled;

        var clipValue = ClipAmp * gains.Clip;
        var pushedAmp = cycle switch
        {
            CycleKind.Mic => MicAmp * gains.Mic,
            CycleKind.App => AppAmp * gains.App,
            _ => 0f,
        };
        var pushedAuth = cycle switch
        {
            CycleKind.Mic => micAuth,
            CycleKind.App => appAuth,
            _ => true,
        };

        foreach (var value in sent)
        {
            Assert.False(float.IsNaN(value), "NaN sample reached the virtual output");
            Assert.True(Math.Abs(value) <= 1f + 1e-4, $"sample out of range: {value}");

            if (down)
            {
                Assert.Equal(0f, value, 6);
                continue;
            }

            // authorized subset sums for this cycle; a frame may legitimately
            // be lower (drained channels) but never hold an unauthorized tone
            var allowed = new List<float> { 0f };
            if (clipAuth)
            {
                allowed.Add(clipValue);
                if (pushedAuth)
                {
                    allowed.Add(clipValue + pushedAmp);
                }
            }

            if (pushedAuth)
            {
                allowed.Add(pushedAmp);
            }

            var match = allowed.Any(a => Math.Abs(value - a) <= Tol);
            Assert.True(match, $"frame {value:F4} matches no authorized sum [{string.Join(", ", allowed.Select(a => a.ToString("F3")))}] (cycle={cycle}, switches={sw}, clip={status.Clip?.Mode})");
        }
    }

    private static void CheckMonitor(float[] monitored, SessionStatus status, bool capturePushed)
    {
        var gains = status.Gains;
        var clipValue = ClipAmp * gains.Clip;
        var captureValue = CaptureAmp * gains.Monitor;

        foreach (var value in monitored)
        {
            Assert.False(float.IsNaN(value), "NaN sample reached the monitor output");
            Assert.True(Math.Abs(value) <= 1f + 1e-4, $"monitor sample out of range: {value}");

            if (status.EmergencyLatched || !status.Switches.MonitorEnabled)
            {
                Assert.Equal(0f, value, 6);
                continue;
            }

            var allowed = new List<float> { 0f };
            if (status.MonitorMode == MonitorMode.FullEndpoint && capturePushed)
            {
                allowed.Add(captureValue);
            }
            else if (status.Clip is not null)
            {
                allowed.Add(clipValue); // ClipOnly routing or local preview audition
            }

            var match = allowed.Any(a => Math.Abs(value - a) <= Tol);
            Assert.True(match, $"monitor frame {value:F4} matches no authorized level [{string.Join(", ", allowed.Select(a => a.ToString("F3")))}] (mode={status.MonitorMode}, suspended={status.MonitorSuspendedForPreview})");
        }
    }

    private static void Accept(CommandResult result)
    {
        Assert.True(result.Accepted, $"expected accepted, got: {result.Rejection}");
    }

    private static float[] Filled(float amplitude)
    {
        var frames = new float[Period];
        Array.Fill(frames, amplitude);
        return frames;
    }

    /// <summary>Fuzz-local session env (same fake wiring as AudioSessionTests).</summary>
    private sealed class FuzzEnv : IDisposable
    {
        public readonly AudioSession Session;
        public readonly FakeSourceAdapter Mic = new();
        public readonly FakeSourceAdapter App = new();
        public readonly FakeVirtualOutput Output = new();
        public readonly FakeMonitorOutput MonitorOutput = new();
        public readonly FakeMonitorCapture MonitorCapture = new();

        private FuzzEnv(AudioSession session) => Session = session;

        public static FuzzEnv Create()
        {
            var mic = new FakeSourceAdapter();
            var app = new FakeSourceAdapter();
            var output = new FakeVirtualOutput();
            var monitorOutput = new FakeMonitorOutput();
            var monitorCapture = new FakeMonitorCapture();
            var decoder = new FakeDecoder { Generator = _ => FakeDecoder.CreateTone(ClipAmp, 44100) };

            var session = new AudioSession(new AudioSessionOptions
            {
                ClipDecoder = decoder,
                ClipBoundaryFadeFrames = 0,
                MicAdapterFactory = (_, channel) =>
                {
                    mic.Channel = channel;
                    return mic;
                },
                AppAdapterFactory = (channel, _) =>
                {
                    app.Channel = channel;
                    return app;
                },
                OutputFactory = _ => output,
                MonitorOutputFactory = _ => monitorOutput,
                MonitorCaptureFactory = (_, _) => monitorCapture,
                InitialDevices = new PresetDeviceConfig("test-mic", "test-render", "test-cable", "test-monitor", null),
                SourcePrerollTimeoutMs = 20,
                AdaptiveRate = false,
            });
            return new FuzzEnv(session);
        }

        public static PresetConfig RandomPreset(Random rng) => new(
            "fuzz-preset",
            "fuzz",
            new PresetDeviceConfig("test-mic", "test-render", "test-cable", "test-monitor", null),
            0.5f + rng.NextSingle(),
            0.5f + rng.NextSingle(),
            0.5f + rng.NextSingle(),
            new MonitorPreferences(rng.Next(2) == 0 ? MonitorMode.ClipOnly : MonitorMode.FullEndpoint, 1f),
            [],
            false,
            null);

        public void Dispose() => Session.Dispose();
    }
}
