using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Threading.Tasks;
using SteamKit2;
using ValvePak;

namespace SteamFileDownloader;

internal sealed record VpkEntryMatch(VpkPattern Pattern, PackageEntry Entry, string Path);

/// <summary>
/// Reads VPK directory files downloaded from a depot, and extracts single entries by downloading only the archive chunks they span.
/// </summary>
internal static class VpkReader
{
    private const ushort DirArchiveIndex = 0x7FFF;
    private const int ParallelEntryChecks = 16;

    /// <summary>
    /// Reads a downloaded directory file. Returns null, with the error logged, when it isn't a valid VPK.
    /// </summary>
    public static Package? TryRead(ManifestJob job, string path, string dirFileName)
    {
#pragma warning disable CA2000 // Disposed here on failure, otherwise by the caller
        var package = new Package();
#pragma warning restore CA2000

        try
        {
            package.Read(path);
            return package;
        }
        catch (Exception e)
        {
            Log.Error($"[{job}] Failed to read {dirFileName} as a VPK: {e.Message}");
            package.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Finds pak01_NNN.vpk of a directory file in its own manifest, or in another depot of the app, like most of Dota 2's.
    /// Of several manifests of that depot, the closest one not newer than the directory file's is likely from the same build,
    /// since a build that changes an archive changes the directory file too. Entries from another build fail their CRC check.
    /// </summary>
    public static (ManifestJob Job, DepotManifest.FileData File)? FindArchive(IEnumerable<ManifestJob> jobs, ManifestJob dirJob, string dirFileName, int archiveIndex)
    {
        if (FindArchiveFile(dirJob, dirFileName, archiveIndex) is { } file)
        {
            return (dirJob, file);
        }

        var dirTime = dirJob.Manifest!.CreationTime;

        (ManifestJob Job, DepotManifest.FileData File)? closest = null;

        foreach (var job in jobs.Where(j => j != dirJob && j.AppID == dirJob.AppID && j.Manifest != null))
        {
            if (FindArchiveFile(job, dirFileName, archiveIndex) is { } archiveFile && (closest == null || IsCloser(job, closest.Value.Job)))
            {
                closest = (job, archiveFile);
            }
        }

        return closest;

        // Not newer than the directory file first, then by time apart
        bool IsCloser(ManifestJob job, ManifestJob other) =>
            (job.Manifest!.CreationTime > dirTime, (job.Manifest.CreationTime - dirTime).Duration())
                .CompareTo((other.Manifest!.CreationTime > dirTime, (other.Manifest.CreationTime - dirTime).Duration())) < 0;
    }

    // pak01_NNN.vpk next to pak01_dir.vpk in the job's manifest
    private static DepotManifest.FileData? FindArchiveFile(ManifestJob job, string dirFileName, int archiveIndex)
    {
        var dirPath = FileFilter.NormalizePath(dirFileName);
        var archivePath = string.Concat(dirPath.AsSpan(0, dirPath.Length - "dir.vpk".Length), $"{archiveIndex:D3}.vpk");

        return job.FilesByPath.GetValueOrDefault(archivePath);
    }

    /// <summary>
    /// Lists entries of a local directory VPK that match the patterns for it.
    /// </summary>
    public static List<VpkEntryMatch> ListEntries(Package package, IReadOnlyList<VpkPattern> patterns)
    {
        var matches = new List<VpkEntryMatch>();

        foreach (var entry in package.Entries!.Values.SelectMany(static e => e))
        {
            var path = entry.GetFullPath();

            foreach (var pattern in patterns)
            {
                if (pattern.Entry.IsMatch(path))
                {
                    matches.Add(new VpkEntryMatch(pattern, entry, path));
                }
            }
        }

        return matches;
    }

    /// <summary>
    /// The chunks of each archive that the entries are in, to download the archives partially next to the directory file
    /// for VRF to read the entries from. Returns false when an archive is in none of the manifests.
    /// </summary>
    public static (bool Success, List<PartialFile> Archives) FindArchiveChunks(
        IReadOnlyList<ManifestJob> jobs,
        ManifestJob dirJob,
        string dirFileName,
        IEnumerable<PackageEntry> entries)
    {
        var success = true;
        var archives = new List<PartialFile>();

        foreach (var archive in entries.Where(static e => e.ArchiveIndex != DirArchiveIndex && e.Length > 0).GroupBy(static e => e.ArchiveIndex))
        {
            if (FindArchive(jobs, dirJob, dirFileName, archive.Key) is not { } found)
            {
                Log.Warn($"[{dirJob}] Failed to find archive {archive.Key} of {dirFileName} in any depot of app {dirJob.AppID}");
                success = false;
                continue;
            }

            // Only in a broken VPK, which would leave such an entry without chunks
            var inArchive = new List<PackageEntry>();

            foreach (var entry in archive)
            {
                if ((ulong)entry.Offset + entry.Length > found.File.TotalSize)
                {
                    Log.Warn($"[{dirJob}] {entry.GetFullPath()} in {dirFileName} is past the end of archive {archive.Key}");
                    success = false;
                    continue;
                }

                inArchive.Add(entry);
            }

            if (inArchive.Count > 0)
            {
                archives.Add(new PartialFile(found.Job, found.File, FindChunks(found.File, inArchive)));
            }
        }

        return (success, archives);
    }

    /// <summary>
    /// Extracts entries into a folder named like the directory file without its extension (pak01_dir/).
    /// Archives are looked up in the other jobs too. Returns the extracted paths and whether all entries succeeded.
    /// </summary>
    public static async Task<(bool Success, List<string> Paths)> ExtractAsync(
        FileDownloader downloader,
        IReadOnlyList<ManifestJob> jobs,
        ManifestJob job,
        string dirFileName,
        Package package,
        IEnumerable<VpkEntryMatch> matches)
    {
        var outputFolder = dirFileName[..^".vpk".Length];
        var extracted = new ConcurrentBag<string>();
        var pending = new ConcurrentBag<(PackageEntry Entry, string Path)>();
        var success = true;

        // A rerun checks every entry, often thousands of small files, so several are read at once. It blocks
        // the caller, so directories extracting in parallel check their entries one directory at a time
        Parallel.ForEach(matches.DistinctBy(static m => m.Entry), new ParallelOptions { MaxDegreeOfParallelism = ParallelEntryChecks }, match =>
        {
            try
            {
                var path = FileDownloader.GetFinalPath(job, $"{outputFolder}/{match.Path}");

                if (IsAlreadyExtracted(match.Entry, path))
                {
                    Log.Detail($"[{job}] Validated {outputFolder}/{match.Path}");
                    extracted.Add(path);
                    return;
                }

                pending.Add((match.Entry, path));
            }
            catch (Exception e) when (FileDownloader.IsFileError(e))
            {
                Log.Error($"[{job}] Failed to extract {outputFolder}/{match.Path}: {e.Message}");
                success = false;
            }
        });

        if (pending.IsEmpty)
        {
            return (success, [.. extracted]);
        }

        // Archives download in parallel, the shared chunk limit keeps the total in check
        var results = await Task.WhenAll(pending
            .GroupBy(static p => p.Entry.ArchiveIndex)
            .Select(archive => ExtractArchiveAsync(downloader, jobs, job, dirFileName, package, archive)));

        return (success && results.All(static r => r.Success), [.. extracted, .. results.SelectMany(static r => r.Paths)]);
    }

    private static async Task<(bool Success, List<string> Paths)> ExtractArchiveAsync(
        FileDownloader downloader,
        IReadOnlyList<ManifestJob> jobs,
        ManifestJob job,
        string dirFileName,
        Package package,
        IGrouping<ushort, (PackageEntry Entry, string Path)> archive)
    {
        var success = true;
        var paths = new List<string>();
        ArchiveRanges? ranges = null;

        try
        {
            if (archive.Key != DirArchiveIndex && archive.Any(static p => p.Entry.Length > 0))
            {
                ranges = await DownloadArchiveRanges(downloader, jobs, job, dirFileName, archive.Key, archive.Select(static p => p.Entry));

                if (ranges == null)
                {
                    return (false, paths);
                }
            }

            using var rangesFile = ranges != null ? File.OpenRead(ranges.Path) : null;

            foreach (var (entry, path) in archive)
            {
                var data = ReadEntry(package, entry, ranges, rangesFile);

                if (data == null)
                {
                    var otherBuild = ranges != null && ranges.Job != job ? $"; archive {archive.Key} came from {ranges.Job}, which may be another build" : "";
                    Log.Warn($"[{job}] CRC mismatch for {entry.GetFullPath()} in {dirFileName}{otherBuild}");
                    success = false;
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await FileDownloader.WriteFileAsync(path, data, job.CreationTime);

                Log.Detail($"[{job}] Extracted {path}");
                paths.Add(path);
            }
        }
        catch (Exception e) when (FileDownloader.IsFileError(e))
        {
            Log.Error($"[{job}] Failed to extract entries of archive {archive.Key} of {dirFileName}: {e.Message}");
            success = false;
        }
        finally
        {
            if (ranges != null)
            {
                File.Delete(ranges.Path);
            }
        }

        return (success, paths);
    }

    // The downloaded chunks of an archive from the job's manifest, packed one after another in a temporary file, ordered by offset
    private sealed record ArchiveRanges(ManifestJob Job, string Path, ChunkPlacement[] Placements);

    // Downloads only the chunks of pak01_NNN.vpk that overlap the entries
    private static async Task<ArchiveRanges?> DownloadArchiveRanges(
        FileDownloader downloader,
        IReadOnlyList<ManifestJob> jobs,
        ManifestJob job,
        string dirFileName,
        int archiveIndex,
        IEnumerable<PackageEntry> entries)
    {
        if (FindArchive(jobs, job, dirFileName, archiveIndex) is not var (archiveJob, archiveFile))
        {
            Log.Warn($"[{job}] Archive {archiveIndex} of {dirFileName} is in none of the manifests of this run. It may be in another depot of app {job.AppID}: add \"all\" for the latest build, or that depot's manifest from the same build.");
            return null;
        }

        var needed = FindChunks(archiveFile, entries);
        var placements = new List<ChunkPlacement>(needed.Count);
        var length = 0L;

        foreach (var chunk in needed)
        {
            placements.Add(new ChunkPlacement(chunk, length));
            length += chunk.UncompressedLength;
        }

        var fromDepot = archiveJob != job ? $" from {archiveJob}" : "";
        Log.Info($"[{job}] Downloading {needed.Count} of {archiveFile.Chunks.Count} chunks ({FileDownloader.FormatSize(length)}) of {archiveFile.FileName}{fromDepot}");

        // Next to the directory file's job, since directory files of several builds can share an archive of another depot
        var path = downloader.GetPartialPath(FileDownloader.GetFinalPath(job, archiveFile.FileName));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var downloaded = false;

        try
        {
            downloaded = await downloader.DownloadChunksToFile(archiveJob, archiveFile.FileName, placements, length, path);

            return downloaded ? new ArchiveRanges(archiveJob, path, [.. placements]) : null;
        }
        finally
        {
            // Including on cancellation, the caller deletes it once the entries are extracted
            if (!downloaded)
            {
                File.Delete(path);
            }
        }
    }

    // The chunks of an archive that overlap the entries, ordered by offset
    private static List<DepotManifest.ChunkData> FindChunks(DepotManifest.FileData archiveFile, IEnumerable<PackageEntry> entries)
    {
        var chunks = archiveFile.Chunks.OrderBy(static c => c.Offset).ToArray();
        var needed = new HashSet<DepotManifest.ChunkData>();

        foreach (var entry in entries)
        {
            var end = (ulong)entry.Offset + entry.Length;

            for (var i = FindFirstChunkEnding(chunks, entry.Offset, static c => c); i < chunks.Length && chunks[i].Offset < end; i++)
            {
                needed.Add(chunks[i]);
            }
        }

        return [.. needed.OrderBy(static c => c.Offset)];
    }

    // Index of the first item, sorted by chunk offset, whose chunk ends after the given offset
    private static int FindFirstChunkEnding<T>(T[] items, ulong offset, Func<T, DepotManifest.ChunkData> getChunk)
    {
        int low = 0, high = items.Length;

        while (low < high)
        {
            var mid = (low + high) / 2;
            var chunk = getChunk(items[mid]);

            if (chunk.Offset + chunk.UncompressedLength <= offset)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    // Returns null on CRC mismatch
    private static byte[]? ReadEntry(Package package, PackageEntry entry, ArchiveRanges? ranges, FileStream? rangesFile)
    {
        if (entry.ArchiveIndex == DirArchiveIndex || entry.Length == 0)
        {
            try
            {
                // Archives are extracted in parallel, but the package reads the directory file through one stream
                lock (package)
                {
                    package.ReadEntry(entry, out var dirData);
                    return dirData;
                }
            }
            catch (InvalidDataException)
            {
                return null;
            }
        }

        var data = new byte[entry.TotalLength];
        entry.SmallData.CopyTo(data, 0);

        var start = (ulong)entry.Offset;
        var end = start + entry.Length;
        var placements = ranges!.Placements;

        // Copy the part of every overlapping chunk that falls inside the entry
        for (var i = FindFirstChunkEnding(placements, start, static p => p.Chunk); i < placements.Length && placements[i].Chunk.Offset < end; i++)
        {
            var (chunk, position) = placements[i];
            var copyStart = Math.Max(start, chunk.Offset);
            var copyEnd = Math.Min(end, chunk.Offset + chunk.UncompressedLength);

            rangesFile!.Seek(position + (long)(copyStart - chunk.Offset), SeekOrigin.Begin);
            rangesFile.ReadExactly(data, entry.SmallData.Length + (int)(copyStart - start), (int)(copyEnd - copyStart));
        }

        return Crc32.HashToUInt32(data) == entry.CRC32 ? data : null;
    }

    private static bool IsAlreadyExtracted(PackageEntry entry, string path)
    {
        if (new FileInfo(path) is not { Exists: true } info || info.Length != entry.TotalLength)
        {
            return false;
        }

        var crc = new Crc32();

        using (var stream = File.OpenRead(path))
        {
            crc.Append(stream);
        }

        return crc.GetCurrentHashAsUInt32() == entry.CRC32;
    }
}
