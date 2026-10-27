using System.Globalization;
using System.Text.Json;
using Brim.Core;
using Brim.Core.Plugins;
using Brim.Core.Widgets;

namespace Brim.Discord;

/// <summary>
/// Bringt Sprachkanal und Nachrichten in die Notch: Bist du in einem Sprachkanal, zeigt sie, wer spricht, mit Stumm,
/// Taub und Auflegen. Discord-Benachrichtigungen kommen als kurze Meldung und werden gezählt, bis du sie gelesen hast.
/// Schickt nur, was sich geändert hat.
/// </summary>
public sealed class DiscordPlugin
{
    /// <summary>So viele Nachrichten merkt sich das Plugin.</summary>
    public const int RememberedMessages = 20;

    private readonly INotchSink _sink;
    private readonly IVoice _voice;
    private readonly Action _openDiscord;
    private readonly TimeProvider _time;
    private readonly bool _overlayAllowed;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, string> _sent = [];
    private readonly List<DiscordMessage> _messages = [];
    private IReadOnlyDictionary<string, IReadOnlyList<TileSize>> _placed = new Dictionary<string, IReadOnlyList<TileSize>>();

    public DiscordPlugin(INotchSink sink, IVoice voice, Action openDiscord, TimeProvider time, PluginSettings settings, bool overlayAllowed)
    {
        _sink = sink;
        _voice = voice;
        _openDiscord = openDiscord;
        _time = time;
        _overlayAllowed = overlayAllowed;
        Settings = settings;
        voice.Configure(settings.VoiceConfig);
        voice.Changed += () => _ = RenderSafeAsync();
    }

    public PluginSettings Settings { get; private set; }

    /// <summary>Neueste zuerst.</summary>
    public IReadOnlyList<DiscordMessage> Messages => _messages;

    public int Unread { get; private set; }

    private static CultureInfo Culture => CultureInfo.CurrentCulture;

    // ───────── Ereignisse von Brim ─────────

    /// <summary>Eine Windows-Benachrichtigung von Discord.</summary>
    public async Task OnNotificationAsync(NotificationParams notification)
    {
        if (!Settings.Messages && !Settings.Popup)
        {
            return;
        }
        if (DiscordMessage.From(notification, _time.GetUtcNow()) is not { } message)
        {
            return;
        }
        await _gate.WaitAsync();
        try
        {
            if (Settings.Messages)
            {
                _messages.Insert(0, message);
                if (_messages.Count > RememberedMessages)
                {
                    _messages.RemoveAt(_messages.Count - 1);
                }
                Unread = Math.Min(Unread + 1, RememberedMessages);
            }
            if (Settings.Popup)
            {
                await _sink.ShowHudAsync(Views.MessageHud(message));
            }
            await RenderAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task HandleActionAsync(string actionId)
    {
        switch (actionId)
        {
            case Views.MuteAction:
                await _voice.ToggleMuteAsync();
                break;
            case Views.DeafAction:
                await _voice.ToggleDeafAsync();
                break;
            case Views.LeaveAction:
                await _voice.LeaveAsync();
                break;
            case Views.AuthorizeAction:
                _voice.Authorize();
                break;
            case Views.OpenAction:
                _openDiscord();
                await MarkReadAsync();
                break;
            case Views.ReadAction:
                await MarkReadAsync();
                break;
        }
    }

    private async Task MarkReadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            Unread = 0;
            await RenderAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ApplySettingsAsync(PluginSettings settings)
    {
        await _gate.WaitAsync();
        try
        {
            if (!settings.Messages)
            {
                _messages.Clear();
                Unread = 0;
            }
            Settings = settings;
            _voice.Configure(settings.VoiceConfig);
            await RenderAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetPlacedWidgetsAsync(IReadOnlyDictionary<string, IReadOnlyList<TileSize>> placed)
    {
        await _gate.WaitAsync();
        try
        {
            _placed = placed;
            await RenderAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    // ───────── Anzeigen ─────────

    private async Task RenderSafeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await RenderAsync();
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or ObjectDisposedException or InvalidOperationException)
        {
            // Brim ist weg oder beendet das Plugin gerade.
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RenderAsync()
    {
        var voice = _voice.Snapshot;
        if (Settings.Voice && voice.InChannel)
        {
            await SetActivityAsync(Views.VoiceActivity(voice, Settings.Glow, Settings.Overlay && _overlayAllowed, Culture));
        }
        else
        {
            await EndActivityAsync(Views.VoiceActivityId);
        }

        if (Settings.Messages && Unread > 0)
        {
            await SetActivityAsync(Views.MessagesActivity(_messages, Unread, Culture));
        }
        else
        {
            await EndActivityAsync(Views.MessagesActivityId);
        }

        foreach (var size in _placed.GetValueOrDefault("voice") ?? [])
        {
            await SetWidgetAsync("voice", size, Views.VoiceWidget(voice, size, Culture));
        }
        foreach (var size in _placed.GetValueOrDefault("messages") ?? [])
        {
            await SetWidgetAsync("messages", size, Views.MessagesWidget(_messages, Unread, Settings.Messages, size, Culture));
        }
    }

    private async Task SetActivityAsync(ActivityParams activity)
    {
        if (Changed("activity:" + activity.Id, activity))
        {
            await _sink.SetActivityAsync(activity);
        }
    }

    private async Task EndActivityAsync(string id)
    {
        if (_sent.Remove("activity:" + id))
        {
            await _sink.EndActivityAsync(id);
        }
    }

    private async Task SetWidgetAsync(string id, TileSize size, Widget widget)
    {
        if (Changed($"widget:{id}:{size}", widget))
        {
            await _sink.SetWidgetAsync(id, size, widget);
        }
    }

    /// <summary>Merkt sich, was zuletzt geschickt wurde; <c>true</c>, wenn <paramref name="value"/> neu ist.</summary>
    private bool Changed(string key, object value)
    {
        var json = JsonSerializer.Serialize(value, value.GetType(), PluginJson.Options);
        if (_sent.TryGetValue(key, out var sent) && sent == json)
        {
            return false;
        }
        _sent[key] = json;
        return true;
    }
}
