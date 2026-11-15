using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;
using SteamKit2.CDN;

namespace SteamFileDownloader;

/// <summary>
/// Steam's answer to a depot key request. The key is only meaningful when the result is OK.
/// </summary>
internal readonly record struct DepotKeyAnswer(EResult Result, byte[] Key);

/// <summary>
/// The Steam and CDN requests that getting a manifest takes. Each throws when the request fails.
/// </summary>
internal interface IManifestSource : ICdnServers
{
    Task<DepotKeyAnswer> RequestDepotKeyAsync(ManifestJob job);

    /// <summary>
    /// Returns 0 when Steam gave no request code.
    /// </summary>
    Task<ulong> GetManifestRequestCodeAsync(ManifestJob job);

    Task<DepotManifest> DownloadManifestAsync(ManifestJob job, ulong requestCode, Server server);
}

/// <summary>
/// Gets the depot keys and manifests of jobs, storing both on them. Failed jobs are left without a manifest, after a report.
/// </summary>
internal sealed class ManifestFetcher(IManifestSource source, DepotCache depotCache, CancellationToken cancellationToken)
{
    private enum ManifestSource
    {
        None,
        Saved,
        Downloaded,
    }

    // Where a job's manifest came from, if it got one, and the problem it shares with the other jobs of its depot, which
    // is reported once for all of them
    private readonly record struct ManifestResult(ManifestSource Source, string? SharedProblem = null, bool NoAccess = false);

    private readonly record struct DepotKeyResult(byte[]? Key, bool NoAccess, string? Problem);

    private const int MaxCdnRetries = 5;

    // Each manifest mostly waits on a Steam request code and a CDN download
    private const int ManifestConcurrency = 16;

    // Up to this many manifests get a line each, more get one line once all are done
    private const int MaxManifestLines = 3;

    private readonly Dictionary<(uint DepotID, uint KeyAppID), Task<DepotKeyResult>> DepotKeys = [];

    public async Task GetManifestsAsync(IReadOnlyList<ManifestJob> jobs)
    {
        if (jobs.Count == 0)
        {
            return;
        }

        // Old manifests can take a while, so each gets a line once all are done, or with many, there is one line for all
        Log.Info($"Getting {Log.Plural(jobs.Count, "manifest")}...");

        var results = new ManifestResult[jobs.Count];

        var options = new ParallelOptions { MaxDegreeOfParallelism = ManifestConcurrency, CancellationToken = cancellationToken };

        await Parallel.ForAsync(0, jobs.Count, options, async (i, _) => results[i] = await GetManifestAsync(jobs[i]));

        if (jobs.Count <= MaxManifestLines)
        {
            for (var i = 0; i < jobs.Count; i++)
            {
                if (results[i].Source != ManifestSource.None)
                {
                    var manifest = jobs[i].Manifest!;
                    var action = results[i].Source == ManifestSource.Saved ? "Read the saved manifest" : "Downloaded the manifest";
                    Log.Info($"[{jobs[i]}] {action}: {manifest.CreationTime:yyyy-MM-dd HH:mm:ss}, {manifest.Files?.Count} files.");
                }
            }
        }

        var problems = new List<(ManifestJob Job, string Problem, bool NoAccess)>();

        for (var i = 0; i < jobs.Count; i++)
        {
            var result = results[i];

            if (result.SharedProblem != null)
            {
                problems.Add((jobs[i], result.SharedProblem, result.NoAccess));
            }
        }

        ReportSharedProblems(problems);

        if (jobs.Count > MaxManifestLines)
        {
            var saved = results.Count(static r => r.Source == ManifestSource.Saved);
            var downloaded = results.Count(static r => r.Source == ManifestSource.Downloaded);
            var sources = new List<string>();

            if (saved > 0)
            {
                sources.Add($"{saved} saved");
            }

            if (downloaded > 0)
            {
                sources.Add($"{downloaded} downloaded");
            }

            var got = Log.PluralOf(saved + downloaded, jobs.Count, "manifest");
            var sourcesNote = sources.Count > 0 ? $" ({string.Join(", ", sources)})" : "";
            Log.Info($"Got {got}{sourcesNote}.");
        }
    }

    private async Task<ManifestResult> GetManifestAsync(ManifestJob job)
    {
        try
        {
            return await GetManifestFromSourceAsync(job);
        }
        catch (Exception e) when (IsError(e))
        {
            // Mostly a lost Steam connection, which fails the pending requests
            Report(job, $"[{job}] Failed to get the manifest: {SteamSession.DescribeError(e)}");
            return default;
        }
    }

    private bool IsError(Exception e) => SteamSession.IsError(e, cancellationToken);

    private async Task<ManifestResult> GetManifestFromSourceAsync(ManifestJob job)
    {
        var keyResult = await GetDepotKeyAsync(job);
        job.DepotKey = keyResult.Key;
        job.NoAccess = keyResult.NoAccess;

        // Jobs that share the key request share the problem too
        if (job.DepotKey == null)
        {
            return new ManifestResult(ManifestSource.None, keyResult.Problem, job.NoAccess);
        }

        // A saved manifest needs no request code, which Steam may refuse for old manifests. Reading and parsing it
        // blocks, so it is off the loop's threads
        var saved = await Task.Run(() => ManifestCache.Load(job));

        if (saved != null)
        {
            job.Manifest = saved;
            return new ManifestResult(ManifestSource.Saved);
        }

        if (!await source.EnsureCdnServersAsync())
        {
            return default;
        }

        var (manifestRequestCode, codeProblem) = await GetManifestRequestCodeAsync(job);

        if (manifestRequestCode == 0)
        {
            return new ManifestResult(ManifestSource.None, codeProblem, NoAccess: true);
        }

        Server? lastServer = null;
        Server? refusedBy = null;
        Exception? lastError = null;

        for (var attempt = 0; attempt <= MaxCdnRetries; attempt++)
        {
            // Each attempt goes to another server than the last
            var lease = source.CdnServers.Rent(lastServer);
            var server = lease.Server;
            lastServer = server;

            try
            {
                // Busy only while downloading, not while waiting to retry
                using (lease)
                {
                    // Without the key the file names stay encrypted, so the saved manifest keeps them as served
                    job.Manifest = await source.DownloadManifestAsync(job, manifestRequestCode, server);
                }
            }
            catch (Exception e) when (IsError(e))
            {
                // Only the last error is reported, once retries run out
                source.CdnServers.MarkBad(server);
                lastError = e;

                if (!SteamSession.IsRefusal(e))
                {
                    if (attempt < MaxCdnRetries)
                    {
                        await Task.Delay(SteamSession.ExponentialBackoff(attempt + 1), cancellationToken);
                    }

                    continue;
                }

                // Refused by a second server too, so the CDN doesn't serve it, rather than one server
                if (refusedBy != null && refusedBy != server)
                {
                    Report(job, $"[{job}] The CDN refused the manifest ({SteamSession.DescribeError(e)}); some old manifests are no longer served.");
                    return default;
                }

                // An expired request code or one bad server can cause this, so another server gets a new code
                if (refusedBy == null)
                {
                    refusedBy = server;
                    (manifestRequestCode, codeProblem) = await GetManifestRequestCodeAsync(job);

                    if (manifestRequestCode == 0)
                    {
                        return new ManifestResult(ManifestSource.None, codeProblem, NoAccess: true);
                    }
                }

                continue;
            }

            source.CdnServers.MarkGood(server);

            // Writing it blocks, so it is off the loop's threads
            await Task.Run(() => ManifestCache.Save(job));

            if (job.Manifest.FilenamesEncrypted && !job.Manifest.DecryptFilenames(job.DepotKey))
            {
                job.Manifest = null;
                Report(job, $"[{job}] Failed to decrypt the file names of the manifest with the depot key.");
                return default;
            }

            return new ManifestResult(ManifestSource.Downloaded);
        }

        var reason = lastError != null ? SteamSession.DescribeError(lastError) : "no server answered";
        Report(job, $"[{job}] Failed to download the manifest after all retries: {reason}.");
        return default;
    }

    // Request codes expire after a few minutes. Returns 0 after reporting why there is none, or with the problem when
    // Steam gave none, which often goes for every manifest of the depot, so it is reported once for them
    private async Task<(ulong Code, string? Problem)> GetManifestRequestCodeAsync(ManifestJob job)
    {
        ulong code;

        try
        {
            code = await source.GetManifestRequestCodeAsync(job);
        }
        catch (Exception e) when (IsError(e))
        {
            Report(job, $"[{job}] Failed to get a manifest request code: {SteamSession.DescribeError(e)}");
            return (0, null);
        }

        if (code != 0)
        {
            return (code, null);
        }

        job.NoAccess = true;

        if (job.ItemID != null)
        {
            return (0, "Steam gave no manifest request code: this account may have no access to the item, or the manifest isn't one of the item's.");
        }

        return (0, "Steam gave no manifest request code: the manifest may be on another branch (set it with --branch, or @<branch> in a target) or in another app, or this account has no access to it. Steam also stops giving codes for many old manifests, so one that isn't the latest of its branch may not be downloadable.");
    }

    // Concurrent jobs of one depot share a single key request. Returns the key, or why there is none
    private Task<DepotKeyResult> GetDepotKeyAsync(ManifestJob job)
    {
        lock (DepotKeys)
        {
            var key = (job.DepotID, job.KeyAppID);

            if (!DepotKeys.TryGetValue(key, out var keyTask))
            {
                keyTask = RequestDepotKeyAsync(job);
                DepotKeys[key] = keyTask;
            }

            return keyTask;
        }
    }

    private async Task<DepotKeyResult> RequestDepotKeyAsync(ManifestJob job)
    {
        var depotKey = depotCache.LoadDepotKey(job.DepotID);

        if (depotKey != null)
        {
            return new DepotKeyResult(depotKey, false, null);
        }

        DepotKeyAnswer keyResult;

        try
        {
            keyResult = await source.RequestDepotKeyAsync(job);
        }
        catch (Exception e) when (IsError(e))
        {
            return new DepotKeyResult(null, false, $"Failed to get the depot key: {SteamSession.DescribeError(e)}");
        }

        // Steam denies a depot the account doesn't own or that is blocked, and doesn't find one that isn't in the app.
        // Other results, like a busy Steam, are failures that running again can fix
        if (keyResult.Result is EResult.AccessDenied or EResult.FileNotFound or EResult.Blocked)
        {
            if (job.ItemID != null)
            {
                return new DepotKeyResult(null, true, $"Steam gave no key for the workshop depot {job.DepotID} of app {job.KeyAppID} ({keyResult.Result}): this account may need to own the app.");
            }

            return new DepotKeyResult(null, true, $"Steam gave no depot key in app {job.KeyAppID} ({keyResult.Result}): this account may not own the depot, or it belongs to another app.");
        }

        if (keyResult.Result != EResult.OK)
        {
            return new DepotKeyResult(null, false, $"Failed to get the depot key: Steam answered {keyResult.Result}.");
        }

        depotCache.SaveDepotKey(job.DepotID, keyResult.Key);

        return new DepotKeyResult(keyResult.Key, false, null);
    }

    // Any failure of a job whose manifest isn't needed is only a warning. Everything else fails the run, so it's an error
    private static void Report(ManifestJob job, string message) => Report([job], message);

    private static void Report(IReadOnlyCollection<ManifestJob> jobs, string message)
    {
        if (jobs.All(static j => j.FailureIsFine))
        {
            Log.Warn(message);
        }
        else
        {
            Log.Error(message);
        }
    }

    // Jobs of one depot share its key, and often the reason Steam gave no request code, so each problem is reported once
    // per depot. Lacking access to an optional depot, like one of every depot of an app, is expected and goes unsaid,
    // since get and ls list those as skipped
    internal static void ReportSharedProblems(IEnumerable<(ManifestJob Job, string Problem, bool NoAccess)> problems)
    {
        var groups = problems
            .Where(static p => !(p.Job.Optional && p.NoAccess))
            .GroupBy(static p => (p.Job.DepotID, p.Problem))
            .OrderBy(static g => g.Key.DepotID);

        foreach (var group in groups)
        {
            var jobs = group.Select(static p => p.Job).OrderBy(static j => j.ToString(), StringComparer.Ordinal).ToList();
            string where;

            if (jobs.Count == 1)
            {
                where = $"[{jobs[0]}]";
            }
            else if (jobs[0].ItemID != null)
            {
                List<string> itemIDs = [.. jobs.Select(static j => j.ItemID!.Value.ToString(CultureInfo.InvariantCulture))];
                where = $"Items {Log.JoinFirst(itemIDs)}:";
            }
            else
            {
                where = $"Depot {group.Key.DepotID} ({Log.Plural(jobs.Count, "manifest")}):";
            }

            Report(jobs, $"{where} {group.Key.Problem}");
        }
    }
}
