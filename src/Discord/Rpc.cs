using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Brim.Discord;

/// <summary>Rahmenarten der lokalen RPC-Schnittstelle von Discord.</summary>
public enum RpcOp
{
    Handshake = 0,
    Frame = 1,
    Close = 2,
    Ping = 3,
    Pong = 4,
}

/// <summary>Ein Rahmen hin oder zurück: Art und JSON.</summary>
public interface IRpcTransport : IAsyncDisposable
{
    Task WriteAsync(RpcOp op, string json, CancellationToken cancellationToken);

    /// <summary>Nächster Rahmen; <see cref="EndOfStreamException"/>, wenn Discord die Verbindung schließt.</summary>
    Task<(RpcOp Op, string Json)> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Rahmen über einen Datenstrom: 4 Byte Art und 4 Byte Länge (Little Endian), danach das JSON in UTF-8. So spricht der
/// Discord-Client über die Pipe <c>\\.\pipe\discord-ipc-0</c>.
/// </summary>
public sealed class StreamTransport(Stream stream) : IRpcTransport
{
    /// <summary>Größer schickt Discord nichts; alles darüber ist kaputt.</summary>
    private const int MaxFrame = 4 * 1024 * 1024;

    private readonly SemaphoreSlim _write = new(1, 1);

    public async Task WriteAsync(RpcOp op, string json, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var frame = new byte[8 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, (int)op);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), body.Length);
        body.CopyTo(frame, 8);
        await _write.WaitAsync(cancellationToken);
        try
        {
            await stream.WriteAsync(frame, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        finally
        {
            _write.Release();
        }
    }

    public async Task<(RpcOp Op, string Json)> ReadAsync(CancellationToken cancellationToken)
    {
        var header = new byte[8];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var op = (RpcOp)BinaryPrimitives.ReadInt32LittleEndian(header);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (length is < 0 or > MaxFrame)
        {
            throw new InvalidDataException($"Rahmen mit {length} Byte von Discord");
        }
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, cancellationToken);
        return (op, Encoding.UTF8.GetString(body));
    }

    public ValueTask DisposeAsync() => stream.DisposeAsync();
}

public static class DiscordPipes
{
    /// <summary>
    /// Verbindet sich mit dem laufenden Discord-Client. Er lauscht auf der ersten freien Pipe von <c>discord-ipc-0</c>
    /// bis <c>discord-ipc-9</c> (mehrere bei Discord, PTB und Canary nebeneinander). <c>null</c>, wenn keiner läuft.
    /// </summary>
    public static async Task<IRpcTransport?> ConnectAsync(CancellationToken cancellationToken)
    {
        for (var i = 0; i < 10; i++)
        {
            var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(200, cancellationToken);
                return new StreamTransport(pipe);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
            {
                await pipe.DisposeAsync();
            }
        }
        return null;
    }
}

/// <summary>Fehler, den Discord gemeldet hat (<c>evt: ERROR</c> oder ein Close-Rahmen), mit Discords Code.</summary>
public sealed class DiscordRpcException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;

    /// <summary>Close-Code für eine unbekannte Client-ID.</summary>
    public const int InvalidClientId = 4000;
}

/// <summary>
/// Eine Verbindung zum Discord-Client: Handshake, Befehle mit <c>nonce</c> und deren Antworten, Ereignisse
/// (<c>DISPATCH</c>) der Reihe nach in <see cref="Events"/>. Liest in einer eigenen Schleife, damit Befehle und Ereignisse
/// sich nicht gegenseitig blockieren.
/// </summary>
public sealed class DiscordRpc : IAsyncDisposable
{
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);

    private readonly IRpcTransport _transport;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly Channel<(string Evt, JsonElement Data)> _events = Channel.CreateUnbounded<(string, JsonElement)>(new() { SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private Task _reader = Task.CompletedTask;

    private DiscordRpc(IRpcTransport transport) => _transport = transport;

    /// <summary>Wer im Discord-Client angemeldet ist (aus <c>READY</c>).</summary>
    public JsonElement User { get; private set; }

    /// <summary>Ereignisse aus Abonnements; endet, wenn die Verbindung abbricht.</summary>
    public ChannelReader<(string Evt, JsonElement Data)> Events => _events.Reader;

    /// <summary>Endet, wenn die Verbindung abbricht.</summary>
    public Task Completion => _reader;

    /// <summary>Handshake mit der Client-ID der Discord-App; wartet auf <c>READY</c>.</summary>
    public static async Task<DiscordRpc> ConnectAsync(IRpcTransport transport, string clientId, CancellationToken cancellationToken)
    {
        var rpc = new DiscordRpc(transport);
        await transport.WriteAsync(RpcOp.Handshake, JsonSerializer.Serialize(new { v = 1, client_id = clientId }), cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        while (true)
        {
            var (op, json) = await transport.ReadAsync(timeout.Token);
            if (op == RpcOp.Close)
            {
                throw Closed(json);
            }
            if (op == RpcOp.Ping)
            {
                await transport.WriteAsync(RpcOp.Pong, json, cancellationToken);
                continue;
            }
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (Str(root, "evt") == "READY")
            {
                rpc.User = root.TryGetProperty("data", out var data) && data.TryGetProperty("user", out var user) ? user.Clone() : default;
                break;
            }
            if (Str(root, "evt") == "ERROR")
            {
                throw Error(root);
            }
        }
        rpc._reader = Task.Run(() => rpc.ReadLoopAsync(rpc._stop.Token));
        return rpc;
    }

    /// <summary>Schickt einen Befehl und wartet auf Discords Antwort (<c>data</c>).</summary>
    public async Task<JsonElement> CommandAsync(string cmd, object? args = null, string? evt = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var nonce = Guid.NewGuid().ToString();
        var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[nonce] = reply;
        try
        {
            if (_reader.IsCompleted)
            {
                throw new IOException("Discord ist nicht mehr verbunden");
            }
            var message = new Dictionary<string, object?> { ["cmd"] = cmd, ["args"] = args ?? new { }, ["nonce"] = nonce };
            if (evt is not null)
            {
                message["evt"] = evt;
            }
            await _transport.WriteAsync(RpcOp.Frame, JsonSerializer.Serialize(message), cancellationToken);
            return await reply.Task.WaitAsync(timeout ?? CommandTimeout, cancellationToken);
        }
        finally
        {
            _pending.TryRemove(nonce, out _);
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        Exception end = new IOException("Discord wurde beendet");
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var (op, json) = await _transport.ReadAsync(cancellationToken);
                switch (op)
                {
                    case RpcOp.Ping:
                        await _transport.WriteAsync(RpcOp.Pong, json, cancellationToken);
                        break;
                    case RpcOp.Close:
                        end = Closed(json);
                        return;
                    case RpcOp.Frame:
                        Dispatch(json);
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or OperationCanceledException or ObjectDisposedException or JsonException or InvalidDataException)
        {
            end = ex is EndOfStreamException ? end : ex;
        }
        finally
        {
            foreach (var pending in _pending.Values)
            {
                pending.TrySetException(end);
            }
            _events.Writer.TryComplete();
        }
    }

    private void Dispatch(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var data = root.TryGetProperty("data", out var d) ? d.Clone() : default;
        if (Str(root, "nonce") is { } nonce && _pending.TryGetValue(nonce, out var reply))
        {
            if (Str(root, "evt") == "ERROR")
            {
                reply.TrySetException(Error(root));
            }
            else
            {
                reply.TrySetResult(data);
            }
            return;
        }
        if (Str(root, "cmd") == "DISPATCH" && Str(root, "evt") is { } evt)
        {
            _events.Writer.TryWrite((evt, data));
        }
    }

    private static DiscordRpcException Error(JsonElement root) => FromData(root.TryGetProperty("data", out var data) ? data : default);

    /// <summary>Discords Fehler stehen als <c>{ "code", "message" }</c> in <c>data</c> oder direkt im Close-Rahmen.</summary>
    private static DiscordRpcException FromData(JsonElement data)
    {
        var code = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("code", out var c) && c.TryGetInt32(out var n) ? n : 0;
        return new DiscordRpcException(code, Str(data, "message") ?? "Fehler von Discord");
    }

    private static DiscordRpcException Closed(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return FromData(doc.RootElement);
        }
        catch (JsonException)
        {
            return new DiscordRpcException(0, "Discord hat die Verbindung geschlossen");
        }
    }

    internal static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await _transport.DisposeAsync();
        try
        {
            await _reader;
        }
        catch (Exception)
        {
            // Endet beim Schließen ohnehin mit einem Fehler.
        }
        _stop.Dispose();
    }
}
