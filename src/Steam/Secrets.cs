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
    /// A broken file is moved aside first, so the next write does not destroy what is left in it.
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
        catch (JsonException e)
        {
            var broken = $"{path}.broken";
            string outcome;

            try
            {
                File.Move(path, broken, overwrite: true);
                outcome = $"so it was moved to {broken}";
            }
            catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException)
            {
                outcome = $"and moving it aside failed: {moveError.Message}";
            }

            Log.Warn($"{path} is not valid JSON ({e.Message}), {outcome}");

            return new T();
        }
    }

    /// <summary>
    /// Writes a JSON file that may hold secrets, readable only by the owner outside of Windows.
    /// It is written to a temporary file, flushed to disk and moved over the target, so neither readers
    /// in other processes nor a crash or power loss can leave a partial or empty file behind.
    /// </summary>
    public static void WriteJson<T>(string path, T value, JsonTypeInfo<T> typeInfo)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var temp = $"{path}.{Environment.ProcessId}.tmp";

        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                // Restricted before anything is written, so secrets are never readable by others
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(stream.SafeFileHandle, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }

                JsonSerializer.Serialize(stream, value, typeInfo);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            // Left over when anything failed
            File.Delete(temp);
        }
    }
}
