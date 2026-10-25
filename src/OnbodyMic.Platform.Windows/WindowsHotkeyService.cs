namespace OnbodyMic.Platform.Windows;

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using OnbodyMic.Application;
using OnbodyMic.Domain;

/// <summary>
/// Global hotkey backend built on RegisterHotKey + a dedicated message-pump
/// thread (no window; hotkeys are posted to the thread queue, control
/// requests via PostThreadMessage). RegisterHotKey fires once per physical
/// press — the OS does not auto-repeat it — which satisfies the hold-dedup
/// rule (docs/01 §5.4) structurally. No injection: it never sends keys to
/// games or bypasses anti-cheat.
/// </summary>
public sealed class WindowsHotkeyService : IHotkeyService
{
    private const int WmHotkey = 0x0312;
    private const int WmAppWake = 0x8000;
    private const int WmQuit = 0x0012;

    private readonly object _gate = new();
    private readonly List<Registration> _registrations = [];
    private readonly ConcurrentQueue<PendingRequest> _requests = new();
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private uint _threadId;
    private int _nextHotkeyId = 1;
    private volatile bool _disposed;

    public event Action<HotkeyTrigger>? Triggered;

    public WindowsHotkeyService()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "hotkeys" };
        _thread.Start();
        _ready.Wait(3000);
    }

    public IReadOnlyList<HotkeyBinding> Active
    {
        get
        {
            lock (_gate)
            {
                return _registrations.Select(r => r.Binding).ToArray();
            }
        }
    }

    public CommandResult Register(HotkeyBinding binding)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Submit(new RegisterRequest(binding));
    }

    public CommandResult Unregister(HotkeyAction action, string? targetId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Submit(new UnregisterRequest(action, targetId));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(2000);
        _ready.Dispose();
    }

    private CommandResult Submit(PendingRequest request)
    {
        _requests.Enqueue(request);
        PostThreadMessage(_threadId, WmAppWake, IntPtr.Zero, IntPtr.Zero);
        return request.Result.Task.GetAwaiter().GetResult();
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        _ready.Set();
        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            switch (message.message)
            {
                case WmHotkey:
                    HandleHotkey((int)message.wParam);
                    break;
                case WmAppWake:
                    DrainRequests();
                    break;
            }
        }

        // RegisterHotKey/UnregisterHotKey are thread-affine; release them here
        lock (_gate)
        {
            foreach (var registration in _registrations)
            {
                _ = UnregisterHotKey(IntPtr.Zero, registration.Id);
            }

            _registrations.Clear();
        }
    }

    private void DrainRequests()
    {
        while (_requests.TryDequeue(out var request))
        {
            try
            {
                var result = request switch
                {
                    RegisterRequest register => DoRegister(register.Binding),
                    UnregisterRequest unregister => DoUnregister(unregister.Action, unregister.TargetId),
                    _ => CommandResult.Ok,
                };
                request.Result.TrySetResult(result);
            }
            catch (Exception ex)
            {
                request.Result.TrySetResult(CommandResult.Reject($"hotkey-thread-fault: {ex.Message}"));
            }
        }
    }

    private CommandResult DoRegister(HotkeyBinding binding)
    {
        lock (_gate)
        {
            var conflict = _registrations.FirstOrDefault(r =>
                r.Binding.Gesture == binding.Gesture && !r.Binding.Matches(binding.Action, binding.TargetId));
            if (conflict is not null)
            {
                return CommandResult.Reject($"hotkey-conflict:{conflict.Binding.Describe()}");
            }

            var id = _nextHotkeyId++;
            if (!RegisterHotKey(IntPtr.Zero, id, (uint)binding.Gesture.Modifiers, (uint)binding.Gesture.Key))
            {
                var error = Marshal.GetLastWin32Error();
                return CommandResult.Reject($"register-failed:0x{error:X}({error})");
            }

            // swap atomically after the OS accepted the new registration so a
            // failed rebind leaves the old binding active (docs/06 §10.8)
            var previous = _registrations.FirstOrDefault(r => r.Binding.Matches(binding.Action, binding.TargetId));
            if (previous is not null)
            {
                _ = UnregisterHotKey(IntPtr.Zero, previous.Id);
                _registrations.Remove(previous);
            }

            _registrations.Add(new Registration(id, binding));
            return CommandResult.Ok;
        }
    }

    private CommandResult DoUnregister(HotkeyAction action, string? targetId)
    {
        lock (_gate)
        {
            var registration = _registrations.FirstOrDefault(r => r.Binding.Matches(action, targetId));
            if (registration is null)
            {
                return CommandResult.Ok;
            }

            _ = UnregisterHotKey(IntPtr.Zero, registration.Id);
            _registrations.Remove(registration);
            return CommandResult.Ok;
        }
    }

    private void HandleHotkey(int hotkeyId)
    {
        HotkeyBinding? binding;
        lock (_gate)
        {
            binding = _registrations.FirstOrDefault(r => r.Id == hotkeyId)?.Binding;
        }

        if (binding is not null)
        {
            Triggered?.Invoke(new HotkeyTrigger(binding, Environment.TickCount64));
        }
    }

    private sealed record Registration(int Id, HotkeyBinding Binding);

    private abstract class PendingRequest
    {
        public TaskCompletionSource<CommandResult> Result { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class RegisterRequest(HotkeyBinding binding) : PendingRequest
    {
        public HotkeyBinding Binding { get; } = binding;
    }

    private sealed class UnregisterRequest(HotkeyAction action, string? targetId) : PendingRequest
    {
        public HotkeyAction Action { get; } = action;

        public string? TargetId { get; } = targetId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public long ptX;
        public long ptY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(out NativeMessage message, IntPtr hWnd, uint min, uint max);
}
