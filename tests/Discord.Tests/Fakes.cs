using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Brim.Core;
using Brim.Core.Plugins;
using Brim.Core.Widgets;

namespace Brim.Discord.Tests;

internal sealed class RecordingSink : INotchSink
{
    public List<ActivityParams> Activities { get; } = [];
    public List<string> Ended { get; } = [];
    public List<HudParams> Huds { get; } = [];
    public List<(string Id, TileSize Size, Widget Content)> Widgets { get; } = [];
    public List<string> Logs { get; } = [];

    public ActivityParams? Last(string id) => Activities.LastOrDefault(a => a.Id == id);

    public Widget? LastWidget(string id, TileSize size) => Widgets.LastOrDefault(w => w.Id == id && w.Size == size).Content;

    public Task SetActivityAsync(ActivityParams activity)
    {
        lock (this)
        {
            Activities.Add(activity);
        }
        return Task.CompletedTask;
    }

    public Task EndActivityAsync(string id)
    {
        lock (this)
        {
            Ended.Add(id);
        }
        return Task.CompletedTask;
    }

    public Task ShowHudAsync(HudParams hud)
    {
        Huds.Add(hud);
        return Task.CompletedTask;
    }

    public Task LogAsync(string message, string level)
    {
        Logs.Add(message);
        return Task.CompletedTask;
    }

    public Task SetWidgetAsync(string id, TileSize size, Widget content)
    {
        lock (this)
        {
            Widgets.Add((id, size, content));
        }
        return Task.CompletedTask;
    }
}

/// <summary>Sprachkanal mit festem Zustand; zählt die Knöpfe.</summary>
internal sealed class FakeVoice : IVoice
{
    public VoiceSnapshot Snapshot { get; set; } = new(VoiceStatus.Connected);
    public List<string> Calls { get; } = [];
    public VoiceConfig? Config { get; private set; }

    public event Action? Changed;

    public void Raise(VoiceSnapshot snapshot)
    {
        Snapshot = snapshot;
        Changed?.Invoke();
    }

    public void Configure(VoiceConfig config) => Config = config;

    public void Authorize() => Calls.Add("authorize");

    public Task ToggleMuteAsync()
    {
        Calls.Add("mute");
        return Task.CompletedTask;
    }

    public Task ToggleDeafAsync()
    {
        Calls.Add("deaf");
        return Task.CompletedTask;
    }

    public Task LeaveAsync()
    {
        Calls.Add("leave");
        return Task.CompletedTask;
    }

    public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Spielt den Discord-Client auf der anderen Seite der Pipe: beantwortet Handshake und Befehle wie Discord und schickt
/// auf Zuruf Ereignisse.
/// </summary>
internal sealed class FakeDiscord : IRpcTransport
{
    /// <summary>Je Verbindung neu, damit das Plugin sich nach dem Schließen wieder verbinden kann.</summary>
    private System.Threading.Channels.Channel<(RpcOp, string)> _toPlugin = System.Threading.Channels.Channel.CreateUnbounded<(RpcOp, string)>();

    public List<(string Cmd, JsonElement Args, string? Evt)> Commands { get; } = [];

    /// <summary>Close-Rahmen statt READY, z. B. <c>{"code":4000,"message":"Invalid Client ID"}</c>.</summary>
    public string? CloseOnHandshake { get; set; }

    public bool DeclineAuthorize { get; set; }

    /// <summary>Hält die Antwort auf AUTHORIZE zurück, bis der Test sie freigibt (Discord zeigt seine Frage).</summary>
    public TaskCompletionSource? AuthorizeGate { get; set; }

    public HashSet<string> ValidTokens { get; } = [];

    public JsonObject? Selected { get; set; }

    public Dictionary<string, JsonObject> Channels { get; } = [];

    public bool Mute { get; set; }

    public bool Deaf { get; set; }

    public bool Disposed { get; private set; }

    public int CountOf(string cmd, string? evt = null)
    {
        lock (Commands)
        {
            return Commands.Count(c => c.Cmd == cmd && (evt is null || c.Evt == evt));
        }
    }

    public async Task WriteAsync(RpcOp op, string json, CancellationToken cancellationToken)
    {
        if (op == RpcOp.Handshake)
        {
            if (_toPlugin.Reader.Completion.IsCompleted)
            {
                _toPlugin = System.Threading.Channels.Channel.CreateUnbounded<(RpcOp, string)>();
            }
            await _toPlugin.Writer.WriteAsync(CloseOnHandshake is { } close
                ? (RpcOp.Close, close)
                : (RpcOp.Frame, """{"cmd":"DISPATCH","evt":"READY","nonce":null,"data":{"v":1,"user":{"id":"42","username":"tobias"}}}"""), cancellationToken);
            return;
        }
        if (op != RpcOp.Frame)
        {
            return;
        }
        var message = JsonNode.Parse(json)!.AsObject();
        var cmd = (string)message["cmd"]!;
        var args = message["args"]?.DeepClone() ?? new JsonObject();
        var evt = (string?)message["evt"];
        var nonce = (string)message["nonce"]!;
        lock (Commands)
        {
            Commands.Add((cmd, JsonSerializer.SerializeToElement(args), evt));
        }
        _ = ReplyAsync(cmd, args, evt, nonce);
    }

    private async Task ReplyAsync(string cmd, JsonNode args, string? evt, string nonce)
    {
        JsonNode? data;
        (int Code, string Message)? error = null;
        switch (cmd)
        {
            case "AUTHORIZE":
                if (AuthorizeGate is { } gate)
                {
                    await gate.Task;
                }
                if (DeclineAuthorize)
                {
                    error = (5000, "OAuth2 Error: access_denied");
                }
                data = new JsonObject { ["code"] = "code-1" };
                break;
            case "AUTHENTICATE":
                var token = (string?)args["access_token"];
                if (token is null || !ValidTokens.Contains(token))
                {
                    error = (4009, "Invalid access token");
                }
                data = new JsonObject { ["user"] = new JsonObject { ["id"] = "42", ["username"] = "tobias" } };
                break;
            case "GET_VOICE_SETTINGS":
                data = Settings();
                break;
            case "SET_VOICE_SETTINGS":
                if (args["mute"] is { } mute)
                {
                    Mute = (bool)mute;
                }
                if (args["deaf"] is { } deaf)
                {
                    Deaf = (bool)deaf;
                }
                data = Settings();
                break;
            case "GET_SELECTED_VOICE_CHANNEL":
                data = Selected?.DeepClone();
                break;
            case "GET_CHANNEL":
                data = Channels.GetValueOrDefault((string)args["channel_id"]!)?.DeepClone();
                break;
            case "GET_GUILD":
                data = new JsonObject { ["id"] = (string?)args["guild_id"], ["name"] = "Freunde" };
                break;
            case "SELECT_VOICE_CHANNEL":
                Selected = null;
                data = null;
                await Push("VOICE_CHANNEL_SELECT", new JsonObject { ["channel_id"] = null, ["guild_id"] = null });
                break;
            default:
                data = new JsonObject { ["evt"] = evt };
                break;
        }
        var reply = new JsonObject
        {
            ["cmd"] = cmd,
            ["evt"] = error is null ? null : "ERROR",
            ["nonce"] = nonce,
            ["data"] = error is { } e ? new JsonObject { ["code"] = e.Code, ["message"] = e.Message } : data,
        };
        await _toPlugin.Writer.WriteAsync((RpcOp.Frame, reply.ToJsonString()));
    }

    private JsonObject Settings() => new() { ["mute"] = Mute, ["deaf"] = Deaf, ["input"] = new JsonObject { ["volume"] = 100 } };

    public async Task Push(string evt, JsonNode? data) =>
        await _toPlugin.Writer.WriteAsync((RpcOp.Frame, new JsonObject { ["cmd"] = "DISPATCH", ["evt"] = evt, ["nonce"] = null, ["data"] = data }.ToJsonString()));

    /// <summary>Discord wird beendet.</summary>
    public void Quit() => _toPlugin.Writer.TryComplete();

    public async Task<(RpcOp Op, string Json)> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _toPlugin.Reader.ReadAsync(cancellationToken);
        }
        catch (ChannelClosedException)
        {
            throw new EndOfStreamException();
        }
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _toPlugin.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    public static JsonObject Channel(string id, string name, params JsonObject[] states) => new()
    {
        ["id"] = id,
        ["name"] = name,
        ["guild_id"] = "g1",
        ["type"] = 2,
        ["voice_states"] = new JsonArray(states.Select(s => (JsonNode)s).ToArray()),
    };

    public static JsonObject State(string id, string username, string? nick = null, bool selfMute = false, bool selfDeaf = false) => new()
    {
        ["nick"] = nick,
        ["mute"] = false,
        ["volume"] = 100,
        ["voice_state"] = new JsonObject { ["mute"] = false, ["deaf"] = false, ["self_mute"] = selfMute, ["self_deaf"] = selfDeaf, ["suppress"] = false },
        ["user"] = new JsonObject { ["id"] = id, ["username"] = username, ["global_name"] = null, ["avatar"] = null },
    };
}

internal sealed class FakeOAuth : IOAuth
{
    public List<string> Calls { get; } = [];
    public Func<string, OAuthToken>? OnExchange { get; set; }
    public OAuthException? RefreshFailure { get; set; }

    public Task<OAuthToken> ExchangeAsync(string clientId, string clientSecret, string code, CancellationToken cancellationToken)
    {
        Calls.Add($"exchange:{clientId}:{code}");
        return Task.FromResult(OnExchange?.Invoke(code) ?? new OAuthToken("access-" + code, "refresh-" + code, Test.Now.AddDays(7)));
    }

    public Task<OAuthToken> RefreshAsync(string clientId, string clientSecret, string refreshToken, CancellationToken cancellationToken)
    {
        Calls.Add("refresh:" + refreshToken);
        return RefreshFailure is { } failure
            ? Task.FromException<OAuthToken>(failure)
            : Task.FromResult(new OAuthToken("access-renewed", "refresh-renewed", Test.Now.AddDays(7)));
    }
}

internal sealed class MemoryTokenStore : ITokenStore
{
    public Dictionary<string, OAuthToken> Tokens { get; } = [];

    public OAuthToken? Load(string clientId) => Tokens.GetValueOrDefault(clientId);

    public void Save(string clientId, OAuthToken token) => Tokens[clientId] = token;

    public void Clear() => Tokens.Clear();
}

internal static class Test
{
    /// <summary>Dienstag, 27.10.2026, 14:30.</summary>
    public static readonly DateTimeOffset Now = new(2026, 10, 27, 14, 30, 0, TimeSpan.FromHours(1));

    public static void Setup() => CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

    private static readonly JsonSerializerOptions Readable = new(PluginJson.Options) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Lesbar statt · usw., damit die Tests nach Text suchen können.</summary>
    public static string Json(object value) => JsonSerializer.Serialize(value, value.GetType(), Readable);

    /// <summary>Wartet, bis <paramref name="condition"/> stimmt (der Lesefaden läuft nebenher).</summary>
    public static async Task Until(Func<bool> condition, string what)
    {
        for (var i = 0; i < 500; i++)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(10);
        }
        Assert.Fail("Nicht eingetreten: " + what);
    }
}
