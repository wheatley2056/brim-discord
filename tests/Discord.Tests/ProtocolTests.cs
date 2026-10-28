using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using Brim.Core.Plugins;
using Microsoft.Extensions.Time.Testing;

namespace Brim.Discord.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public async Task Frames_are_op_length_and_utf8_json()
    {
        var stream = new MemoryStream();
        var transport = new StreamTransport(stream);
        await transport.WriteAsync(RpcOp.Handshake, """{"v":1,"client_id":"ü"}""", CancellationToken.None);

        var bytes = stream.ToArray();
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(bytes));
        Assert.Equal(bytes.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4)));
        Assert.Equal("""{"v":1,"client_id":"ü"}""", Encoding.UTF8.GetString(bytes, 8, bytes.Length - 8));

        stream.Position = 0;
        var (op, json) = await new StreamTransport(stream).ReadAsync(CancellationToken.None);
        Assert.Equal(RpcOp.Handshake, op);
        Assert.Contains("ü", json);
    }

    [Fact]
    public async Task A_closed_pipe_ends_reading()
    {
        var transport = new StreamTransport(new MemoryStream([1, 0, 0, 0]));
        await Assert.ThrowsAsync<EndOfStreamException>(() => transport.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Oversized_frames_are_rejected()
    {
        var header = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(header, 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), int.MaxValue);
        await Assert.ThrowsAsync<InvalidDataException>(() => new StreamTransport(new MemoryStream(header)).ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Errors_from_Discord_carry_their_code()
    {
        var discord = new FakeDiscord();
        await using var rpc = await DiscordRpc.ConnectAsync(discord, "1", CancellationToken.None);
        Assert.Equal("42", DiscordRpc.Str(rpc.User, "id"));

        var error = await Assert.ThrowsAsync<DiscordRpcException>(() => rpc.CommandAsync("AUTHENTICATE", new { access_token = "falsch" }));
        Assert.Equal(4009, error.Code);
    }

    [Fact]
    public async Task Commands_fail_when_Discord_quits()
    {
        var discord = new FakeDiscord();
        await using var rpc = await DiscordRpc.ConnectAsync(discord, "1", CancellationToken.None);
        discord.Quit();
        await rpc.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<IOException>(() => rpc.CommandAsync("GET_VOICE_SETTINGS"));
    }

    [Theory]
    [InlineData("Anna (#allgemein, Freunde)", "Anna", "allgemein", "Freunde", "#allgemein · Freunde")]
    [InlineData("Anna", "Anna", null, null, "")]
    [InlineData("Anna (Zocker-Gruppe)", "Anna", null, "Zocker-Gruppe", "Zocker-Gruppe")]
    [InlineData("Anna (die Echte) (#off-topic, Server (EU))", "Anna (die Echte) (#off-topic, Server (EU))", null, null, "")]
    public void Notification_titles_name_author_channel_and_server(string title, string author, string? channel, string? place, string where)
    {
        var message = DiscordMessage.From(new NotificationParams("Discord", title, "Hallo"), Test.Now)!;
        Assert.Equal(author, message.Author);
        Assert.Equal(channel, message.Channel);
        Assert.Equal(place, message.Place);
        Assert.Equal(where, message.Where);
    }

    [Theory]
    [InlineData("Discord", "Ein Update ist bereit")]
    [InlineData("Anna", "")]
    [InlineData("", "Hallo")]
    public void Other_notifications_are_no_messages(string title, string body) =>
        Assert.Null(DiscordMessage.From(new NotificationParams("Discord", title, body), Test.Now));

    [Fact]
    public void Settings_accept_text_for_switches()
    {
        var values = new Dictionary<string, JsonElement>
        {
            ["popup"] = JsonSerializer.SerializeToElement("false"),
            ["clientId"] = JsonSerializer.SerializeToElement("  123  "),
            ["clientSecret"] = JsonSerializer.SerializeToElement("abc"),
        };
        var settings = PluginSettings.From(values);
        Assert.False(settings.Popup);
        Assert.True(settings.Messages);
        Assert.Equal(new VoiceConfig(true, "123", "abc"), settings.VoiceConfig);
        Assert.True(settings.VoiceConfig.Configured);
    }

    [Theory]
    [InlineData("""{"error":"invalid_client"}""", "Client-ID oder Client-Secret stimmt nicht")]
    [InlineData("""{"error":"invalid_request","error_description":"Invalid \"redirect_uri\" in request."}""", "In der Discord-App fehlt der Redirect http://localhost")]
    [InlineData("<html>", "Discord antwortet mit 400")]
    public void OAuth_errors_are_explained(string body, string text) => Assert.Equal(text, DiscordOAuth.Describe(body, 400));

    [Fact]
    public async Task Exchange_posts_the_code_with_the_redirect()
    {
        var handler = new TokenHandler();
        var oauth = new DiscordOAuth(new HttpClient(handler), new FakeTimeProvider(Test.Now));

        var token = await oauth.ExchangeAsync("123", "geheim", "abc", CancellationToken.None);

        Assert.Equal("at", token.AccessToken);
        Assert.Equal("rt", token.RefreshToken);
        Assert.Equal(Test.Now.AddSeconds(604800), token.ExpiresAt);
        Assert.Equal(DiscordOAuth.TokenUrl, handler.Url);
        Assert.Contains("grant_type=authorization_code", handler.Body);
        Assert.Contains("code=abc", handler.Body);
        Assert.Contains("redirect_uri=http%3A%2F%2Flocalhost", handler.Body);
    }

    [Fact]
    public void Token_store_keeps_one_token_per_app()
    {
        var path = Path.Combine(Path.GetTempPath(), "brim-discord-tests", Guid.NewGuid() + ".bin");
        var store = new FileTokenStore(path);
        Assert.Null(store.Load("1"));

        store.Save("1", new OAuthToken("a", "r", Test.Now));
        Assert.Equal(new OAuthToken("a", "r", Test.Now), store.Load("1"));
        Assert.Null(store.Load("2"));

        store.Clear();
        Assert.Null(store.Load("1"));
    }

    private sealed class TokenHandler : HttpMessageHandler
    {
        public string? Url { get; private set; }
        public string Body { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Url = request.RequestUri!.ToString();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"access_token":"at","token_type":"Bearer","expires_in":604800,"refresh_token":"rt","scope":"rpc"}""", Encoding.UTF8, "application/json"),
            };
        }
    }
}
