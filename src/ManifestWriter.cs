using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace SteamFileDownloader;

internal static class ManifestWriter
{
    private const string FilePrefix = "manifest_";
    private const string ManifestIdLabel = "Manifest ID / date     : ";

    /// <summary>
    /// Writes a manifest in DepotDownloader's text format to manifests/manifest_{depot}.txt.
    /// </summary>
    public static void DumpToTextFile(string outputPath, ManifestJob job)
    {
        var manifest = job.Manifest;
        Debug.Assert(manifest?.Files != null);

        var manifestDir = Path.Combine(outputPath, "manifests");
        Directory.CreateDirectory(manifestDir);

        var txtManifest = Path.Combine(manifestDir, $"{FilePrefix}{job.DepotID}.txt");
        using var sw = new StreamWriter(txtManifest);

        sw.WriteLine($"Content Manifest for Depot {job.DepotID} ");
        sw.WriteLine();
        sw.WriteLine($"{ManifestIdLabel}{job.ManifestID} / {manifest.CreationTime} ");

        var uniqueChunks = manifest.Files.SelectMany(static f => f.Chunks).Select(static c => Convert.ToHexString(c.ChunkID!)).Distinct().Count();

        sw.WriteLine($"Total number of files  : {manifest.Files.Count} ");
        sw.WriteLine($"Total number of chunks : {uniqueChunks} ");
        sw.WriteLine($"Total bytes on disk    : {manifest.TotalUncompressedSize} ");
        sw.WriteLine($"Total bytes compressed : {manifest.TotalCompressedSize} ");
        sw.WriteLine();
        sw.WriteLine();
        sw.WriteLine("          Size Chunks File SHA                                 Flags Name");

        foreach (var file in manifest.Files)
        {
            var sha1Hash = Convert.ToHexStringLower(file.FileHash);
            sw.WriteLine($"{file.TotalSize,14:d} {file.Chunks.Count,6:d} {sha1Hash} {(int)file.Flags,5:x} {file.FileName}");
        }
    }

    /// <summary>
    /// Reads the manifest id of each manifest_{depot}.txt in a folder, written by <see cref="DumpToTextFile"/> or DepotDownloader.
    /// </summary>
    public static Dictionary<uint, ulong> ReadManifestIds(string folder)
    {
        var ids = new Dictionary<uint, ulong>();

        foreach (var path in Directory.EnumerateFiles(folder, $"{FilePrefix}*.txt"))
        {
            if (!uint.TryParse(Path.GetFileNameWithoutExtension(path)[FilePrefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var depotID))
            {
                continue;
            }

            var line = File.ReadLines(path).Take(5).FirstOrDefault(static l => l.StartsWith(ManifestIdLabel, StringComparison.Ordinal));
            var idText = line?[ManifestIdLabel.Length..].Split(' ', 2)[0];

            if (!ulong.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out var manifestID) || manifestID == 0)
            {
                throw new InvalidDataException($"{path} has no \"{ManifestIdLabel.TrimEnd()}\" line.");
            }

            ids[depotID] = manifestID;
        }

        if (ids.Count == 0)
        {
            throw new InvalidDataException($"{folder} has no manifest_<depot>.txt files.");
        }

        return ids;
    }
}
