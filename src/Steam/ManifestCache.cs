using System;
using System.Globalization;
using System.IO;
using SteamKit2;

namespace SteamFileDownloader;

/// <summary>
/// Downloaded manifests, kept as &lt;folder&gt;/&lt;depot&gt;_&lt;manifest&gt;.manifest like in Steam's depotcache folder, whose files can be used too.
/// </summary>
internal static class ManifestCache
{
    public static string GetPath(string folder, uint depotID, ulong manifestID) =>
        Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"{depotID}_{manifestID}.manifest"));

    /// <summary>
    /// The job's saved manifest with its file names decrypted, or null when there is none or it can't be used.
    /// </summary>
    public static DepotManifest? Load(ManifestJob job)
    {
        var path = job.ManifestFile;

        if (path == null || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var manifest = DepotManifest.LoadFromFile(path);

            if (manifest == null || manifest.DepotID != job.DepotID || manifest.ManifestGID != job.ManifestID)
            {
                Log.Warn($"[{job}] {path} has the manifest {manifest?.DepotID}:{manifest?.ManifestGID}, so it is downloaded again.");
                return null;
            }

            // Saved like served, with encrypted file names, as in Steam's depotcache files
            if (manifest.FilenamesEncrypted && !manifest.DecryptFilenames(job.DepotKey!))
            {
                Log.Warn($"[{job}] Failed to decrypt the file names in {path}, so it is downloaded again.");
                return null;
            }

            return manifest;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A broken file of any kind is replaced by a download
            Log.Warn($"[{job}] Failed to read {path}, so it is downloaded again: {e.Message}");
            return null;
        }
    }

    public static void Save(ManifestJob job)
    {
        var path = job.ManifestFile;

        if (path == null)
        {
            return;
        }

        try
        {
            // Written whole before it replaces anything, since other runs may read it meanwhile
            Secrets.WriteFileAtomic(path, temp => job.Manifest!.SaveToFile(temp));
        }
        catch (Exception e) when (OutputFiles.IsFileError(e))
        {
            Log.Warn($"[{job}] Failed to save the manifest to {path}: {e.Message}");
        }
    }
}
