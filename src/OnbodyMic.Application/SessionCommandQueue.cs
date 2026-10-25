namespace OnbodyMic.Application;

/// <summary>
/// Serialized session command processor (ISessionController role from docs/06 §6).
/// Urgent lane: emergency/safety commands jump ahead of queued playback traffic.
/// </summary>
public sealed class SessionCommandQueue : IDisposable
{
    private readonly record struct Command(string Name, Func<object?> Execute, TaskCompletionSource<object?>? Waiter);

    private readonly Queue<Command> _urgent = new();
    private readonly Queue<Command> _normal = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cancel = new();
    private bool _disposed;

    public SessionCommandQueue()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "session-commands" };
        _thread.Start();
    }

    /// <summary>Enqueues and blocks until the command ran on the session thread.</summary>
    public CommandResult Submit(string name, Func<CommandResult> execute, bool urgent = false)
        => SubmitValue(name, execute, urgent);

    /// <summary>Enqueues a value-returning command and blocks for the result.</summary>
    public T SubmitValue<T>(string name, Func<T> execute, bool urgent = false)
    {
        var waiter = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(name, () => execute(), waiter, urgent);
        return (T)waiter.Task.GetAwaiter().GetResult()!;
    }

    /// <summary>Enqueues without waiting (event/fault callbacks).</summary>
    public void Post(string name, Action execute, bool urgent = false)
        => Enqueue(name, () => { execute(); return null; }, null, urgent);

    private void Enqueue(string name, Func<object?> execute, TaskCompletionSource<object?>? waiter, bool urgent)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            (urgent ? _urgent : _normal).Enqueue(new Command(name, execute, waiter));
        }

        _signal.Release();
    }

    private void Run()
    {
        while (true)
        {
            try
            {
                _signal.Wait(_cancel.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            Command? next = null;
            lock (_gate)
            {
                if (_urgent.Count > 0)
                {
                    next = _urgent.Dequeue();
                }
                else if (_normal.Count > 0)
                {
                    next = _normal.Dequeue();
                }
            }

            if (next is not { } command)
            {
                continue;
            }

            object? result;
            try
            {
                result = command.Execute();
            }
            catch (Exception ex)
            {
                result = CommandResult.Reject(ex.Message);
            }

            command.Waiter?.TrySetResult(result ?? CommandResult.Ok);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _cancel.Cancel();
        _thread.Join(1000);
        TaskCompletionSource<object?>[] waiters;
        lock (_gate)
        {
            waiters = _urgent.Concat(_normal)
                .Where(c => c.Waiter is not null)
                .Select(c => c.Waiter!)
                .ToArray();
        }

        foreach (var waiter in waiters)
        {
            waiter.TrySetResult(CommandResult.Ok);
        }
    }
}
