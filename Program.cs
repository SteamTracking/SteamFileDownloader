using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ConsoleAppFramework;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.CDN;

namespace SteamFileDownloader;

internal static partial class Program
{
    private static readonly bool IsCI = Environment.GetEnvironmentVariable("CI") != null;
    private static readonly List<Server> CDNServers = [];
    private static int NextCdnServer;

    internal static void LogWarn(string message)
    {
        if (IsCI)
        {
            Console.Error.WriteLine($"::warning::{message}");
        }
        else
        {
            Console.Error.WriteLine($"[WARN] {message}");
        }
    }

    private static Server GetContentServer()
    {
        var i = NextCdnServer % CDNServers.Count;
        return CDNServers[i];
    }

    private static void MarkContentServerAsBad(Server server)
    {
        lock (CDNServers)
        {
            if (CDNServers[NextCdnServer % CDNServers.Count] == server)
            {
                NextCdnServer++;
            }
        }

        LogWarn($"Download failed from server {server}");
    }

    private static void Main(string[] args)
    {
        ConsoleApp.Run(args, Run);
    }

    /// <summary>
    /// Downloads files from Steam depots.
    /// </summary>
    /// <param name="appid">Steam App ID to download.</param>
    /// <param name="username">Steam username.</param>
    /// <param name="password">Steam password.</param>
    /// <param name="output">Output directory for downloaded files.</param>
    /// <param name="branch">Depot branch to download from.</param>
    /// <param name="saveManifest">Save manifest text files to the output directory.</param>
    private static async Task<int> Run(uint appid, string username, string password, string output, string branch = "public", bool saveManifest = false)
    {
        using var cts = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var outputPath = Path.GetFullPath(output);

        var client = new SteamClient();
        var user = client.GetHandler<SteamUser>()!;
        var apps = client.GetHandler<SteamApps>()!;
        var content = client.GetHandler<SteamContent>()!;
        var manager = new CallbackManager(client);

        // Setup file downloader early to validate file mapping before logging in
        var cdnClient = new Client(client);
        Client.RequestTimeout = TimeSpan.FromSeconds(60);
        Client.ResponseBodyTimeout = TimeSpan.FromSeconds(120);

        using var fileDownloader = new FileDownloader(cdnClient, outputPath, GetContentServer, MarkContentServerAsBad, cts.Token);

        // Connect
        Console.WriteLine("Connecting to Steam...");

        var connectedTcs = new TaskCompletionSource<bool>();
        var loggedOnTcs = new TaskCompletionSource<SteamUser.LoggedOnCallback>();

        using var sub1 = manager.Subscribe<SteamClient.ConnectedCallback>(_ => connectedTcs.TrySetResult(true));
        using var sub2 = manager.Subscribe<SteamClient.DisconnectedCallback>(_ =>
        {
            connectedTcs.TrySetResult(false);
            loggedOnTcs.TrySetResult(null!);
        });
        using var sub3 = manager.Subscribe<SteamUser.LoggedOnCallback>(cb => loggedOnTcs.TrySetResult(cb));

        client.Connect();

        // Run callback pump in background
        using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);

        _ = Task.Run(async () =>
        {
            try
            {
                while (!pumpCts.IsCancellationRequested)
                {
                    await manager.RunWaitCallbackAsync(pumpCts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        });

        if (!await connectedTcs.Task)
        {
            LogWarn("Failed to connect to Steam.");
            return 1;
        }

        if (username == "anonymous")
        {
            Console.WriteLine("Connected. Logging in anonymously...");

            user.LogOnAnonymous();
        }
        else
        {
            Console.WriteLine("Connected. Authenticating...");

            // Authenticate
            string refreshToken;

            try
            {
                var authSession = await client.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
                {
                    Username = username,
                    Password = password,
                    IsPersistentSession = false,
                    DeviceFriendlyName = nameof(SteamFileDownloader),
                    Authenticator = !Console.IsOutputRedirected ? new UserConsoleAuthenticator() : null,
                });

                var pollResult = await authSession.PollingWaitForResultAsync();
                refreshToken = pollResult.RefreshToken;
            }
            catch (Exception e)
            {
                LogWarn($"Authentication failed: {e.Message}");
                client.Disconnect();
                return 1;
            }

            Console.WriteLine("Authenticated. Logging in...");

            user.LogOn(new SteamUser.LogOnDetails
            {
                Username = username,
                AccessToken = refreshToken,
                ShouldRememberPassword = false,
            });
        }

        var logOnResult = await loggedOnTcs.Task;

        if (logOnResult?.Result != EResult.OK)
        {
            LogWarn($"Failed to log on: {logOnResult?.Result}");
            client.Disconnect();
            return 1;
        }

        Console.WriteLine($"Logged in. Cell ID: {logOnResult.CellID}");

        // Fetch CDN servers
        try
        {
            var servers = await content.GetServersForSteamPipe(cellId: logOnResult.CellID, maxNumServers: 100);

            foreach (var server in servers)
            {
                if (server.AllowedAppIds.Length > 0 || server.UseAsProxy || server.SteamChinaOnly)
                {
                    continue;
                }

                if (server.Type is not "SteamCache" and not "CDN")
                {
                    continue;
                }

                CDNServers.Add(server);
            }
        }
        catch (Exception e)
        {
            LogWarn($"Failed to get CDN servers: {e.Message}");
            client.Disconnect();
            return 1;
        }

        if (CDNServers.Count == 0)
        {
            LogWarn("No CDN servers available.");
            client.Disconnect();
            return 1;
        }

        Console.WriteLine($"Got {CDNServers.Count} CDN servers.");

        // Fetch app info via PICS
        Console.WriteLine($"Fetching app info for {appid}...");

        var tokenResult = await apps.PICSGetAccessTokens(appid, null);
        var accessToken = tokenResult.AppTokens.TryGetValue(appid, out var t) ? t : 0UL;

        var productInfo = await apps.PICSGetProductInfo(
            new SteamApps.PICSRequest(appid, accessToken),
            null
        );

        if (productInfo.Results == null || productInfo.Results.Count == 0 || !productInfo.Results[0].Apps.TryGetValue(appid, out var appInfo))
        {
            LogWarn("Failed to get app info.");
            client.Disconnect();
            return 1;
        }

        var depots = appInfo.KeyValues["depots"];

        if (depots == KeyValue.Invalid || depots.Children.Count == 0)
        {
            LogWarn("App has no depots.");
            client.Disconnect();
            return 1;
        }

        // Log build ID for this branch
        int? buildId = null;
        var branchesKv = depots["branches"];

        if (branchesKv != KeyValue.Invalid)
        {
            var branchInfoKv = branchesKv[branch];

            if (branchInfoKv != KeyValue.Invalid && int.TryParse(branchInfoKv["buildid"].Value, out var parsedBuildId))
            {
                buildId = parsedBuildId;
                Console.WriteLine($"Branch \"{branch}\": build {parsedBuildId}");
            }
        }

        // Parse depots and find important ones
        var manifestJobs = new List<ManifestJob>();

        foreach (var depot in depots.Children)
        {
            if (!uint.TryParse(depot.Name, out var depotID))
            {
                continue;
            }

            if (!fileDownloader.IsImportantDepot(depotID))
            {
                continue;
            }

            // Skip depots with depotfromapp (shared depots)
            if (depot["depotfromapp"].Value != null)
            {
                continue;
            }

            var manifests = depot["manifests"];
            var manifestID = 0UL;
            var manifestBranch = branch;

            if (manifests != KeyValue.Invalid)
            {
                manifestID = GetManifestIdForBranch(manifests, branch);

                // If the requested branch has no manifest, fall back to public
                if (manifestID == 0 && !string.Equals(branch, "public", StringComparison.OrdinalIgnoreCase))
                {
                    manifestID = GetManifestIdForBranch(manifests, "public");

                    if (manifestID != 0)
                    {
                        manifestBranch = "public";
                        Console.WriteLine($"Depot {depotID}: branch \"{branch}\" has no manifest, falling back to \"public\"");
                    }
                }
            }

            if (manifestID == 0)
            {
                LogWarn($"No manifest found for depot {depotID} on branch \"{branch}\"");
                continue;
            }

            manifestJobs.Add(new ManifestJob
            {
                DepotID = depotID,
                ManifestID = manifestID,
                Branch = manifestBranch,
            });

            Console.WriteLine($"Found depot {depotID}: manifest {manifestID}");
        }

        if (manifestJobs.Count == 0)
        {
            Console.WriteLine("No depots to download found. Check your files.json configuration.");
            client.Disconnect();
            return 1;
        }

        // Fetch depot keys, manifest request codes, and manifests
        var depotManifests = new List<(ManifestJob Job, DepotManifest Manifest)>();

        foreach (var job in manifestJobs)
        {
            Console.WriteLine();
            Console.WriteLine($"Processing depot {job.DepotID}...");

            // Get depot decryption key
            try
            {
                var keyTask = apps.GetDepotDecryptionKey(job.DepotID, appid);
                keyTask.Timeout = TimeSpan.FromSeconds(30);
                var keyResult = await keyTask;

                if (keyResult.Result != EResult.OK)
                {
                    LogWarn($"No access to depot {job.DepotID} ({keyResult.Result})");
                    continue;
                }

                job.DepotKey = keyResult.DepotKey;
            }
            catch (TaskCanceledException)
            {
                LogWarn($"Depot key request timed out for {job.DepotID}");
                continue;
            }

            // Get manifest request code
            ulong manifestRequestCode;

            try
            {
                manifestRequestCode = await content.GetManifestRequestCode(job.DepotID, appid, job.ManifestID, job.Branch);
            }
            catch
            {
                // Retry once after delay
                await Task.Delay(2000, cts.Token);

                try
                {
                    manifestRequestCode = await content.GetManifestRequestCode(job.DepotID, appid, job.ManifestID, job.Branch);
                }
                catch
                {
                    LogWarn($"Manifest request code timed out for depot {job.DepotID}");
                    continue;
                }
            }

            if (manifestRequestCode == 0)
            {
                LogWarn($"No manifest request code for depot {job.DepotID}");
                continue;
            }

            // Download manifest with retries
            DepotManifest? depotManifest = null;

            job.Server = GetContentServer();

            for (var i = 0; i <= 5; i++)
            {
                try
                {
                    depotManifest = await cdnClient.DownloadManifestAsync(job.DepotID, job.ManifestID, manifestRequestCode, job.Server, job.DepotKey);
                    break;
                }
                catch (Exception e)
                {
                    LogWarn($"Failed to download manifest for depot {job.DepotID} ({job.Server}: {e.Message}) (#{i})");

                    MarkContentServerAsBad(job.Server);

                    if (i < 5)
                    {
                        await Task.Delay(FileDownloader.ExponentialBackoff(i + 1), cts.Token);
                        job.Server = GetContentServer();
                    }
                }
            }

            if (depotManifest == null)
            {
                LogWarn($"Failed to download manifest for depot {job.DepotID} after all retries.");
                continue;
            }

            Console.WriteLine($"Downloaded manifest for depot {job.DepotID} ({depotManifest.Files?.Count} files in manifest)");

            if (saveManifest)
            {
                DumpManifestToTextFile(outputPath, job, depotManifest);
            }

            depotManifests.Add((job, depotManifest));
        }

        // Done with Steam, disconnect before downloading files from CDN
        Console.WriteLine();
        Console.WriteLine("Disconnecting from Steam...");
        pumpCts.Cancel();
        client.Disconnect();

        // Download files from all depots concurrently
        var downloadTasks = depotManifests.Select(async entry =>
        {
            var result = await fileDownloader.DownloadFilesFromDepot(entry.Job, entry.Manifest);

            if (result == EResult.OK)
            {
                Console.WriteLine($"Depot {entry.Job.DepotID} downloaded successfully.");
            }
            else
            {
                LogWarn($"Depot {entry.Job.DepotID} download result: {result}");
            }

            return result;
        }).ToArray();

        var results = await Task.WhenAll(downloadTasks);
        var allSucceeded = results.All(r => r == EResult.OK);

        if (allSucceeded && buildId.HasValue)
        {
            await File.WriteAllTextAsync(Path.Combine(outputPath, "steam_buildid.txt"), buildId.Value.ToString());
        }

        Console.WriteLine();
        Console.WriteLine("Done.");

        return allSucceeded ? 0 : 1;
    }

    static ulong GetManifestIdForBranch(KeyValue manifests, string branch)
    {
        var branchKv = manifests[branch];

        if (branchKv == KeyValue.Invalid)
            return 0;

        // Can be either direct value or have a "gid" child
        if (branchKv.Value != null)
        {
            return ulong.TryParse(branchKv.Value, out var id) ? id : 0;
        }

        return ulong.TryParse(branchKv["gid"].Value, out var gid) ? gid : 0;
    }

    static void DumpManifestToTextFile(string outputPath, ManifestJob job, DepotManifest manifest)
    {
        Debug.Assert(manifest.Files != null);

        var manifestDir = Path.Combine(outputPath, "manifests");
        Directory.CreateDirectory(manifestDir);

        var txtManifest = Path.Combine(manifestDir, $"manifest_{job.DepotID}.txt");
        using var sw = new StreamWriter(txtManifest);

        sw.WriteLine($"Content Manifest for Depot {job.DepotID} ");
        sw.WriteLine();
        sw.WriteLine($"Manifest ID / date     : {job.ManifestID} / {manifest.CreationTime} ");

        var uniqueChunks = new HashSet<byte[]>(new ChunkIdComparer());

        foreach (var file in manifest.Files)
        {
            foreach (var chunk in file.Chunks)
            {
                Debug.Assert(chunk.ChunkID != null);

                uniqueChunks.Add(chunk.ChunkID);
            }
        }

        sw.WriteLine($"Total number of files  : {manifest.Files.Count} ");
        sw.WriteLine($"Total number of chunks : {uniqueChunks.Count} ");
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
