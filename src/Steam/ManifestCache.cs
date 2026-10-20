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

            SetTime(job, path, manifest.CreationTime);

            return manifest;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A broken file of any kind is replaced by a download
            Log.Warn($"[{job}] Failed to read {path}, so it is downloaded again: {e.Message}");
            return null;
        }
    }

    // Dates a saved manifest to when it was created, like downloaded files, also when copied from Steam's depotcache
    private static void SetTime(ManifestJob job, string path, DateTime creationTime)
    {
        try
        {
            if (File.GetLastWriteTimeUtc(path) != creationTime)
            {
                File.SetLastWriteTimeUtc(path, creationTime);
            }
        }
        catch (Exception e) when (OutputFiles.IsFileError(e))
        {
            Log.Warn($"[{job}] Failed to set the date of {path}: {e.Message}");
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
            // Written whole and dated before it replaces anything, since other runs may read it meanwhile
            Secrets.WriteFileAtomic(path, temp =>
            {
                job.Manifest!.SaveToFile(temp);
                File.SetLastWriteTimeUtc(temp, job.CreationTime);
            });
        }
        catch (Exception e) when (OutputFiles.IsFileError(e))
        {
            Log.Warn($"[{job}] Failed to save the manifest to {path}: {e.Message}");
        }
    }
}
