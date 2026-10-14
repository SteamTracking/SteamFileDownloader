using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.CDN;
using SteamKit2.Internal;

namespace SteamFileDownloader;

/// <summary>
/// How to log on: anonymously, with a password (authenticated on every run), or with a saved refresh token.
/// </summary>
internal sealed record LogOnCredentials(string Username, string? Password = null, string? RefreshToken = null)
{
    public static LogOnCredentials Anonymous { get; } = new("anonymous");

    public bool IsAnonymous => IsAnonymousName(Username);

    public static bool IsAnonymousName(string? username) => string.Equals(username, "anonymous", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// One Steam connection, used to get depot keys and manifests, and the CDN client that manifests download from. As an
/// <see cref="IChunkSource"/> it also downloads the chunks of files, which doesn't need the Steam connection.
/// </summary>
internal sealed class SteamSession : IDisposable, IChunkSource, IWorkshopSource
{
    private const int MaxRetries = 7;
    private const int MaxCdnRetries = 5;
    private const int MaxRelogons = 2;

    // Each manifest mostly waits on a Steam request code and a CDN download
    private const int ManifestConcurrency = 16;

    private readonly SteamClient SteamClient = new(SteamConfiguration.Create(static b => b.WithHttpClientFactory(CreateHttpClient)));
    private readonly CallbackManager Manager;
    private readonly SteamUser User;
    private readonly SteamApps Apps;
    private readonly SteamContent Content;
    private readonly PublishedFile PublishedFiles;
    private readonly CancellationTokenSource PumpCts;
    private readonly CancellationToken CancellationToken;
    private readonly Dictionary<(uint DepotID, uint AppID), Task<(byte[]? Key, bool NoAccess, string? Problem)>> DepotKeys = [];
    private readonly Lock LazyLock = new();
    private readonly Client CdnClient;
    private Task<bool>? CdnServersTask;
    private LogOnCredentials? Credentials;
    private Task<bool>? RelogonTask;
    private int Relogons;
    private uint CellID;
    private volatile bool IsLoggedOn;

    private TaskCompletionSource<bool> ConnectedTcs = new();
    private TaskCompletionSource<SteamUser.LoggedOnCallback?> LoggedOnTcs = new();

    public CdnServerPool CdnServers { get; } = new(TimeProvider.System);

    // The get, ls and depots commands keep depot keys between runs
    public bool CacheDepotKeys { get; init; }

    // The get and ls commands take any number of targets, so a run per target only adds logins
    public bool TakesTargets { get; init; }

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

        CdnClient = new Client(SteamClient);
        Content = SteamClient.GetHandler<SteamContent>()!;
        PublishedFiles = SteamClient.GetHandler<SteamUnifiedMessages>()!.CreateService<PublishedFile>();

        // A server that doesn't answer is given up on soon, since the retry goes to another one. A body gets much
        // longer, since many chunks share the connection and a slow one can take minutes for each, as can big manifests
        Client.RequestTimeout = TimeSpan.FromSeconds(10);
        Client.ResponseBodyTimeout = TimeSpan.FromMinutes(5);

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
        CdnClient.Dispose();
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

    /// <summary>
    /// CDN requests ask for HTTP/2, so each server gets one connection for all of them instead of one per request.
    /// Servers without HTTP/2, or without HTTPS, get HTTP/1.1. The CM connection is a WebSocket, which stays on HTTP/1.1.
    /// </summary>
    private static HttpClient CreateHttpClient(HttpClientPurpose purpose)
    {
#pragma warning disable CA2000 // The client disposes its handlers
        var client = purpose == HttpClientPurpose.CDN ? new HttpClient(new Http2Handler { InnerHandler = new SocketsHttpHandler() }) : new HttpClient();
#pragma warning restore CA2000
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(nameof(SteamFileDownloader), Program.Version));

        return client;
    }

    // SteamKit creates its requests as HTTP/1.1, which a client's default version doesn't change
    private sealed class Http2Handler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Version = HttpVersion.Version20;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;

            return base.SendAsync(request, cancellationToken);
        }
    }

    private bool IsError(Exception e) => IsError(e, CancellationToken);

    public async Task<bool> ConnectAsync(int attempt = 0)
    {
        await DelayBeforeRetryAsync("connection", attempt);

        ConnectedTcs = new TaskCompletionSource<bool>();
        LoggedOnTcs = new TaskCompletionSource<SteamUser.LoggedOnCallback?>();

        SteamClient.Connect();

        // The callback pump stops on cancellation, so the callbacks would never come
        if (!await ConnectedTcs.Task.WaitAsync(CancellationToken))
        {
            Log.Info("Failed to connect to Steam.");
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

        return ExplainLogOnResult(await LogOnWithRetriesAsync(credentials), credentials);
    }

    /// <summary>
    /// Connects and logs on with retries like <see cref="LogOnAsync"/>, but leaves explaining a failure to the caller.
    /// </summary>
    public Task<EResult> TryLogOnAsync(LogOnCredentials credentials)
    {
        Credentials = credentials;

        return LogOnWithRetriesAsync(credentials);
    }

    /// <summary>
    /// The exit code of a logon result, after explaining a failure.
    /// </summary>
    public int ExplainLogOnResult(EResult result, LogOnCredentials credentials)
    {
        var exitCode = ExitCodes.FromLogOnResult(result);

        if (exitCode == ExitCodes.RateLimited)
        {
            Log.Error($"Steam is rate limiting logins ({result}). Wait about 15 minutes{(TakesTargets ? ", and put all targets in one run instead of one run per target" : "")}.");
        }
        else if (exitCode == ExitCodes.NoAccess && credentials.RefreshToken != null)
        {
            Log.Error($"Steam rejected the saved login for {credentials.Username} ({result}). Run \"{Help.ExeName} login\" again with that account; it needs a person to scan a QR code.");
        }
        else if (exitCode != ExitCodes.Ok)
        {
            Log.Error($"Failed to log in to Steam ({result}). Check the credentials and network, or retry in a few minutes if Steam is down.");
        }

        return exitCode;
    }

    /// <summary>
    /// When logged on with a saved login in its last 30 days, asks Steam for a new token, so it keeps working without
    /// a new QR code. A failure only warns, since the old token still works until it expires.
    /// </summary>
    public async Task RenewSavedLoginAsync()
    {
        if (Credentials is not { RefreshToken: { } refreshToken } credentials
            || AccountStore.GetExpiry(refreshToken) is not { } expiry
            || expiry > DateTimeOffset.UtcNow.AddDays(30)
            || SteamClient.SteamID is not { } steamID)
        {
            return;
        }

        try
        {
            var renewed = await SteamClient.Authentication.GenerateAccessTokenForAppAsync(steamID, refreshToken, allowRenewal: true).WaitAsync(CancellationToken);

            // Steam decides whether to renew
            if (string.IsNullOrEmpty(renewed.RefreshToken))
            {
                return;
            }

            AccountStore.Renew(credentials.Username, renewed.RefreshToken);
            Credentials = credentials with { RefreshToken = renewed.RefreshToken };

            var newExpiry = AccountStore.GetExpiry(renewed.RefreshToken);
            Log.Info($"Renewed the saved login for {credentials.Username}{(newExpiry is { } date ? $", it now expires {AccountStore.FormatDate(date)}" : "")}.");
        }
        catch (Exception e) when (IsError(e) || e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Renewing the saved login for {credentials.Username} failed ({DescribeError(e)}); it still works until {AccountStore.FormatDate(expiry)}.");
        }
    }

    /// <summary>
    /// Creates a session and logs on. Returns no session with the exit code after explaining why; missing credentials
    /// were explained when they were looked up.
    /// </summary>
    public static async Task<(SteamSession? Session, int ExitCode)> CreateLoggedOnAsync(LogOnCredentials? credentials, bool cacheDepotKeys, bool takesTargets, CancellationToken cancellationToken)
    {
        if (credentials == null)
        {
            return (null, ExitCodes.NoAccess);
        }

        var session = new SteamSession(cancellationToken) { CacheDepotKeys = cacheDepotKeys, TakesTargets = takesTargets };
        var exitCode = await session.LogOnAsync(credentials);

        if (exitCode != ExitCodes.Ok)
        {
            session.Dispose();
            return (null, exitCode);
        }

        // Not in LogOnAsync, so relogons during the run don't try again
        await session.RenewSavedLoginAsync();

        return (session, ExitCodes.Ok);
    }

    /// <summary>
    /// Logs on with --anonymous, or a saved login picked by --username, which may be anonymous.
    /// </summary>
    public static Task<(SteamSession? Session, int ExitCode)> CreateLoggedOnAsync(DepotOptions options, bool takesTargets, CancellationToken cancellationToken)
    {
        var credentials = options.Anonymous ? LogOnCredentials.Anonymous : AccountStore.LoadCredentials(options.Username);

        return CreateLoggedOnAsync(credentials, cacheDepotKeys: true, takesTargets, cancellationToken);
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
                User.LogOnAnonymous();
            }
            else
            {
                var accessToken = credentials.RefreshToken;

                if (accessToken == null)
                {
                    try
                    {
                        accessToken = await AuthenticateAsync(credentials.Username, credentials.Password!, isPersistentSession: false);
                    }
                    catch (Exception e) when (IsError(e))
                    {
                        Log.Info($"Authentication failed: {DescribeError(e)}");
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
                Log.Info($"Logged in as {credentials.Username}.");

                return result;
            }

            Log.Info($"Failed to log in: {result}.");
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

                CdnServers.SetServers([.. servers.Where(static server =>
                    server.AllowedAppIds.Length == 0
                    && !server.UseAsProxy
                    && !server.SteamChinaOnly
                    && server.Type is "SteamCache" or "CDN")]);

                if (CdnServers.Count > 0)
                {
                    break;
                }

                Log.Info("Got no usable CDN servers.");
            }
            catch (Exception e) when (IsError(e))
            {
                Log.Info($"Failed to get CDN servers: {DescribeError(e)}");
            }
        }

        if (CdnServers.Count == 0)
        {
            Log.Error("Failed to get any Steam CDN servers. Check the network, or retry later.");
            return false;
        }

        return true;
    }

    public Task<int> DownloadChunkAsync(ManifestJob job, DepotManifest.ChunkData chunk, Server server, byte[] destination) =>
        CdnClient.DownloadDepotChunkAsync(job.DepotID, chunk, server, destination, job.DepotKey);

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
            Log.Error($"Failed to get app info for {string.Join(", ", appIDs)} ({DescribeError(e)}). Retry later.");
            return appInfos;
        }

        foreach (var appID in appIDs.Where(appID => !appInfos.ContainsKey(appID)))
        {
            Log.Error($"App {appID} does not exist, or this account can't see it. Check the app id, which is the number in the app's store page url.");
        }

        return appInfos;
    }

    public async Task<List<PublishedFileDetails>> GetPublishedFileDetailsAsync(IReadOnlyCollection<ulong> itemIDs)
    {
        var request = new CPublishedFile_GetDetails_Request
        {
            includechildren = true,
        };
        request.publishedfileids.AddRange(itemIDs);

        return (await CallServiceAsync("workshop item details", () => PublishedFiles.GetDetails(request))).publishedfiledetails;
    }

    public Task<CPublishedFile_GetChangeHistory_Response> GetChangeHistoryAsync(ulong itemID, uint startIndex, uint count) =>
        CallServiceAsync("workshop change history", () => PublishedFiles.GetChangeHistory(new CPublishedFile_GetChangeHistory_Request
        {
            publishedfileid = itemID,
            startindex = startIndex,
            count = count,
        }));

    public Task<CPublishedFile_QueryFiles_Response> QueryFilesAsync(CPublishedFile_QueryFiles_Request request) =>
        CallServiceAsync("workshop search", () => PublishedFiles.QueryFiles(request));

    // Throws when Steam answers with an error
    private async Task<T> CallServiceAsync<T>(string what, Func<AsyncJob<SteamUnifiedMessages.ServiceMethodResponse<T>>> call)
        where T : ProtoBuf.IExtensible, new()
    {
        var response = await RequestFromSteamAsync(what, async () => await call());

        if (response.Result != EResult.OK)
        {
            throw new InvalidOperationException($"Steam answered {response.Result}");
        }

        return response.Body;
    }

    /// <summary>
    /// Gets the depot keys and manifests of jobs, storing both on them. Failed jobs are left without a manifest, after a report.
    /// </summary>
    public Task DownloadManifestsAsync(IReadOnlyCollection<ManifestJob> jobs)
    {
        // Old manifests can take a while, and each prints a line only once it's done
        if (jobs.Count > 0)
        {
            Log.Info($"Getting {jobs.Count} manifests...");
        }

        return Parallel.ForEachAsync(jobs, new ParallelOptions { MaxDegreeOfParallelism = ManifestConcurrency, CancellationToken = CancellationToken }, async (job, _) => await DownloadManifestAsync(job));
    }

    private async Task DownloadManifestAsync(ManifestJob job)
    {
        try
        {
            await DownloadManifestFromSteamAsync(job);
        }
        catch (Exception e) when (IsError(e))
        {
            // Mostly a lost Steam connection, which fails the pending requests
            Report(job, $"[{job}] Failed to get the manifest: {DescribeError(e)}");
        }
    }

    private async Task DownloadManifestFromSteamAsync(ManifestJob job)
    {
        (job.DepotKey, job.NoAccess, var keyProblem) = await GetDepotKeyAsync(job);

        // Jobs that share the key request each report it, at their own level
        if (keyProblem != null)
        {
            Report(job, $"[{job}] {keyProblem}", job.NoAccess);
        }

        if (job.DepotKey == null)
        {
            return;
        }

        // A saved manifest needs no request code, which Steam may refuse for old manifests. Reading and parsing it
        // blocks, so it is off the loop's threads
        if (await Task.Run(() => ManifestCache.Load(job)) is { } saved)
        {
            job.Manifest = saved;
            Log.Info($"[{job}] Read the saved manifest: {saved.CreationTime:yyyy-MM-dd HH:mm:ss}, {saved.Files?.Count} files.");
            return;
        }

        if (!await EnsureCdnServersAsync())
        {
            return;
        }

        var manifestRequestCode = await GetManifestRequestCodeAsync(job);

        if (manifestRequestCode == 0)
        {
            return;
        }

        Server? lastServer = null;
        Server? refusedBy = null;
        Exception? lastError = null;

        for (var attempt = 0; attempt <= MaxCdnRetries; attempt++)
        {
            // Each attempt goes to another server than the last
            var server = CdnServers.Get(lastServer);
            lastServer = server;

            try
            {
                // Without the key the file names stay encrypted, so the saved manifest keeps them as served
                job.Manifest = await CdnClient.DownloadManifestAsync(job.DepotID, job.ManifestID, manifestRequestCode, server);
            }
            catch (Exception e) when (IsError(e))
            {
                Log.Info($"[{job}] Failed to download the manifest from {server}: {DescribeError(e)} (attempt {attempt + 1}/{MaxCdnRetries + 1}).");
                CdnServers.MarkBad(server);
                lastError = e;

                if (!IsRefusal(e))
                {
                    if (attempt < MaxCdnRetries)
                    {
                        await Task.Delay(ExponentialBackoff(attempt + 1), CancellationToken);
                    }

                    continue;
                }

                // Refused by a second server too, so the CDN doesn't serve it, rather than one server
                if (refusedBy != null && refusedBy != server)
                {
                    Report(job, $"[{job}] The CDN refused the manifest ({DescribeError(e)}); some old manifests are no longer served.");
                    return;
                }

                // An expired request code or one bad server can cause this, so another server gets a new code
                if (refusedBy == null)
                {
                    refusedBy = server;
                    manifestRequestCode = await GetManifestRequestCodeAsync(job);

                    if (manifestRequestCode == 0)
                    {
                        return;
                    }
                }

                continue;
            }

            CdnServers.MarkGood(server);

            // Writing it blocks, so it is off the loop's threads
            await Task.Run(() => ManifestCache.Save(job));

            if (job.Manifest.FilenamesEncrypted && !job.Manifest.DecryptFilenames(job.DepotKey))
            {
                job.Manifest = null;
                Report(job, $"[{job}] Failed to decrypt the file names of the manifest with the depot key.");
                return;
            }

            Log.Info($"[{job}] Downloaded the manifest: {job.Manifest.CreationTime:yyyy-MM-dd HH:mm:ss}, {job.Manifest.Files?.Count} files.");

            return;
        }

        Report(job, $"[{job}] Failed to download the manifest after all retries: {(lastError != null ? DescribeError(lastError) : "no server answered")}.");
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
            Report(job, $"[{job}] Failed to get a manifest request code: {DescribeError(e)}");
            return 0;
        }

        if (code == 0)
        {
            job.NoAccess = true;
            Report(job, job.ItemID != null
                ? $"[{job}] Steam gave no manifest request code: this account may have no access to the item, or the manifest isn't one of the item's."
                : $"[{job}] Steam gave no manifest request code: the manifest may be on another branch (set it with --branch, or @<branch> in a target) or in another app, or this account has no access to it.", noAccess: true);
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
                throw new InvalidOperationException("the Steam connection was lost, and logging in again failed");
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

    public static string DescribeError(Exception e) => e is TaskCanceledException ? "timed out" : e.Message;

    // Concurrent jobs of one depot share a single key request. Returns the key, or why there is none
    private Task<(byte[]? Key, bool NoAccess, string? Problem)> GetDepotKeyAsync(ManifestJob job)
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

    private async Task<(byte[]? Key, bool NoAccess, string? Problem)> RequestDepotKeyAsync(ManifestJob job)
    {
        var depotKey = CacheDepotKeys ? DepotCache.LoadDepotKey(job.DepotID) : null;

        if (depotKey != null)
        {
            return (depotKey, false, null);
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
            return (null, false, $"Failed to get the depot key: {DescribeError(e)}");
        }

        // Steam denies a depot the account doesn't own, and doesn't find one that isn't in the app. Other results,
        // like a busy Steam, are failures that running again can fix
        if (keyResult.Result is EResult.AccessDenied or EResult.FileNotFound)
        {
            return (null, true, job.ItemID != null
                ? $"Steam gave no key for the workshop depot {job.DepotID} of app {job.AppID} ({keyResult.Result}): this account may need to own the app."
                : $"Steam gave no depot key for depot {job.DepotID} in app {job.AppID} ({keyResult.Result}): this account may not own it, or it belongs to another app.");
        }

        if (keyResult.Result != EResult.OK)
        {
            return (null, false, $"Failed to get the depot key: Steam answered {keyResult.Result}.");
        }

        if (CacheDepotKeys)
        {
            DepotCache.SaveDepotKey(job.DepotID, keyResult.DepotKey);
        }

        return (keyResult.DepotKey, false, null);
    }

    // Lacking access to an optional depot, like one of an "all" target, is expected, and any failure of a job whose
    // manifest isn't needed is only a warning. Everything else fails the run, so it's an error
    private static void Report(ManifestJob job, string message, bool noAccess = false)
    {
        if (job.Optional && noAccess)
        {
            Log.Info(message);
        }
        else if (job.FailureIsFine)
        {
            Log.Warn(message);
        }
        else
        {
            Log.Error(message);
        }
    }

    private async Task DelayBeforeRetryAsync(string what, int attempt, int maxAttempts = MaxRetries + 1)
    {
        if (attempt == 0)
        {
            return;
        }

        var delay = ExponentialBackoff(attempt);
        Log.Info($"Retrying the {what} in {delay / 1000.0:F1}s (attempt {attempt + 1}/{maxAttempts})...");
        await Task.Delay(delay, CancellationToken);
    }

    internal static int ExponentialBackoff(int i)
    {
        return ((1 << i) * 1000) + Random.Shared.Next(1001);
    }
}
