using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2.Internal;

namespace SteamFileDownloader;

/// <summary>
/// The one file of a legacy workshop item, which is a plain download instead of a depot manifest.
/// </summary>
internal sealed class LegacyItem
{
    public required ulong ItemID;
    public required string FileName;
    public required Uri Url;

    // 0 when Steam doesn't say
    public required long Size;
    public required uint TimeUpdated;
    public required string OutputFolder;

    // Whether the file name matched the patterns of the run
    public bool Requested;

    // The manifest column of the rows of legacy items, which have no manifest
    public const string ManifestColumn = "legacy";

    // How messages name the item where its id alone could be a depot
    public string Label => $"{this}:legacy";

    public override string ToString() => ItemID.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Creates the item of a workshop item that says where its file is, with a web address, and what it's called.
    /// Returns null for one that doesn't.
    /// </summary>
    public static LegacyItem? TryCreate(PublishedFileDetails details, string outputFolder)
    {
        if (!Uri.TryCreate(details.file_url, UriKind.Absolute, out var url))
        {
            return null;
        }

        if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        var fileName = GetFileName(details.filename);

        if (fileName == null)
        {
            return null;
        }

        return new LegacyItem
        {
            ItemID = details.publishedfileid,
            FileName = fileName,
            Url = url,
            Size = (long)details.file_size,
            TimeUpdated = details.time_updated,
            OutputFolder = outputFolder,
        };
    }

    /// <summary>
    /// The name of the file in a path Steam gives for it: the part after the last slash, or null when that is no name
    /// a file can have.
    /// </summary>
    public static string? GetFileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var name = path[(path.LastIndexOfAny(['/', '\\']) + 1)..].Trim();

        if (name.Length == 0 || name is "." or "..")
        {
            return null;
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return null;
        }

        return name;
    }
}

/// <summary>
/// Downloads the files of legacy workshop items with plain HTTP requests.
/// </summary>
internal sealed class LegacyFileDownloader(HttpClient client, CancellationToken cancellationToken)
{
    private const int MaxAttempts = 5;

    // Items downloaded at once
    private const int Connections = 8;

    private const int BufferSize = 81920;

    private enum DownloadResult
    {
        Downloaded,
        AlreadyValid,
        Failed,
    }

    private readonly record struct Attempt(string? Error, bool Retryable);

    // The time to get the response headers, and the time a download may go without receiving data
    public TimeSpan HeaderTimeout { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromMinutes(1);

    // The wait in milliseconds before the retry after the given number of failed attempts
    public Func<int, int> Backoff { get; init; } = SteamSession.ExponentialBackoff;

    // The clock of the download speeds
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    private long DownloadedBytes;

    /// <summary>
    /// Whether the file is already there: it has the size Steam gives, and is not older than the item's last update.
    /// A file of unknown size is never up to date, since a size is all that can tell its versions apart.
    /// </summary>
    public static bool IsUpToDate(string path, LegacyItem item)
    {
        var file = new FileInfo(path);

        if (!file.Exists || item.Size == 0 || file.Length != item.Size)
        {
            return false;
        }

        var written = new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeSeconds();

        return item.TimeUpdated <= written;
    }

    /// <summary>
    /// Downloads the files of items, into the items' folders. Returns the items whose files are on disk, with
    /// their paths, and whether all of them succeeded.
    /// </summary>
    public async Task<(bool Success, List<(LegacyItem Item, string Path)> Files)> DownloadAllAsync(IReadOnlyList<LegacyItem> items)
    {
        var files = new ConcurrentBag<(LegacyItem Item, string Path)>();
        var failed = new ConcurrentBag<string>();
        var downloaded = 0;
        var alreadyPresent = 0;
        var done = 0;
        var skippedBytes = 0L;
        var totalBytes = items.Sum(static i => i.Size);
        var startTime = TimeProvider.GetTimestamp();

        (double, string) RenderProgress()
        {
            var bytes = Interlocked.Read(ref DownloadedBytes);

            return DownloadProgress.FormatProgress(
                bytes,
                bytes,
                Interlocked.Read(ref skippedBytes),
                totalBytes,
                TimeProvider.GetElapsedTime(startTime),
                $"{Volatile.Read(ref done)} / {items.Count} files");
        }

        async ValueTask DownloadItem(LegacyItem item)
        {
            try
            {
                var (result, path, error) = await DownloadAsync(item);

                if (result == DownloadResult.Failed)
                {
                    Log.Error($"[{item}] Failed to download {item.FileName}: {error}");
                    failed.Add(item.ToString());
                    return;
                }

                if (result == DownloadResult.AlreadyValid)
                {
                    Interlocked.Increment(ref alreadyPresent);
                    Interlocked.Add(ref skippedBytes, item.Size);
                }
                else
                {
                    Interlocked.Increment(ref downloaded);
                }

                files.Add((item, path));
            }
            catch (Exception e) when (OutputFiles.IsFileError(e))
            {
                Log.Error($"[{item}] Failed to write {item.FileName}: {e.Message}");
                failed.Add(item.ToString());
            }
            finally
            {
                Interlocked.Increment(ref done);
            }
        }

        var totalSize = DownloadProgress.FormatSize(totalBytes);

        Log.Info($"Downloading {Log.Plural(items.Count, "legacy file")} ({totalSize})...");

        await using (Log.StartProgress(RenderProgress))
        {
            var options = new ParallelOptions { MaxDegreeOfParallelism = Connections, CancellationToken = cancellationToken };

            await Parallel.ForEachAsync(items, options, (item, _) => DownloadItem(item));
        }

        var counts = new List<string>();

        if (downloaded > 0)
        {
            var bytes = Interlocked.Read(ref DownloadedBytes);
            var transfer = DownloadProgress.FormatTransfer(bytes, bytes, TimeProvider.GetElapsedTime(startTime));

            counts.Add($"{downloaded} downloaded ({transfer})");
        }

        if (alreadyPresent > 0)
        {
            counts.Add($"{alreadyPresent} already present");
        }

        if (counts.Count > 0)
        {
            Log.Info($"Legacy files: {string.Join(", ", counts)}.");
        }

        FileDownloader.LogFailures("legacy files", failed, "Running the same command again retries only those.");

        return (failed.IsEmpty, [.. files]);
    }

    private async Task<(DownloadResult Result, string Path, string? Error)> DownloadAsync(LegacyItem item)
    {
        var path = OutputFiles.GetFinalPath(item.OutputFolder, item.FileName);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (IsUpToDate(path, item))
        {
            return (DownloadResult.AlreadyValid, path, null);
        }

        var partialPath = path + ".partial";

        try
        {
            for (var attempt = 1; ; attempt++)
            {
                var result = await TryDownloadAsync(item, partialPath);

                if (result.Error == null)
                {
                    break;
                }

                if (!result.Retryable || attempt == MaxAttempts)
                {
                    return (DownloadResult.Failed, path, result.Error);
                }

                await SteamSession.DelayAfterFailureAsync($"[{item}] {item.FileName}: {result.Error}", attempt, MaxAttempts, Backoff, cancellationToken);
            }

            if (item.TimeUpdated != 0)
            {
                File.SetLastWriteTimeUtc(partialPath, DateTimeOffset.FromUnixTimeSeconds(item.TimeUpdated).UtcDateTime);
            }

            File.Move(partialPath, path, overwrite: true);

            return (DownloadResult.Downloaded, path, null);
        }
        finally
        {
            // Left over when anything failed, including cancellation
            File.Delete(partialPath);
        }
    }

    // Network failures end the attempt with an error, which is retried unless it is a refusal. Failures to write the file throw
    private async Task<Attempt> TryDownloadAsync(LegacyItem item, string partialPath)
    {
        var attemptBytes = 0L;
        var complete = false;

        try
        {
            // The file's URL is the only request, since HEAD gets a 404 where GET works
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            timeout.CancelAfter(HeaderTimeout);

            HttpResponseMessage response;

            try
            {
                response = await client.GetAsync(item.Url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            }
            catch (Exception e) when (SteamSession.IsError(e, cancellationToken))
            {
                return new Attempt(Describe(e), Retryable: true);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    var status = (int)response.StatusCode;

                    return new Attempt($"HTTP {status}", IsRetryable(response.StatusCode));
                }

                Stream responseBody;

                try
                {
                    responseBody = await response.Content.ReadAsStreamAsync(timeout.Token);
                }
                catch (Exception e) when (SteamSession.IsError(e, cancellationToken))
                {
                    return new Attempt(Describe(e), Retryable: true);
                }

                await using var body = responseBody;
                var buffer = new byte[BufferSize];

                using var file = OutputFiles.Open(partialPath, FileMode.Create, FileAccess.Write, FileOptions.Asynchronous, item.Size);

                while (true)
                {
                    timeout.CancelAfter(ReadTimeout);

                    int read;

                    try
                    {
                        read = await body.ReadAsync(buffer, timeout.Token);
                    }
                    catch (Exception e) when (SteamSession.IsError(e, cancellationToken))
                    {
                        return new Attempt(Describe(e), Retryable: true);
                    }

                    if (read == 0)
                    {
                        break;
                    }

                    await RandomAccess.WriteAsync(file.SafeFileHandle, buffer.AsMemory(0, read), attemptBytes, cancellationToken);

                    attemptBytes += read;
                    Interlocked.Add(ref DownloadedBytes, read);
                }

                if (item.Size != 0 && attemptBytes != item.Size)
                {
                    return new Attempt($"got {attemptBytes} bytes, but Steam says the file has {item.Size}", Retryable: false);
                }

                complete = true;

                return new Attempt(null, Retryable: false);
            }
        }
        finally
        {
            // A failed attempt's bytes are downloaded again by the next
            if (!complete && attemptBytes > 0)
            {
                Interlocked.Add(ref DownloadedBytes, -attemptBytes);
            }
        }
    }

    // Requests that did not fail for a reason that repeating them can't change
    private static bool IsRetryable(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;

    private static string Describe(Exception e) => e is OperationCanceledException ? "timed out" : e.Message;
}
