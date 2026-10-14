using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using SteamKit2;
using SteamKit2.CDN;

namespace SteamFileDownloader;

internal record struct FileJob(ManifestJob Job, DepotManifest.FileData File);

internal sealed record DownloadedFile(ManifestJob Job, DepotManifest.FileData File, string Path);

// Where a chunk goes in the output file: its own offset for whole and partial files, packed together for VPK entries that get extracts
internal record struct ChunkPlacement(DepotManifest.ChunkData Chunk, long Position);

// Some chunks of a file, written at their own offsets with the rest of the file left empty
internal sealed record PartialFile(ManifestJob Job, DepotManifest.FileData File, IReadOnlyCollection<DepotManifest.ChunkData> Chunks);

/// <summary>
/// The CDN servers that chunks download from, and the downloads themselves.
/// </summary>
internal interface IChunkSource
{
    Task<bool> EnsureCdnServersAsync();

    CdnServerPool CdnServers { get; }

    /// <summary>
    /// Downloads a chunk of the job's depot into the buffer. Returns its length.
    /// </summary>
    Task<int> DownloadChunkAsync(ManifestJob job, DepotManifest.ChunkData chunk, Server server, byte[] destination);
}

internal sealed class FileDownloader(IChunkSource session, CancellationToken cancellationToken) : IDisposable
{
    // Chunks downloaded at once, which fills a gigabit line, while many more only slow down the last files
    private const int Connections = 64;

    // Most files are one chunk, and a file also holds its worker while it's hashed or written, so files can't be what limits downloads, only chunks are
    private ParallelOptions FileWorkers => new() { MaxDegreeOfParallelism = Connections * 2, CancellationToken = cancellationToken };

    private enum DownloadResult
    {
        Success,
        AlreadyValid,
        Copied,
        DownloadFailed,
    }

    // When set, partial downloads go here instead of next to the file, so they never end up in the output folder
    public string? TempFolder { get; init; }

    // Whether DownloadAllFiles logs how many files were downloaded, copied and already present, which is noise when
    // they are only directory files to read VPK entries from
    public bool LogFilesSummary { get; init; } = true;

    // The clock of the download speeds and of the waits between chunk retries
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    private readonly SemaphoreSlim SemaphorePerDownloadChunk = new(Connections, Connections);

    // Chunk bytes downloaded from the CDN, for the progress line
    private long DownloadedBytes;

    // Failed chunk requests by reason and by server, summed up after each download instead of a line each
    private readonly ConcurrentDictionary<string, int> ChunkErrorReasons = new();
    private readonly ConcurrentDictionary<string, int> ChunkErrorServers = new();
    private int FailedChunks;

    public void Dispose() => SemaphorePerDownloadChunk.Dispose();

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

        var downloadedFiles = new ConcurrentBag<DownloadedFile>();
        var resultCounts = new int[Enum.GetValues<DownloadResult>().Length];
        var failedFiles = new ConcurrentBag<FileJob>();
        var totalBytes = fileJobs.Sum(static f => (long)f.File.TotalSize);
        var skippedBytes = 0L;
        var startBytes = Interlocked.Read(ref DownloadedBytes);
        var startTime = TimeProvider.GetTimestamp();

        // Returns the file's path, or null when it failed
        async Task<string?> DownloadFileLocal(FileJob fileJob, string? copyFrom)
        {
            DownloadResult fileState;
            string finalPath;

            try
            {
                (fileState, finalPath) = await DownloadFile(fileJob.Job, fileJob.File, copyFrom);
            }
            catch (Exception e) when (OutputFiles.IsFileError(e))
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

            downloadedFiles.Add(new DownloadedFile(fileJob.Job, fileJob.File, finalPath));
            Interlocked.Increment(ref resultCounts[(int)fileState]);

            if (fileState is not DownloadResult.Success)
            {
                Interlocked.Add(ref skippedBytes, (long)fileJob.File.TotalSize);
            }

            return finalPath;
        }

        (double, string) RenderProgress() => DownloadProgress.FormatProgress(
            BytesSince(startBytes),
            Interlocked.Read(ref skippedBytes),
            totalBytes,
            TimeProvider.GetElapsedTime(startTime),
            $"{resultCounts.Sum()} / {fileJobs.Count} files{FormatChunkErrorCount()}");

        var groups = fileJobs.GroupBy(static f => GetHashKey(f.File)).ToList();
        var uniqueBytes = groups.Sum(static g => (long)g.First().File.TotalSize);
        var sources = new string?[groups.Count];

        Log.Info($"Downloading {Log.Plural(fileJobs.Count, "file")} ({DownloadProgress.FormatSize(uniqueBytes)})...");

        await using (Log.StartProgress(RenderProgress))
        {
            // The first file of each hash is downloaded, then the rest are copied from it
            await Parallel.ForAsync(0, groups.Count, FileWorkers, async (i, _) => sources[i] = await DownloadFileLocal(groups[i].First(), null));

            var copies = new List<(FileJob FileJob, string? Source)>();

            for (var i = 0; i < groups.Count; i++)
            {
                foreach (var fileJob in groups[i].Skip(1))
                {
                    copies.Add((fileJob, sources[i]));
                }
            }

            await Parallel.ForEachAsync(copies, FileWorkers, async (copy, _) => await DownloadFileLocal(copy.FileJob, copy.Source));
        }

        var downloaded = resultCounts[(int)DownloadResult.Success];
        var copied = resultCounts[(int)DownloadResult.Copied];
        var alreadyPresent = resultCounts[(int)DownloadResult.AlreadyValid];
        var counts = new List<string>();

        if (downloaded > 0)
        {
            var transfer = DownloadProgress.FormatTransfer(BytesSince(startBytes), TimeProvider.GetElapsedTime(startTime));

            counts.Add($"{downloaded} downloaded ({transfer})");
        }

        if (copied > 0)
        {
            counts.Add($"{copied} copied from identical files");
        }

        if (alreadyPresent > 0)
        {
            counts.Add($"{alreadyPresent} already present");
        }

        if (LogFilesSummary && counts.Count > 0)
        {
            Log.Info($"Files: {string.Join(", ", counts)}.");
        }

        LogChunkErrors();

        LogFailures("files", failedFiles.Select(static f => $"{f.Job}/{FileFilter.NormalizePath(f.File.FileName)}"), "Running the same command again retries only those.");

        return (failedFiles.IsEmpty, [.. downloadedFiles]);
    }

    /// <summary>
    /// Runs work that downloads chunks of an unknown total size, like extracting VPK entries, under a progress line
    /// of the bytes downloaded and how many of the items are done.
    /// </summary>
    public async Task<TResult[]> RunWithProgress<TItem, TResult>(string what, IReadOnlyList<TItem> items, Func<TItem, Task<TResult>> work)
    {
        if (items.Count == 0)
        {
            return [];
        }

        var done = 0;
        var startBytes = Interlocked.Read(ref DownloadedBytes);
        var startTime = TimeProvider.GetTimestamp();

        (double, string) RenderProgress()
        {
            var bytes = BytesSince(startBytes);
            var finished = Volatile.Read(ref done);
            var speed = (long)DownloadProgress.Speed(bytes, TimeProvider.GetElapsedTime(startTime));
            var text = $"{DownloadProgress.FormatSize(bytes)} at {DownloadProgress.FormatSize(speed)}/s{DownloadProgress.Separator}{finished} / {items.Count} {what}{FormatChunkErrorCount()}";

            return ((double)finished / items.Count, text);
        }

        await using (Log.StartProgress(RenderProgress))
        {
            return await Task.WhenAll(items.Select(async item =>
            {
                var result = await work(item);
                Interlocked.Increment(ref done);
                return result;
            }));
        }
    }

    private long BytesSince(long startBytes) => Interlocked.Read(ref DownloadedBytes) - startBytes;

    private string FormatChunkErrorCount()
    {
        var errors = ChunkErrorReasons.Values.Sum();

        return errors > 0 ? $"{DownloadProgress.Separator}{errors} chunk errors" : "";
    }

    /// <summary>
    /// Sums up the chunk errors since the last summary in one warning, when chunks failed for good. Errors that retries
    /// fixed aren't worth a line.
    /// </summary>
    public void LogChunkErrors()
    {
        var reasons = TakeCounts(ChunkErrorReasons);
        var servers = TakeCounts(ChunkErrorServers);
        var failed = Interlocked.Exchange(ref FailedChunks, 0);

        if (reasons.Count == 0 || failed == 0)
        {
            return;
        }

        Log.Warn($"{reasons.Sum(static c => c.Value)} chunk errors ({Format(reasons)}) from {Format(servers)}; {failed} chunks failed after all retries.");

        // Most first
        static List<KeyValuePair<string, int>> TakeCounts(ConcurrentDictionary<string, int> counts)
        {
            var sorted = counts.OrderByDescending(static c => c.Value).ThenBy(static c => c.Key, StringComparer.Ordinal).ToList();
            counts.Clear();
            return sorted;
        }

        static string Format(List<KeyValuePair<string, int>> counts) => Log.JoinFirst([.. counts.Select(static c => $"{c.Key} x{c.Value}")]);
    }

    // Short enough to count by: the status code, a timeout, or data that didn't decrypt, decompress or match its hash
    private static string DescribeChunkError(Exception e) => e switch
    {
        SteamKitWebRequestException { StatusCode: var status } => $"HTTP {(int)status}",
        CryptographicException or InvalidDataException => "corrupt data",
        HttpRequestException or IOException => "connection failed",
        _ => SteamSession.DescribeError(e),
    };

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
        var finalPath = OutputFiles.GetFinalPath(job, file.FileName);

        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

        // Manifests list empty files with an all-zero hash, so only their size is checked
        if (file.TotalSize == 0)
        {
            if (HasExpectedSize(finalPath, file))
            {
                return (DownloadResult.AlreadyValid, finalPath);
            }

            await OutputFiles.WriteFileAsync(finalPath, [], job.CreationTime, cancellationToken);

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

            if (!await DownloadChunksToFile(job, placements, (long)file.TotalSize, partialPath))
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

    private static bool HasExpectedSize(string path, DepotManifest.FileData file)
    {
        var info = new FileInfo(path);

        return info.Exists && info.Length == (long)file.TotalSize;
    }

    // A chunk's id is the SHA-1 of its data
    private static bool IsChunkData(ReadOnlySpan<byte> data, DepotManifest.ChunkData chunk)
    {
        Span<byte> hash = stackalloc byte[SHA1.HashSizeInBytes];
        SHA1.HashData(data, hash);

        return hash.SequenceEqual(chunk.ChunkID);
    }

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
        IReadOnlyCollection<ChunkPlacement> placements,
        long length,
        string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, FileOptions.Asynchronous, length);
        RandomAccess.SetLength(handle, length);

        if (!await DownloadChunks(job, placements, handle))
        {
            return false;
        }

        File.SetLastWriteTimeUtc(handle, job.CreationTime);

        return true;
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

        // Directory files of several builds can need one archive, which is opened once with the chunks of all of them
        partialFiles = [.. partialFiles
            .GroupBy(static f => (f.Job, f.File))
            .Select(static g => new PartialFile(g.Key.Job, g.Key.File, [.. g.SelectMany(static f => f.Chunks).Distinct()]))];

        var totalBytes = partialFiles.Sum(static f => f.Chunks.Sum(static c => c.UncompressedLength));
        var skippedBytes = 0L;
        var doneFiles = 0;
        var failedFiles = new ConcurrentBag<string>();
        var startBytes = Interlocked.Read(ref DownloadedBytes);
        var startTime = TimeProvider.GetTimestamp();

        (double, string) RenderProgress() => DownloadProgress.FormatProgress(
            BytesSince(startBytes),
            Interlocked.Read(ref skippedBytes),
            totalBytes,
            TimeProvider.GetElapsedTime(startTime),
            $"{Volatile.Read(ref doneFiles)} / {partialFiles.Count} archives{FormatChunkErrorCount()}");

        async ValueTask DownloadPartialFileLocal(PartialFile partialFile)
        {
            try
            {
                var (success, keptBytes) = await DownloadPartialFile(partialFile);
                Interlocked.Add(ref skippedBytes, keptBytes);

                if (!success)
                {
                    failedFiles.Add(FileFilter.NormalizePath(partialFile.File.FileName));
                }
            }
            catch (Exception e) when (OutputFiles.IsFileError(e))
            {
                Log.Error($"[{partialFile.Job}] Failed to write {partialFile.File.FileName}: {e.Message}");
                failedFiles.Add(FileFilter.NormalizePath(partialFile.File.FileName));
            }
            finally
            {
                Interlocked.Increment(ref doneFiles);
            }
        }

        Log.Info($"Downloading {partialFiles.Sum(static f => f.Chunks.Count)} chunks ({DownloadProgress.FormatSize(totalBytes)}) of {partialFiles.Count} VPK archives...");

        await using (Log.StartProgress(RenderProgress))
        {
            await Parallel.ForEachAsync(partialFiles, FileWorkers, (partialFile, _) => DownloadPartialFileLocal(partialFile));
        }

        var transfer = DownloadProgress.FormatTransfer(BytesSince(startBytes), TimeProvider.GetElapsedTime(startTime));

        Log.Info($"VPK archives: {transfer} downloaded, {DownloadProgress.FormatSize(skippedBytes)} already present.");
        LogChunkErrors();

        LogFailures("VPK archives", failedFiles, "Running the same command again retries only their missing chunks.");

        return failedFiles.IsEmpty;
    }

    private static void LogFailures(string what, IEnumerable<string> names, string retryHint)
    {
        var sorted = names.Order(StringComparer.Ordinal).ToList();

        if (sorted.Count > 0)
        {
            Log.Error($"{sorted.Count} {what} failed: {Log.JoinFirst(sorted, 20)}. {retryHint}");
        }
    }

    // Returns whether all chunks are in the file, and the bytes of the ones that already were
    private async Task<(bool Success, long KeptBytes)> DownloadPartialFile(PartialFile partialFile)
    {
        var (job, file, chunks) = partialFile;

        if (chunks.Count == 0)
        {
            return (true, 0);
        }

        var path = OutputFiles.GetFinalPath(job, file.FileName);
        var length = (long)file.TotalSize;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Not asynchronous, which marking it sparse needs
        using var handle = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite);
        var existingLength = RandomAccess.GetLength(handle);

        // Linux leaves the gaps unallocated on its own, NTFS only in a sparse file
        if (existingLength == 0 && OperatingSystem.IsWindows())
        {
            OutputFiles.SetSparse(handle);
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
                var offset = (long)chunk.Offset;
                var isInFile = false;

                if (offset + data.Length <= existingLength)
                {
                    var read = await RandomAccess.ReadAsync(handle, data, offset, cancellationToken);
                    isInFile = read == data.Length && IsChunkData(data.Span, chunk);
                }

                if (isInFile)
                {
                    keptBytes += data.Length;
                    continue;
                }

                missing.Add(new ChunkPlacement(chunk, offset));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        var success = missing.Count == 0 || await DownloadChunks(job, missing, handle);

        if (success && missing.Count > 0)
        {
            File.SetLastWriteTimeUtc(handle, job.CreationTime);
        }

        return (success, keptBytes);
    }

    // Returns false when any chunk failed
    private async Task<bool> DownloadChunks(
        ManifestJob job,
        IReadOnlyCollection<ChunkPlacement> placements,
        SafeFileHandle handle)
    {
        using var chunkCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var failed = false;

        // The first chunk that fails cancels the others of the file
        async Task DownloadChunkOrCancelFile(ChunkPlacement placement)
        {
            if (!await DownloadChunk(job, placement, handle, chunkCancellation.Token))
            {
                failed = true;
                await chunkCancellation.CancelAsync();
            }
        }

        await Task.WhenAll(placements.Select(DownloadChunkOrCancelFile));

        return !failed;
    }

    // Returns false when the chunk failed, or right away when another chunk failed the file. Ctrl+C throws
    private async Task<bool> DownloadChunk(
        ManifestJob job,
        ChunkPlacement placement,
        SafeFileHandle handle,
        CancellationToken chunkCancellation)
    {
        // Each attempt goes to another server than the last, so the first retries don't wait. The waits after them add
        // up to about two minutes, which rides out a short network outage
        const int ImmediateRetries = 2;
        const int MaxChunkRetries = 8;

        Server? lastServer = null;
        Server? refusedBy = null;

        for (var attempt = 0; ; attempt++)
        {
            var (error, server) = await TryDownloadChunk(job, placement, handle, lastServer, chunkCancellation);

            if (error == null)
            {
                return true;
            }

            // A chunk that Ctrl+C stopped is not in the file, so it must not end up as a complete file
            cancellationToken.ThrowIfCancellationRequested();

            if (chunkCancellation.IsCancellationRequested)
            {
                return false;
            }

            lastServer = server;
            var isRefusal = SteamSession.IsRefusal(error);

            // Refused by a second server too, so the CDN doesn't have it, rather than one server
            var refusedByAnotherServer = isRefusal && refusedBy != null && refusedBy != server;

            if (attempt >= MaxChunkRetries || refusedByAnotherServer)
            {
                Interlocked.Increment(ref FailedChunks);
                return false;
            }

            // Another server may have it, so it's tried right away
            if (isRefusal)
            {
                refusedBy ??= server;
                continue;
            }

            if (attempt >= ImmediateRetries)
            {
                var delay = SteamSession.ExponentialBackoff(attempt - ImmediateRetries + 1);

                if (!await DelayUnlessCancelled(delay, chunkCancellation))
                {
                    return false;
                }
            }
        }
    }

    // One attempt, on another server than the one to avoid if there is one. The download slot is held until the chunk is
    // written, so a disk slower than the network slows the downloads instead of piling up chunks in memory, and it's
    // free during the waits between attempts. Returns the error and its server, or no error once the chunk is in the file.
    private async Task<(Exception? Error, Server? Server)> TryDownloadChunk(
        ManifestJob job,
        ChunkPlacement placement,
        SafeFileHandle handle,
        Server? avoid,
        CancellationToken chunkCancellation)
    {
        var chunk = placement.Chunk;
        byte[]? buffer = null;

        await SemaphorePerDownloadChunk.WaitAsync(cancellationToken);

        try
        {
            // Once one chunk failed, the file failed, so the rest are skipped
            if (chunkCancellation.IsCancellationRequested)
            {
                return (new OperationCanceledException(chunkCancellation), null);
            }

            buffer = ArrayPool<byte>.Shared.Rent((int)chunk.UncompressedLength);
            var server = session.CdnServers.Get(avoid);
            int written;

            try
            {
                written = await session.DownloadChunkAsync(job, chunk, server, buffer);

                // SteamKit only checks an Adler-32 checksum. The chunk id is the SHA-1 of the data, and a file
                // of chunks that match it needs no hashing after
                if (!IsChunkData(buffer.AsSpan(0, written), chunk))
                {
                    throw new InvalidDataException("the chunk does not match its hash");
                }
            }
            catch (Exception e) when (SteamSession.IsError(e, cancellationToken))
            {
                ChunkErrorReasons.AddOrUpdate(DescribeChunkError(e), 1, static (_, count) => count + 1);
                ChunkErrorServers.AddOrUpdate(server.Host ?? server.ToString(), 1, static (_, count) => count + 1);
                session.CdnServers.MarkBad(server);
                return (e, server);
            }

            session.CdnServers.MarkGood(server);

            // Disk errors are not the server's fault, so they fail the file
            await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, written), placement.Position, cancellationToken);
            Interlocked.Add(ref DownloadedBytes, written);

            return (null, server);
        }
        finally
        {
            if (buffer != null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            SemaphorePerDownloadChunk.Release();
        }
    }

    // Returns false when the file failed meanwhile, the user's own cancellation still throws
    private async Task<bool> DelayUnlessCancelled(int milliseconds, CancellationToken chunkCancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(milliseconds), TimeProvider, chunkCancellation);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
