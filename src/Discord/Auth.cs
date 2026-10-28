using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Brim.Discord;

/// <summary>Zugang zur RPC-Schnittstelle, wie Discord ihn nach dem Bestätigen ausstellt.</summary>
public sealed record OAuthToken(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt);

/// <summary>Tauscht den Code aus <c>AUTHORIZE</c> gegen ein Token und erneuert es.</summary>
public interface IOAuth
{
    Task<OAuthToken> ExchangeAsync(string clientId, string clientSecret, string code, CancellationToken cancellationToken);

    Task<OAuthToken> RefreshAsync(string clientId, string clientSecret, string refreshToken, CancellationToken cancellationToken);
}

public sealed class DiscordOAuth(HttpClient http, TimeProvider time) : IOAuth
{
    public const string TokenUrl = "https://discord.com/api/v10/oauth2/token";

    /// <summary>
    /// Muss in der Discord-App unter OAuth2 → Redirects stehen. Aufgerufen wird sie nie: Den Code liefert der Discord-Client
    /// direkt über RPC, Discord prüft beim Tausch aber, dass die Adresse eingetragen ist.
    /// </summary>
    public const string RedirectUri = "http://localhost";

    public Task<OAuthToken> ExchangeAsync(string clientId, string clientSecret, string code, CancellationToken cancellationToken) =>
        PostAsync(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
        }, cancellationToken);

    public Task<OAuthToken> RefreshAsync(string clientId, string clientSecret, string refreshToken, CancellationToken cancellationToken) =>
        PostAsync(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        }, cancellationToken);

    private async Task<OAuthToken> PostAsync(Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsync(TokenUrl, new FormUrlEncodedContent(form), cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new OAuthException(Describe(body, (int)response.StatusCode));
        }
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken)
            ?? throw new OAuthException("Leere Antwort von Discord");
        return new OAuthToken(token.AccessToken, token.RefreshToken, time.GetUtcNow().AddSeconds(token.ExpiresIn));
    }

    /// <summary>Discords Fehler (<c>{"error": "invalid_client", "error_description": …}</c>) in Worten.</summary>
    public static string Describe(string body, int status)
    {
        string? error = null;
        string? description = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            error = DiscordRpc.Str(doc.RootElement, "error");
            description = DiscordRpc.Str(doc.RootElement, "error_description");
        }
        catch (JsonException)
        {
        }
        return error switch
        {
            "invalid_client" => "Client-ID oder Client-Secret stimmt nicht",
            "invalid_request" when description?.Contains("redirect", StringComparison.OrdinalIgnoreCase) == true =>
                $"In der Discord-App fehlt der Redirect {RedirectUri}",
            "invalid_grant" => "Discord hat die Anmeldung nicht angenommen, bitte neu verbinden",
            null => $"Discord antwortet mit {status}",
            _ => $"Discord: {description ?? error}",
        };
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] double ExpiresIn);
}

public sealed class OAuthException(string message) : Exception(message);

/// <summary>Merkt sich das Token zwischen zwei Starts, damit Discord nur einmal nachfragt.</summary>
public interface ITokenStore
{
    OAuthToken? Load(string clientId);

    void Save(string clientId, OAuthToken token);

    void Clear();
}

/// <summary>
/// Token als Datei unter <c>%LocalAppData%\Brim\PluginData\de.brim.discord</c>, nicht im Plugin-Ordner (der wird beim
/// Aktualisieren ersetzt). Unter Windows mit DPAPI verschlüsselt, sodass nur dein Windows-Konto es lesen kann.
/// </summary>
public sealed class FileTokenStore(string path) : ITokenStore
{
    public static FileTokenStore Default() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Brim", "PluginData", "de.brim.discord", "token.bin"));

    private static readonly byte[] Entropy = "Brim Discord"u8.ToArray();

    public OAuthToken? Load(string clientId)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }
            var bytes = File.ReadAllBytes(path);
            if (OperatingSystem.IsWindows())
            {
                bytes = ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser);
            }
            var stored = JsonSerializer.Deserialize<Stored>(bytes);
            return stored is not null && stored.ClientId == clientId ? stored.Token : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or CryptographicException)
        {
            return null;
        }
    }

    public void Save(string clientId, OAuthToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Stored(clientId, token));
        if (OperatingSystem.IsWindows())
        {
            bytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    public void Clear()
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record Stored(string ClientId, OAuthToken Token);
}
