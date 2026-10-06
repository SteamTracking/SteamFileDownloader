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

    public static HashSet<int> FindArchivesWithExtensions(string dirPath, string[] extensions)
    {
        using var package = new Package();
        package.Read(dirPath);

        var archives = new HashSet<int>();

        foreach (var ext in extensions)
        {
            if (package.Entries!.TryGetValue(ext, out var entries))
            {
                foreach (var entry in entries)
                {
                    if (entry.ArchiveIndex != DirArchiveIndex)
                    {
                        archives.Add(entry.ArchiveIndex);
                    }
                }
            }
        }

        return archives;
    }

    /// <summary>
    /// Finds pak01_NNN.vpk next to pak01_dir.vpk in the job's manifest.
    /// </summary>
    public static DepotManifest.FileData? FindArchiveFile(ManifestJob job, string dirFileName, int archiveIndex)
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
    /// Extracts entries into a folder named like the directory file without its extension (pak01_dir/).
    /// Returns the extracted paths and whether all entries succeeded.
    /// </summary>
    public static async Task<(bool Success, List<string> Paths)> ExtractAsync(
        FileDownloader downloader,
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
            .Select(archive => ExtractArchiveAsync(downloader, job, dirFileName, package, archive)));

        return (success && results.All(static r => r.Success), [.. extracted, .. results.SelectMany(static r => r.Paths)]);
    }

    private static async Task<(bool Success, List<string> Paths)> ExtractArchiveAsync(
        FileDownloader downloader,
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
                ranges = await DownloadArchiveRanges(downloader, job, dirFileName, archive.Key, archive.Select(static p => p.Entry));

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
                    Log.Warn($"[{job}] CRC mismatch for {entry.GetFullPath()} in {dirFileName}");
                    success = false;
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, data);

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

    // The downloaded chunks of an archive, packed one after another in a temporary file, ordered by offset
    private sealed record ArchiveRanges(string Path, ChunkPlacement[] Placements);

    // Downloads only the chunks of pak01_NNN.vpk that overlap the entries
    private static async Task<ArchiveRanges?> DownloadArchiveRanges(
        FileDownloader downloader,
        ManifestJob job,
        string dirFileName,
        int archiveIndex,
        IEnumerable<PackageEntry> entries)
    {
        var archiveFile = FindArchiveFile(job, dirFileName, archiveIndex);

        if (archiveFile == null)
        {
            Log.Warn($"[{job}] Failed to find archive {archiveIndex} of {dirFileName}");
            return null;
        }

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

        var placements = new List<ChunkPlacement>(needed.Count);
        var length = 0L;

        foreach (var chunk in needed.OrderBy(static c => c.Offset))
        {
            placements.Add(new ChunkPlacement(chunk, length));
            length += chunk.UncompressedLength;
        }

        Log.Info($"[{job}] Downloading {needed.Count} of {chunks.Length} chunks ({FileDownloader.FormatSize(length)}) of {archiveFile.FileName}");

        var path = downloader.GetPartialPath(FileDownloader.GetFinalPath(job, archiveFile.FileName));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var downloaded = false;

        try
        {
            downloaded = await downloader.DownloadChunksToFile(job, archiveFile.FileName, placements, length, path);

            return downloaded ? new ArchiveRanges(path, [.. placements]) : null;
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
