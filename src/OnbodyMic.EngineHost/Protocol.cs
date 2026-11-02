namespace OnbodyMic.EngineHost;

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using OnbodyMic.Domain;

internal sealed class HostCommandException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
    public static HostCommandException Invalid(string message) => new("invalid-argument", message);
    public static HostCommandException Rejected(string message) => new("rejected", message);
}

internal static class CommandArguments
{
    public static JsonElement Required(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var value))
            throw HostCommandException.Invalid($"missing argument: {name}");
        return value;
    }

    public static string String(JsonElement args, string name)
    {
        var value = Required(args, name);
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw HostCommandException.Invalid($"{name} must be a non-empty string");
        return value.GetString()!;
    }

    public static string? OptionalString(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw HostCommandException.Invalid($"{name} must be a string or null");
        return value.GetString();
    }

    public static bool Boolean(JsonElement args, string name)
    {
        var value = Required(args, name);
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw HostCommandException.Invalid($"{name} must be a boolean");
        return value.GetBoolean();
    }

    public static bool OptionalBoolean(JsonElement args, string name, bool defaultValue = false) =>
        args.TryGetProperty(name, out _) ? Boolean(args, name) : defaultValue;

    public static float Gain(JsonElement args, string name)
    {
        var value = Required(args, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetSingle(out var gain))
            throw HostCommandException.Invalid($"{name} must be a finite number in 0..4");
        ValidateGain(gain, name);
        return gain;
    }

    public static void ValidateGain(float gain, string name)
    {
        if (!PresetConfigRules.IsValidGain(gain, PresetConfig.GainLimits))
            throw HostCommandException.Invalid($"{name} must be a finite number in 0..4");
    }

    public static uint ProcessId(JsonElement args)
    {
        var value = Required(args, "processId");
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetUInt32(out var id) || id == 0 || id > int.MaxValue)
            throw HostCommandException.Invalid("processId must identify a live process");
        return id;
    }

    public static T Object<T>(JsonElement args, string name) where T : class
    {
        try
        {
            return Required(args, name).Deserialize<T>(NdjsonWriter.JsonOptions)
                ?? throw HostCommandException.Invalid($"{name} must not be null");
        }
        catch (JsonException ex) { throw HostCommandException.Invalid($"invalid {name}: {ex.Message}"); }
    }

    public static MonitorMode MonitorMode(string? mode) => mode switch
    {
        "ClipOnly" => OnbodyMic.Domain.MonitorMode.ClipOnly,
        "FullEndpoint" => OnbodyMic.Domain.MonitorMode.FullEndpoint,
        _ => throw HostCommandException.Invalid("monitorMode must be ClipOnly or FullEndpoint"),
    };
}

internal sealed class NdjsonWriter : IAsyncDisposable
{
    private sealed record PendingLine(object Message, TaskCompletionSource? Flushed);
    private readonly Channel<PendingLine> _lines = Channel.CreateUnbounded<PendingLine>(new UnboundedChannelOptions
    {
        SingleReader = true,
        AllowSynchronousContinuations = false,
    });
    private readonly StreamWriter _stream;
    private readonly Task _pump;
    private Exception? _failure;

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    public event Action<Exception>? Faulted;

    public NdjsonWriter(Stream stdout)
    {
        _stream = new StreamWriter(stdout, new UTF8Encoding(false), 16384, leaveOpen: true) { NewLine = "\n" };
        _pump = Task.Run(PumpAsync);
    }

    public void Event(string name, object payload)
    {
        _lines.Writer.TryWrite(new PendingLine(new { @event = name, payload }, null));
    }

    public Task ResponseAsync(ulong id, object? result) => SendAsync(new { id, ok = true, result });

    public Task ErrorAsync(ulong id, Exception error)
    {
        var code = error switch
        {
            HostCommandException command => command.Code,
            ArgumentException or JsonException => "invalid-argument",
            OperationCanceledException => "rejected",
            _ => "backend-error",
        };
        return SendAsync(new { id, ok = false, error = new { code, message = error.Message } });
    }

    private Task SendAsync(object message)
    {
        var flushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_lines.Writer.TryWrite(new PendingLine(message, flushed)))
            flushed.TrySetException(_failure ?? new IOException("engine stdout is closed"));
        return flushed.Task;
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var line in _lines.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    await _stream.WriteLineAsync(JsonSerializer.Serialize(line.Message, JsonOptions)).ConfigureAwait(false);
                    await _stream.FlushAsync().ConfigureAwait(false);
                    line.Flushed?.TrySetResult();
                }
                catch (Exception ex)
                {
                    line.Flushed?.TrySetException(ex);
                    throw;
                }
            }
        }
        catch (Exception ex)
        {
            _failure = ex;
            _lines.Writer.TryComplete(ex);
            while (_lines.Reader.TryRead(out var line)) line.Flushed?.TrySetException(ex);
            Faulted?.Invoke(ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lines.Writer.TryComplete();
        await _pump.ConfigureAwait(false);
        await _stream.DisposeAsync().ConfigureAwait(false);
    }
}
