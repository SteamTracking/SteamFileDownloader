using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.CDN;

namespace SteamFileDownloader;

/// <summary>
/// How to log on: anonymously, with a password (authenticated on every run), or with a saved refresh token.
/// </summary>
internal sealed record LogOnCredentials(string Username, string? Password = null, string? RefreshToken = null)
{
    public static LogOnCredentials Anonymous { get; } = new("anonymous");

    public bool IsAnonymous => Username == "anonymous";
}

/// <summary>
/// One Steam connection, used to get depot keys and manifests. Files themselves come from the CDN without it.
/// </summary>
internal sealed class SteamSession : IDisposable
{
    private const int MaxRetries = 7;
    private const int MaxCdnRetries = 5;
    private const int MaxRelogons = 2;

    // Each manifest mostly waits on a Steam request code and a CDN download
    private const int ManifestConcurrency = 16;

    private readonly SteamClient SteamClient = new();
    private readonly CallbackManager Manager;
    private readonly SteamUser User;
    private readonly SteamApps Apps;
    private readonly SteamContent Content;
    private readonly CancellationTokenSource PumpCts;
    private readonly CancellationToken CancellationToken;
    private readonly Dictionary<(uint DepotID, uint AppID), Task<(byte[]? Key, bool NoAccess)>> DepotKeys = [];
    private readonly Lock LazyLock = new();
    private readonly Lock CdnServerLock = new();
    private List<Server> CDNServers = [];
    private Task<bool>? CdnServersTask;
    private LogOnCredentials? Credentials;
    private Task<bool>? RelogonTask;
    private int Relogons;
    private int NextCdnServer;
    private uint CellID;
    private volatile bool IsLoggedOn;

    private TaskCompletionSource<bool> ConnectedTcs = new();
    private TaskCompletionSource<SteamUser.LoggedOnCallback?> LoggedOnTcs = new();

    public Client CDNClient { get; }

    // The get and ls commands keep depot keys between runs
    public bool CacheDepotKeys { get; init; }

    /// <summary>
    /// The exit code for a run that failed: a rate limit or rejected login when logging in again after a lost
    /// connection caused it, since that explains the failures after it.
    /// </summary>
    public int FailureExitCode { get; private set; } = ExitCodes.Failed;

    public SteamSession(CancellationToken cancellationToken)
    {
        CancellationToken = cancellationToken;
        Manager = new CallbackManager(SteamClient);
        User = SteamClient.GetHandler<SteamUser>()!;
        Apps = SteamClient.GetHandler<SteamApps>()!;
        Content = SteamClient.GetHandler<SteamContent>()!;

        CDNClient = new Client(SteamClient);
        Client.RequestTimeout = TimeSpan.FromSeconds(60);
        Client.ResponseBodyTimeout = TimeSpan.FromSeconds(120);

        Manager.Subscribe<SteamClient.ConnectedCallback>(_ => ConnectedTcs.TrySetResult(true));
        Manager.Subscribe<SteamClient.DisconnectedCallback>(cb =>
        {
            ConnectedTcs.TrySetResult(false);
            LoggedOnTcs.TrySetResult(null);

            if (!cb.UserInitiated && IsLoggedOn)
            {
                IsLoggedOn = false;
                Log.Warn("Lost the Steam connection.");
            }
        });
        Manager.Subscribe<SteamUser.LoggedOnCallback>(cb => LoggedOnTcs.TrySetResult(cb));

        PumpCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _ = Task.Run(async () =>
        {
            try
            {
                while (!PumpCts.IsCancellationRequested)
                {
                    await Manager.RunWaitCallbackAsync(PumpCts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        }, CancellationToken.None);
    }

    public void Dispose()
    {
        Disconnect();
        CDNClient.Dispose();
        PumpCts.Dispose();
    }

    public void Disconnect()
    {
        PumpCts.Cancel();
        IsLoggedOn = false;
        SteamClient.Disconnect();
    }

    /// <summary>
    /// A failure or timeout, as opposed to cancellation by the user. Timeouts are cancellations too.
    /// </summary>
    public static bool IsError(Exception e, CancellationToken cancellationToken) => e is not OperationCanceledException || !cancellationToken.IsCancellationRequested;

    /// <summary>
    /// The CDN refusing content, from an expired request code, missing access, or a server without it.
    /// </summary>
    public static bool IsRefusal(Exception e) => e is SteamKitWebRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound };

    private bool IsError(Exception e) => IsError(e, CancellationToken);

    public async Task<bool> ConnectAsync(int attempt = 0)
    {
        await DelayBeforeRetryAsync("connection", attempt);

        ConnectedTcs = new TaskCompletionSource<bool>();
        LoggedOnTcs = new TaskCompletionSource<SteamUser.LoggedOnCallback?>();

        Log.Info("Connecting to Steam...");
        SteamClient.Connect();

        // The callback pump stops on cancellation, so the callbacks would never come
        if (!await ConnectedTcs.Task.WaitAsync(CancellationToken))
        {
            Log.Warn("Failed to connect to Steam.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Connects and logs on, retrying failures that may be temporary. Returns an exit code, after explaining failures.
    /// </summary>
    public async Task<int> LogOnAsync(LogOnCredentials credentials)
    {
        Credentials = credentials;

        var result = await LogOnWithRetriesAsync(credentials);
        var exitCode = ExitCodes.FromLogOnResult(result);

        if (exitCode == ExitCodes.RateLimited)
        {
            Log.Error($"Steam is rate limiting logins ({result}), wait about 15 minutes. Put all targets in one run instead of one run per target.");
        }
        else if (exitCode == ExitCodes.NoAccess && credentials.RefreshToken != null)
        {
            Log.Error($"Steam rejected the saved login for {credentials.Username} ({result}). Run \"{Help.ExeName} login\" again with that account.");
        }
        else if (exitCode != ExitCodes.Ok)
        {
            Log.Error($"Could not log in to Steam ({result}). Check the credentials and network, or retry in a few minutes if Steam is down.");
        }

        return exitCode;
    }

    private async Task<EResult> LogOnWithRetriesAsync(LogOnCredentials credentials)
    {
        var result = EResult.Fail;

        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            if (!await ConnectAsync(attempt))
            {
                result = EResult.NoConnection;
                continue;
            }

            if (credentials.IsAnonymous)
            {
                Log.Info("Connected. Logging in anonymously...");
                User.LogOnAnonymous();
            }
            else
            {
                var accessToken = credentials.RefreshToken;

                if (accessToken == null)
                {
                    Log.Info("Connected. Authenticating...");

                    try
                    {
                        accessToken = await AuthenticateAsync(credentials.Username, credentials.Password!, isPersistentSession: false);
                    }
                    catch (Exception e) when (IsError(e))
                    {
                        Log.Warn($"Authentication failed: {e.Message}");
                        SteamClient.Disconnect();

                        // Steam Guard without a terminal to ask for the code fails the same way every time
                        result = e switch
                        {
                            AuthenticationException authException => authException.Result,
                            InvalidOperationException => EResult.AccountLoginDeniedNeedTwoFactor,
                            _ => EResult.Fail,
                        };

                        if (IsFinal(result))
                        {
                            break;
                        }

                        continue;
                    }
                }

                Log.Info("Logging in...");

                User.LogOn(new SteamUser.LogOnDetails
                {
                    Username = credentials.Username,
                    AccessToken = accessToken,
                    ShouldRememberPassword = credentials.RefreshToken != null,

                    // Sessions on one account and IP need distinct login ids, or they disconnect each other
                    LoginID = (uint)Random.Shared.Next(1, int.MaxValue),
                });
            }

            var logOnResult = await LoggedOnTcs.Task.WaitAsync(CancellationToken);
            result = logOnResult?.Result ?? EResult.NoConnection;

            if (result == EResult.OK)
            {
                IsLoggedOn = true;
                CellID = logOnResult!.CellID;
                Log.Info($"Logged in. Cell ID: {CellID}");

                return result;
            }

            Log.Warn($"Failed to log in: {result}");
            SteamClient.Disconnect();

            if (IsFinal(result))
            {
                break;
            }
        }

        return result;

        // Only connection problems are retried: every attempt is a logon, and retrying rejected ones trips the rate limit
        static bool IsFinal(EResult result) => result is not (EResult.Fail
            or EResult.NoConnection
            or EResult.Busy
            or EResult.Timeout
            or EResult.ServiceUnavailable
            or EResult.TryAnotherCM
            or EResult.RemoteDisconnect);
    }

    // After a lost connection, requests log on again, a couple of times at most since every logon counts against the rate limit
    private Task<bool> EnsureLoggedOnAsync()
    {
        if (IsLoggedOn)
        {
            return Task.FromResult(true);
        }

        lock (LazyLock)
        {
            if (RelogonTask is not { IsCompleted: false })
            {
                // After Disconnect the callback pump is stopped, so a logon could never complete
                if (Credentials == null || PumpCts.IsCancellationRequested || Relogons++ >= MaxRelogons)
                {
                    return Task.FromResult(false);
                }

                RelogonTask = RelogonAsync(Credentials);
            }

            return RelogonTask;
        }

        async Task<bool> RelogonAsync(LogOnCredentials credentials)
        {
            var exitCode = await LogOnAsync(credentials);

            if (exitCode is ExitCodes.RateLimited or ExitCodes.NoAccess)
            {
                FailureExitCode = exitCode;
            }

            return exitCode == ExitCodes.Ok;
        }
    }

    /// <summary>
    /// Authenticates with a password and returns a refresh token, prompting for Steam Guard when interactive.
    /// </summary>
    public async Task<string> AuthenticateAsync(string username, string password, bool isPersistentSession)
    {
        var authSession = await SteamClient.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
        {
            Username = username,
            Password = password,
            IsPersistentSession = isPersistentSession,
            DeviceFriendlyName = nameof(SteamFileDownloader),
            Authenticator = !Console.IsOutputRedirected ? new UserConsoleAuthenticator() : null,
        });

        var pollResult = await authSession.PollingWaitForResultAsync(CancellationToken);

        return pollResult.RefreshToken;
    }

    /// <summary>
    /// Authenticates by scanning a QR code in the Steam mobile app, so no username or password is needed.
    /// The challenge url changes over time, and each change is passed to showChallenge again.
    /// </summary>
    public async Task<AuthPollResult> AuthenticateWithQrAsync(Action<string> showChallenge)
    {
        var authSession = await SteamClient.Authentication.BeginAuthSessionViaQRAsync(new AuthSessionDetails
        {
            IsPersistentSession = true,
            DeviceFriendlyName = nameof(SteamFileDownloader),
        });

        authSession.ChallengeURLChanged = () => showChallenge(authSession.ChallengeURL);
        showChallenge(authSession.ChallengeURL);

        return await authSession.PollingWaitForResultAsync(CancellationToken);
    }

    /// <summary>
    /// Gets the CDN server list once, for the cell id of the login.
    /// </summary>
    public Task<bool> EnsureCdnServersAsync()
    {
        lock (LazyLock)
        {
            return CdnServersTask ??= FetchCdnServersAsync();
        }
    }

    private async Task<bool> FetchCdnServersAsync()
    {
        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            await DelayBeforeRetryAsync("CDN server fetch", attempt);

            try
            {
                var servers = await ContentServerDirectoryService.LoadAsync(SteamClient.Configuration, CellID, 100, CancellationToken);

                CDNServers = [.. servers.Where(static server =>
                    server.AllowedAppIds.Length == 0
                    && !server.UseAsProxy
                    && !server.SteamChinaOnly
                    && server.Type is "SteamCache" or "CDN")];

                break;
            }
            catch (Exception e) when (IsError(e))
            {
                Log.Warn($"Failed to get CDN servers: {e.Message}");
            }
        }

        if (CDNServers.Count == 0)
        {
            Log.Error("Could not get any Steam CDN servers. Check the network, or retry later.");
            return false;
        }

        Log.Info($"Got {CDNServers.Count} CDN servers.");

        return true;
    }

    public Server GetContentServer()
    {
        lock (CdnServerLock)
        {
            return CDNServers[NextCdnServer % CDNServers.Count];
        }
    }

    public void MarkContentServerAsBad(Server server)
    {
        lock (CdnServerLock)
        {
            // Concurrent failures on one server move on only once
            if (CDNServers[NextCdnServer % CDNServers.Count] == server)
            {
                NextCdnServer++;
                Log.Info($"Switching from CDN server {server} to {CDNServers[NextCdnServer % CDNServers.Count]}");
            }
        }
    }

    public async Task<KeyValue?> GetAppInfoAsync(uint appID) => (await GetAppInfosAsync([appID])).GetValueOrDefault(appID);

    /// <summary>
    /// Gets the info of several apps in one request. Apps that failed are missing, after an error for each.
    /// </summary>
    public async Task<Dictionary<uint, KeyValue>> GetAppInfosAsync(IReadOnlyCollection<uint> appIDs)
    {
        var appInfos = new Dictionary<uint, KeyValue>();

        if (appIDs.Count == 0 || !await EnsureLoggedOnAsync())
        {
            return appInfos;
        }

        var appList = string.Join(", ", appIDs);
        Log.Info($"Fetching app info for {appList}...");

        try
        {
            var tokenResult = await Apps.PICSGetAccessTokens(appIDs, []);
            var requests = appIDs.Select(appID => new SteamApps.PICSRequest(appID, tokenResult.AppTokens.GetValueOrDefault(appID)));
            var productInfo = await Apps.PICSGetProductInfo(requests, []);

            foreach (var result in productInfo.Results ?? [])
            {
                foreach (var (appID, appInfo) in result.Apps)
                {
                    appInfos[appID] = appInfo.KeyValues;
                }
            }
        }
        catch (Exception e) when (IsError(e))
        {
            Log.Error($"Failed to get app info for {appList} ({e.Message}). Retry later.");
            return appInfos;
        }

        foreach (var appID in appIDs.Where(appID => !appInfos.ContainsKey(appID)))
        {
            Log.Error($"App {appID} does not exist, or this account can't see it. Check the app id, it's the number in the app's store or SteamDB url.");
        }

        return appInfos;
    }

    /// <summary>
    /// The depots in an app's "depots" section, skipping its other keys like "branches".
    /// </summary>
    public static IEnumerable<(uint ID, KeyValue Depot)> GetDepots(KeyValue depots)
    {
        foreach (var depot in depots.Children)
        {
            if (uint.TryParse(depot.Name, out var depotID))
            {
                yield return (depotID, depot);
            }
        }
    }

    /// <summary>
    /// Whether the app has the branch and its manifests are visible, since otherwise every depot would silently
    /// fall back to public. Logs an error when not.
    /// </summary>
    public static bool CheckBranch(KeyValue depots, uint appID, string branch)
    {
        if (string.Equals(branch, "public", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var branches = depots["branches"];
        var branchInfo = branches[branch];

        if (branchInfo == KeyValue.Invalid)
        {
            Log.Error($"App {appID} has no branch \"{branch}\". Its branches are: {string.Join(", ", branches.Children.Select(static b => b.Name))}.");
            return false;
        }

        // Password protected branches only list their manifests to accounts with access
        if (branchInfo["pwdrequired"].AsBoolean() && !GetDepots(depots).Any(d => d.Depot["manifests"][branch] != KeyValue.Invalid))
        {
            Log.Error($"Branch \"{branch}\" of app {appID} is password protected, which is not supported.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Gets the manifest id of a depot on a branch, falling back to public. Returns 0 when there is none.
    /// </summary>
    public static ulong GetManifestIdForBranch(KeyValue depot, ref string branch)
    {
        var manifests = depot["manifests"];
        var manifestID = ParseManifestId(manifests[branch]);

        if (manifestID == 0 && !string.Equals(branch, "public", StringComparison.OrdinalIgnoreCase))
        {
            manifestID = ParseManifestId(manifests["public"]);

            if (manifestID != 0)
            {
                Log.Info($"Depot {depot.Name}: branch \"{branch}\" has no manifest, falling back to \"public\"");
                branch = "public";
            }
        }

        return manifestID;

        // Either the id itself, or a "gid" child
        static ulong ParseManifestId(KeyValue manifest) => ulong.TryParse(manifest.Value ?? manifest["gid"].Value, out var id) ? id : 0;
    }

    /// <summary>
    /// Gets the depot keys and manifests of jobs, storing both on them. Failed jobs are left without a manifest, after a report.
    /// </summary>
    public Task DownloadManifestsAsync(IEnumerable<ManifestJob> jobs) =>
        Parallel.ForEachAsync(jobs, new ParallelOptions { MaxDegreeOfParallelism = ManifestConcurrency, CancellationToken = CancellationToken }, async (job, _) => await DownloadManifestAsync(job));

    private async Task DownloadManifestAsync(ManifestJob job)
    {
        try
        {
            await DownloadManifestFromSteamAsync(job);
        }
        catch (Exception e) when (IsError(e))
        {
            // Mostly a lost Steam connection, which fails the pending requests
            Report(job, $"Failed to get manifest {job.ManifestID} for depot {job.DepotID}: {e.Message}");
        }
    }

    private async Task DownloadManifestFromSteamAsync(ManifestJob job)
    {
        (job.DepotKey, job.NoAccess) = await GetDepotKeyAsync(job);

        if (job.DepotKey == null || !await EnsureCdnServersAsync())
        {
            return;
        }

        var manifestRequestCode = await GetManifestRequestCodeAsync(job);

        if (manifestRequestCode == 0)
        {
            return;
        }

        var refused = false;

        for (var attempt = 0; attempt <= MaxCdnRetries; attempt++)
        {
            var server = GetContentServer();

            try
            {
                job.Manifest = await CDNClient.DownloadManifestAsync(job.DepotID, job.ManifestID, manifestRequestCode, server, job.DepotKey);
            }
            catch (Exception e) when (IsError(e))
            {
                Report(job, $"Failed to download manifest for depot {job.DepotID} ({server}: {e.Message}) (#{attempt})");
                MarkContentServerAsBad(server);

                if (!IsRefusal(e))
                {
                    if (attempt < MaxCdnRetries)
                    {
                        await Task.Delay(ExponentialBackoff(attempt + 1), CancellationToken);
                    }

                    continue;
                }

                // An expired request code or a bad server can cause this, so retry once on another server with a new code
                if (refused)
                {
                    Report(job, $"The CDN refused depot {job.DepotID} manifest {job.ManifestID} ({e.Message}), some old manifests are no longer served");
                    return;
                }

                refused = true;
                manifestRequestCode = await GetManifestRequestCodeAsync(job);

                if (manifestRequestCode == 0)
                {
                    return;
                }

                continue;
            }

            Log.Info($"Downloaded manifest {job.ManifestID} for depot {job.DepotID} ({job.Manifest.CreationTime:yyyy-MM-dd HH:mm:ss}, {job.Manifest.Files?.Count} files)");

            return;
        }

        Report(job, $"Failed to download manifest for depot {job.DepotID} after all retries.");
    }

    // Request codes expire after a few minutes. Returns 0 after reporting why there is none.
    private async Task<ulong> GetManifestRequestCodeAsync(ManifestJob job)
    {
        ulong code;

        try
        {
            code = await RequestFromSteamAsync("manifest request code", () => Content.GetManifestRequestCode(job.DepotID, job.AppID, job.ManifestID, job.Branch));
        }
        catch (Exception e) when (IsError(e))
        {
            Report(job, $"Manifest request code request failed for depot {job.DepotID}: {DescribeError(e)}");
            return 0;
        }

        if (code == 0)
        {
            job.NoAccess = true;
            Report(job, $"Steam gave no manifest request code for depot {job.DepotID} manifest {job.ManifestID}: the manifest may be from another branch (pass --branch), another app, or not accessible to this account", noAccess: true);
        }

        return code;
    }

    /// <summary>
    /// Logs on if needed and sends a request, retrying once since a lost connection fails the requests in flight.
    /// </summary>
    private async Task<T> RequestFromSteamAsync<T>(string what, Func<Task<T>> request)
    {
        for (var attempt = 0; ; attempt++)
        {
            await DelayBeforeRetryAsync(what, attempt, maxAttempts: 2);

            if (!await EnsureLoggedOnAsync())
            {
                throw new InvalidOperationException("there is no Steam login");
            }

            try
            {
                return await request();
            }
            catch (Exception e) when (IsError(e) && attempt == 0)
            {
                // Retried after the delay
            }
        }
    }

    private static string DescribeError(Exception e) => e is TaskCanceledException ? "timed out" : e.Message;

    // Concurrent jobs of one depot share a single key request
    private Task<(byte[]? Key, bool NoAccess)> GetDepotKeyAsync(ManifestJob job)
    {
        lock (DepotKeys)
        {
            var key = (job.DepotID, job.AppID);

            if (!DepotKeys.TryGetValue(key, out var keyTask))
            {
                keyTask = RequestDepotKeyAsync(job);
                DepotKeys[key] = keyTask;
            }

            return keyTask;
        }
    }

    private async Task<(byte[]? Key, bool NoAccess)> RequestDepotKeyAsync(ManifestJob job)
    {
        var depotKey = CacheDepotKeys ? DepotCache.LoadDepotKey(job.DepotID) : null;

        if (depotKey != null)
        {
            return (depotKey, false);
        }

        SteamApps.DepotKeyCallback keyResult;

        try
        {
            keyResult = await RequestFromSteamAsync("depot key request", async () =>
            {
                var keyTask = Apps.GetDepotDecryptionKey(job.DepotID, job.AppID);
                keyTask.Timeout = TimeSpan.FromSeconds(30);
                return await keyTask;
            });
        }
        catch (Exception e) when (IsError(e))
        {
            Report(job, $"Depot key request failed for {job.DepotID}: {DescribeError(e)}");
            return (null, false);
        }

        if (keyResult.Result != EResult.OK)
        {
            Report(job, $"No access to depot {job.DepotID} in app {job.AppID} ({keyResult.Result}): the account may not own it, or the depot belongs to another app", noAccess: true);
            return (null, true);
        }

        if (CacheDepotKeys)
        {
            DepotCache.SaveDepotKey(job.DepotID, keyResult.DepotKey);
        }

        return (keyResult.DepotKey, false);
    }

    // Optional depots, like ones only fetched for their manifest, can lack access without it being a problem
    private static void Report(ManifestJob job, string message, bool noAccess = false)
    {
        if (job.Optional && noAccess)
        {
            Log.Info(message);
        }
        else
        {
            Log.Warn(message);
        }
    }

    private async Task DelayBeforeRetryAsync(string what, int attempt, int maxAttempts = MaxRetries + 1)
    {
        if (attempt == 0)
        {
            return;
        }

        var delay = ExponentialBackoff(attempt);
        Log.Info($"Retrying {what} in {delay / 1000.0:F1}s (attempt {attempt + 1}/{maxAttempts})...");
        await Task.Delay(delay, CancellationToken);
    }

    internal static int ExponentialBackoff(int i)
    {
        return ((1 << i) * 1000) + Random.Shared.Next(1001);
    }
}
