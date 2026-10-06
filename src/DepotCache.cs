using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace SteamFileDownloader;

/// <summary>
/// Caches depot keys, which never change, so each depot's key is only requested once.
/// </summary>
internal static partial class DepotCache
{
    private static readonly string DepotKeysPath = Path.Combine(Secrets.AppDataFolder, "depot_keys.json");
    private static readonly Lock DepotKeysLock = new();
    private static Dictionary<string, string>? DepotKeys;

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(Dictionary<string, string>))]
    private sealed partial class DepotKeysJsonContext : JsonSerializerContext
    {
    }

    public static byte[]? LoadDepotKey(uint depotID)
    {
        lock (DepotKeysLock)
        {
            DepotKeys ??= Secrets.ReadJson(DepotKeysPath, DepotKeysJsonContext.Default.DictionaryStringString);

            if (!DepotKeys.TryGetValue(depotID.ToString(CultureInfo.InvariantCulture), out var hex))
            {
                return null;
            }

            try
            {
                return Convert.FromHexString(hex);
            }
            catch (FormatException)
            {
                // A broken entry is requested from Steam again, and replaced
                return null;
            }
        }
    }

    public static void SaveDepotKey(uint depotID, byte[] depotKey)
    {
        lock (DepotKeysLock)
        {
            try
            {
                // Re-read so keys saved by other processes meanwhile are kept
                DepotKeys = Secrets.ReadJson(DepotKeysPath, DepotKeysJsonContext.Default.DictionaryStringString);
                DepotKeys[depotID.ToString(CultureInfo.InvariantCulture)] = Convert.ToHexString(depotKey);

                Secrets.WriteAtomic(DepotKeysPath, temp => File.WriteAllText(temp, JsonSerializer.Serialize(DepotKeys, DepotKeysJsonContext.Default.DictionaryStringString)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Like another process writing it at the same time, the key is still used for this run
                Log.Warn($"Could not cache the depot key for {depotID}: {e.Message}");
            }
        }
    }
}
