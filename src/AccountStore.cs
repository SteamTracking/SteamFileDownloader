using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace SteamFileDownloader;

internal sealed record SavedAccount(string Username, string RefreshToken);

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
    /// Credentials of the given saved login, or the last one logged in when no username is given, or anonymous ones
    /// for the username anonymous. Returns null with an error explaining how to log in.
    /// </summary>
    public static LogOnCredentials? LoadCredentials(string? username)
    {
        if (LogOnCredentials.IsAnonymousName(username))
        {
            return LogOnCredentials.Anonymous;
        }

        var stored = Read();

        if ((username ?? stored.LastUsername) is not { } wanted || !stored.Accounts.TryGetValue(wanted, out var refreshToken))
        {
            var saved = stored.Accounts.Keys;

            Log.Error($"No saved login{(username == null ? "" : $" for {username}")}{(saved.Count > 0 ? $" (saved: {string.Join(", ", saved)})" : "")}. Run \"{Help.ExeName} login\"{(username == null ? "" : " with that account")} to save one; it needs a person to scan a QR code.");
            return null;
        }

        // Use the casing it was saved with
        var savedUsername = stored.Accounts.Keys.First(k => k.Equals(wanted, StringComparison.OrdinalIgnoreCase));

        try
        {
            return new LogOnCredentials(savedUsername, RefreshToken: Secrets.Unprotect(refreshToken));
        }
        catch (CryptographicException)
        {
            Log.Error($"The saved login for {savedUsername} can't be decrypted, since another Windows user or machine saved it. Run \"{Help.ExeName} login\" again with that account; it needs a person to scan a QR code.");
            return null;
        }
    }

    public static IReadOnlyCollection<string> GetUsernames() => Read().Accounts.Keys;

    /// <summary>
    /// Adds or replaces an account and makes it the default.
    /// </summary>
    public static void Save(SavedAccount account)
    {
        var stored = Read();

        // Steam usernames ignore case, so replace an existing entry with different casing
        stored.Accounts.Remove(account.Username);
        stored.Accounts[account.Username] = Secrets.Protect(account.RefreshToken);
        stored.LastUsername = account.Username;

        Secrets.WriteJson(FilePath, stored, AccountJsonContext.Default.StoredAccounts);
    }

    private static StoredAccounts Read()
    {
        var stored = Secrets.ReadJson(FilePath, AccountJsonContext.Default.StoredAccounts);
        stored.Accounts = new Dictionary<string, string>(stored.Accounts, StringComparer.OrdinalIgnoreCase);

        return stored;
    }
}
