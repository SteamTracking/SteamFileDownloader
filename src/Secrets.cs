using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace SteamFileDownloader;

/// <summary>
/// Where the tool keeps its own files, and how secrets in them are stored.
/// </summary>
internal static class Secrets
{
    public static string AppDataFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
        nameof(SteamFileDownloader)
    );

    // Windows encrypts secrets with DPAPI for the current user, elsewhere the files are only readable by their owner
    private const string ProtectedPrefix = "dpapi:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes(nameof(SteamFileDownloader));

    public static string Protect(string secret)
    {
        if (!OperatingSystem.IsWindows())
        {
            return secret;
        }

        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser);

        return ProtectedPrefix + Convert.ToBase64String(encrypted);
    }

    /// <summary>
    /// Decrypts a value from <see cref="Protect"/>, values without the prefix are returned as they are.
    /// </summary>
    /// <exception cref="CryptographicException">The value was encrypted by another user or machine.</exception>
    public static string Unprotect(string stored)
    {
        if (!stored.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
        {
            return stored;
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new CryptographicException("DPAPI encrypted values can only be read on Windows.");
        }

        var decrypted = ProtectedData.Unprotect(Convert.FromBase64String(stored[ProtectedPrefix.Length..]), Entropy, DataProtectionScope.CurrentUser);

        return Encoding.UTF8.GetString(decrypted);
    }

    /// <summary>
    /// Reads a JSON file, or returns a new value when it is missing or broken.
    /// </summary>
    public static T ReadJson<T>(string path, JsonTypeInfo<T> typeInfo)
        where T : new()
    {
        if (!File.Exists(path))
        {
            return new T();
        }

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), typeInfo) ?? new T();
        }
        catch (JsonException)
        {
            return new T();
        }
    }

    /// <summary>
    /// Writes a JSON file that holds secrets, readable only by the owner outside of Windows.
    /// </summary>
    public static void WriteSecretJson<T>(string path, T value, JsonTypeInfo<T> typeInfo)
    {
        WriteAtomic(path, temp =>
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(value, typeInfo));

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        });
    }

    /// <summary>
    /// Writes a temporary file and moves it over the target, so readers in other processes never see a partial file.
    /// </summary>
    public static void WriteAtomic(string path, Action<string> writeTemp)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var temp = $"{path}.{Environment.ProcessId}.tmp";
        writeTemp(temp);
        File.Move(temp, path, overwrite: true);
    }
}
