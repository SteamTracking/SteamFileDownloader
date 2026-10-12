using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;

namespace SteamFileDownloader;

/// <summary>
/// Caches depot keys, which never change, so each depot's key is only requested once.
/// </summary>
internal static partial class DepotCache
{
    private static readonly string DepotKeysPath = Path.Combine(Secrets.AppDataFolder, "depot_keys.json");
    private static readonly Lock DepotKeysLock = new();
    private static SortedDictionary<uint, string>? DepotKeys;

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(SortedDictionary<uint, string>))]
    private sealed partial class DepotKeysJsonContext : JsonSerializerContext
    {
    }

    private static JsonTypeInfo<SortedDictionary<uint, string>> DepotKeysTypeInfo => DepotKeysJsonContext.Default.SortedDictionaryUInt32String;

    public static byte[]? LoadDepotKey(uint depotID)
    {
        lock (DepotKeysLock)
        {
            DepotKeys ??= Secrets.ReadJson(DepotKeysPath, DepotKeysTypeInfo);

            if (!DepotKeys.TryGetValue(depotID, out var hex))
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
                DepotKeys = Secrets.ReadJson(DepotKeysPath, DepotKeysTypeInfo);
                DepotKeys[depotID] = Convert.ToHexString(depotKey);

                Secrets.WriteJson(DepotKeysPath, DepotKeys, DepotKeysTypeInfo);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Like another process writing it at the same time, the key is still used for this run
                Log.Warn($"Could not cache the depot key for {depotID}: {e.Message}");
            }
        }
    }
}
