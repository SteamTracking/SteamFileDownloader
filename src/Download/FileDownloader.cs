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

    // Chunks keep retrying until nothing has downloaded for this long, which means the CDN is down, not one chunk or server
    private static readonly TimeSpan OutageTimeout = TimeSpan.FromMinutes(15);

    // The waits between retries of a chunk double up to 2^this seconds
    private const int MaxBackoffExponent = 5;

    // Most files are one chunk, and a file also holds its worker while it's hashed or written, so files can't be what limits downloads, only chunks are
    private ParallelOptions FileWorkers => new() { MaxDegreeOfParallelism = Connections * 2, CancellationToken = cancellationToken };

    private enum DownloadResult
    {
        Success,
        AlreadyValid,
        Copied,
        Linked,
        LinkSkipped,
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

    // Files hashed or preallocated at once. Many more only make a hard disk seek between them
    private static readonly int DiskOperations = Math.Min(Environment.ProcessorCount, 8);

    private readonly SemaphoreSlim SemaphorePerDiskOperation = new(DiskOperations, DiskOperations);

    // Chunk bytes as they came from the CDN, compressed, and as they were written to disk, for the progress line
    private long NetworkBytes;
    private long DiskBytes;

    // Failed chunk requests by reason and by server, summed up after each download instead of a line each
    private readonly ConcurrentDictionary<string, int> ChunkErrorReasons = new();
    private readonly ConcurrentDictionary<string, int> ChunkErrorServers = new();
    private int FailedChunks;

    // Links that were not written, summed up after each download instead of a line each
    private readonly ConcurrentBag<string> LinksOutsideFolder = [];
    private int LinksWithoutPrivilege;

    // When a chunk last completed, while chunks are downloading. It restarts with the first chunk after an idle time
    private long LastProgress;
    private int ActiveChunks;
    private int OutageDetected;

    public void Dispose()
    {
        SemaphorePerDownloadChunk.Dispose();
        SemaphorePerDiskOperation.Dispose();
    }

    /// <summary>
    /// Identifies identical file contents across depots and manifests.
    /// </summary>
    internal static string GetHashKey(DepotManifest.FileData file) => $"{Convert.ToHexString(file.FileHash)}:{file.TotalSize}";

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
        var startBytes = ReadBytes();
        var startTime = TimeProvider.GetTimestamp();

        // Returns the file's path, or null when it failed
        async Task<string?> DownloadFileLocal(FileJob fileJob, string? copyFrom)
        {
            FileOutcome outcome;

            try
            {
                outcome = await DownloadFile(fileJob.Job, fileJob.File, copyFrom);
            }
            catch (Exception e) when (OutputFiles.IsFileError(e))
            {
                Log.Error($"[{fileJob.Job}] Failed to read or write {fileJob.File.FileName}: {OutputFiles.DescribeError(e)}");
                failedFiles.Add(fileJob);
                return null;
            }

            var fileState = outcome.Result;

            if (fileState is DownloadResult.DownloadFailed)
            {
                failedFiles.Add(fileJob);
                return null;
            }

            if (fileState is DownloadResult.LinkSkipped)
            {
                Interlocked.Increment(ref resultCounts[(int)fileState]);
                return null;
            }

            downloadedFiles.Add(new DownloadedFile(fileJob.Job, fileJob.File, outcome.Path));
            Interlocked.Increment(ref resultCounts[(int)fileState]);

            if (fileState is DownloadResult.Success)
            {
                Interlocked.Add(ref skippedBytes, outcome.KeptBytes);
            }
            else
            {
                Interlocked.Add(ref skippedBytes, (long)fileJob.File.TotalSize);
            }

            return outcome.Path;
        }

        (double, string) RenderProgress()
        {
            var (network, disk) = BytesSince(startBytes);

            return DownloadProgress.FormatProgress(
                network,
                disk,
                Interlocked.Read(ref skippedBytes),
                totalBytes,
                TimeProvider.GetElapsedTime(startTime),
                $"{resultCounts.Sum()} / {fileJobs.Count} files{FormatChunkErrorCount()}");
        }

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
        var linked = resultCounts[(int)DownloadResult.Linked];
        var counts = new List<string>();

        if (downloaded > 0)
        {
            var (network, disk) = BytesSince(startBytes);
            var transfer = DownloadProgress.FormatTransfer(network, disk, TimeProvider.GetElapsedTime(startTime));

            counts.Add($"{downloaded} downloaded ({transfer})");
        }

        if (copied > 0)
        {
            counts.Add($"{copied} copied from identical files");
        }

        if (linked > 0)
        {
            counts.Add($"{Log.Plural(linked, "link")} created");
        }

        if (alreadyPresent > 0)
        {
            counts.Add($"{alreadyPresent} already present");
        }

        if (LogFilesSummary && counts.Count > 0)
        {
            Log.Info($"Files: {string.Join(", ", counts)}.");
        }

        LogSkippedLinks();
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
        var startBytes = ReadBytes();
        var startTime = TimeProvider.GetTimestamp();

        (double, string) RenderProgress()
        {
            var (network, disk) = BytesSince(startBytes);
            var finished = Volatile.Read(ref done);
            var rates = DownloadProgress.FormatRates(network, disk, TimeProvider.GetElapsedTime(startTime));
            var text = $"{DownloadProgress.FormatSize(disk)}{DownloadProgress.Separator}{finished} / {items.Count} {what}{FormatChunkErrorCount()}{DownloadProgress.Separator}{rates}";

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

    private (long Network, long Disk) ReadBytes() => (Interlocked.Read(ref NetworkBytes), Interlocked.Read(ref DiskBytes));

    private (long Network, long Disk) BytesSince((long Network, long Disk) start)
    {
        var (network, disk) = ReadBytes();

        return (network - start.Network, disk - start.Disk);
    }

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

        Log.Warn($"{reasons.Sum(static c => c.Value)} chunk errors ({Format(reasons)}) from {Format(servers)}; {failed} chunks failed.");

        if (Interlocked.Exchange(ref OutageDetected, 0) == 1)
        {
            Log.Error($"No chunk was downloaded in {OutageTimeout.TotalMinutes} minutes, so the CDN seems to be down. Check the network, or retry later.");
        }

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
    /// where the next run picks it up.
    /// </summary>
    public string GetPartialPath(string finalPath) => TempFolder != null
        ? Path.Combine(TempFolder, Path.GetRandomFileName())
        : finalPath + ".partial";

    // KeptBytes are the ones of a partial file that were already right, which didn't need downloading
    private readonly record struct FileOutcome(DownloadResult Result, string Path, long KeptBytes = 0);

    private async Task<FileOutcome> DownloadFile(
        ManifestJob job,
        DepotManifest.FileData file,
        string? copyFrom)
    {
        var finalPath = OutputFiles.GetFinalPath(job, file.FileName);

        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

        if ((file.Flags & EDepotFileFlag.Symlink) != 0)
        {
            return new FileOutcome(DownloadLink(job, file, finalPath), finalPath);
        }

        // Manifests list empty files with an all-zero hash, so only their size is checked
        if (file.TotalSize == 0)
        {
            if (HasExpectedSize(finalPath, file))
            {
                return new FileOutcome(DownloadResult.AlreadyValid, finalPath);
            }

            await OutputFiles.WriteFileAsync(finalPath, [], job.CreationTime, cancellationToken);

            return new FileOutcome(DownloadResult.Success, finalPath);
        }

        if (await HasExpectedHash(finalPath, file))
        {
            return new FileOutcome(DownloadResult.AlreadyValid, finalPath);
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

                return new FileOutcome(DownloadResult.Copied, finalPath);
            }

            var placements = file.Chunks.Select(static c => new ChunkPlacement(c, (long)c.Offset)).ToList();
            var length = (long)file.TotalSize;
            bool downloaded;
            var keptBytes = 0L;

            if (TempFolder == null)
            {
                (downloaded, keptBytes) = await ResumeChunksToFile(job, placements, length, partialPath);
            }
            else
            {
                downloaded = await DownloadChunksToFile(job, placements, length, partialPath);
            }

            if (!downloaded)
            {
                return new FileOutcome(DownloadResult.DownloadFailed, finalPath);
            }

            File.Move(partialPath, finalPath, overwrite: true);

            return new FileOutcome(DownloadResult.Success, finalPath, keptBytes);
        }
        finally
        {
            // A partial file next to the file is kept when anything failed, including cancellation, for the next run to
            // resume. One in the temp folder has a random name that no run looks for again
            if (TempFolder != null)
            {
                File.Delete(partialPath);
            }
        }
    }

    private DownloadResult DownloadLink(ManifestJob job, DepotManifest.FileData file, string finalPath)
    {
        if (string.IsNullOrEmpty(file.LinkTarget))
        {
            throw new InvalidDataException($"Link has no target: {file.FileName}");
        }

        var isDirectory = (file.Flags & EDepotFileFlag.Directory) != 0;

        switch (OutputFiles.WriteLink(job, finalPath, file.LinkTarget, isDirectory))
        {
            case LinkResult.AlreadyValid:
                return DownloadResult.AlreadyValid;

            case LinkResult.OutsideFolder:
                LinksOutsideFolder.Add($"{job}/{FileFilter.NormalizePath(file.FileName)}");
                return DownloadResult.LinkSkipped;

            case LinkResult.NoPrivilege:
                Interlocked.Increment(ref LinksWithoutPrivilege);
                return DownloadResult.LinkSkipped;

            default:
                return DownloadResult.Linked;
        }
    }

    private void LogSkippedLinks()
    {
        var withoutPrivilege = Interlocked.Exchange(ref LinksWithoutPrivilege, 0);

        if (withoutPrivilege > 0)
        {
            Log.Warn($"{Log.Plural(withoutPrivilege, "symbolic link")} skipped, since Windows doesn't allow this account to create them. Turn on Developer Mode in the Windows settings, or run in an elevated terminal.");
        }

        var outside = LinksOutsideFolder.Order(StringComparer.Ordinal).ToList();
        LinksOutsideFolder.Clear();

        if (outside.Count > 0)
        {
            Log.Warn($"{Log.Plural(outside.Count, "symbolic link")} skipped, since they point outside the output folder: {Log.JoinFirst(outside, 20)}.");
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
        using (await EnterDiskOperation())
        {
            await using var fs = File.OpenRead(path);
            var hash = await SHA1.HashDataAsync(fs, cancellationToken);

            return file.FileHash.SequenceEqual(hash);
        }
    }

    private readonly struct DiskOperation(SemaphoreSlim semaphore) : IDisposable
    {
        public void Dispose() => semaphore.Release();
    }

    // Limits the disk operations at once, until the returned value is disposed
    private async Task<DiskOperation> EnterDiskOperation()
    {
        await SemaphorePerDiskOperation.WaitAsync(cancellationToken);

        return new DiskOperation(SemaphorePerDiskOperation);
    }

    // Opens the file at the given length, and disposes it when that fails. A new file has its space allocated up front.
    // Returns the file's length from before
    private static FileStream OpenAtLength(string path, FileMode mode, long length, out long previousLength)
    {
        var preallocationSize = mode == FileMode.Create ? length : 0;
        var stream = OutputFiles.Open(path, mode, FileAccess.ReadWrite, FileOptions.Asynchronous, preallocationSize);

        try
        {
            previousLength = RandomAccess.GetLength(stream.SafeFileHandle);

            if (previousLength != length)
            {
                RandomAccess.SetLength(stream.SafeFileHandle, length);
            }

            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private async Task<FileStream> CreatePreallocatedFile(string path, long length)
    {
        using (await EnterDiskOperation())
        {
            return OpenAtLength(path, FileMode.Create, length, out _);
        }
    }

    // Opens the file at the given length, which keeps what an earlier run wrote. Returns its length from before, which is 0
    // for a file that is new, and then has its space allocated up front
    private async Task<(FileStream Stream, long ExistingLength)> OpenPartialFile(string path, long length)
    {
        using (await EnterDiskOperation())
        {
            var info = new FileInfo(path);

            if (!info.Exists || info.Length == 0)
            {
                return (OpenAtLength(path, FileMode.Create, length, out _), 0);
            }

            var stream = OpenAtLength(path, FileMode.OpenOrCreate, length, out var existingLength);

            return (stream, existingLength);
        }
    }

    // Downloads the chunks that a partial file of an earlier run doesn't have. Returns whether the file is complete, and the bytes it had
    private async Task<(bool Success, long KeptBytes)> ResumeChunksToFile(
        ManifestJob job,
        IReadOnlyCollection<ChunkPlacement> placements,
        long length,
        string path)
    {
        var (stream, existingLength) = await OpenPartialFile(path, length);

        using (stream)
        {
            return await DownloadMissingChunks(job, placements, stream.SafeFileHandle, existingLength);
        }
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
        using var stream = await CreatePreallocatedFile(path, length);
        var handle = stream.SafeFileHandle;

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
        var startBytes = ReadBytes();
        var startTime = TimeProvider.GetTimestamp();

        (double, string) RenderProgress()
        {
            var (network, disk) = BytesSince(startBytes);

            return DownloadProgress.FormatProgress(
                network,
                disk,
                Interlocked.Read(ref skippedBytes),
                totalBytes,
                TimeProvider.GetElapsedTime(startTime),
                $"{Volatile.Read(ref doneFiles)} / {partialFiles.Count} archives{FormatChunkErrorCount()}");
        }

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
                Log.Error($"[{partialFile.Job}] Failed to write {partialFile.File.FileName}: {OutputFiles.DescribeError(e)}");
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

        var (network, disk) = BytesSince(startBytes);
        var transfer = DownloadProgress.FormatTransfer(network, disk, TimeProvider.GetElapsedTime(startTime));

        Log.Info($"VPK archives: downloaded {transfer}; {DownloadProgress.FormatSize(skippedBytes)} already present.");
        LogChunkErrors();

        LogFailures("VPK archives", failedFiles, "Running the same command again retries only their missing chunks.");

        return failedFiles.IsEmpty;
    }

    internal static void LogFailures(string what, IEnumerable<string> names, string retryHint)
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
        using var stream = OutputFiles.Open(path, FileMode.OpenOrCreate, FileAccess.ReadWrite);
        var handle = stream.SafeFileHandle;
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

        var placements = chunks.Select(static c => new ChunkPlacement(c, (long)c.Offset)).ToList();

        return await DownloadMissingChunks(job, placements, handle, existingLength);
    }

    // Chunks are identified by their hash, so ones written by an earlier run, even of another build, are kept.
    // Returns whether all chunks are in the file, and the bytes of the ones that already were
    private async Task<(bool Success, long KeptBytes)> DownloadMissingChunks(
        ManifestJob job,
        IReadOnlyCollection<ChunkPlacement> placements,
        SafeFileHandle handle,
        long existingLength)
    {
        List<ChunkPlacement> missing;
        long keptBytes;

        using (await EnterDiskOperation())
        {
            (missing, keptBytes) = await FindMissingChunks(handle, existingLength, placements, cancellationToken);
        }

        var success = missing.Count == 0 || await DownloadChunks(job, missing, handle);

        if (success && missing.Count > 0)
        {
            File.SetLastWriteTimeUtc(handle, job.CreationTime);
        }

        return (success, keptBytes);
    }

    /// <summary>
    /// Finds the chunks that are not in the first existingLength bytes of a file at their position, by their hash.
    /// Returns them with the bytes of the chunks that are.
    /// </summary>
    internal static async Task<(List<ChunkPlacement> Missing, long KeptBytes)> FindMissingChunks(
        SafeFileHandle handle,
        long existingLength,
        IReadOnlyCollection<ChunkPlacement> placements,
        CancellationToken cancellationToken)
    {
        var missing = new List<ChunkPlacement>();
        var keptBytes = 0L;

        if (placements.Count == 0)
        {
            return (missing, keptBytes);
        }

        if (existingLength == 0)
        {
            missing.AddRange(placements);

            return (missing, keptBytes);
        }

        var buffer = ArrayPool<byte>.Shared.Rent((int)placements.Max(static p => p.Chunk.UncompressedLength));

        try
        {
            foreach (var placement in placements)
            {
                var chunk = placement.Chunk;
                var data = buffer.AsMemory(0, (int)chunk.UncompressedLength);
                var isInFile = false;

                if (placement.Position + data.Length <= existingLength)
                {
                    var read = await RandomAccess.ReadAsync(handle, data, placement.Position, cancellationToken);

                    // A gap that was never written reads as zeros, which is not worth hashing
                    isInFile = read == data.Length && data.Span.IndexOfAnyExcept((byte)0) >= 0 && IsChunkData(data.Span, chunk);
                }

                if (isInFile)
                {
                    keptBytes += data.Length;
                    continue;
                }

                missing.Add(placement);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return (missing, keptBytes);
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
        if (Interlocked.Increment(ref ActiveChunks) == 1)
        {
            Volatile.Write(ref LastProgress, TimeProvider.GetTimestamp());
        }

        try
        {
            return await DownloadChunkWithRetries(job, placement, handle, chunkCancellation);
        }
        finally
        {
            Interlocked.Decrement(ref ActiveChunks);
        }
    }

    // A chunk is retried until it downloads, a second server refuses it, or no chunk of the run has downloaded for OutageTimeout
    private async Task<bool> DownloadChunkWithRetries(
        ManifestJob job,
        ChunkPlacement placement,
        SafeFileHandle handle,
        CancellationToken chunkCancellation)
    {
        // Each attempt goes to another server than the last, so the first retries don't wait. The waits after them grow
        // to half a minute, which rides out a network outage
        const int ImmediateRetries = 2;

        Server? lastServer = null;
        Server? refusedBy = null;

        for (var attempt = 0; ; attempt++)
        {
            var (error, server) = await TryDownloadChunk(job, placement, handle, lastServer, chunkCancellation);

            if (error == null)
            {
                Volatile.Write(ref LastProgress, TimeProvider.GetTimestamp());
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

            if (refusedByAnotherServer || IsOutage())
            {
                Interlocked.Increment(ref FailedChunks);
                return false;
            }

            // Another server may have it, so it's tried right away. When the same server refuses it again, because it's the
            // only one, the wait applies
            if (isRefusal && refusedBy == null)
            {
                refusedBy = server;
                continue;
            }

            if (attempt >= ImmediateRetries)
            {
                var exponent = Math.Min(attempt - ImmediateRetries + 1, MaxBackoffExponent);
                var delay = SteamSession.ExponentialBackoff(exponent);

                if (!await DelayUnlessCancelled(delay, chunkCancellation))
                {
                    return false;
                }
            }
        }
    }

    private bool IsOutage()
    {
        var sinceProgress = TimeProvider.GetElapsedTime(Volatile.Read(ref LastProgress));

        if (sinceProgress < OutageTimeout)
        {
            return false;
        }

        Volatile.Write(ref OutageDetected, 1);

        return true;
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
            var lease = session.CdnServers.Rent(avoid);
            var server = lease.Server;
            int written;

            try
            {
                using (lease)
                {
                    written = await session.DownloadChunkAsync(job, chunk, server, buffer);
                }

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
            Interlocked.Add(ref NetworkBytes, chunk.CompressedLength);
            Interlocked.Add(ref DiskBytes, written);

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
