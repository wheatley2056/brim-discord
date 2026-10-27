using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;

namespace Brim.Discord.Tests;

public sealed class VoiceClientTests : IAsyncLifetime
{
    private const string ClientId = "1300000000000000000";

    private readonly FakeDiscord _discord = new();
    private readonly FakeOAuth _oauth = new();
    private readonly MemoryTokenStore _tokens = new();
    private readonly FakeTimeProvider _time = new(Test.Now);
    private readonly List<string> _log = [];
    private readonly CancellationTokenSource _stop = new();
    private Task _run = Task.CompletedTask;
    private bool _discordRunning = true;
    private int _connects;

    public VoiceClientTests() => Test.Setup();

    private VoiceClient Start(VoiceConfig? config = null)
    {
        var voice = new VoiceClient(_ =>
        {
            _connects++;
            return Task.FromResult<IRpcTransport?>(_discordRunning ? _discord : null);
        }, _oauth, _tokens, _time, (message, _) =>
        {
            lock (_log)
            {
                _log.Add(message);
            }
        });
        voice.Configure(config ?? new VoiceConfig(true, ClientId, "secret"));
        _run = voice.RunAsync(_stop.Token);
        return voice;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _stop.Cancel();
        await _run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task First_start_asks_Discord_saves_the_token_and_shows_the_channel()
    {
        _discord.ValidTokens.Add("access-code-1");
        _discord.Selected = FakeDiscord.Channel("c1", "Zocken", FakeDiscord.State("42", "tobias"), FakeDiscord.State("7", "anna", nick: "Anna"));

        var voice = Start();
        await Test.Until(() => voice.Snapshot.InChannel, "im Kanal");

        var snapshot = voice.Snapshot;
        Assert.Equal("Zocken", snapshot.Channel!.Name);
        Assert.Equal("Freunde", snapshot.Channel.Guild);
        Assert.Equal(["Anna", "tobias"], snapshot.Channel.Members.Select(m => m.Name));
        Assert.True(snapshot.Channel.Members.Single(m => m.Name == "tobias").Self);
        Assert.Equal([$"exchange:{ClientId}:code-1"], _oauth.Calls);
        Assert.Equal("access-code-1", _tokens.Tokens[ClientId].AccessToken);
        var authorize = _discord.Commands.Single(c => c.Cmd == "AUTHORIZE");
        Assert.Equal(ClientId, authorize.Args.GetProperty("client_id").GetString());
        Assert.Equal(["rpc", "rpc.voice.read", "rpc.voice.write"], authorize.Args.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()));
        Assert.Equal(1, _discord.CountOf("SUBSCRIBE", "SPEAKING_START"));
        Assert.Equal("c1", _discord.Commands.First(c => c.Evt == "SPEAKING_START").Args.GetProperty("channel_id").GetString());
    }

    [Fact]
    public async Task A_saved_token_needs_no_question()
    {
        _tokens.Save(ClientId, new OAuthToken("saved", "r", Test.Now.AddDays(5)));
        _discord.ValidTokens.Add("saved");

        var voice = Start();
        await Test.Until(() => voice.Snapshot.Status == VoiceStatus.Connected, "verbunden");

        Assert.Equal(0, _discord.CountOf("AUTHORIZE"));
        Assert.Null(voice.Snapshot.Channel);
    }

    [Fact]
    public async Task A_rejected_token_is_renewed_with_the_refresh_token()
    {
        _tokens.Save(ClientId, new OAuthToken("old", "r-old", Test.Now.AddDays(5)));
        _discord.ValidTokens.Add("access-renewed");

        var voice = Start();
        await Test.Until(() => voice.Snapshot.Status == VoiceStatus.Connected, "verbunden");

        Assert.Equal(["refresh:r-old"], _oauth.Calls);
        Assert.Equal(0, _discord.CountOf("AUTHORIZE"));
        Assert.Equal("access-renewed", _tokens.Tokens[ClientId].AccessToken);
    }

    [Fact]
    public async Task Declining_waits_for_connect_instead_of_asking_again()
    {
        _discord.DeclineAuthorize = true;
        var voice = Start();
        await Test.Until(() => voice.Snapshot.Status == VoiceStatus.NeedsAuthorization, "wartet auf Verbinden");
        await Task.Delay(50);
        Assert.Equal(1, _discord.CountOf("AUTHORIZE"));

        _discord.DeclineAuthorize = false;
        _discord.ValidTokens.Add("access-code-1");
        voice.Authorize();

        await Test.Until(() => voice.Snapshot.Status == VoiceStatus.Connected, "verbunden");
        Assert.Equal(2, _discord.CountOf("AUTHORIZE"));
    }

    [Fact]
    public async Task While_Discord_asks_the_status_says_so()
    {
        _discord.AuthorizeGate = new TaskCompletionSource();
        _discord.ValidTokens.Add("access-code-1");
        var voice = Start();

        await Test.Until(() => voice.Snapshot.Status == VoiceStatus.Authorizing, "fragt");
        _discord.AuthorizeGate.SetResult();
        await Test.Until(() => voice.Snapshot.Status == VoiceStatus.Connected, "verbunden");
    }

    [Fact]
    public async Task Speaking_joining_and_leaving_follow_the_events()
    {
        _tokens.Save(ClientId, new OAuthToken("t", null, Test.Now.AddDays(5)));
        _discord.ValidTokens.Add("t");
        _discord.Selected = FakeDiscord.Channel("c1", "Zocken", FakeDiscord.State("42", "tobias"));
        var voice = Start();
        await Test.Until(() => _discord.CountOf("SUBSCRIBE", "SPEAKING_STOP") == 1, "abonniert");

        await _discord.Push("VOICE_STATE_CREATE", FakeDiscord.State("7", "anna", selfMute: true));
        await _discord.Push("SPEAKING_START", new JsonObject { ["user_id"] = "42", ["channel_id"] = "c1" });
        await Test.Until(() => voice.Snapshot.Channel?.Members.Count == 2 && voice.Snapshot.Channel.Speakers.Any(), "Anna da, Tobias spricht");

        var channel = voice.Snapshot.Channel!;
        Assert.Equal("tobias", channel.Speakers.Single().Name);
        Assert.True(channel.Members.Single(m => m.Name == "anna").Muted);

        await _discord.Push("SPEAKING_STOP", new JsonObject { ["user_id"] = "42" });
        await _discord.Push("VOICE_STATE_DELETE", FakeDiscord.State("7", "anna"));
        await Test.Until(() => voice.Snapshot.Channel is { Members.Count: 1 } c && !c.Speakers.Any(), "Anna weg, still");
    }

    [Fact]
    public async Task Switching_channels_moves_the_subscriptions()
    {
        _tokens.Save(ClientId, new OAuthToken("t", null, Test.Now.AddDays(5)));
        _discord.ValidTokens.Add("t");
        _discord.Selected = FakeDiscord.Channel("c1", "Zocken");
        _discord.Channels["c2"] = FakeDiscord.Channel("c2", "Quatschen", FakeDiscord.State("8", "ben"));
        var voice = Start();
        await Test.Until(() => voice.Snapshot.InChannel, "im Kanal");

        await _discord.Push("VOICE_CHANNEL_SELECT", new JsonObject { ["channel_id"] = "c2", ["guild_id"] = "g1" });
        await Test.Until(() => voice.Snapshot.Channel?.Name == "Quatschen", "gewechselt");

        Assert.Equal(5, _discord.CountOf("UNSUBSCRIBE"));
        Assert.All(_discord.Commands.Where(c => c.Cmd == "UNSUBSCRIBE"), c => Assert.Equal("c1", c.Args.GetProperty("channel_id").GetString()));
        Assert.Equal("ben", voice.Snapshot.Channel!.Members.Single().Name);
    }

    [Fact]
    public async Task Buttons_mute_deafen_and_leave()
    {
        _tokens.Save(ClientId, new OAuthToken("t", null, Test.Now.AddDays(5)));
        _discord.ValidTokens.Add("t");
        _discord.Selected = FakeDiscord.Channel("c1", "Zocken", FakeDiscord.State("42", "tobias"));
        var voice = Start();
        await Test.Until(() => voice.Snapshot.InChannel, "im Kanal");

        await voice.ToggleMuteAsync();
        Assert.True(voice.Snapshot.SelfMuted);
        Assert.True(_discord.Mute);
        await voice.ToggleMuteAsync();
        Assert.False(_discord.Mute);

        await voice.ToggleDeafAsync();
        Assert.True(voice.Snapshot.SelfDeafened);

        await voice.LeaveAsync();
        await Test.Until(() => voice.Snapshot is { Status: VoiceStatus.Connected, Channel: null }, "Kanal verlassen");
        var select = _discord.Commands.Single(c => c.Cmd == "SELECT_VOICE_CHANNEL");
        Assert.Equal(System.Text.Json.JsonValueKind.Null, select.Args.GetProperty("channel_id").ValueKind);
    }

    [Fact]
    public async Task Without_Discord_it_waits_and_tries_again()
    {
        _discordRunning = false;
        var voice = Start();
        await Test.Until(() => voice.Snapshot.Status == VoiceStatus.DiscordClosed, "Discord zu");
        Assert.Equal(1, _connects);

        _discordRunning = true;
        _tokens.Save(ClientId, new OAuthToken("t", null, Test.Now.AddDays(5)));
        _discord.ValidTokens.Add("t");
        _time.Advance(VoiceClient.RetryDelay);
        await Test.Until(() => voice.Snapshot.Status == VoiceStatus.Connected, "verbunden");
    }

    [Fact]
    public async Task Closing_Discord_clears_the_channel()
    {
        _tokens.Save(ClientId, new OAuthToken("t", null, Test.Now.AddDays(5)));
        _discord.ValidTokens.Add("t");
        _discord.Selected = FakeDiscord.Channel("c1", "Zocken", FakeDiscord.State("42", "tobias"));
        var voice = Start();
        await Test.Until(() => voice.Snapshot.InChannel, "im Kanal");

        _discord.Quit();

        await Test.Until(() => voice.Snapshot is { Status: VoiceStatus.DiscordClosed, Channel: null }, "Discord zu");
    }

    [Fact]
    public async Task An_unknown_client_id_is_reported()
    {
        _discord.CloseOnHandshake = """{"code":4000,"message":"Invalid Client ID"}""";
        var voice = Start();

        await Test.Until(() => voice.Snapshot.Status == VoiceStatus.Error, "Fehler");
        Assert.Equal("Discord kennt diese Client-ID nicht", voice.Snapshot.Error);
    }

    [Fact]
    public async Task A_failed_exchange_does_not_ask_again_by_itself()
    {
        _oauth.OnExchange = _ => throw new OAuthException("Client-ID oder Client-Secret stimmt nicht");
        var voice = Start();

        await Test.Until(() => voice.Snapshot.Status == VoiceStatus.Error, "Fehler");
        _time.Advance(VoiceClient.RetryDelay);
        await Test.Until(() => voice.Snapshot.Status == VoiceStatus.NeedsAuthorization, "wartet auf Verbinden");

        Assert.Equal("Client-ID oder Client-Secret stimmt nicht", voice.Snapshot.Error);
        Assert.Equal(1, _discord.CountOf("AUTHORIZE"));
    }

    [Fact]
    public async Task Switched_off_or_without_secret_it_does_not_connect()
    {
        var voice = Start(new VoiceConfig(true, ClientId, ""));
        await Test.Until(() => voice.Snapshot.Status == VoiceStatus.NotConfigured, "nicht eingerichtet");

        voice.Configure(new VoiceConfig(false, ClientId, "secret"));
        await Test.Until(() => voice.Snapshot.Status == VoiceStatus.Off, "aus");
        Assert.Equal(0, _connects);

        _discord.ValidTokens.Add("access-code-1");
        voice.Configure(new VoiceConfig(true, ClientId, "secret"));
        await Test.Until(() => voice.Snapshot.Status == VoiceStatus.Connected, "verbunden");
    }

    [Fact]
    public void Members_use_nick_then_global_name_then_username()
    {
        var state = FakeDiscord.State("1", "anna_99");
        Assert.Equal("anna_99", VoiceClient.Member(System.Text.Json.JsonSerializer.SerializeToElement(state))!.Name);
        state["user"]!["global_name"] = "Anna";
        Assert.Equal("Anna", VoiceClient.Member(System.Text.Json.JsonSerializer.SerializeToElement(state))!.Name);
        state["nick"] = "Anni";
        Assert.Equal("Anni", VoiceClient.Member(System.Text.Json.JsonSerializer.SerializeToElement(state))!.Name);
    }
}
