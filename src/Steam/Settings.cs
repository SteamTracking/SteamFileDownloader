using System;
using System.IO;
using System.Text.Json.Serialization;

namespace SteamFileDownloader;

/// <summary>
/// Settings saved by the config command, which apply to every later run.
/// </summary>
internal static partial class Settings
{
    internal sealed class StoredSettings
    {
        // The absolute root output folder for commands run without --output
        public string? Output { get; set; }
    }

    [JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(StoredSettings))]
    private sealed partial class SettingsJsonContext : JsonSerializerContext
    {
    }

    public static string FilePath { get; } = Path.Combine(Secrets.AppDataFolder, "settings.json");

    public static StoredSettings Read(string? path = null) => Secrets.ReadJson(path ?? FilePath, SettingsJsonContext.Default.StoredSettings);

    /// <summary>
    /// The saved settings, or null after an error naming the file when it can't be read, like when another program
    /// locks it.
    /// </summary>
    public static StoredSettings? TryRead(string? path = null)
    {
        try
        {
            return Read(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Failed to read the settings in {path ?? FilePath}: {e.Message}");
            return null;
        }
    }

    public static void Write(StoredSettings settings, string? path = null) =>
        Secrets.WriteJson(path ?? FilePath, settings, SettingsJsonContext.Default.StoredSettings);

    /// <summary>
    /// The absolute root output folder: the one given on the command line, or else the saved one, or else depots in the
    /// current folder. Null after an error when the settings can't be read.
    /// </summary>
    public static string? GetOutputRoot(string? output, string? path = null)
    {
        if (output != null)
        {
            return Path.GetFullPath(output);
        }

        var settings = TryRead(path);

        if (settings == null)
        {
            return null;
        }

        return Path.GetFullPath(settings.Output ?? "depots");
    }
}
