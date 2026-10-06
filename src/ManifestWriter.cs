using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace SteamFileDownloader;

internal static class ManifestWriter
{
    /// <summary>
    /// Writes a manifest in DepotDownloader's text format to manifests/manifest_{depot}.txt.
    /// </summary>
    public static void DumpToTextFile(string outputPath, ManifestJob job)
    {
        var manifest = job.Manifest;
        Debug.Assert(manifest?.Files != null);

        var manifestDir = Path.Combine(outputPath, "manifests");
        Directory.CreateDirectory(manifestDir);

        var txtManifest = Path.Combine(manifestDir, $"manifest_{job.DepotID}.txt");
        using var sw = new StreamWriter(txtManifest);

        sw.WriteLine($"Content Manifest for Depot {job.DepotID} ");
        sw.WriteLine();
        sw.WriteLine($"Manifest ID / date     : {job.ManifestID} / {manifest.CreationTime} ");

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
}
