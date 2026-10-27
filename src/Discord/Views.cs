using System.Globalization;
using Brim.Core;
using Brim.Core.Plugins;
using Brim.Core.Widgets;

namespace Brim.Discord;

/// <summary>Alle Inhalte des Plugins als Widget-Bäume. Reine Funktionen, damit Tests sie prüfen können.</summary>
public static class Views
{
    /// <summary>Discords „Blurple“.</summary>
    public const string Accent = "#5865F2";

    /// <summary>Der grüne Ring um jemanden, der spricht.</summary>
    public const string Speaking = "#23A55A";

    public static readonly string MicGlyph = char.ConvertFromUtf32(0xE720);
    public static readonly string MicOffGlyph = char.ConvertFromUtf32(0xEC54);
    public static readonly string HeadphonesGlyph = char.ConvertFromUtf32(0xE7F6);
    public static readonly string HangUpGlyph = char.ConvertFromUtf32(0xE778);
    public static readonly string ChatGlyph = char.ConvertFromUtf32(0xE8BD);
    public static readonly string OpenGlyph = char.ConvertFromUtf32(0xE8A7);
    public static readonly string CheckGlyph = char.ConvertFromUtf32(0xE73E);
    public static readonly string LinkGlyph = char.ConvertFromUtf32(0xE71B);
    public static readonly string DotGlyph = char.ConvertFromUtf32(0xE91F);

    public const string VoiceActivityId = "voice";
    public const string MessagesActivityId = "messages";

    public const string MuteAction = "mute";
    public const string DeafAction = "deaf";
    public const string LeaveAction = "leave";
    public const string AuthorizeAction = "authorize";
    public const string OpenAction = "open";
    public const string ReadAction = "read";

    public const string SetupText = "Client-ID und Secret unter Einstellungen → Plugins → Discord eintragen";

    // ───────── Texte ─────────

    /// <summary>„Anna spricht“, „Anna, Ben sprechen“, „Anna, Ben +2“.</summary>
    public static string Speakers(IReadOnlyList<VoiceMember> speakers) => speakers.Count switch
    {
        0 => "",
        1 => speakers[0].Name + " spricht",
        2 => $"{speakers[0].Name}, {speakers[1].Name} sprechen",
        _ => $"{speakers[0].Name}, {speakers[1].Name} +{speakers.Count - 2}",
    };

    /// <summary>Kompakt: wer spricht, sonst „Kanal · 4“.</summary>
    public static string Compact(VoiceChannel channel, CultureInfo culture)
    {
        var speakers = channel.Speakers.ToList();
        return speakers.Count > 0 ? Speakers(speakers) : $"{channel.Name} · {channel.Members.Count.ToString(culture)}";
    }

    /// <summary>„Kanal · Server“ bzw. nur der Kanal.</summary>
    public static string Title(VoiceChannel channel) => channel.Guild is { Length: > 0 } guild ? $"{channel.Name} · {guild}" : channel.Name;

    /// <summary>Für die Sprachausgabe: „Sprachkanal Allgemein · Freunde, 3 Personen, Anna spricht, du bist stumm“.</summary>
    public static string Describe(VoiceSnapshot voice, CultureInfo culture)
    {
        if (voice.Channel is not { } channel)
        {
            return "Discord, in keinem Sprachkanal";
        }
        var parts = new List<string> { "Sprachkanal " + Title(channel), $"{channel.Members.Count.ToString(culture)} Personen" };
        var speakers = channel.Speakers.ToList();
        if (speakers.Count > 0)
        {
            parts.Add(string.Join(", ", speakers.Select(s => s.Name)) + (speakers.Count == 1 ? " spricht" : " sprechen"));
        }
        if (voice.SelfDeafened)
        {
            parts.Add("du bist taub");
        }
        else if (voice.SelfMuted)
        {
            parts.Add("du bist stumm");
        }
        return string.Join(", ", parts);
    }

    /// <summary>„Anna in #allgemein · Freunde: Hallo“.</summary>
    public static string Describe(DiscordMessage m) => m.Where.Length > 0 ? $"{m.Author} in {m.Where}: {m.Text}" : $"{m.Author}: {m.Text}";

    // ───────── Sprachkanal in der Notch ─────────

    /// <summary>
    /// Die Aktivität „voice“, solange du in einem Sprachkanal bist: Mikrofon (rot, wenn du stumm oder taub bist) und wer
    /// spricht. Spricht jemand, schimmert die Notch in Blurple; im Vollbild bleibt das als kleine Pille stehen.
    /// </summary>
    public static ActivityParams VoiceActivity(VoiceSnapshot voice, bool glow, bool overlay, CultureInfo culture)
    {
        var channel = voice.Channel!;
        var speaking = channel.Speakers.Any();
        var text = Compact(channel, culture);
        return new ActivityParams
        {
            Id = VoiceActivityId,
            Priority = ActivityPriority.Ongoing,
            Leading = StatusIcon(voice, channel, 14),
            Trailing = new TextWidget(text)
            {
                Style = TextStyle.Caption,
                FontSize = 12,
                Color = speaking ? BrimColors.Fog : BrimColors.Slate,
                AccessibleName = Describe(voice, culture),
            },
            Bead = StatusIcon(voice, channel, 8),
            Expanded = VoiceExpanded(voice, culture),
            Content = overlay
                ? new StackWidget(StatusIcon(voice, channel, 12), new TextWidget(text) { Style = TextStyle.Caption, FontSize = 12, Color = BrimColors.Fog }) { Spacing = 6 }
                : null,
            Overlay = overlay,
            Accent = Accent,
            Glow = glow && speaking ? 0.6 : null,
            Rank = 6,
        };
    }

    /// <summary>Mikrofon: rot durchgestrichen, wenn du stumm bist, rote Kopfhörer, wenn du taub bist, grün, wenn du sprichst.</summary>
    private static IconWidget StatusIcon(VoiceSnapshot voice, VoiceChannel channel, double size) =>
        voice.SelfDeafened ? new IconWidget(HeadphonesGlyph) { Size = size, Color = BrimColors.Danger }
        : voice.SelfMuted ? new IconWidget(MicOffGlyph) { Size = size, Color = BrimColors.Danger }
        : new IconWidget(MicGlyph) { Size = size, Color = channel.Members.Any(m => m.Self && m.Speaking) ? Speaking : Accent };

    /// <summary>Aufgeklappt: Kanal, wer drin ist und wer spricht, darunter Stumm, Taub und Auflegen.</summary>
    public static Widget VoiceExpanded(VoiceSnapshot voice, CultureInfo culture)
    {
        var channel = voice.Channel!;
        var children = new List<Widget> { VoiceHeader(channel, culture) };
        children.AddRange(channel.Members.Take(6).Select(MemberRow));
        if (channel.Members.Count > 6)
        {
            children.Add(new TextWidget($"und {(channel.Members.Count - 6).ToString(culture)} weitere") { Style = TextStyle.Caption });
        }
        children.Add(new StackWidget(VoiceButtons(voice, 40)) { Spacing = 10, Justify = Justify.Center, Padding = (0, 4, 0, 0) });
        return new StackWidget(children)
        {
            Orientation = StackOrientation.Vertical,
            Spacing = 4,
            Align = CrossAlign.Stretch,
        };
    }

    /// <summary>Stumm, Taub (gefüllt, wenn an) und Auflegen.</summary>
    public static IReadOnlyList<Widget> VoiceButtons(VoiceSnapshot voice, double size) =>
    [
        new ButtonWidget(MuteAction)
        {
            Kind = ButtonKind.Round,
            Glyph = voice.SelfMuted ? MicOffGlyph : MicGlyph,
            Selected = voice.SelfMuted,
            Size = size,
            GlyphSize = size * 0.4,
            AccessibleName = voice.SelfMuted ? "Stummschaltung aufheben" : "Stummschalten",
        },
        new ButtonWidget(DeafAction)
        {
            Kind = ButtonKind.Round,
            Glyph = HeadphonesGlyph,
            Selected = voice.SelfDeafened,
            Size = size,
            GlyphSize = size * 0.4,
            AccessibleName = voice.SelfDeafened ? "Wieder hören" : "Taub schalten",
        },
        new ButtonWidget(LeaveAction)
        {
            Kind = ButtonKind.Round,
            Glyph = HangUpGlyph,
            GlyphColor = BrimColors.Danger,
            Size = size,
            GlyphSize = size * 0.4,
            AccessibleName = "Sprachkanal verlassen",
        },
    ];

    /// <summary>Eine Person: grüner Punkt, solange sie spricht, Name, rechts stumm/taub.</summary>
    public static Widget MemberRow(VoiceMember m)
    {
        var children = new List<Widget>
        {
            new IconWidget(DotGlyph) { Size = 8, Color = m.Speaking ? Speaking : BrimColors.Track },
            new TextWidget(m.Self ? m.Name + " (du)" : m.Name)
            {
                Style = m.Speaking ? TextStyle.Body : TextStyle.Secondary,
                FontSize = 12,
                Grow = 1,
            },
        };
        if (m.Deafened)
        {
            children.Add(new IconWidget(HeadphonesGlyph) { Size = 11, Color = BrimColors.Danger });
        }
        else if (m.Muted)
        {
            children.Add(new IconWidget(MicOffGlyph) { Size = 11, Color = BrimColors.Danger });
        }
        var state = m.Deafened ? ", taub" : m.Muted ? ", stumm" : "";
        return new StackWidget(children) { Spacing = 6, AccessibleName = m.Name + (m.Speaking ? ", spricht" : "") + state };
    }

    private static StackWidget VoiceHeader(VoiceChannel channel, CultureInfo culture) =>
        Header(MicGlyph, Title(channel), channel.Members.Count.ToString(culture), null, null);

    // ───────── Widget „Sprachkanal“ ─────────

    public static Widget VoiceWidget(VoiceSnapshot voice, TileSize size, CultureInfo culture)
    {
        var small = size == TileSize.Small;
        switch (voice.Status)
        {
            case VoiceStatus.Off:
                return TileCard.Empty(MicGlyph, small ? "Aus" : "Sprachkanal ist ausgeschaltet");
            case VoiceStatus.NotConfigured:
                return TileCard.Empty(MicGlyph, small ? "Nicht eingerichtet" : SetupText);
            case VoiceStatus.DiscordClosed:
                return TileCard.Empty(MicGlyph, small ? "Discord zu" : "Discord ist nicht geöffnet");
            case VoiceStatus.Authorizing:
                return TileCard.Empty(MicGlyph, small ? "In Discord bestätigen" : "In Discord auf „Autorisieren“ klicken");
            case VoiceStatus.NeedsAuthorization or VoiceStatus.Error:
                var button = new ButtonWidget(AuthorizeAction)
                {
                    Kind = ButtonKind.Secondary,
                    Glyph = LinkGlyph,
                    Label = small ? "Verbinden" : voice.Status == VoiceStatus.Error ? "Neu verbinden" : "Mit Discord verbinden",
                    GlyphSize = 12,
                };
                Widget body = small || voice.Error is null ? button
                    : new StackWidget(new TextWidget(voice.Error) { Style = TextStyle.Caption, Color = BrimColors.Warning, Grow = 1 }, button) { Spacing = 8 };
                return TileCard.Card(Header(MicGlyph, "Discord", null, null, null), body);
        }
        if (voice.Channel is not { } channel)
        {
            return TileCard.Empty(MicGlyph, small ? "Kein Kanal" : "In keinem Sprachkanal");
        }

        var header = Header(MicGlyph, small ? channel.Name : Title(channel), channel.Members.Count.ToString(culture), null, null);
        if (small)
        {
            return TileCard.Card(header, new StackWidget(VoiceButtons(voice, 30).Take(2).ToList()) { Spacing = 6, Justify = Justify.End });
        }
        var speakers = channel.Speakers.ToList();
        if (size == TileSize.Large)
        {
            var rows = channel.Members.Take(4).Select(MemberRow).ToList<Widget>();
            rows.Add(new StackWidget(VoiceButtons(voice, 32)) { Spacing = 8, Justify = Justify.End });
            return TileCard.Card(header, new StackWidget(rows) { Orientation = StackOrientation.Vertical, Spacing = 3, Align = CrossAlign.Stretch });
        }
        Widget who = size == TileSize.Wide
            ? new StackWidget(channel.Members.Take(5).Select(Chip).ToList()) { Spacing = 10, Grow = 1 }
            : new TextWidget(speakers.Count > 0 ? Speakers(speakers) : "Gerade still")
            {
                Style = speakers.Count > 0 ? TextStyle.Body : TextStyle.Secondary,
                FontSize = 12,
                Grow = 1,
            };
        var line = new List<Widget> { who };
        line.AddRange(VoiceButtons(voice, 30));
        return TileCard.Card(header, new StackWidget(line) { Spacing = 6 });
    }

    /// <summary>Breit: Punkt und Name nebeneinander.</summary>
    private static Widget Chip(VoiceMember m) => new StackWidget(
        new IconWidget(DotGlyph) { Size = 8, Color = m.Speaking ? Speaking : BrimColors.Track },
        new TextWidget(m.Name) { Style = m.Speaking ? TextStyle.Body : TextStyle.Secondary, FontSize = 12 })
    {
        Spacing = 4,
        AccessibleName = m.Name + (m.Speaking ? ", spricht" : ""),
    };

    // ───────── Nachrichten ─────────

    /// <summary>Die Aktivität „messages“, solange es ungelesene Nachrichten gibt: Sprechblase und „3 neu“.</summary>
    public static ActivityParams MessagesActivity(IReadOnlyList<DiscordMessage> messages, int unread, CultureInfo culture)
    {
        var text = unread == 1 ? "1 neue" : $"{unread.ToString(culture)} neu";
        return new ActivityParams
        {
            Id = MessagesActivityId,
            Priority = ActivityPriority.Ongoing,
            Leading = new IconWidget(ChatGlyph) { Size = 14, Color = Accent },
            Trailing = new TextWidget(text)
            {
                Style = TextStyle.Caption,
                FontSize = 12,
                Color = BrimColors.Fog,
                AccessibleName = unread == 1 ? "Eine neue Discord-Nachricht" : $"{unread.ToString(culture)} neue Discord-Nachrichten",
            },
            Bead = new IconWidget(ChatGlyph) { Size = 8, Color = Accent },
            Expanded = MessagesExpanded(messages, unread, culture),
            Accent = Accent,
            Rank = 4,
        };
    }

    /// <summary>Aufgeklappt: die neuesten Nachrichten, darunter „Discord öffnen“ und „Gelesen“.</summary>
    public static Widget MessagesExpanded(IReadOnlyList<DiscordMessage> messages, int unread, CultureInfo culture)
    {
        var children = new List<Widget> { Header(ChatGlyph, "Discord", unread > 0 ? $"{unread.ToString(culture)} neu" : null, null, OpenAction) };
        children.AddRange(messages.Take(5).Select((m, i) => MessageRow(m, true, i < unread, culture)));
        children.Add(new StackWidget(
            new ButtonWidget(OpenAction) { Kind = ButtonKind.Secondary, Glyph = OpenGlyph, Label = "Discord öffnen", GlyphSize = 12 },
            new ButtonWidget(ReadAction) { Kind = ButtonKind.Secondary, Glyph = CheckGlyph, Label = "Gelesen", GlyphSize = 12 })
        {
            Spacing = 8,
            Justify = Justify.End,
            Padding = (0, 4, 0, 0),
        });
        return new StackWidget(children)
        {
            Orientation = StackOrientation.Vertical,
            Spacing = 4,
            Align = CrossAlign.Stretch,
        };
    }

    /// <summary>Kurze Meldung: „Anna · #allgemein · Freunde“, darunter die Nachricht.</summary>
    public static HudParams MessageHud(DiscordMessage m)
    {
        var from = m.Where.Length > 0 ? $"{m.Author} · {m.Where}" : m.Author;
        return new HudParams
        {
            Id = "message",
            Content = new StackWidget(
                new IconWidget(ChatGlyph) { Size = 16, Color = Accent },
                new StackWidget(
                    new TextWidget(from) { Style = TextStyle.Caption },
                    new TextWidget(m.Text) { Style = TextStyle.Body })
                {
                    Orientation = StackOrientation.Vertical,
                    Spacing = 2,
                    Align = CrossAlign.Start,
                    Grow = 1,
                })
            {
                Spacing = 12,
                Padding = (18, 0, 18, 0),
            },
            DurationMs = 6000,
            Announcement = "Discord: " + Describe(m),
        };
    }

    // ───────── Widget „Nachrichten“ ─────────

    public static Widget MessagesWidget(IReadOnlyList<DiscordMessage> messages, int unread, bool enabled, TileSize size, CultureInfo culture)
    {
        if (!enabled)
        {
            return TileCard.Empty(ChatGlyph, "Nachrichten sind ausgeschaltet");
        }
        if (messages.Count == 0)
        {
            return TileCard.Empty(ChatGlyph, "Keine neuen Nachrichten");
        }
        var header = Header(ChatGlyph, "Discord", unread > 0 ? $"{unread.ToString(culture)} neu" : null, null, OpenAction);
        var wide = size is TileSize.Wide or TileSize.Full;
        var rows = size is TileSize.Large or TileSize.Full ? 5 : 1;
        return TileCard.Card(header, new StackWidget(messages.Take(rows).Select((m, i) => MessageRow(m, wide, i < unread, culture)).ToList())
        {
            Orientation = StackOrientation.Vertical,
            Spacing = 3,
            Align = CrossAlign.Stretch,
        });
    }

    /// <summary>Eine Zeile: Absender, Nachricht (hell, solange ungelesen), breit auch Kanal und Uhrzeit. Ein Klick öffnet Discord.</summary>
    public static Widget MessageRow(DiscordMessage m, bool wide, bool unread, CultureInfo culture)
    {
        var children = new List<Widget>
        {
            new TextWidget(m.Author) { Style = TextStyle.Body, FontSize = 12, Color = unread ? BrimColors.Fog : BrimColors.Slate, FixedWidth = wide ? 110 : 80 },
            new TextWidget(m.Text) { Style = unread ? TextStyle.Body : TextStyle.Secondary, FontSize = 12, Grow = 1 },
        };
        if (wide && m.Where.Length > 0)
        {
            children.Add(new TextWidget(m.Where) { Style = TextStyle.Caption, FixedWidth = 140 });
        }
        if (wide)
        {
            children.Add(new TextWidget(m.At.ToLocalTime().ToString("t", culture)) { Style = TextStyle.Caption, FixedWidth = 40 });
        }
        return new StackWidget(children) { Spacing = 6, ActionId = OpenAction, AccessibleName = Describe(m) };
    }

    // ───────── Bausteine ─────────

    /// <summary>Kopfzeile wie <see cref="TileCard.Header"/> in Blurple, rechts optional ein farbiger Hinweis.</summary>
    public static StackWidget Header(string glyph, string title, string? trailing, string? trailingColor, string? actionId)
    {
        var children = new List<Widget>
        {
            new IconWidget(glyph) { Size = 12, Color = Accent },
            new TextWidget(title) { Style = TextStyle.Caption, Grow = 1 },
        };
        if (trailing is not null)
        {
            children.Add(new TextWidget(trailing) { Style = TextStyle.Caption, Color = trailingColor });
        }
        return new StackWidget(children) { Spacing = 6, ActionId = actionId };
    }
}
