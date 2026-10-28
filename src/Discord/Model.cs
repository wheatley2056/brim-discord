using System.Text.RegularExpressions;
using Brim.Core.Plugins;

namespace Brim.Discord;

/// <summary>Wie weit die Verbindung zum Sprachkanal ist.</summary>
public enum VoiceStatus
{
    /// <summary>In den Einstellungen abgeschaltet.</summary>
    Off,
    /// <summary>Client-ID oder Secret fehlt.</summary>
    NotConfigured,
    /// <summary>Discord läuft nicht (oder noch nicht).</summary>
    DiscordClosed,
    /// <summary>Discord läuft, das Plugin wartet darauf, dass du es verbindest.</summary>
    NeedsAuthorization,
    /// <summary>Discord zeigt gerade die Frage, ob Brim zugreifen darf.</summary>
    Authorizing,
    Connected,
    /// <summary>Ein Fehler, den nur du beheben kannst (z. B. falsche Client-ID); steht in <see cref="VoiceSnapshot.Error"/>.</summary>
    Error,
}

/// <summary>Jemand im Sprachkanal.</summary>
public sealed record VoiceMember(string Id, string Name)
{
    /// <summary>Stummgeschaltet (selbst oder vom Server).</summary>
    public bool Muted { get; init; }

    public bool Deafened { get; init; }

    public bool Speaking { get; init; }

    /// <summary>Das bist du.</summary>
    public bool Self { get; init; }
}

public sealed record VoiceChannel(string Id, string Name, string? Guild, IReadOnlyList<VoiceMember> Members)
{
    public IEnumerable<VoiceMember> Speakers => Members.Where(m => m.Speaking);
}

/// <summary>Alles, was die Ansichten vom Sprachkanal brauchen; unveränderlich.</summary>
public sealed record VoiceSnapshot(VoiceStatus Status, VoiceChannel? Channel = null, bool SelfMuted = false, bool SelfDeafened = false, string? Error = null)
{
    public bool InChannel => Status == VoiceStatus.Connected && Channel is not null;
}

/// <summary>Eine Discord-Nachricht aus einer Windows-Benachrichtigung.</summary>
public sealed record DiscordMessage(string Author, string? Channel, string? Place, string Text, DateTimeOffset At)
{
    /// <summary>„#allgemein · Mein Server“, bei Direktnachrichten leer.</summary>
    public string Where => (Channel, Place) switch
    {
        ({ } c, { } p) => $"#{c} · {p}",
        (null, { } p) => p,
        ({ } c, null) => "#" + c,
        _ => "",
    };

    // Discord schreibt in den Titel „Name (#kanal, Server)“, in Gruppen „Name (Gruppe)“, bei Direktnachrichten nur den Namen.
    private static readonly Regex Title = new(@"^(?<author>.+?) \((?:#(?<channel>[^,]+), )?(?<place>[^()]+)\)$", RegexOptions.CultureInvariant);

    /// <summary>Liest eine Benachrichtigung von Discord; <c>null</c>, wenn sie keine Nachricht ist (z. B. „Discord wurde aktualisiert“).</summary>
    public static DiscordMessage? From(NotificationParams notification, DateTimeOffset at)
    {
        var title = notification.Title.Trim();
        var body = notification.Body.Trim();
        if (title.Length == 0 || body.Length == 0 || string.Equals(title, "Discord", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var match = Title.Match(title);
        if (!match.Success)
        {
            return new DiscordMessage(title, null, null, body, at);
        }
        var channel = match.Groups["channel"].Success ? match.Groups["channel"].Value.Trim() : null;
        return new DiscordMessage(match.Groups["author"].Value.Trim(), channel, match.Groups["place"].Value.Trim(), body, at);
    }
}
