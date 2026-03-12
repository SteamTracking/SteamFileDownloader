using System;
using System.Buffers;
using System.Collections.Frozen;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;
using SteamKit2.CDN;

namespace SteamFileDownloader;

internal partial class FileDownloader : IDisposable
{
    private const string PAK01_DIR = "pak01_dir.vpk";

    private enum DownloadResult
    {
        SomethingWentWrong,
        Success,
        DownloadFailed,
    }

    [JsonSourceGenerationOptions(AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip)]
    [JsonSerializable(typeof(Dictionary<uint, List<string>>))]
    private partial class FileDownloaderContext : JsonSerializerContext
    {
    }

    private readonly SemaphoreSlim SemaphorePerFile = new(6, 6);
    private readonly SemaphoreSlim SemaphorePerDownloadChunk = new(20, 20);
    private FrozenDictionary<uint, Regex> Files = FrozenDictionary<uint, Regex>.Empty;
    private FrozenDictionary<uint, string[]> DownloadFromPaks = FrozenDictionary<uint, string[]>.Empty;
    private readonly string OutputFolder;
    private readonly Client CDNClient;
    private readonly Func<Server> GetContentServer;
    private readonly Action<Server> MarkContentServerAsBad;
    private readonly CancellationToken CancellationToken;

    public FileDownloader(Client cdnClient, string outputFolder, Func<Server> getContentServer, Action<Server> markContentServerAsBad, CancellationToken cancellationToken)
    {
        CDNClient = cdnClient;
        OutputFolder = outputFolder;
        GetContentServer = getContentServer;
        MarkContentServerAsBad = markContentServerAsBad;
        CancellationToken = cancellationToken;

        LoadFilesMapping();
    }

    public void Dispose()
    {
        SemaphorePerFile.Dispose();
        SemaphorePerDownloadChunk.Dispose();
    }

    private void LoadFilesMapping()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "files.json");
        var files = JsonSerializer.Deserialize(File.ReadAllText(file), FileDownloaderContext.Default.DictionaryUInt32ListString);
        Debug.Assert(files != null);

        var filesMapping = new Dictionary<uint, Regex>();
        var paksMapping = new Dictionary<uint, string[]>();

        foreach (var (depotid, fileMatches) in files)
        {
            var patterns = new List<string>(fileMatches.Count);

            foreach (var fileMatch in fileMatches)
            {
                if (fileMatch.StartsWith("vpk:", StringComparison.Ordinal))
                {
                    paksMapping.Add(depotid, fileMatch["vpk:".Length..].Split(','));
                    continue;
                }

                patterns.Add(ConvertFileMatch(fileMatch));
            }

            var pattern = $"^({string.Join("|", patterns)})$";

            filesMapping.Add(depotid, new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture));
        }

        Files = filesMapping.ToFrozenDictionary();
        DownloadFromPaks = paksMapping.ToFrozenDictionary();
    }

    public bool IsImportantDepot(uint depotID) => Files.ContainsKey(depotID);

    /*
     * Here be dragons.
     */
    public async Task<EResult> DownloadFilesFromDepot(ManifestJob job, DepotManifest depotManifest)
    {
        Debug.Assert(depotManifest.Files != null);

        var filesRegex = Files[job.DepotID];
        var files = depotManifest.Files
            .Where(static x => (x.Flags & EDepotFileFlag.Directory) == 0)
            .Where(x => filesRegex.IsMatch(x.FileName.Replace('\\', '/')))
            .ToList();
        var downloadState = (int)DownloadResult.SomethingWentWrong;

        DownloadFromPaks.TryGetValue(job.DepotID, out var pakExtensions);

        Console.WriteLine($"[Depot {job.DepotID}] Will download {files.Count} files");

        var downloadedFiles = 0;
        var totalFileCount = files.Count;
        var fileTasks = new Task[files.Count];

        var additionalTasks = new ConcurrentBag<Task>();

        async Task DownloadFileLocal(DepotManifest.FileData file)
        {
            var (fileState, finalPath) = await DownloadFile(job, file);

            if (fileState is DownloadResult.Success)
            {
                var done = Interlocked.Increment(ref downloadedFiles);
                var remaining = totalFileCount - done;
                Console.WriteLine($"[Depot {job.DepotID}] Downloaded {file.FileName} ({remaining} files left)");

                if (pakExtensions != default && finalPath.Name == PAK01_DIR)
                {
                    HashSet<int> archives;

                    try
                    {
                        archives = ParsePak(finalPath.ToString(), pakExtensions);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"[ERROR] Failed to parse VPK: {e.Message}");
                        return;
                    }

                    Interlocked.Add(ref totalFileCount, archives.Count);

                    foreach (var archiveIndex in archives.Order())
                    {
                        var archiveFileName = Path.Join(Path.GetDirectoryName(file.FileName), $"pak01_{archiveIndex:D3}.vpk").Replace('\\', '/');
                        var archiveFile = depotManifest.Files.Find(f => f.FileName.Replace('\\', '/') == archiveFileName);

                        if (archiveFile == default)
                        {
                            Console.WriteLine($"[WARN] [Depot {job.DepotID}] Failed to find {archiveFileName}");
                            continue;
                        }

                        additionalTasks.Add(Task.Run(async () =>
                        {
                            await SemaphorePerFile.WaitAsync(CancellationToken);

                            try
                            {
                                await DownloadFileLocal(archiveFile);
                            }
                            finally
                            {
                                SemaphorePerFile.Release();
                            }
                        }));
                    }
                }
            }

            if (fileState is DownloadResult.DownloadFailed)
            {
                Interlocked.Exchange(ref downloadState, (int)DownloadResult.DownloadFailed);
            }
            else if (fileState is DownloadResult.Success)
            {
                Interlocked.CompareExchange(ref downloadState, (int)DownloadResult.Success, (int)DownloadResult.SomethingWentWrong);
            }
        }

        for (var i = 0; i < fileTasks.Length; i++)
        {
            var file = files[i];
            fileTasks[i] = Task.Run(async () =>
            {
                await SemaphorePerFile.WaitAsync(CancellationToken);

                try
                {
                    await DownloadFileLocal(file);
                }
                finally
                {
                    SemaphorePerFile.Release();
                }
            });
        }

        await Task.WhenAll(fileTasks);
        await Task.WhenAll(additionalTasks);

#pragma warning disable IDE0072
        return (DownloadResult)downloadState switch
        {
            DownloadResult.Success => EResult.OK,
            DownloadResult.DownloadFailed => EResult.DataCorruption,
            _ => EResult.Ignored
        };
#pragma warning restore IDE0072
    }

    private FileInfo GetFinalPath(string fileName)
    {
        var path = Path.Combine(OutputFolder, fileName);
        return new FileInfo(path);
    }

    private async Task<(DownloadResult Result, FileInfo Path)> DownloadFile(ManifestJob job, DepotManifest.FileData file)
    {
        var finalPath = GetFinalPath(file.FileName);
        var downloadPath = new FileInfo(Path.Combine(Path.GetTempPath(), Path.ChangeExtension(Path.GetRandomFileName(), ".steamdb_tmp")));

        Directory.CreateDirectory(finalPath.Directory!.FullName);

        if (file.TotalSize == 0)
        {
            await using (var _ = finalPath.Create())
            {
            }

            return (DownloadResult.Success, finalPath);
        }

        var chunks = file.Chunks;

        await using (var fs = downloadPath.Open(FileMode.OpenOrCreate, FileAccess.ReadWrite))
        {
            fs.SetLength((long)file.TotalSize);
        }

        using var chunkCancellation = new CancellationTokenSource();
        var chunkTasks = new Task[chunks.Count];

        for (var i = 0; i < chunkTasks.Length; i++)
        {
            var chunk = chunks[i];
            chunkTasks[i] = Task.Run(async () =>
            {
                await SemaphorePerDownloadChunk.WaitAsync(CancellationToken);

                try
                {
                    var result = await DownloadChunk(job, chunk, downloadPath, chunkCancellation);

                    if (!result)
                    {
                        Console.WriteLine($"[WARN] [Depot {job.DepotID}] Failed to download chunk for {file.FileName} ({chunk.Offset})");

                        await chunkCancellation.CancelAsync();
                    }
                }
                finally
                {
                    SemaphorePerDownloadChunk.Release();
                }
            });
        }

        await Task.WhenAll(chunkTasks);

        byte[] checksum;

        await using (var fs = downloadPath.Open(FileMode.Open, FileAccess.Read))
        {
            using var sha = SHA1.Create();
            checksum = await sha.ComputeHashAsync(fs);
        }

        if (!file.FileHash.SequenceEqual(checksum))
        {
            Console.WriteLine($"[ERROR] [Depot {job.DepotID}] Hash check failed for {file.FileName} ({job.Server})");

            downloadPath.Delete();

            return (DownloadResult.DownloadFailed, finalPath);
        }

        finalPath.Delete();

        downloadPath.MoveTo(finalPath.FullName);

        return (DownloadResult.Success, finalPath);
    }

    private async Task<bool> DownloadChunk(ManifestJob job, DepotManifest.ChunkData chunk, FileInfo downloadPath, CancellationTokenSource chunkCancellation)
    {
        const int TRIES = 6;

        for (var i = 0; i <= TRIES; i++)
        {
            if (chunkCancellation.Token.IsCancellationRequested)
            {
                return false;
            }

            try
            {
                var buffer = ArrayPool<byte>.Shared.Rent((int)chunk.UncompressedLength);

                try
                {
                    var written = await CDNClient.DownloadDepotChunkAsync(job.DepotID, chunk, job.Server!, buffer, job.DepotKey);

                    await using var fs = downloadPath.Open(FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                    fs.Seek((long)chunk.Offset, SeekOrigin.Begin);
                    await fs.WriteAsync(buffer.AsMemory(0, written));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                return true;
            }
            catch (Exception e)
            {
                Console.WriteLine($"[WARN] [Depot {job.DepotID}] Chunk download error: {e.Message}");

                if (i > 1 && e is SteamKitWebRequestException webRequestException && (int)webRequestException.StatusCode >= 500)
                {
                    MarkContentServerAsBad(job.Server!);
                }
            }

            if (i < TRIES)
            {
                await Task.Delay(ExponentialBackoff(i + 1), CancellationToken);

                if (i > 0)
                {
                    job.Server = GetContentServer();
                }
            }
        }

        return false;
    }

    private static HashSet<int> ParsePak(string filePath, string[] extensions)
    {
        using var package = new SteamDatabase.ValvePak.Package();
        package.Read(filePath);

        Debug.Assert(package.Entries != null);

        var archives = new HashSet<int>();

        foreach (var ext in extensions)
        {
            if (package.Entries.TryGetValue(ext, out var entries))
            {
                foreach (var entry in entries)
                {
                    if (entry.ArchiveIndex != 32767)
                    {
                        archives.Add(entry.ArchiveIndex);
                    }
                }
            }
        }

        return archives;
    }

    private static string ConvertFileMatch(string input)
    {
        if (input.StartsWith("regex:", StringComparison.Ordinal))
        {
            return input[6..];
        }

        return Regex.Escape(input);
    }

    internal static int ExponentialBackoff(int i)
    {
        return ((1 << i) * 1000) + Random.Shared.Next(1001);
    }
}
