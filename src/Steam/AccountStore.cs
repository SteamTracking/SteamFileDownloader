using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SteamFileDownloader;

internal sealed record SavedAccount(string Username, string RefreshToken);

internal sealed record SavedLogin(string Username, bool IsDefault, string? RefreshToken);

/// <summary>
/// Keeps refresh tokens from the login command so later runs never prompt. Tokens are DPAPI encrypted on Windows.
/// Several accounts can be saved, the last one logged in is the default.
/// </summary>
internal static partial class AccountStore
{
    private sealed class StoredAccounts
    {
        public string? LastUsername { get; set; }

        public Dictionary<string, string> Accounts { get; set; } = [];
    }

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(StoredAccounts))]
    private sealed partial class AccountJsonContext : JsonSerializerContext
    {
    }

    public static string FilePath { get; } = Path.Combine(Secrets.AppDataFolder, "account.json");

    /// <summary>
    /// Credentials of the given saved login, or the default one when no username is given, or anonymous ones for the
    /// username anonymous. Returns null with an error explaining how to log in.
    /// </summary>
    public static LogOnCredentials? LoadCredentials(string? username, string? path = null)
    {
        if (LogOnCredentials.IsAnonymousName(username))
        {
            return LogOnCredentials.Anonymous;
        }

        var stored = Read(path);

        if ((username ?? stored.LastUsername) is not { } wanted || !stored.Accounts.TryGetValue(wanted, out var refreshToken))
        {
            var saved = stored.Accounts.Keys;

            Log.Error($"No saved login{(username == null ? "" : $" for {username}")}{(saved.Count > 0 ? $" (saved: {string.Join(", ", saved)})" : "")}. Run \"{Help.ExeName} login\"{(username == null ? "" : " with that account")} to save one; it needs a person to scan a QR code.");
            return null;
        }

        // Use the casing it was saved with
        var savedUsername = stored.Accounts.Keys.First(k => k.Equals(wanted, StringComparison.OrdinalIgnoreCase));

        if (TryUnprotect(refreshToken) is not { } decrypted)
        {
            Log.Error($"{CantDecrypt(savedUsername)} Run \"{Help.ExeName} login\" again with that account; it needs a person to scan a QR code.");
            return null;
        }

        return new LogOnCredentials(savedUsername, RefreshToken: decrypted);
    }

    public static IReadOnlyCollection<string> GetUsernames() => Read().Accounts.Keys;

    /// <summary>
    /// Every saved login, the default first, then by username. RefreshToken is null when it can't be decrypted.
    /// </summary>
    public static List<SavedLogin> GetLogins(string? path = null)
    {
        var stored = Read(path);

        return [.. stored.Accounts
            .Select(a => new SavedLogin(a.Key, a.Key.Equals(stored.LastUsername, StringComparison.OrdinalIgnoreCase), TryUnprotect(a.Value)))
            .OrderByDescending(static l => l.IsDefault)
            .ThenBy(static l => l.Username, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// The saved login of a username, in any casing, or null when there is none.
    /// </summary>
    public static SavedLogin? Find(string username, string? path = null) =>
        GetLogins(path).Find(l => l.Username.Equals(username, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Adds or replaces an account and makes it the default. Accounts are kept in the order they were saved, the last
    /// saved last.
    /// </summary>
    public static void Save(SavedAccount account, string? path = null) => Update(path, stored =>
    {
        // Steam usernames ignore case, so replace an existing entry with different casing. A new dictionary, since one
        // would put a re-added key back in its old place
        stored.Accounts = new Dictionary<string, string>(stored.Accounts.Where(a => !a.Key.Equals(account.Username, StringComparison.OrdinalIgnoreCase)), StringComparer.OrdinalIgnoreCase)
        {
            [account.Username] = Secrets.Protect(account.RefreshToken),
        };
        stored.LastUsername = account.Username;
    });

    /// <summary>
    /// Replaces a saved account's token with a renewed one, keeping the default. Does nothing when the account was
    /// removed meanwhile.
    /// </summary>
    public static void Renew(string username, string refreshToken, string? path = null) => Update(path, stored =>
    {
        if (stored.Accounts.ContainsKey(username))
        {
            stored.Accounts[username] = Secrets.Protect(refreshToken);
        }
    });

    /// <summary>
    /// Forgets accounts. When the default is among them, the last saved of the remaining accounts becomes the default.
    /// Returns the default, or null when no account is left.
    /// </summary>
    public static string? Remove(IEnumerable<string> usernames, string? path = null) => Update(path, stored =>
    {
        foreach (var username in usernames)
        {
            stored.Accounts.Remove(username);
        }

        // Removing keeps the order of the others, so the last one is still the last saved
        if (stored.LastUsername == null || !stored.Accounts.ContainsKey(stored.LastUsername))
        {
            stored.LastUsername = stored.Accounts.Count > 0 ? stored.Accounts.Keys.Last() : null;
        }
    }).LastUsername;

    public static string CantDecrypt(string username) => $"The saved login for {username} can't be decrypted, since another Windows user or machine saved it.";

    public static string FormatDate(DateTimeOffset date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// When a refresh token stops working, read from the JWT's exp claim. Null when the token isn't a readable JWT.
    /// </summary>
    public static DateTimeOffset? GetExpiry(string refreshToken)
    {
        var parts = refreshToken.Split('.');

        if (parts.Length != 3)
        {
            return null;
        }

        try
        {
            using var payload = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1]));

            return payload.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : null;
        }
        catch (Exception e) when (e is FormatException or JsonException or ArgumentOutOfRangeException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string? TryUnprotect(string stored)
    {
        try
        {
            return Secrets.Unprotect(stored);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static StoredAccounts Read(string? path = null)
    {
        var stored = Secrets.ReadJson(path ?? FilePath, AccountJsonContext.Default.StoredAccounts);
        stored.Accounts = new Dictionary<string, string>(stored.Accounts, StringComparer.OrdinalIgnoreCase);

        return stored;
    }

    private static StoredAccounts Update(string? path, Action<StoredAccounts> change)
    {
        var stored = Read(path);
        change(stored);
        Secrets.WriteJson(path ?? FilePath, stored, AccountJsonContext.Default.StoredAccounts);

        return stored;
    }
}
