// Discord-Plugin für Brim: Sprachkanal in der Notch (wer spricht, Stumm, Taub, Auflegen) über die lokale
// RPC-Schnittstelle des Discord-Clients, dazu Discord-Nachrichten aus den Windows-Benachrichtigungen.
using System.Diagnostics;
using System.Reflection;
using Brim.Discord;
using Brim.Sdk;

BrimPlugin brim;
try
{
    brim = await BrimPlugin.ConnectAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Keine Verbindung zu Brim: {ex.Message}");
    return 1;
}

await using (brim)
{
    var settings = PluginSettings.From(brim.Settings);
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
    http.DefaultRequestHeaders.UserAgent.ParseAdd($"BrimDiscord/{version} (+https://github.com/wheatley2056/brim-discord)");

    var voice = new VoiceClient(DiscordPipes.ConnectAsync, new DiscordOAuth(http, TimeProvider.System), FileTokenStore.Default(), TimeProvider.System,
        (message, level) =>
        {
            Console.WriteLine(message);
            _ = brim.LogAsync(message, level);
        });
    var plugin = new DiscordPlugin(new BrimSink(brim), voice, OpenDiscord, TimeProvider.System, settings, brim.Permissions.Contains("overlay"));
    Console.WriteLine(settings.VoiceConfig.Configured
        ? $"Discord-App {settings.ClientId}, Sprachkanal {(settings.Voice ? "an" : "aus")}"
        : "Sprachkanal ist noch nicht eingerichtet (Client-ID und Secret fehlen)");

    brim.ActionInvoked += (action, value) => _ = plugin.HandleActionAsync(action);
    brim.SettingsChanged += values => _ = plugin.ApplySettingsAsync(PluginSettings.From(values));
    brim.WidgetsPlaced += placed => _ = plugin.SetPlacedWidgetsAsync(placed);
    brim.NotificationPosted += notification => _ = plugin.OnNotificationAsync(notification);
    _ = plugin.SetPlacedWidgetsAsync(brim.PlacedWidgets);

    using var stop = new CancellationTokenSource();
    var loop = voice.RunAsync(stop.Token);
    await brim.RunAsync();

    // Brim gibt 2 Sekunden zum Beenden.
    stop.Cancel();
    await Task.WhenAny(loop, Task.Delay(500));
}
return 0;

// Öffnet den Discord-Client über sein Protokoll; fest verdrahtet, damit aus einer Nachricht nichts anderes startet.
static void OpenDiscord()
{
    try
    {
        Process.Start(new ProcessStartInfo("discord://-/channels/@me") { UseShellExecute = true })?.Dispose();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Konnte Discord nicht öffnen: {ex.Message}");
    }
}
