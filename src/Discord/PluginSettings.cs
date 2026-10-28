using System.Text.Json;

namespace Brim.Discord;

/// <summary>Einstellungen aus <c>plugin.json</c> mit den Werten des Nutzers. Schalter dürfen auch als Text kommen.</summary>
public sealed record PluginSettings
{
    /// <summary>Neue Nachrichten in der Notch zählen und im Widget zeigen.</summary>
    public bool Messages { get; init; } = true;

    /// <summary>Neue Nachricht kurz einblenden.</summary>
    public bool Popup { get; init; } = true;

    public bool Voice { get; init; } = true;

    /// <summary>Client-ID der eigenen Discord-App (Developer Portal → OAuth2).</summary>
    public string ClientId { get; init; } = "";

    public string ClientSecret { get; init; } = "";

    /// <summary>Lichtschein der Notch, solange jemand spricht.</summary>
    public bool Glow { get; init; } = true;

    /// <summary>Wer spricht, als Pille über Spielen im Vollbild (Recht „overlay“).</summary>
    public bool Overlay { get; init; } = true;

    public VoiceConfig VoiceConfig => new(Voice, ClientId, ClientSecret);

    public static PluginSettings From(IReadOnlyDictionary<string, JsonElement> values)
    {
        var defaults = new PluginSettings();
        return new PluginSettings
        {
            Messages = Flag(values, "messages", defaults.Messages),
            Popup = Flag(values, "popup", defaults.Popup),
            Voice = Flag(values, "voice", defaults.Voice),
            ClientId = Text(values, "clientId"),
            ClientSecret = Text(values, "clientSecret"),
            Glow = Flag(values, "glow", defaults.Glow),
            Overlay = Flag(values, "overlay", defaults.Overlay),
        };
    }

    private static string Text(IReadOnlyDictionary<string, JsonElement> values, string key) =>
        values.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String ? (value.GetString() ?? "").Trim() : "";

    private static bool Flag(IReadOnlyDictionary<string, JsonElement> values, string key, bool fallback)
    {
        if (!values.TryGetValue(key, out var value))
        {
            return fallback;
        }
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            JsonValueKind.Number => value.GetDouble() != 0,
            _ => fallback,
        };
    }
}
