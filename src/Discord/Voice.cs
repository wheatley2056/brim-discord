using System.Text.Json;

namespace Brim.Discord;

/// <summary>Der Sprachkanal, wie das Plugin ihn sieht. Getrennt von <see cref="VoiceClient"/>, damit Tests ihn nachbauen können.</summary>
public interface IVoice
{
    VoiceSnapshot Snapshot { get; }

    /// <summary>Etwas am Sprachkanal hat sich geändert; kommt aus einem beliebigen Thread.</summary>
    event Action? Changed;

    /// <summary>Neue Einstellungen; eine andere App oder ein anderes Secret verbindet neu.</summary>
    void Configure(VoiceConfig config);

    /// <summary>Fragt Discord (noch einmal), ob Brim zugreifen darf.</summary>
    void Authorize();

    Task ToggleMuteAsync();

    Task ToggleDeafAsync();

    /// <summary>Verlässt den Sprachkanal.</summary>
    Task LeaveAsync();

    Task RunAsync(CancellationToken cancellationToken);
}

public sealed record VoiceConfig(bool Enabled, string ClientId, string ClientSecret)
{
    public bool Configured => ClientId.Length > 0 && ClientSecret.Length > 0;
}

/// <summary>
/// Spricht mit dem Discord-Client über seine lokale RPC-Schnittstelle: verbindet sich, sobald Discord läuft, holt sich
/// beim ersten Mal die Erlaubnis (Discord fragt in einem eigenen Fenster), abonniert den Sprachkanal, in dem du gerade
/// bist, und verfolgt, wer drin ist, wer spricht und ob du stumm oder taub bist.
/// </summary>
/// <remarks>
/// Die RPC-Rechte (<c>rpc</c>, <c>rpc.voice.*</c>) gibt Discord nur Apps, die es freigeschaltet hat, oder ihrem
/// Besitzer und eingetragenen Testern. Deshalb legt jeder Nutzer seine eigene Discord-App an (README).
/// </remarks>
public sealed class VoiceClient(
    Func<CancellationToken, Task<IRpcTransport?>> connect,
    IOAuth oauth,
    ITokenStore tokens,
    TimeProvider time,
    Action<string, string> log) : IVoice
{
    /// <summary>So lange wartet das Plugin, bevor es wieder nach Discord sucht.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);

    /// <summary>So lange hat man, um in Discord „Autorisieren“ zu klicken.</summary>
    public static readonly TimeSpan AuthorizeTimeout = TimeSpan.FromMinutes(3);

    public static readonly string[] Scopes = ["rpc", "rpc.voice.read", "rpc.voice.write"];

    /// <summary>Ereignisse eines Sprachkanals, die das Plugin abonniert.</summary>
    private static readonly string[] ChannelEvents = ["VOICE_STATE_CREATE", "VOICE_STATE_UPDATE", "VOICE_STATE_DELETE", "SPEAKING_START", "SPEAKING_STOP"];

    private readonly Lock _lock = new();
    private readonly Dictionary<string, VoiceMember> _members = [];
    private readonly HashSet<string> _speaking = [];
    private readonly Dictionary<string, string> _guilds = [];
    private VoiceConfig _config = new(false, "", "");
    private CancellationTokenSource? _session;
    private TaskCompletionSource _authorizeRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile DiscordRpc? _rpc;
    private VoiceStatus _status = VoiceStatus.Off;
    private string? _error;
    private (string Id, string Name, string? GuildId)? _channel;
    private string? _selfId;
    private bool _selfMuted;
    private bool _selfDeafened;

    /// <summary>Discord hat die Frage abgelehnt oder sie lief ab: nicht von selbst noch einmal fragen.</summary>
    private bool _declined;

    public event Action? Changed;

    public VoiceSnapshot Snapshot
    {
        get
        {
            lock (_lock)
            {
                VoiceChannel? channel = null;
                if (_channel is { } c)
                {
                    var members = _members.Values
                        .Select(m => m with { Speaking = _speaking.Contains(m.Id), Self = m.Id == _selfId })
                        .OrderByDescending(m => m.Speaking)
                        .ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)
                        .ToList();
                    channel = new VoiceChannel(c.Id, c.Name, c.GuildId is { } g ? _guilds.GetValueOrDefault(g) : null, members);
                }
                return new VoiceSnapshot(_status, channel, _selfMuted, _selfDeafened, _error);
            }
        }
    }

    public void Configure(VoiceConfig config)
    {
        lock (_lock)
        {
            if (config == _config)
            {
                return;
            }
            if (config.ClientId != _config.ClientId || config.ClientSecret != _config.ClientSecret)
            {
                _declined = false;
            }
            _config = config;
            Restart();
        }
    }

    public void Authorize()
    {
        lock (_lock)
        {
            _declined = false;
            _authorizeRequested.TrySetResult();
            if (_status is VoiceStatus.Error or VoiceStatus.DiscordClosed)
            {
                // Sofort neu versuchen statt erst nach der Pause.
                Restart();
            }
        }
    }

    /// <summary>Beendet die laufende Verbindung oder Pause; <see cref="RunAsync"/> fängt mit den aktuellen Einstellungen neu an.</summary>
    private void Restart()
    {
        try
        {
            _session?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public Task ToggleMuteAsync() => SetVoiceSettingsAsync(() => new { mute = !_selfMuted });

    public Task ToggleDeafAsync() => SetVoiceSettingsAsync(() => new { deaf = !_selfDeafened });

    public async Task LeaveAsync()
    {
        if (_rpc is { } rpc)
        {
            await TryAsync(() => rpc.CommandAsync("SELECT_VOICE_CHANNEL", new { channel_id = (string?)null }));
        }
    }

    private async Task SetVoiceSettingsAsync(Func<object> args)
    {
        if (_rpc is not { } rpc)
        {
            return;
        }
        object value;
        lock (_lock)
        {
            value = args();
        }
        var settings = await TryAsync(() => rpc.CommandAsync("SET_VOICE_SETTINGS", value));
        if (settings is { } s)
        {
            ApplyVoiceSettings(s);
        }
    }

    private async Task<JsonElement?> TryAsync(Func<Task<JsonElement>> command)
    {
        try
        {
            return await command();
        }
        catch (Exception ex) when (ex is DiscordRpcException or IOException or TimeoutException)
        {
            log($"Discord: {ex.Message}", "warn");
            return null;
        }
    }

    // ───────── Verbindung ─────────

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            VoiceConfig config;
            using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (_lock)
            {
                _session = session;
                config = _config;
            }
            try
            {
                if (!config.Enabled || !config.Configured)
                {
                    Set(config.Enabled ? VoiceStatus.NotConfigured : VoiceStatus.Off);
                    await Task.Delay(Timeout.InfiniteTimeSpan, session.Token);
                    continue;
                }
                await SessionAsync(config, session.Token);
                Set(VoiceStatus.DiscordClosed);
            }
            catch (OperationCanceledException) when (session.IsCancellationRequested)
            {
                // Neue Einstellungen, „Verbinden“ oder Ende: gleich weiter.
                Leave();
                continue;
            }
            catch (DiscordRpcException ex) when (ex.Code == DiscordRpcException.InvalidClientId)
            {
                Set(VoiceStatus.Error, "Discord kennt diese Client-ID nicht");
            }
            catch (OAuthException ex)
            {
                tokens.Clear();
                lock (_lock)
                {
                    // Nicht alle 10 Sekunden wieder fragen, erst nach „Verbinden“ oder neuen Einstellungen.
                    _declined = true;
                }
                Set(VoiceStatus.Error, ex.Message);
            }
            catch (Exception ex) when (ex is DiscordRpcException or IOException or TimeoutException or JsonException or HttpRequestException or InvalidOperationException)
            {
                log($"Discord: {ex.Message}", "warn");
                Set(VoiceStatus.DiscordClosed);
            }
            finally
            {
                _rpc = null;
            }
            Leave();
            try
            {
                await Task.Delay(RetryDelay, time, session.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>Eine Verbindung zu Discord von Anfang bis Ende; endet, wenn Discord geschlossen wird.</summary>
    private async Task SessionAsync(VoiceConfig config, CancellationToken cancellationToken)
    {
        var transport = await connect(cancellationToken);
        if (transport is null)
        {
            Set(VoiceStatus.DiscordClosed);
            return;
        }
        await using var rpc = await ConnectOrDisposeAsync(transport, config.ClientId, cancellationToken);
        await AuthenticateAsync(rpc, config, cancellationToken);
        lock (_lock)
        {
            _selfId ??= DiscordRpc.Str(rpc.User, "id");
        }

        await rpc.CommandAsync("SUBSCRIBE", null, "VOICE_CHANNEL_SELECT", cancellationToken: cancellationToken);
        await rpc.CommandAsync("SUBSCRIBE", null, "VOICE_SETTINGS_UPDATE", cancellationToken: cancellationToken);
        ApplyVoiceSettings(await rpc.CommandAsync("GET_VOICE_SETTINGS", cancellationToken: cancellationToken));
        await SelectAsync(rpc, await rpc.CommandAsync("GET_SELECTED_VOICE_CHANNEL", cancellationToken: cancellationToken), cancellationToken);
        _rpc = rpc;
        Set(VoiceStatus.Connected);
        log("Discord verbunden", "info");

        await foreach (var (evt, data) in rpc.Events.ReadAllAsync(cancellationToken))
        {
            await HandleAsync(rpc, evt, data, cancellationToken);
        }
        log("Discord wurde beendet", "info");
    }

    private static async Task<DiscordRpc> ConnectOrDisposeAsync(IRpcTransport transport, string clientId, CancellationToken cancellationToken)
    {
        try
        {
            return await DiscordRpc.ConnectAsync(transport, clientId, cancellationToken);
        }
        catch
        {
            await transport.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Meldet sich mit dem gespeicherten Token an, erneuert es, wenn nötig, und fragt sonst Discord. Beim allerersten
    /// Mal fragt es von selbst; hat man abgelehnt, erst wieder nach „Verbinden“.
    /// </summary>
    private async Task AuthenticateAsync(DiscordRpc rpc, VoiceConfig config, CancellationToken cancellationToken)
    {
        if (tokens.Load(config.ClientId) is { } token)
        {
            if (token.ExpiresAt - time.GetUtcNow() < TimeSpan.FromDays(1) && token.RefreshToken is { } refresh)
            {
                token = await RefreshAsync(config, refresh, cancellationToken) ?? token;
            }
            if (await TryAuthenticateAsync(rpc, token, cancellationToken))
            {
                return;
            }
            if (token.RefreshToken is { } again && await RefreshAsync(config, again, cancellationToken) is { } renewed
                && await TryAuthenticateAsync(rpc, renewed, cancellationToken))
            {
                return;
            }
            tokens.Clear();
        }

        while (true)
        {
            Task requested;
            lock (_lock)
            {
                requested = _authorizeRequested.Task;
                if (_declined)
                {
                    // Der letzte Fehler bleibt stehen, damit man sieht, warum es nicht geklappt hat.
                    _status = VoiceStatus.NeedsAuthorization;
                }
            }
            if (Snapshot.Status == VoiceStatus.NeedsAuthorization)
            {
                Changed?.Invoke();
                // Wartet auf „Verbinden“; endet Discord vorher, geht es von vorn los.
                await Task.WhenAny(requested, rpc.Completion).WaitAsync(cancellationToken);
                if (rpc.Completion.IsCompleted)
                {
                    throw new IOException("Discord wurde beendet");
                }
                lock (_lock)
                {
                    _authorizeRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }

            Set(VoiceStatus.Authorizing);
            string? code;
            try
            {
                var data = await rpc.CommandAsync("AUTHORIZE", new { client_id = config.ClientId, scopes = Scopes }, timeout: AuthorizeTimeout, cancellationToken: cancellationToken);
                code = DiscordRpc.Str(data, "code");
            }
            catch (Exception ex) when (ex is DiscordRpcException or TimeoutException)
            {
                log($"Discord hat den Zugriff nicht erlaubt: {ex.Message}", "warn");
                code = null;
            }
            if (code is null)
            {
                lock (_lock)
                {
                    _declined = true;
                }
                continue;
            }
            var fresh = await oauth.ExchangeAsync(config.ClientId, config.ClientSecret, code, cancellationToken);
            tokens.Save(config.ClientId, fresh);
            if (await TryAuthenticateAsync(rpc, fresh, cancellationToken))
            {
                return;
            }
            throw new OAuthException("Discord hat das neue Token nicht angenommen");
        }
    }

    private async Task<OAuthToken?> RefreshAsync(VoiceConfig config, string refreshToken, CancellationToken cancellationToken)
    {
        try
        {
            var token = await oauth.RefreshAsync(config.ClientId, config.ClientSecret, refreshToken, cancellationToken);
            tokens.Save(config.ClientId, token);
            return token;
        }
        catch (OAuthException ex)
        {
            log($"Token nicht erneuert: {ex.Message}", "warn");
            return null;
        }
    }

    private async Task<bool> TryAuthenticateAsync(DiscordRpc rpc, OAuthToken token, CancellationToken cancellationToken)
    {
        try
        {
            var data = await rpc.CommandAsync("AUTHENTICATE", new { access_token = token.AccessToken }, cancellationToken: cancellationToken);
            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("user", out var user))
            {
                lock (_lock)
                {
                    _selfId = DiscordRpc.Str(user, "id");
                }
            }
            return true;
        }
        catch (DiscordRpcException ex)
        {
            log($"Token abgelehnt: {ex.Message}", "info");
            return false;
        }
    }

    // ───────── Ereignisse ─────────

    private async Task HandleAsync(DiscordRpc rpc, string evt, JsonElement data, CancellationToken cancellationToken)
    {
        switch (evt)
        {
            case "VOICE_CHANNEL_SELECT":
                var id = DiscordRpc.Str(data, "channel_id");
                JsonElement channel = default;
                if (id is not null)
                {
                    try
                    {
                        channel = await rpc.CommandAsync("GET_CHANNEL", new { channel_id = id }, cancellationToken: cancellationToken);
                    }
                    catch (DiscordRpcException ex)
                    {
                        log($"Kanal {id}: {ex.Message}", "warn");
                    }
                }
                await SelectAsync(rpc, channel, cancellationToken);
                break;
            case "VOICE_SETTINGS_UPDATE":
                ApplyVoiceSettings(data);
                break;
            case "VOICE_STATE_CREATE" or "VOICE_STATE_UPDATE":
                if (Member(data) is { } member)
                {
                    lock (_lock)
                    {
                        _members[member.Id] = member;
                    }
                    Changed?.Invoke();
                }
                break;
            case "VOICE_STATE_DELETE":
                if (Member(data) is { } gone)
                {
                    lock (_lock)
                    {
                        _members.Remove(gone.Id);
                        _speaking.Remove(gone.Id);
                    }
                    Changed?.Invoke();
                }
                break;
            case "SPEAKING_START" or "SPEAKING_STOP":
                if (DiscordRpc.Str(data, "user_id") is { } user)
                {
                    bool changed;
                    lock (_lock)
                    {
                        changed = evt == "SPEAKING_START" ? _speaking.Add(user) : _speaking.Remove(user);
                    }
                    if (changed)
                    {
                        Changed?.Invoke();
                    }
                }
                break;
        }
    }

    /// <summary>Wechselt zu einem Sprachkanal (Daten aus <c>GET_SELECTED_VOICE_CHANNEL</c>/<c>GET_CHANNEL</c>) oder verlässt ihn (<c>null</c>).</summary>
    private async Task SelectAsync(DiscordRpc rpc, JsonElement channel, CancellationToken cancellationToken)
    {
        var id = DiscordRpc.Str(channel, "id");
        string? previous;
        lock (_lock)
        {
            previous = _channel?.Id;
        }
        if (previous is not null && previous != id)
        {
            foreach (var evt in ChannelEvents)
            {
                await TryAsync(() => rpc.CommandAsync("UNSUBSCRIBE", new { channel_id = previous }, evt, cancellationToken: cancellationToken));
            }
        }
        if (id is null)
        {
            Leave();
            Changed?.Invoke();
            return;
        }

        var guildId = DiscordRpc.Str(channel, "guild_id");
        if (guildId is not null && !_guilds.ContainsKey(guildId)
            && await TryAsync(() => rpc.CommandAsync("GET_GUILD", new { guild_id = guildId }, cancellationToken: cancellationToken)) is { } guild
            && DiscordRpc.Str(guild, "name") is { } guildName)
        {
            lock (_lock)
            {
                _guilds[guildId] = guildName;
            }
        }
        lock (_lock)
        {
            _channel = (id, DiscordRpc.Str(channel, "name") is { Length: > 0 } name ? name : "Sprachkanal", guildId);
            _members.Clear();
            _speaking.Clear();
            if (channel.TryGetProperty("voice_states", out var states) && states.ValueKind == JsonValueKind.Array)
            {
                foreach (var state in states.EnumerateArray())
                {
                    if (Member(state) is { } member)
                    {
                        _members[member.Id] = member;
                    }
                }
            }
        }
        if (previous != id)
        {
            foreach (var evt in ChannelEvents)
            {
                await rpc.CommandAsync("SUBSCRIBE", new { channel_id = id }, evt, cancellationToken: cancellationToken);
            }
        }
        Changed?.Invoke();
    }

    /// <summary>Ein Eintrag aus <c>voice_states</c>: Spitzname, sonst Anzeigename, sonst Benutzername.</summary>
    internal static VoiceMember? Member(JsonElement state)
    {
        if (state.ValueKind != JsonValueKind.Object || !state.TryGetProperty("user", out var user) || DiscordRpc.Str(user, "id") is not { } id)
        {
            return null;
        }
        var name = DiscordRpc.Str(state, "nick") is { Length: > 0 } nick ? nick
            : DiscordRpc.Str(user, "global_name") is { Length: > 0 } global ? global
            : DiscordRpc.Str(user, "username") ?? id;
        var voice = state.TryGetProperty("voice_state", out var v) ? v : default;
        return new VoiceMember(id, name)
        {
            Muted = Flag(voice, "self_mute") || Flag(voice, "mute") || Flag(voice, "suppress"),
            Deafened = Flag(voice, "self_deaf") || Flag(voice, "deaf"),
        };
    }

    private void ApplyVoiceSettings(JsonElement settings)
    {
        if (settings.ValueKind != JsonValueKind.Object)
        {
            return;
        }
        lock (_lock)
        {
            if (settings.TryGetProperty("mute", out var mute) && mute.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                _selfMuted = mute.GetBoolean();
            }
            if (settings.TryGetProperty("deaf", out var deaf) && deaf.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                _selfDeafened = deaf.GetBoolean();
            }
        }
        Changed?.Invoke();
    }

    private static bool Flag(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private void Leave()
    {
        lock (_lock)
        {
            _channel = null;
            _members.Clear();
            _speaking.Clear();
        }
    }

    private void Set(VoiceStatus status, string? error = null)
    {
        lock (_lock)
        {
            if (_status == status && _error == error)
            {
                return;
            }
            _status = status;
            _error = error;
            if (status != VoiceStatus.Connected)
            {
                _channel = null;
                _members.Clear();
                _speaking.Clear();
            }
        }
        Changed?.Invoke();
    }
}
