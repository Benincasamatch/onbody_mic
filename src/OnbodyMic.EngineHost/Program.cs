namespace OnbodyMic.EngineHost;

using System.Text;
using System.Text.Json;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        string root;
        try
        {
            if (args.Length != 2 || args[0] != "--root" || string.IsNullOrWhiteSpace(args[1]) || !Path.IsPathFullyQualified(args[1]))
                throw new ArgumentException("usage: OnbodyMic.EngineHost --root <absolute data directory>");
            root = Path.GetFullPath(args[1]);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 2;
        }

        Console.OutputEncoding = new UTF8Encoding(false);
        using var reading = new CancellationTokenSource();
        var writer = new NdjsonWriter(Console.OpenStandardOutput());
        var runtime = new EngineRuntime(root, writer);
        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: true, bufferSize: 16384, leaveOpen: true);
        var responseGate = new object();
        var responses = new HashSet<Task>();
        var exitCode = 0;
        ConsoleCancelEventHandler cancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            reading.Cancel();
        };
        Console.CancelKeyPress += cancel;
        writer.Faulted += _ =>
        {
            Interlocked.Exchange(ref exitCode, 1);
            reading.Cancel();
        };

        try
        {
            while (!reading.IsCancellationRequested)
            {
                var line = await input.ReadLineAsync(reading.Token).ConfigureAwait(false);
                if (line is null) break;
                ulong id = 0;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var request = document.RootElement;
                    if (request.ValueKind != JsonValueKind.Object)
                        throw HostCommandException.Invalid("request must be an object");
                    if (!request.TryGetProperty("id", out var requestId) || requestId.ValueKind != JsonValueKind.Number
                        || !requestId.TryGetUInt64(out id) || id == 0)
                        throw HostCommandException.Invalid("request id must be a positive integer");
                    var operation = CommandArguments.String(request, "op");
                    var arguments = CommandArguments.Required(request, "args");
                    if (arguments.ValueKind != JsonValueKind.Object)
                        throw HostCommandException.Invalid("request args must be an object");
                    // Only clone the argument object: queued work must not retain a disposed JsonDocument.
                    var execution = runtime.SubmitAsync(operation, arguments.Clone());
                    var response = RespondAsync(id, execution, writer, reading);
                    lock (responseGate) responses.Add(response);
                    _ = response.ContinueWith(completed =>
                    {
                        lock (responseGate) responses.Remove(completed);
                    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    if (operation == "exit")
                    {
                        await response.ConfigureAwait(false);
                        break;
                    }
                }
                catch (Exception ex) when (ex is HostCommandException or JsonException)
                {
                    if (id > 0) await writer.ErrorAsync(id, ex).ConfigureAwait(false);
                    else writer.Event("notice", new { kind = "ProtocolError", message = ex.Message });
                }
            }
        }
        catch (OperationCanceledException) when (reading.IsCancellationRequested) { }
        catch (Exception ex)
        {
            exitCode = 1;
            await Console.Error.WriteLineAsync($"engine request reader failed: {ex.Message}").ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
            // EOF, cancellation and exit all stop the graph before joining long decode/import requests.
            try { await runtime.StopAsync().ConfigureAwait(false); }
            catch (Exception ex)
            {
                exitCode = 1;
                await Console.Error.WriteLineAsync($"engine shutdown failed: {ex.Message}").ConfigureAwait(false);
            }
            Task[] pending;
            lock (responseGate) pending = responses.ToArray();
            await Task.WhenAll(pending).ConfigureAwait(false);
            try { await writer.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex)
            {
                exitCode = 1;
                await Console.Error.WriteLineAsync($"engine stdout failed: {ex.Message}").ConfigureAwait(false);
            }
        }
        return exitCode;
    }

    private static async Task RespondAsync(ulong id, Task<object?> execution, NdjsonWriter writer, CancellationTokenSource reading)
    {
        try
        {
            object? result;
            try { result = await execution.ConfigureAwait(false); }
            catch (Exception ex)
            {
                await writer.ErrorAsync(id, ex).ConfigureAwait(false);
                return;
            }
            await writer.ResponseAsync(id, result).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            reading.Cancel();
            await Console.Error.WriteLineAsync($"engine response could not be written: {ex.Message}").ConfigureAwait(false);
        }
    }
}
