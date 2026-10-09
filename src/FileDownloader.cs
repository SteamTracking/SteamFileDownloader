using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using SteamKit2;

namespace SteamFileDownloader;

internal record struct FileJob(ManifestJob Job, DepotManifest.FileData File);

internal sealed record DownloadedFile(ManifestJob Job, DepotManifest.FileData File, string Path);

// Where a chunk goes in the output file: its own offset for whole and partial files, packed together for VPK entries that get extracts
internal record struct ChunkPlacement(DepotManifest.ChunkData Chunk, long Position);

// Some chunks of a file, written at their own offsets with the rest of the file left empty
internal sealed record PartialFile(ManifestJob Job, DepotManifest.FileData File, IReadOnlyCollection<DepotManifest.ChunkData> Chunks);

internal sealed partial class FileDownloader(SteamSession session, CancellationToken cancellationToken) : IDisposable
{
    private enum DownloadResult
    {
        Success,
        AlreadyValid,
        Copied,
        DownloadFailed,
    }

    // When set, partial downloads go here instead of next to the file, so they never end up in the output folder
    public string? TempFolder { get; init; }

    // Most files are one chunk, so files can't be what limits downloads, only chunks are
    private readonly SemaphoreSlim SemaphorePerFile = new(64, 64);
    private readonly SemaphoreSlim SemaphorePerDownloadChunk = new(32, 32);

    // Chunk bytes downloaded from the CDN, for the progress line
    private long DownloadedBytes;

    public void Dispose()
    {
        SemaphorePerFile.Dispose();
        SemaphorePerDownloadChunk.Dispose();
    }

    public static bool IsDirectory(DepotManifest.FileData file) => (file.Flags & EDepotFileFlag.Directory) != 0;

    /// <summary>
    /// A file that can't be written, like on a full disk, locked by another program, or with a path that escapes the output folder.
    /// </summary>
    public static bool IsFileError(Exception e) => e is IOException or UnauthorizedAccessException or InvalidDataException;

    /// <summary>
    /// Identifies identical file contents across depots and manifests.
    /// </summary>
    private static string GetHashKey(DepotManifest.FileData file) => $"{Convert.ToHexString(file.FileHash)}:{file.TotalSize}";

    /// <summary>
    /// Downloads files concurrently. Returns the files that are on disk and whether all of them succeeded.
    /// Files with the same hash, like one DLL in several builds, are downloaded once and copied.
    /// </summary>
    public async Task<(bool Success, List<DownloadedFile> Files)> DownloadAllFiles(List<FileJob> fileJobs)
    {
        if (fileJobs.Count == 0)
        {
            return (true, []);
        }

        if (!await session.EnsureCdnServersAsync())
        {
            return (false, []);
        }

        var downloaded = new ConcurrentBag<DownloadedFile>();
        var resultCounts = new int[Enum.GetValues<DownloadResult>().Length];
        var failedFiles = new ConcurrentBag<FileJob>();
        var totalBytes = fileJobs.Sum(static f => (long)f.File.TotalSize);
        var skippedBytes = 0L;
        var startBytes = Interlocked.Read(ref DownloadedBytes);
        var stopwatch = Stopwatch.StartNew();

        Task<string?> RunFileTask(FileJob fileJob, string? copyFrom = null) => RunPerFile(() => DownloadFileLocal(fileJob, copyFrom));

        // Returns the file's path, or null when it failed
        async Task<string?> DownloadFileLocal(FileJob fileJob, string? copyFrom)
        {
            DownloadResult fileState;
            string finalPath;

            try
            {
                (fileState, finalPath) = await DownloadFile(fileJob.Job, fileJob.File, copyFrom);
            }
            catch (Exception e) when (IsFileError(e))
            {
                Log.Error($"[{fileJob.Job}] Failed to write {fileJob.File.FileName}: {e.Message}");
                failedFiles.Add(fileJob);
                return null;
            }

            if (fileState is DownloadResult.DownloadFailed)
            {
                failedFiles.Add(fileJob);
                return null;
            }

            downloaded.Add(new DownloadedFile(fileJob.Job, fileJob.File, finalPath));
            Interlocked.Increment(ref resultCounts[(int)fileState]);

            if (fileState is not DownloadResult.Success)
            {
                Interlocked.Add(ref skippedBytes, (long)fileJob.File.TotalSize);
            }

            var remaining = fileJobs.Count - resultCounts.Sum();
            var action = fileState switch
            {
                DownloadResult.AlreadyValid => "Validated",
                DownloadResult.Copied => "Copied",
                _ => "Downloaded",
            };
            Log.Detail($"[{fileJob.Job}] {action} {fileJob.File.FileName} ({remaining} files left)");

            return finalPath;
        }

        (double, string) RenderProgress() => FormatProgress(
            Interlocked.Read(ref DownloadedBytes) - startBytes,
            Interlocked.Read(ref skippedBytes),
            totalBytes,
            stopwatch.Elapsed,
            $"{resultCounts.Sum()} / {fileJobs.Count} files");

        // The first file of each hash is downloaded, the rest are copied from it once it's done
        async Task RunHashGroup(IGrouping<string, FileJob> group)
        {
            var source = await RunFileTask(group.First());

            await Task.WhenAll(group.Skip(1).Select(fileJob => RunFileTask(fileJob, source)));
        }

        var groups = fileJobs.GroupBy(static f => GetHashKey(f.File)).ToList();
        Log.Info($"Downloading {fileJobs.Count} files ({FormatSize(groups.Sum(static g => (long)g.First().File.TotalSize))})...");

        await using (Log.StartProgress(RenderProgress))
        {
            await Task.WhenAll(groups.Select(RunHashGroup));
        }

        Log.Info($"Files: {resultCounts[(int)DownloadResult.Success]} downloaded, {resultCounts[(int)DownloadResult.Copied]} copied from identical files, {resultCounts[(int)DownloadResult.AlreadyValid]} already present.");

        LogFailures("files", failedFiles.Select(static f => $"{f.Job}/{FileFilter.NormalizePath(f.File.FileName)}"), "Running the same command again retries only those.");

        return (failedFiles.IsEmpty, [.. downloaded]);
    }

    // Bytes already on disk count as done, without being downloaded
    private static (double Fraction, string Text) FormatProgress(long downloadedBytes, long skippedBytes, long totalBytes, TimeSpan elapsed, string count)
    {
        const string Separator = " \u00B7 ";

        var doneBytes = downloadedBytes + skippedBytes;
        var total = Math.Max(totalBytes, 1);
        var speed = downloadedBytes / Math.Max(elapsed.TotalSeconds, 0.001);
        var left = speed > 0 ? $"{Separator}{FormatDuration((total - doneBytes) / speed)} left" : "";

        return ((double)doneBytes / total, $"{FormatSize(doneBytes)} / {FormatSize(total)}{Separator}{FormatSize((long)speed)}/s{Separator}{count}{left}");
    }

    private static string FormatDuration(double seconds) => TimeSpan.FromSeconds(Math.Min(seconds, TimeSpan.MaxValue.TotalSeconds / 2)) switch
    {
        { TotalHours: >= 1 } t => $"{(int)t.TotalHours}h {t.Minutes}m",
        { TotalMinutes: >= 1 } t => $"{t.Minutes}m {t.Seconds}s",
        var t => $"{t.Seconds}s",
    };

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:F1} GiB",
        >= 1024 * 1024 => $"{bytes / 1024.0 / 1024:F1} MiB",
        _ => $"{bytes / 1024.0:F1} KiB",
    };

    /// <summary>
    /// Resolves a depot file name inside the job's output folder, refusing paths that escape it.
    /// </summary>
    public static string GetFinalPath(ManifestJob job, string fileName)
    {
        var root = Path.GetFullPath(job.OutputFolder);
        var path = Path.GetFullPath(Path.Combine(root, FileFilter.NormalizePath(fileName)));

        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"File path escapes the output folder: {fileName}");
        }

        return path;
    }

    /// <summary>
    /// Where a file is written before it is complete. By default next to it, so an interrupted run leaves it
    /// where the next run overwrites it.
    /// </summary>
    public string GetPartialPath(string finalPath) => TempFolder != null
        ? Path.Combine(TempFolder, Path.GetRandomFileName())
        : finalPath + ".partial";

    private async Task<(DownloadResult Result, string Path)> DownloadFile(
        ManifestJob job,
        DepotManifest.FileData file,
        string? copyFrom)
    {
        var finalPath = GetFinalPath(job, file.FileName);

        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

        // Manifests list empty files with an all-zero hash, so only their size is checked
        if (file.TotalSize == 0)
        {
            if (HasExpectedSize(finalPath, file))
            {
                return (DownloadResult.AlreadyValid, finalPath);
            }

            await WriteFileAsync(finalPath, [], job.CreationTime, cancellationToken);

            return (DownloadResult.Success, finalPath);
        }

        if (await HasExpectedHash(finalPath, file))
        {
            return (DownloadResult.AlreadyValid, finalPath);
        }

        var partialPath = GetPartialPath(finalPath);

        try
        {
            // A same-hash file from this run is already verified, ones from earlier runs of other manifests are checked
            var reusable = copyFrom ?? await FindReusableFile(job, file);

            if (reusable != null)
            {
                // A copy keeps the date of the build it came from
                File.Copy(reusable, partialPath, overwrite: true);
                File.SetLastWriteTimeUtc(partialPath, job.CreationTime);
                File.Move(partialPath, finalPath, overwrite: true);

                return (DownloadResult.Copied, finalPath);
            }

            var placements = file.Chunks.Select(static c => new ChunkPlacement(c, (long)c.Offset)).ToList();

            if (!await DownloadChunksToFile(job, file.FileName, placements, (long)file.TotalSize, partialPath))
            {
                return (DownloadResult.DownloadFailed, finalPath);
            }

            File.Move(partialPath, finalPath, overwrite: true);

            return (DownloadResult.Success, finalPath);
        }
        finally
        {
            // Left over when anything failed, including cancellation
            File.Delete(partialPath);
        }
    }

    private async Task<string?> FindReusableFile(ManifestJob job, DepotManifest.FileData file)
    {
        // Every candidate of the right size is hashed, and older manifests mostly hold the same old version, so only a few are tried
        const int MaxHashedCandidates = 3;

        var candidates = job.ReuseFolders
            .Select(folder => Path.Combine(folder, FileFilter.NormalizePath(file.FileName)))
            .Where(candidate => HasExpectedSize(candidate, file))
            .Take(MaxHashedCandidates);

        foreach (var candidate in candidates)
        {
            if (await HashMatches(candidate, file))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool HasExpectedSize(string path, DepotManifest.FileData file) => new FileInfo(path) is { Exists: true } info && info.Length == (long)file.TotalSize;

    private async Task<bool> HasExpectedHash(string path, DepotManifest.FileData file) => HasExpectedSize(path, file) && await HashMatches(path, file);

    private async Task<bool> HashMatches(string path, DepotManifest.FileData file)
    {
        await using var fs = File.OpenRead(path);
        var hash = await SHA1.HashDataAsync(fs, cancellationToken);

        return file.FileHash.SequenceEqual(hash);
    }

    /// <summary>
    /// Downloads chunks into a file of the given length, each at its position, dated to when the job's manifest was created.
    /// Returns false when any chunk failed.
    /// </summary>
    public async Task<bool> DownloadChunksToFile(
        ManifestJob job,
        string fileName,
        IReadOnlyCollection<ChunkPlacement> placements,
        long length,
        string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, FileOptions.Asynchronous, length);
        RandomAccess.SetLength(handle, length);

        if (!await DownloadChunks(job, fileName, placements, handle))
        {
            return false;
        }

        File.SetLastWriteTimeUtc(handle, job.CreationTime);

        return true;
    }

    /// <summary>
    /// Writes a whole file, dated to the given time.
    /// </summary>
    public static async Task WriteFileAsync(string path, byte[] data, DateTime lastWriteTime, CancellationToken cancellationToken = default)
    {
        using var handle = File.OpenHandle(path, FileMode.Create, FileAccess.Write, FileShare.None, FileOptions.Asynchronous, data.Length);

        await RandomAccess.WriteAsync(handle, data, 0, cancellationToken);
        File.SetLastWriteTimeUtc(handle, lastWriteTime);
    }

    /// <summary>
    /// Downloads only some chunks of files into them at their own offsets, for VPK archives that VRF reads single entries from.
    /// The rest of each file stays empty, and sparse on file systems that support it. Chunks already in the file are kept.
    /// Returns false when any file failed.
    /// </summary>
    public async Task<bool> DownloadPartialFiles(List<PartialFile> partialFiles)
    {
        if (partialFiles.Count == 0)
        {
            return true;
        }

        if (!await session.EnsureCdnServersAsync())
        {
            return false;
        }

        var totalBytes = partialFiles.Sum(static f => f.Chunks.Sum(static c => c.UncompressedLength));
        var skippedBytes = 0L;
        var doneFiles = 0;
        bool[] results;
        var startBytes = Interlocked.Read(ref DownloadedBytes);
        var stopwatch = Stopwatch.StartNew();

        (double, string) RenderProgress() => FormatProgress(
            Interlocked.Read(ref DownloadedBytes) - startBytes,
            Interlocked.Read(ref skippedBytes),
            totalBytes,
            stopwatch.Elapsed,
            $"{Volatile.Read(ref doneFiles)} / {partialFiles.Count} archives");

        Log.Info($"Downloading {partialFiles.Sum(static f => f.Chunks.Count)} chunks ({FormatSize(totalBytes)}) of {partialFiles.Count} VPK archives...");

        await using (Log.StartProgress(RenderProgress))
        {
            results = await Task.WhenAll(partialFiles.Select(partialFile => RunPerFile(async () =>
            {
                try
                {
                    var (success, keptBytes) = await DownloadPartialFile(partialFile);
                    Interlocked.Add(ref skippedBytes, keptBytes);
                    return success;
                }
                catch (Exception e) when (IsFileError(e))
                {
                    Log.Error($"[{partialFile.Job}] Failed to write {partialFile.File.FileName}: {e.Message}");
                    return false;
                }
                finally
                {
                    Interlocked.Increment(ref doneFiles);
                }
            })));
        }

        Log.Info($"VPK archives: {FormatSize(Interlocked.Read(ref DownloadedBytes) - startBytes)} downloaded, {FormatSize(skippedBytes)} already present.");

        var failedFiles = partialFiles.Where((_, i) => !results[i]).Select(static f => FileFilter.NormalizePath(f.File.FileName)).ToList();
        LogFailures("VPK archives", failedFiles, "Running the same command again retries only their missing chunks.");

        return failedFiles.Count == 0;
    }

    // On the thread pool, with at most SemaphorePerFile files at once
    private Task<T> RunPerFile<T>(Func<Task<T>> work) => Task.Run(async () =>
    {
        await SemaphorePerFile.WaitAsync(cancellationToken);

        try
        {
            return await work();
        }
        finally
        {
            SemaphorePerFile.Release();
        }
    });

    private static void LogFailures(string what, IEnumerable<string> names, string retryHint)
    {
        var sorted = names.Order(StringComparer.Ordinal).ToList();

        if (sorted.Count > 0)
        {
            Log.Error($"{sorted.Count} {what} failed: {string.Join(", ", sorted.Take(20))}{(sorted.Count > 20 ? ", ..." : "")}. {retryHint}");
        }
    }

    // Returns whether all chunks are in the file, and the bytes of the ones that already were
    private async Task<(bool Success, long KeptBytes)> DownloadPartialFile(PartialFile partialFile)
    {
        var (job, file, chunks) = partialFile;
        var path = GetFinalPath(job, file.FileName);
        var length = (long)file.TotalSize;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Not asynchronous, which marking it sparse needs
        using var handle = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite);
        var existingLength = RandomAccess.GetLength(handle);

        // Linux leaves the gaps unallocated on its own, NTFS only in a sparse file
        if (existingLength == 0 && OperatingSystem.IsWindows())
        {
            DeviceIoControl(handle, FsctlSetSparse, 0, 0, 0, 0, out _, 0);
        }

        if (existingLength != length)
        {
            RandomAccess.SetLength(handle, length);
        }

        // Chunks are identified by their hash, so ones written by an earlier run, even of another build, are kept
        var missing = new List<ChunkPlacement>();
        var keptBytes = 0L;
        var buffer = ArrayPool<byte>.Shared.Rent((int)chunks.Max(static c => c.UncompressedLength));

        try
        {
            foreach (var chunk in chunks)
            {
                var data = buffer.AsMemory(0, (int)chunk.UncompressedLength);

                if ((long)chunk.Offset + data.Length <= existingLength
                    && await RandomAccess.ReadAsync(handle, data, (long)chunk.Offset, cancellationToken) == data.Length
                    && SHA1.HashData(data.Span).SequenceEqual(chunk.ChunkID))
                {
                    keptBytes += data.Length;
                    continue;
                }

                missing.Add(new ChunkPlacement(chunk, (long)chunk.Offset));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        var success = missing.Count == 0 || await DownloadChunks(job, file.FileName, missing, handle);

        if (success)
        {
            if (missing.Count > 0)
            {
                File.SetLastWriteTimeUtc(handle, job.CreationTime);
            }

            Log.Detail($"[{job}] Downloaded {missing.Count} of {chunks.Count} needed chunks of {file.FileName}");
        }

        return (success, keptBytes);
    }

    private const uint FsctlSetSparse = 0x900C4;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, nint inBuffer, uint inBufferSize, nint outBuffer, uint outBufferSize, out uint bytesReturned, nint overlapped);

    // Returns false when any chunk failed
    private async Task<bool> DownloadChunks(
        ManifestJob job,
        string fileName,
        IReadOnlyCollection<ChunkPlacement> placements,
        SafeFileHandle handle)
    {
        using var chunkCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var failed = false;

        await Task.WhenAll(placements.Select(placement => Task.Run(async () =>
        {
            await SemaphorePerDownloadChunk.WaitAsync(cancellationToken);

            try
            {
                // Once one chunk failed, the file failed, so the rest are skipped
                if (!chunkCancellation.IsCancellationRequested && !await DownloadChunk(job, placement, handle, chunkCancellation.Token))
                {
                    Log.Warn($"[{job}] Failed to download chunk for {fileName} ({placement.Chunk.Offset})");

                    failed = true;
                    await chunkCancellation.CancelAsync();
                }
            }
            finally
            {
                SemaphorePerDownloadChunk.Release();
            }
        })));

        return !failed;
    }

    // Returns false when the chunk failed, or right away when the file failed
    private async Task<bool> DownloadChunk(
        ManifestJob job,
        ChunkPlacement placement,
        SafeFileHandle handle,
        CancellationToken chunkCancellation)
    {
        const int MaxChunkRetries = 6;

        var chunk = placement.Chunk;
        var buffer = ArrayPool<byte>.Shared.Rent((int)chunk.UncompressedLength);
        var refused = false;

        try
        {
            for (var i = 0; i <= MaxChunkRetries; i++)
            {
                var server = session.GetContentServer();
                int written;

                try
                {
                    written = await session.CDNClient.DownloadDepotChunkAsync(job.DepotID, chunk, server, buffer, job.DepotKey);

                    // SteamKit only checks an Adler-32 checksum. The chunk id is the SHA-1 of the data, and a file
                    // of chunks that match it needs no hashing after
                    if (!SHA1.HashData(buffer.AsSpan(0, written)).SequenceEqual(chunk.ChunkID))
                    {
                        throw new InvalidDataException("the chunk does not match its hash");
                    }
                }
                catch (Exception e) when (SteamSession.IsError(e, cancellationToken))
                {
                    Log.Warn($"[{job}] Chunk download error: {e.Message}");

                    // Any server can be broken, so every error moves on to the next one
                    session.MarkContentServerAsBad(server);

                    // Refused by a second server, so it's not that server
                    if (SteamSession.IsRefusal(e) && refused)
                    {
                        Log.Warn($"[{job}] The CDN refused the chunk ({e.Message}), aborting");
                        return false;
                    }

                    refused |= SteamSession.IsRefusal(e);

                    if (i < MaxChunkRetries && !await DelayUnlessCancelled(SteamSession.ExponentialBackoff(i + 1), chunkCancellation))
                    {
                        return false;
                    }

                    continue;
                }

                // Disk errors are not the server's fault, so they fail the file
                await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, written), placement.Position, cancellationToken);
                Interlocked.Add(ref DownloadedBytes, written);

                return true;
            }

            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // Returns false when the file failed meanwhile, the user's own cancellation still throws
    private async Task<bool> DelayUnlessCancelled(int milliseconds, CancellationToken chunkCancellation)
    {
        try
        {
            await Task.Delay(milliseconds, chunkCancellation);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
