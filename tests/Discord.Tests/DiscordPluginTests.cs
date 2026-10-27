using System.Text;
using System.Text.Json;
using Brim.Core;
using Brim.Core.Plugins;
using Brim.Core.Widgets;
using Microsoft.Extensions.Time.Testing;

namespace Brim.Discord.Tests;

public sealed class DiscordPluginTests
{
    private readonly RecordingSink _sink = new();
    private readonly FakeVoice _voice = new();
    private readonly FakeTimeProvider _time = new(Test.Now);
    private int _opened;

    public DiscordPluginTests() => Test.Setup();

    private DiscordPlugin Create(PluginSettings? settings = null, bool overlay = true) =>
        new(_sink, _voice, () => _opened++, _time, settings ?? new PluginSettings { ClientId = "1", ClientSecret = "s" }, overlay);

    private static IReadOnlyDictionary<string, IReadOnlyList<TileSize>> Placed(string id, params TileSize[] sizes) =>
        new Dictionary<string, IReadOnlyList<TileSize>> { [id] = sizes };

    private static VoiceSnapshot InChannel(bool annaSpeaks = false, bool muted = false, bool deaf = false) => new(
        VoiceStatus.Connected,
        new VoiceChannel("c1", "Zocken", "Freunde",
        [
            new VoiceMember("7", "Anna") { Speaking = annaSpeaks },
            new VoiceMember("42", "tobias") { Self = true, Muted = muted },
        ]),
        muted || deaf, deaf);

    private static NotificationParams Note(string title, string body) => new("Discord", title, body);

    [Fact]
    public async Task Joining_a_channel_shows_it_in_the_notch()
    {
        var plugin = Create();
        _voice.Raise(InChannel());
        await plugin.RenderAsync();

        var activity = Assert.IsType<ActivityParams>(_sink.Last(Views.VoiceActivityId));
        Assert.Equal("Zocken · 2", Assert.IsType<TextWidget>(activity.Trailing).Text);
        Assert.Null(activity.Glow);
        Assert.Equal(Views.Accent, Assert.IsType<IconWidget>(activity.Leading).Color);
        var expanded = Test.Json(activity.Expanded!);
        Assert.Contains("Zocken · Freunde", expanded);
        Assert.Contains("tobias (du)", expanded);
        Assert.Contains("\"actionId\":\"leave\"", expanded);
    }

    [Fact]
    public async Task Someone_speaking_makes_the_notch_glow_and_names_them()
    {
        var plugin = Create();
        _voice.Raise(InChannel(annaSpeaks: true));
        await plugin.RenderAsync();

        var activity = _sink.Last(Views.VoiceActivityId)!;
        Assert.Equal("Anna spricht", Assert.IsType<TextWidget>(activity.Trailing).Text);
        Assert.Equal(0.6, activity.Glow);
        Assert.True(activity.Overlay);
        Assert.Contains("Anna spricht", Test.Json(activity.Content!));
    }

    [Fact]
    public async Task Glow_and_overlay_follow_settings_and_permission()
    {
        var plugin = Create(new PluginSettings { Glow = false, Overlay = true }, overlay: false);
        _voice.Raise(InChannel(annaSpeaks: true));
        await plugin.RenderAsync();

        var activity = _sink.Last(Views.VoiceActivityId)!;
        Assert.Null(activity.Glow);
        Assert.False(activity.Overlay);
        Assert.Null(activity.Content);
    }

    [Fact]
    public async Task Muted_turns_the_microphone_red()
    {
        var plugin = Create();
        _voice.Raise(InChannel(muted: true));
        await plugin.RenderAsync();

        var leading = Assert.IsType<IconWidget>(_sink.Last(Views.VoiceActivityId)!.Leading);
        Assert.Equal(Views.MicOffGlyph, leading.Glyph);
        Assert.Equal(BrimColors.Danger, leading.Color);
        Assert.Contains("du bist stumm", ((TextWidget)_sink.Last(Views.VoiceActivityId)!.Trailing!).AccessibleName);
    }

    [Fact]
    public async Task Leaving_the_channel_ends_the_activity_once()
    {
        var plugin = Create();
        _voice.Raise(InChannel());
        await plugin.RenderAsync();
        _voice.Raise(new VoiceSnapshot(VoiceStatus.Connected));
        await plugin.RenderAsync();
        await plugin.RenderAsync();

        Assert.Equal([Views.VoiceActivityId], _sink.Ended);
    }

    [Fact]
    public async Task Unchanged_content_is_not_sent_again()
    {
        var plugin = Create();
        _voice.Raise(InChannel());
        await plugin.RenderAsync();
        await plugin.RenderAsync();

        Assert.Single(_sink.Activities, a => a.Id == Views.VoiceActivityId);
    }

    [Fact]
    public async Task Voice_switched_off_shows_nothing_in_the_notch()
    {
        var plugin = Create(new PluginSettings { Voice = false });
        _voice.Raise(InChannel());
        await plugin.RenderAsync();

        Assert.Null(_sink.Last(Views.VoiceActivityId));
        Assert.False(_voice.Config!.Enabled);
    }

    [Fact]
    public async Task Buttons_reach_Discord()
    {
        var plugin = Create();
        await plugin.HandleActionAsync(Views.MuteAction);
        await plugin.HandleActionAsync(Views.DeafAction);
        await plugin.HandleActionAsync(Views.LeaveAction);
        await plugin.HandleActionAsync(Views.AuthorizeAction);

        Assert.Equal(["mute", "deaf", "leave", "authorize"], _voice.Calls);
    }

    [Fact]
    public async Task A_message_pops_up_and_is_counted_until_read()
    {
        var plugin = Create();
        _voice.Snapshot = new VoiceSnapshot(VoiceStatus.Connected);

        await plugin.OnNotificationAsync(Note("Anna (#allgemein, Freunde)", "Kommst du heute Abend?"));
        await plugin.OnNotificationAsync(Note("Ben", "Hast du kurz Zeit?"));

        var hud = _sink.Huds[0];
        Assert.Contains("Anna · #allgemein · Freunde", Test.Json(hud.Content!));
        Assert.Contains("Kommst du heute Abend?", Test.Json(hud.Content!));
        Assert.Equal("Discord: Anna in #allgemein · Freunde: Kommst du heute Abend?", hud.Announcement);
        var activity = _sink.Last(Views.MessagesActivityId)!;
        Assert.Equal("2 neu", Assert.IsType<TextWidget>(activity.Trailing).Text);
        var expanded = Test.Json(activity.Expanded!);
        Assert.True(expanded.IndexOf("Hast du kurz Zeit?", StringComparison.Ordinal) < expanded.IndexOf("Kommst du", StringComparison.Ordinal));

        await plugin.HandleActionAsync(Views.ReadAction);

        Assert.Equal(0, plugin.Unread);
        Assert.Contains(Views.MessagesActivityId, _sink.Ended);
        Assert.Equal(2, plugin.Messages.Count);
    }

    [Fact]
    public async Task Opening_Discord_counts_as_read()
    {
        var plugin = Create();
        await plugin.OnNotificationAsync(Note("Ben", "Hallo"));

        await plugin.HandleActionAsync(Views.OpenAction);

        Assert.Equal(1, _opened);
        Assert.Equal(0, plugin.Unread);
    }

    [Fact]
    public async Task Popup_and_count_can_be_switched_off_separately()
    {
        var plugin = Create(new PluginSettings { Popup = false });
        await plugin.OnNotificationAsync(Note("Ben", "Hallo"));
        Assert.Empty(_sink.Huds);
        Assert.Equal(1, plugin.Unread);

        await plugin.ApplySettingsAsync(new PluginSettings { Messages = false, Popup = true });
        Assert.Equal(0, plugin.Unread);
        Assert.Empty(plugin.Messages);
        await plugin.OnNotificationAsync(Note("Ben", "Noch da?"));
        Assert.Single(_sink.Huds);
        Assert.Empty(plugin.Messages);
    }

    [Fact]
    public async Task Only_the_last_messages_are_kept()
    {
        var plugin = Create(new PluginSettings { Popup = false });
        for (var i = 0; i < DiscordPlugin.RememberedMessages + 5; i++)
        {
            await plugin.OnNotificationAsync(Note("Ben", $"Nachricht {i}"));
        }

        Assert.Equal(DiscordPlugin.RememberedMessages, plugin.Messages.Count);
        Assert.Equal($"Nachricht {DiscordPlugin.RememberedMessages + 4}", plugin.Messages[0].Text);
    }

    [Fact]
    public async Task Widgets_are_sent_for_placed_sizes_only()
    {
        var plugin = Create();
        _voice.Snapshot = InChannel(annaSpeaks: true);
        await plugin.OnNotificationAsync(Note("Ben", "Hallo"));
        await plugin.SetPlacedWidgetsAsync(new Dictionary<string, IReadOnlyList<TileSize>>
        {
            ["voice"] = [TileSize.Medium, TileSize.Large],
            ["messages"] = [TileSize.Wide],
        });

        Assert.Equal(3, _sink.Widgets.Count);
        Assert.Contains("Anna spricht", Test.Json(_sink.LastWidget("voice", TileSize.Medium)!));
        Assert.Contains("tobias (du)", Test.Json(_sink.LastWidget("voice", TileSize.Large)!));
        var messages = Test.Json(_sink.LastWidget("messages", TileSize.Wide)!);
        Assert.Contains("Hallo", messages);
    }

    [Theory]
    [InlineData(VoiceStatus.NotConfigured, "Client-ID und Secret")]
    [InlineData(VoiceStatus.DiscordClosed, "Discord ist nicht geöffnet")]
    [InlineData(VoiceStatus.Authorizing, "Autorisieren")]
    [InlineData(VoiceStatus.NeedsAuthorization, "Mit Discord verbinden")]
    [InlineData(VoiceStatus.Off, "ausgeschaltet")]
    public void Voice_widget_explains_what_is_missing(VoiceStatus status, string text)
    {
        var widget = Views.VoiceWidget(new VoiceSnapshot(status), TileSize.Medium, System.Globalization.CultureInfo.CurrentCulture);
        Assert.Contains(text, Test.Json(widget));
    }

    [Fact]
    public void Voice_widget_shows_the_error_with_a_reconnect_button()
    {
        var widget = Test.Json(Views.VoiceWidget(new VoiceSnapshot(VoiceStatus.Error, Error: "Client-ID oder Client-Secret stimmt nicht"), TileSize.Medium, System.Globalization.CultureInfo.CurrentCulture));
        Assert.Contains("Client-ID oder Client-Secret stimmt nicht", widget);
        Assert.Contains("\"actionId\":\"authorize\"", widget);
    }

    [Fact]
    public void Every_size_of_every_widget_is_valid()
    {
        var states = new[]
        {
            InChannel(annaSpeaks: true, muted: true),
            new VoiceSnapshot(VoiceStatus.Connected),
            new VoiceSnapshot(VoiceStatus.NeedsAuthorization, Error: "abgelehnt"),
        };
        var messages = new List<DiscordMessage> { new("Anna", "allgemein", "Freunde", "Hallo", Test.Now) };
        foreach (var size in Enum.GetValues<TileSize>())
        {
            foreach (var state in states)
            {
                Assert.Null(WidgetValidator.Validate(Views.VoiceWidget(state, size, System.Globalization.CultureInfo.CurrentCulture)));
            }
            Assert.Null(WidgetValidator.Validate(Views.MessagesWidget(messages, 1, true, size, System.Globalization.CultureInfo.CurrentCulture)));
            Assert.Null(WidgetValidator.Validate(Views.MessagesWidget([], 0, true, size, System.Globalization.CultureInfo.CurrentCulture)));
        }
    }

    [Fact]
    public void Manifest_lists_the_widgets_and_settings_the_code_uses()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "plugin.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
        var root = doc.RootElement;
        Assert.Equal("de.brim.discord", root.GetProperty("id").GetString());
        Assert.Equal(["voice", "messages"], root.GetProperty("widgets").EnumerateArray().Select(w => w.GetProperty("id").GetString()));
        Assert.Contains("Discord", root.GetProperty("notificationApps").EnumerateArray().Select(a => a.GetString()));
        var keys = root.GetProperty("settings").EnumerateArray().Select(s => s.GetProperty("key").GetString()!).ToList();
        Assert.Equal(["messages", "popup", "voice", "clientId", "clientSecret", "glow", "overlay"], keys);

        var defaults = root.GetProperty("settings").EnumerateArray().ToDictionary(s => s.GetProperty("key").GetString()!, s => s.GetProperty("default").Clone());
        Assert.Equal(new PluginSettings(), PluginSettings.From(defaults));
    }
}
