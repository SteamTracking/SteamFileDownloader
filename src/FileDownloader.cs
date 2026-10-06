using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using SteamKit2;

namespace SteamFileDownloader;

internal record struct FileJob(ManifestJob Job, DepotManifest.FileData File);

internal sealed record DownloadedFile(ManifestJob Job, DepotManifest.FileData File, string Path);

// Where a chunk goes in the output file: its own offset for whole files, packed together for partial VPK archives
internal record struct ChunkPlacement(DepotManifest.ChunkData Chunk, long Position);

internal sealed class FileDownloader(SteamSession session, CancellationToken cancellationToken) : IDisposable
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
        var additionalTasks = new ConcurrentBag<Task>();
        var failedFiles = new ConcurrentBag<FileJob>();
        var queuedFiles = new ConcurrentDictionary<DepotManifest.FileData, byte>(fileJobs.Select(static f => KeyValuePair.Create(f.File, (byte)0)));
        var totalFileCount = fileJobs.Count;

        // Bytes of files that were already on disk or copied count as done, without being downloaded
        var totalBytes = fileJobs.Sum(static f => (long)f.File.TotalSize);
        var skippedBytes = 0L;
        var startBytes = Interlocked.Read(ref DownloadedBytes);
        var stopwatch = Stopwatch.StartNew();

        // Returns the file's path, or null when it failed
        Task<string?> RunFileTask(FileJob fileJob, string? copyFrom = null) => Task.Run(async () =>
        {
            await SemaphorePerFile.WaitAsync(cancellationToken);

            try
            {
                return await DownloadFileLocal(fileJob, copyFrom);
            }
            finally
            {
                SemaphorePerFile.Release();
            }
        });

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

            var remaining = totalFileCount - resultCounts.Sum();
            var action = fileState switch
            {
                DownloadResult.AlreadyValid => "Validated",
                DownloadResult.Copied => "Copied",
                _ => "Downloaded",
            };
            Log.Detail($"[{fileJob.Job}] {action} {fileJob.File.FileName} ({remaining} files left)");

            QueueArchives(fileJob, finalPath);

            return finalPath;
        }

        // files.json "vpk:" entries download every archive of pak01_dir.vpk that holds those extensions
        void QueueArchives(FileJob fileJob, string finalPath)
        {
            var pakExtensions = fileJob.Job.Filter?.PakExtensions;

            if (pakExtensions is not { Length: > 0 } || Path.GetFileName(finalPath) != "pak01_dir.vpk")
            {
                return;
            }

            HashSet<int> archives;

            try
            {
                archives = VpkReader.FindArchivesWithExtensions(finalPath, pakExtensions);
            }
            catch (Exception e)
            {
                Log.Warn($"[{fileJob.Job}] Failed to read {fileJob.File.FileName} as a VPK: {e.Message}");
                return;
            }

            foreach (var archiveIndex in archives.Order())
            {
                var archiveFile = VpkReader.FindArchiveFile(fileJob.Job, fileJob.File.FileName, archiveIndex);

                if (archiveFile == null)
                {
                    Log.Warn($"[{fileJob.Job}] Failed to find archive {archiveIndex} of {fileJob.File.FileName}");
                    continue;
                }

                // Other files.json entries can match the archive too
                if (!queuedFiles.TryAdd(archiveFile, 0))
                {
                    continue;
                }

                Interlocked.Increment(ref totalFileCount);
                Interlocked.Add(ref totalBytes, (long)archiveFile.TotalSize);
                additionalTasks.Add(RunFileTask(new FileJob(fileJob.Job, archiveFile)));
            }
        }

        (double, string) RenderProgress()
        {
            const string Separator = " \u00B7 ";

            var downloadedBytes = Interlocked.Read(ref DownloadedBytes) - startBytes;
            var doneBytes = downloadedBytes + Interlocked.Read(ref skippedBytes);
            var total = Math.Max(Interlocked.Read(ref totalBytes), 1);
            var speed = downloadedBytes / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
            var left = speed > 0 ? $"{Separator}{FormatDuration((total - doneBytes) / speed)} left" : "";

            return ((double)doneBytes / total, $"{FormatSize(doneBytes)} / {FormatSize(total)}{Separator}{FormatSize((long)speed)}/s{Separator}{resultCounts.Sum()} / {Volatile.Read(ref totalFileCount)} files{left}");
        }

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
            // Archive downloads are queued by the directory file tasks, so they are all known once those finish
            await Task.WhenAll(groups.Select(RunHashGroup));
            await Task.WhenAll(additionalTasks);
        }

        Log.Info($"Files: {resultCounts[(int)DownloadResult.Success]} downloaded, {resultCounts[(int)DownloadResult.Copied]} copied from identical files, {resultCounts[(int)DownloadResult.AlreadyValid]} already present.");

        if (!failedFiles.IsEmpty)
        {
            var names = failedFiles.Select(static f => $"{f.Job}/{FileFilter.NormalizePath(f.File.FileName)}").Order(StringComparer.Ordinal).ToList();
            Log.Error($"{names.Count} files failed: {string.Join(", ", names.Take(20))}{(names.Count > 20 ? ", ..." : "")}. Running the same command again retries only those.");
        }

        return (failedFiles.IsEmpty, [.. downloaded]);
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

        if (file.TotalSize == 0)
        {
            await File.WriteAllBytesAsync(finalPath, [], cancellationToken);

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
                File.Copy(reusable, partialPath, overwrite: true);
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
    /// Downloads chunks into a file of the given length, each at its position. Returns false when any chunk failed.
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
