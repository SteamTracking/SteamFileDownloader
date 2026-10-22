using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;

namespace SteamFileDownloader;

/// <summary>
/// The download command: installs the depots of one app into one folder, picking the depots for this machine like Steam.
/// </summary>
internal static class DownloadCommand
{
    /// <summary>
    /// Runs download. Depots named as targets are installed as they are, and without targets the depots that Steam
    /// would install are.
    /// </summary>
    public static async Task<int> RunAsync(DepotArguments arguments, CancellationToken cancellationToken)
    {
        var targets = new List<Target>();

        foreach (var arg in arguments.Targets)
        {
            var target = DepotArgumentParser.ParseTarget(arg, arguments);

            if (target == null)
            {
                return ExitCodes.ForUsageError(arguments.Command);
            }

            targets.Add(target);
        }

        Task ResolveJobs(GetCommand.Run run)
        {
            if (targets.Count > 0)
            {
                return GetCommand.ResolveJobsAsync(run, targets);
            }

            return ResolveAppJobsAsync(run, arguments.Options);
        }

        // Every manifest's files go to one folder, which is the app's in the output folder unless --dir names one
        void Configure(GetCommand.Run run)
        {
            var appFolder = Path.Combine(run.OutputRoot, arguments.Options.App!.Value.ToString(CultureInfo.InvariantCulture));

            run.InstallFolder = Path.GetFullPath(arguments.Options.Install.Dir ?? appFolder);
            run.SkipSymlinks = arguments.Options.Install.IsForOtherOs;
        }

        return await GetCommand.RunAsync(arguments.Command, arguments.Options, arguments.Patterns, ResolveJobs, cancellationToken, Configure);
    }

    /// <summary>
    /// Adds a job for each depot of the app that matches the options, in the order of the app's info, since the depot that
    /// comes last wins a path that several have.
    /// </summary>
    internal static async Task ResolveAppJobsAsync(GetCommand.Run run, DepotOptions options)
    {
        var appID = options.App!.Value;
        var source = $"app {appID}";
        var appInfos = await GetCommand.GetAppInfosAsync(run, [appID]);

        if (appInfos == null)
        {
            return;
        }

        if (!appInfos.TryGetValue(appID, out var appInfo))
        {
            run.Fail($"{source} (no app info)");
            return;
        }

        var depots = appInfo["depots"];

        if (!AppInfo.GetDepots(depots).Any())
        {
            Log.Error($"App {appID} has no depots. DLC and tools often keep their files in depots of their parent app.");
            run.Fail($"{source} (no depots)");
            return;
        }

        var branch = options.Branch;

        if (!AppInfo.CheckBranch(depots, appID, branch, run.UnlockedBranches.GetValueOrDefault(appID)))
        {
            run.Fail($"{source} (branch {branch})");
            return;
        }

        if (AppInfo.RequiresLocalContentServer(depots, branch))
        {
            Log.Info($"Branch \"{branch}\" of app {appID} needs a local content server, using \"public\".");
            branch = "public";
        }

        var install = options.Install;
        var selection = DepotFilter.SelectForApp(appInfo, install);

        if (install.HasIncludeOrExclude)
        {
            var include = install.Include ?? [];
            var exclude = install.Exclude ?? [];

            if (!CheckIds(run, appID, "--include", include, depots) || !CheckIds(run, appID, "--exclude", exclude, depots))
            {
                return;
            }

            selection = DepotFilter.ApplyIncludeExclude(depots, selection, include, exclude);

            if (selection.Included.Count > 0)
            {
                var reasons = selection.Included.Select(Describe).ToList();

                Log.Info($"Including {Log.Plural(selection.Included.Count, "depot")} that the filters skipped: {Log.JoinFirst(reasons)}.");
            }

            var excluded = selection.Skipped.Where(static s => s.Reason == DepotFilter.ExcludedReason).Select(static s => s.ID.ToString(CultureInfo.InvariantCulture)).ToList();

            if (excluded.Count > 0)
            {
                Log.Info($"Excluding {Log.Plural(excluded.Count, "depot")}: {Log.JoinFirst(excluded)}.");
            }
        }

        var requestedLanguage = install.Language ?? "english";

        if (!install.AllLanguages && !selection.Language.Equals(requestedLanguage, StringComparison.OrdinalIgnoreCase))
        {
            Log.Info($"App {appID} has no \"{requestedLanguage}\" depots, using \"{selection.Language}\".");
        }

        var skipped = selection.Skipped;
        var sourceInfos = await GetSharedInstallInfosAsync(run, appID, selection);
        var publicFallbacks = new PublicFallbacks();
        var jobs = new List<ManifestJob>();

        foreach (var (depotID, depot) in selection.Depots)
        {
            var depotName = depotID.ToString(CultureInfo.InvariantCulture);
            var jobAppID = appID;
            var jobDepot = depot;

            // A shared install has the manifests and key of the app it comes from, and goes in the same folder
            if (AppInfo.GetSharedInstallSourceApp(depot, appID) is { } sourceAppID)
            {
                var sourceDepot = sourceInfos.GetValueOrDefault(sourceAppID)?["depots"][depotName];

                if (sourceDepot == null || sourceDepot == KeyValue.Invalid)
                {
                    skipped.Add(new SkippedDepot(depotID, $"app {sourceAppID} unavailable"));
                    continue;
                }

                jobAppID = sourceAppID;
                jobDepot = sourceDepot;
            }

            var (manifestID, manifestBranch) = publicFallbacks.Resolve(jobAppID, depotID, jobDepot, branch);

            if (manifestID == 0)
            {
                skipped.Add(new SkippedDepot(depotID, "no manifest"));
                continue;
            }

            // Depots of DLC that the account doesn't own fail their key request, which is expected
            var parentFolder = Path.Combine(run.OutputRoot, depotName);

            jobs.Add(run.AddJob(jobAppID, jobDepot, depotID, manifestID, manifestBranch, optional: true, parentFolder));
        }

        publicFallbacks.Log();

        // Excluded depots have their own line
        var filtered = skipped.Where(static s => s.Reason != DepotFilter.ExcludedReason).ToList();

        if (filtered.Count > 0)
        {
            var reasons = filtered.Select(Describe).ToList();

            Log.Info($"Skipping {Log.Plural(filtered.Count, "depot")}: {Log.JoinFirst(reasons)}.");
        }

        if (jobs.Count == 0)
        {
            Log.Error($"App {appID} has no depots with a manifest on \"{branch}\" for this platform and language. Name depots to install them whatever they are for, see \"{Help.ExeName} depots {appID}\".");
            run.Fail($"{source} (no depots)");
            return;
        }

        Log.Info($"App {appID}: {Log.Plural(jobs.Count, "depot")} with a manifest on \"{branch}\".");
        run.AllTargets[source] = jobs;
    }

    // Like "81 (mod czero)", with the depot's own value of the config key when the reason is one
    private static string Describe(SkippedDepot depot)
    {
        if (string.IsNullOrEmpty(depot.Value))
        {
            return $"{depot.ID} ({depot.Reason})";
        }

        return $"{depot.ID} ({depot.Reason} {depot.Value})";
    }

    // Fails the run with a usage error when an id names no depot of the app. Returns false after that
    private static bool CheckIds(GetCommand.Run run, uint appID, string option, IReadOnlyList<uint> ids, KeyValue depots)
    {
        var unknown = DepotFilter.FindUnknownIds(depots, ids);

        if (unknown.Count == 0)
        {
            return true;
        }

        Log.Error($"{option} {unknown[0]} is not a depot or DLC app id of app {appID}. See \"{Help.ExeName} depots {appID}\" for its depots.");
        run.AbortExitCode = ExitCodes.ForUsageError("download");
        return false;
    }

    // The info of the other apps that the selected depots are shared from
    private static async Task<Dictionary<uint, KeyValue>> GetSharedInstallInfosAsync(GetCommand.Run run, uint appID, DepotSelection selection)
    {
        var sourceAppIDs = new List<uint>();

        foreach (var (_, depot) in selection.Depots)
        {
            var sourceAppID = AppInfo.GetSharedInstallSourceApp(depot, appID);

            if (sourceAppID != null && !sourceAppIDs.Contains(sourceAppID.Value))
            {
                sourceAppIDs.Add(sourceAppID.Value);
            }
        }

        return await run.Session.GetAppInfosAsync(sourceAppIDs, logFailures: false);
    }
}
