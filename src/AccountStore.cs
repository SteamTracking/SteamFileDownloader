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
    /// Credentials of the given saved login, or the last one logged in when no username is given.
    /// Returns null with an error explaining how to log in.
    /// </summary>
    public static LogOnCredentials? LoadCredentials(string? username)
    {
        var stored = Read();

        if ((username ?? stored.LastUsername) is not { } wanted || !stored.Accounts.TryGetValue(wanted, out var refreshToken))
        {
            var saved = stored.Accounts.Keys;

            Log.Error($"No saved login{(username == null ? "" : $" for {username}")}. Run \"{Help.ExeName} login\"{(username == null ? "" : " with that account")} to save one with a QR code{(saved.Count > 0 ? $". Saved logins: {string.Join(", ", saved)}" : "")}.");
            return null;
        }

        // Use the casing it was saved with
        var savedUsername = stored.Accounts.Keys.First(k => k.Equals(wanted, StringComparison.OrdinalIgnoreCase));

        LogOnCredentials credentials;

        try
        {
            credentials = new LogOnCredentials(savedUsername, RefreshToken: Secrets.Unprotect(refreshToken));
        }
        catch (CryptographicException)
        {
            Log.Error($"The saved login for {savedUsername} can't be decrypted, it was saved by another Windows user or machine. Run \"{Help.ExeName} login\" again.");
            return null;
        }

        Log.Info($"Using saved login {savedUsername}");

        return credentials;
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

        Secrets.WriteSecretJson(FilePath, stored, AccountJsonContext.Default.StoredAccounts);
    }

    private static StoredAccounts Read()
    {
        var stored = Secrets.ReadJson(FilePath, AccountJsonContext.Default.StoredAccounts);
        stored.Accounts = new Dictionary<string, string>(stored.Accounts, StringComparer.OrdinalIgnoreCase);

        return stored;
    }
}
