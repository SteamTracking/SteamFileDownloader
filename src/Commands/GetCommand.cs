using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;
using ValvePak;

namespace SteamFileDownloader;

/// <summary>
/// The get and ls commands: many depot manifests over one Steam login, with results on stdout.
/// </summary>
internal static class GetCommand
{
    // A matching manifest file with its normalized path. Requested is false for directory VPKs only needed for their entries,
    // and VpkPatterns are the VPK patterns that read entries from it
    internal sealed record MatchedFile(
        DepotManifest.FileData File,
        string Path,
        bool Requested,
        IReadOnlyList<VpkPattern> VpkPatterns);

    internal sealed class Run
    {
        public required FileFilter Filter;
        public required SteamSession Session;
        public required string OutputRoot;

        // The folder of --install-dir, which every manifest's files go to directly instead of a folder per manifest
        public string? InstallFolder;

        // The command's name, which decides the exit code of a usage error
        public string Command = "get";

        // The password of the password protected branches to unlock, and the branches it unlocked per app
        public string? BranchPassword;
        public Dictionary<uint, List<string>> UnlockedBranches = [];

        // Added to the error of a workshop item that does not exist, and once after manifests that Steam gave no access to
        public string? ItemNotFoundHint;
        public string? ManifestNotFoundHint;

        // The exit code to end the run with right away, after an error that makes the rest pointless
        public int? AbortExitCode;

        public List<ManifestJob> Jobs = [];

        // The workshop items whose one file is a plain download, which have no manifest
        public List<LegacyItem> LegacyItems = [];
        public Dictionary<ManifestJob, List<MatchedFile>> Matches = [];
        public Dictionary<string, int> MatchCounts = [];

        // The jobs of each target that names several manifests, like every depot of an app, by the target's source.
        // Its manifests fail quietly one by one, but not all of them
        public Dictionary<string, List<ManifestJob>> AllTargets = [];
        public int Rows;

        // Each failure: the target it failed, if it was one, and whether only for lack of access
        private readonly List<(string? Target, bool NoAccess)> Failures = [];

        public bool Failed => Failures.Count > 0;

        // Something failed other than Steam giving no access
        public bool FailedOtherwise => Failures.Exists(static f => !f.NoAccess);

        // A target failed for lack of access, which another saved login may have
        public bool FailedForAccess => Failures.Exists(static f => f.Target != null && f.NoAccess);

        public List<string> FailedTargets => [.. Failures.Where(static f => f.Target != null).Select(static f => f.Target!)];

        // VPK patterns of directory files that failed to download or read, which were reported then, so they don't
        // count as matching nothing
        public HashSet<string> FailedVpkPatterns = [];

        // Whole depots are thousands of files, which nobody asked to see one by one
        public bool HideRows;

        // How to search every depot of the targets' app, up to the patterns, suggested when a pattern matched nothing
        public string? SearchAllHint;

        // With only VPK patterns, every row is a VPK entry, and directory files are downloaded only to read them
        public bool VpkOnly => Filter.Patterns.Count == 0 && Filter.VpkPatterns.Count > 0;

        private readonly Dictionary<(uint DepotID, ulong ManifestID), ManifestJob> JobsByManifest = [];

        /// <summary>
        /// Adds the job of a manifest, or returns the one already added for it. Its files go to &lt;parentFolder&gt;/&lt;manifest&gt;,
        /// or to the install folder when there is one.
        /// </summary>
        public ManifestJob AddJob(uint appID, KeyValue? depot, uint depotID, ulong manifestID, string branch, bool optional, string parentFolder)
        {
            if (JobsByManifest.TryGetValue((depotID, manifestID), out var existing))
            {
                // Also asked for by an explicit target, so its failure counts
                existing.Optional &= optional;
                return existing;
            }

            ManifestJob job;

            if (InstallFolder != null)
            {
                job = ManifestJob.CreateIn(appID, depotID, manifestID, branch, InstallFolder);
            }
            else
            {
                job = ManifestJob.Create(appID, depotID, manifestID, branch, parentFolder);
            }

            job.KeyAppID = AppInfo.GetKeyAppID(depot, appID);
            job.Optional = optional;
            job.ManifestFile = ManifestCache.GetPath(Path.Combine(OutputRoot, "manifests"), depotID, manifestID);

            JobsByManifest[(depotID, manifestID)] = job;
            Jobs.Add(job);

            return job;
        }

        public void Fail(string? target = null, bool noAccess = false) => Failures.Add((target, noAccess));

        public void WriteRow(ManifestJob job, params string[] columns)
        {
            // Workshop rows start with the item, since all items of an app share its workshop depot
            WriteRow($"{job.ItemID ?? job.DepotID}", job.ManifestID.ToString(CultureInfo.InvariantCulture), columns);
        }

        /// <summary>
        /// A row of a legacy item, whose manifest column says legacy.
        /// </summary>
        public void WriteLegacyRow(LegacyItem item, params string[] columns) => WriteRow(item.ToString(), LegacyItem.ManifestColumn, columns);

        private void WriteRow(string first, string manifest, string[] columns)
        {
            Rows++;

            if (!HideRows)
            {
                Log.Result($"{first}\t{manifest}\t{string.Join('\t', columns)}");
            }
        }
    }

    /// <summary>
    /// Runs get or ls on depot targets.
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

        if (targets.Count == 0)
        {
            var app = arguments.Options.App!.Value;

            targets.Add(new Target($"app {app}", app, DepotID: null, ManifestID: null, arguments.Options.Branch));
        }

        return await RunAsync(arguments.Command, arguments.Options, arguments.Patterns, run => ResolveJobsAsync(run, targets), cancellationToken);
    }

    /// <summary>
    /// Runs get or ls, or workshop get or ls: logs in, adds the run's jobs with resolveJobs, gets every manifest, and
    /// lists or downloads the matching files.
    /// </summary>
    internal static async Task<int> RunAsync(
        string command,
        DepotOptions options,
        List<string> patterns,
        Func<Run, Task> resolveJobs,
        CancellationToken cancellationToken)
    {
        FileFilter filter;

        try
        {
            filter = FileFilter.FromPatterns(patterns);
        }
        catch (ArgumentException e)
        {
            Log.Error($"Invalid regex pattern: {e.Message}");
            return ExitCodes.ForUsageError(command);
        }

        // VPK entries are extracted into a folder next to their VPK, which would put them among the installed files
        if (options.InstallDir != null && filter.VpkPatterns.Count > 0)
        {
            Log.Error("--install-dir can't be combined with <dir.vpk>:<pattern> patterns, whose entries are extracted next to the VPK. Use --output for those.");
            return ExitCodes.ForUsageError(command);
        }

        // Before logging in, since a settings file that can't be read fails the run
        var outputRoot = Settings.GetOutputRoot(options.Output);

        if (outputRoot == null)
        {
            return ExitCodes.Failed;
        }

        var (loggedOn, logOnExitCode) = await SteamSession.CreateLoggedOnAsync(options.Login, takesTargets: true, cancellationToken);

        if (loggedOn == null)
        {
            return logOnExitCode;
        }

        using var session = loggedOn;

        var downloads = command is "get" or "workshop get";
        var run = new Run
        {
            Command = command,
            BranchPassword = options.BranchPassword,
            ItemNotFoundHint = options.ItemNotFoundHint,
            ManifestNotFoundHint = options.ManifestNotFoundHint,
            Filter = filter,
            Session = session,
            OutputRoot = outputRoot,
            InstallFolder = options.InstallDir != null ? Path.GetFullPath(options.InstallDir) : null,
            HideRows = downloads && patterns.Count == 0,
        };

        // Manifests come from the CDN too, so its server list loads while the jobs resolve
        _ = session.EnsureCdnServersAsync();

        await resolveJobs(run);

        if (run.AbortExitCode != null)
        {
            return run.AbortExitCode.Value;
        }

        await session.DownloadManifestsAsync(run.Jobs);

        // Depots of all targets that the account can't access are expected, errors are not
        foreach (var job in run.Jobs.Where(static j => j.Manifest == null && !(j.Optional && j.NoAccess)))
        {
            run.Fail(job.ToString(), job.NoAccess);
        }

        if (run.ManifestNotFoundHint != null && run.Jobs.Exists(static j => j.Manifest == null && j.NoAccess))
        {
            Log.Info(run.ManifestNotFoundHint);
        }

        // Files come from the CDN, which does not need the Steam connection
        session.Disconnect();

        MatchFiles(run);

        return downloads ? await GetFilesAsync(run, cancellationToken) : await ListFilesAsync(run, cancellationToken);
    }

    private static async Task<int> GetFilesAsync(Run run, CancellationToken cancellationToken)
    {
        // With only VPK patterns, the files are directory files to read entries from
        using var downloader = new FileDownloader(run.Session, cancellationToken) { LogFilesSummary = !run.VpkOnly };

        var fileJobs = run.Matches
            .SelectMany(static m => m.Value.Select(f => new FileJob(m.Key, f.File)))
            .ToList();

        if (run.InstallFolder != null)
        {
            fileJobs = KeepLastAtEachPath(fileJobs);
        }

        var (success, files) = await downloader.DownloadAllFiles(fileJobs);

        if (!success)
        {
            run.Fail();
        }

        // Without patterns the whole depot is wanted, including its empty directories. With patterns only the folders of matched files exist
        var folders = new List<(ManifestJob Job, string Path)>();

        if (run.Filter.Patterns.Count == 0 && run.Filter.VpkPatterns.Count == 0)
        {
            foreach (var job in run.Jobs.Where(static j => j.Manifest != null))
            {
                var created = new List<string>();

                if (!OutputFiles.CreateDirectories(job, created))
                {
                    run.Fail();
                }

                folders.AddRange(created.Select(path => (job, path)));
            }
        }

        if (run.HideRows)
        {
            // Every file was asked for, and there is no VPK pattern to fetch directory files for
            run.Rows += files.Count;
        }
        else
        {
            var requested = run.Matches.SelectMany(static m => m.Value.Where(static f => f.Requested).Select(static f => f.File)).ToHashSet();

            // Directory files that were only fetched for VPK patterns are not printed. Rows follow the targets, like in ls
            var rows = files
                .Where(f => requested.Contains(f.File))
                .OrderBy(f => run.Jobs.IndexOf(f.Job))
                .ThenBy(static f => f.Path, StringComparer.Ordinal);

            foreach (var file in rows)
            {
                run.WriteRow(file.Job, file.Path);
            }
        }

        var directories = OpenVpkDirectories(run, files).ToList();
        var paths = files.Select(static f => (f.Job, f.Path)).ToList();

        try
        {
            // Directories extract in parallel, the shared chunk limit keeps the total in check
            var results = await downloader.RunWithProgress(
                "directory files",
                directories,
                d => VpkReader.ExtractAsync(downloader, run.Jobs, d.Job, d.DirFileName, d.Package, d.Matches));

            for (var i = 0; i < directories.Count; i++)
            {
                if (!results[i].Success)
                {
                    run.Fail();
                }

                foreach (var path in results[i].Paths.Order(StringComparer.Ordinal))
                {
                    run.WriteRow(directories[i].Job, path);
                    paths.Add((directories[i].Job, path));
                }
            }

            if (directories.Count > 0)
            {
                // With only VPK patterns, the closing line counts the entries
                if (!run.VpkOnly)
                {
                    Log.Info($"VPK entries: {results.Sum(static r => r.Paths.Count)} from {Log.Plural(directories.Count, "directory file")}.");
                }

                downloader.LogChunkErrors();
            }
        }
        finally
        {
            foreach (var directory in directories)
            {
                directory.Package.Dispose();
            }
        }

        await DownloadLegacyFilesAsync(run, cancellationToken);

        // Folders of an install hold files of every depot, so they keep their own dates
        if (run.InstallFolder == null)
        {
            OutputFiles.SetFolderTimes(paths, folders);
        }

        return Finish(run, downloads: true);
    }

    private static async Task DownloadLegacyFilesAsync(Run run, CancellationToken cancellationToken)
    {
        var items = run.LegacyItems.Where(static i => i.Requested).ToList();

        if (items.Count == 0)
        {
            return;
        }

        // The downloader times out requests that make no progress, since a file takes as long as its size needs
        using var client = SteamSession.CreateHttpClient(HttpClientPurpose.CDN);
        client.Timeout = Timeout.InfiniteTimeSpan;

        var downloader = new LegacyFileDownloader(client, cancellationToken);
        var (success, files) = await downloader.DownloadAllAsync(items);

        if (!success)
        {
            run.Fail();
        }

        var pathsByItem = files.ToDictionary(static f => f.Item, static f => f.Path);

        foreach (var item in items)
        {
            if (pathsByItem.TryGetValue(item, out var path))
            {
                run.WriteLegacyRow(item, path);
            }
        }
    }

    /// <summary>
    /// Keeps one file per path when every manifest writes into the install folder: the one of the manifest that comes
    /// last, like a later target. Warns about the files it overwrites that differ, once per pair of manifests. Takes the
    /// files in the order of their manifests.
    /// </summary>
    internal static List<FileJob> KeepLastAtEachPath(List<FileJob> fileJobs)
    {
        // Linux file systems tell paths apart by case, the others don't
        var comparer = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var paths = new List<string>(fileJobs.Count);
        var lastIndexes = new Dictionary<string, int>(comparer);
        var overwritten = new Dictionary<(ManifestJob Earlier, ManifestJob Later), List<string>>();

        for (var i = 0; i < fileJobs.Count; i++)
        {
            var fileJob = fileJobs[i];
            var path = FileFilter.NormalizePath(fileJob.File.FileName);

            paths.Add(path);

            if (lastIndexes.TryGetValue(path, out var earlierIndex) && FileDownloader.GetHashKey(fileJobs[earlierIndex].File) != FileDownloader.GetHashKey(fileJob.File))
            {
                var earlier = fileJobs[earlierIndex];
                var pair = (earlier.Job, fileJob.Job);

                if (!overwritten.TryGetValue(pair, out var pairPaths))
                {
                    pairPaths = [];
                    overwritten[pair] = pairPaths;
                }

                pairPaths.Add(path);
            }

            lastIndexes[path] = i;
        }

        foreach (var (pair, pairPaths) in overwritten)
        {
            Log.Warn($"{pair.Later} overwrites {Log.Plural(pairPaths.Count, "different file")} of {pair.Earlier}, which comes before it: {Log.JoinFirst(pairPaths)}.");
        }

        var kept = new List<FileJob>(lastIndexes.Count);

        for (var i = 0; i < fileJobs.Count; i++)
        {
            if (lastIndexes[paths[i]] == i)
            {
                kept.Add(fileJobs[i]);
            }
        }

        return kept;
    }

    private static async Task<int> ListFilesAsync(Run run, CancellationToken cancellationToken)
    {
        foreach (var (job, files) in run.Matches)
        {
            foreach (var file in files.Where(static f => f.Requested).OrderBy(static f => f.Path, StringComparer.Ordinal))
            {
                var hash = Convert.ToHexStringLower(file.File.FileHash);
                var size = file.File.TotalSize.ToString(CultureInfo.InvariantCulture);

                run.WriteRow(job, hash, size, file.Path);
            }
        }

        // Steam gives no hash of a legacy item's file
        foreach (var item in run.LegacyItems.Where(static i => i.Requested))
        {
            run.WriteLegacyRow(item, "-", item.Size.ToString(CultureInfo.InvariantCulture), item.FileName);
        }

        // Directory files have to be downloaded to read their entries
        using var downloader = new FileDownloader(run.Session, cancellationToken) { LogFilesSummary = false };
        var dirJobs = run.Matches
            .SelectMany(static m => m.Value.Where(static f => f.VpkPatterns.Count > 0).Select(f => new FileJob(m.Key, f.File)))
            .ToList();
        var (success, dirFiles) = await downloader.DownloadAllFiles(dirJobs);

        if (!success)
        {
            run.Fail();
        }

        foreach (var (job, dirFileName, package, matches) in OpenVpkDirectories(run, dirFiles))
        {
            using (package)
            {
                // An entry matched by several patterns is listed once
                foreach (var match in matches.DistinctBy(static m => m.Entry).OrderBy(static m => m.Path, StringComparer.Ordinal))
                {
                    var crc = match.Entry.CRC32.ToString("x8", CultureInfo.InvariantCulture);
                    var size = match.Entry.TotalLength.ToString(CultureInfo.InvariantCulture);

                    run.WriteRow(job, crc, size, $"{dirFileName}:{match.Path}");
                }
            }
        }

        OutputFiles.SetFolderTimes(dirFiles.Select(static f => (f.Job, f.Path)));

        return Finish(run, downloads: false);
    }

    private static async Task ResolveJobsAsync(Run run, List<Target> targets)
    {
        var appIDs = targets
            .Where(static t => t.DepotID == null || t.ManifestID == null)
            .Select(static t => t.AppID)
            .Distinct()
            .ToList();
        var appInfos = await run.Session.GetAppInfosAsync(appIDs);

        await run.Session.AddDlcDepotsAsync(appInfos.Values);

        // Explicit targets only need their app to find the depot's DLC for its key, so an app that can't be found is no error
        var explicitAppIDs = targets.Select(static t => t.AppID).Distinct().Except(appIDs).ToList();
        var explicitAppInfos = await run.Session.GetAppInfosAsync(explicitAppIDs, logFailures: false);

        foreach (var (appID, appInfo) in explicitAppInfos)
        {
            appInfos[appID] = appInfo;
        }

        if (run.BranchPassword != null)
        {
            foreach (var appID in appIDs.Where(appInfos.ContainsKey))
            {
                var (unlocked, exitCode) = await run.Session.UnlockBranchesAsync(run.Command, appID, appInfos[appID], run.BranchPassword);

                if (unlocked == null)
                {
                    run.AbortExitCode = exitCode;
                    return;
                }

                run.UnlockedBranches[appID] = unlocked;
            }
        }

        run.SearchAllHint = GetSearchAllHint(targets);

        // Depots without a manifest on the target's branch that use public's instead, said once per app and branch
        var publicFallbacks = new PublicFallbacks();

        // Earlier runs of other manifests of the depot may already have identical files
        ManifestJob AddJob(Target target, KeyValue? depot, uint depotID, ulong manifestID, string branch, bool optional)
        {
            var parentFolder = Path.Combine(run.OutputRoot, depotID.ToString(CultureInfo.InvariantCulture));

            return run.AddJob(target.AppID, depot, depotID, manifestID, branch, optional, parentFolder);
        }

        foreach (var target in targets)
        {
            if (target.DepotID != null && target.ManifestID != null)
            {
                // The depot may be missing from the app's info, like a removed one, which is fine for a known manifest
                var depotName = target.DepotID.Value.ToString(CultureInfo.InvariantCulture);
                var explicitDepot = appInfos.GetValueOrDefault(target.AppID)?["depots"][depotName];

                AddJob(target, explicitDepot, target.DepotID.Value, target.ManifestID.Value, target.Branch, optional: false);
                continue;
            }

            var depots = appInfos.GetValueOrDefault(target.AppID)?["depots"];

            if (depots == null)
            {
                // A lone depot id reads as the app id
                if (target.DepotID == null)
                {
                    Log.Info($"If {target.AppID} is a depot, put its app id before it, like <app> {target.AppID}.");
                }

                run.Fail($"{target.Source} (no app info for {target.AppID})");
                continue;
            }

            if (!AppInfo.CheckBranch(depots, target.AppID, target.Branch, run.UnlockedBranches.GetValueOrDefault(target.AppID)))
            {
                run.Fail($"{target.Source} (branch {target.Branch})");
                continue;
            }

            if (target.DepotID == null)
            {
                var allJobs = new List<ManifestJob>();

                // Shared installs are listed under the app that owns them
                foreach (var (depotID, depot) in AppInfo.GetDepots(depots).Where(static d => AppInfo.GetSharedInstallApp(d.Depot) == null))
                {
                    var (manifestID, branch) = publicFallbacks.Resolve(target.AppID, depotID, depot, target.Branch);

                    if (manifestID != 0)
                    {
                        allJobs.Add(AddJob(target, depot, depotID, manifestID, branch, optional: true));
                    }
                }

                if (allJobs.Count == 0)
                {
                    Log.Error($"App {target.AppID} has no depots with a manifest on \"{target.Branch}\", see \"{Help.ExeName} depots {target.AppID}\".");
                    run.Fail($"{target.Source} (no depots)");
                }
                else
                {
                    Log.Info($"App {target.AppID}: {allJobs.Count} depots with a manifest on \"{target.Branch}\".");
                    run.AllTargets[target.Source] = allJobs;
                }

                continue;
            }

            var depotInfo = depots[target.DepotID.Value.ToString(CultureInfo.InvariantCulture)];

            if (depotInfo == KeyValue.Invalid && Workshop.GetWorkshopDepot(appInfos[target.AppID]) == target.DepotID)
            {
                Log.Error($"Depot {target.DepotID} is the workshop depot of app {target.AppID}, whose manifests are workshop items. Use \"{Help.ExeName} workshop ls <item>\" or \"workshop get\", or give <depot>:<manifest>.");
                run.Fail($"{target.Source} (workshop depot)");
                continue;
            }

            if (depotInfo == KeyValue.Invalid)
            {
                Log.Error($"Depot {target.DepotID} is not in app {target.AppID}, so its latest manifest is unknown. Check with \"{Help.ExeName} depots {target.AppID}\", name its app as <app>/<depot>, or give <depot>:<manifest>.");
                run.Fail($"{target.Source} (not in app {target.AppID})");
                continue;
            }

            var (latestID, manifestBranch) = publicFallbacks.Resolve(target.AppID, target.DepotID.Value, depotInfo, target.Branch);

            if (latestID == 0)
            {
                var fromApp = AppInfo.GetSharedInstallApp(depotInfo);

                if (fromApp != null)
                {
                    Log.Error($"Depot {target.DepotID} is shared from app {fromApp}, use {fromApp}/{target.DepotID} as the target instead.");
                }
                else
                {
                    Log.Error($"Depot {target.DepotID} has no manifest on branch \"{target.Branch}\" (it may be a DLC or another branch's depot), see \"{Help.ExeName} depots {target.AppID}\".");
                }

                run.Fail($"{target.Source} (no manifest)");
                continue;
            }

            AddJob(target, depotInfo, target.DepotID.Value, latestID, manifestBranch, optional: false);
        }

        publicFallbacks.Log();
    }

    /// <summary>
    /// How to search every depot of the targets' app, up to the patterns, or null when they are of several apps or
    /// already cover every depot. That search only has the latest manifests, on the targets' branch when they share one.
    /// </summary>
    internal static string? GetSearchAllHint(IReadOnlyList<Target> targets)
    {
        if (targets.Count == 0)
        {
            return null;
        }

        if (targets.Select(static t => t.AppID).Distinct().Count() > 1)
        {
            return null;
        }

        if (targets.Any(static t => t.DepotID == null))
        {
            return null;
        }

        var what = targets.Any(static t => t.ManifestID != null) ? "every depot's latest manifest" : "every depot";
        var branches = targets.Select(static t => t.Branch).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var needsBranch = branches.Count == 1 && !branches[0].Equals("public", StringComparison.OrdinalIgnoreCase);
        var branch = needsBranch ? $" --branch {Help.QuoteArgument(branches[0])}" : "";

        return $"To search {what}: {Help.ExeName} ls {targets[0].AppID}{branch} --";
    }

    // One pass over every manifest: which files match, and how often each pattern matched
    private static void MatchFiles(Run run)
    {
        var filter = run.Filter;
        var listAll = filter.Patterns.Count == 0 && filter.VpkPatterns.Count == 0;

        foreach (var pattern in filter.Patterns.Select(static p => p.Source).Concat(filter.VpkPatterns.Select(static p => p.Source)))
        {
            run.MatchCounts[pattern] = 0;
        }

        foreach (var job in run.Jobs.Where(static j => j.Manifest != null))
        {
            var matches = new List<MatchedFile>();

            foreach (var (file, path) in job.GetFiles())
            {
                var matching = filter.GetMatchingPatterns(path);
                var requested = listAll || matching.Count > 0;

                foreach (var pattern in matching)
                {
                    run.MatchCounts[pattern.Source]++;
                }

                var vpkPatterns = filter.GetVpkPatterns(path);

                if (requested || vpkPatterns.Count > 0)
                {
                    matches.Add(new MatchedFile(file, path, requested, vpkPatterns));
                }
            }

            run.Matches[job] = matches;
        }

        // A legacy item has one file, which only patterns that match its name select
        foreach (var item in run.LegacyItems)
        {
            var matching = filter.GetMatchingPatterns(item.FileName);

            foreach (var pattern in matching)
            {
                run.MatchCounts[pattern.Source]++;
            }

            item.Requested = listAll || matching.Count > 0;
        }
    }

    /// <summary>
    /// Opens each downloaded directory VPK that VPK patterns point at, with the entries they match.
    /// </summary>
    private static IEnumerable<(ManifestJob Job, string DirFileName, Package Package, List<VpkEntryMatch> Matches)> OpenVpkDirectories(
        Run run,
        List<DownloadedFile> files)
    {
        var directories = run.Matches.Values
            .SelectMany(static files => files)
            .Where(static f => f.VpkPatterns.Count > 0)
            .ToDictionary(static f => f.File);

        if (directories.Count == 0)
        {
            yield break;
        }

        var downloaded = files.Select(static f => f.File).ToHashSet();

        foreach (var directory in directories.Values.Where(d => !downloaded.Contains(d.File)))
        {
            run.FailedVpkPatterns.UnionWith(directory.VpkPatterns.Select(static p => p.Source));
        }

        foreach (var file in files.OrderBy(f => run.Jobs.IndexOf(f.Job)))
        {
            if (!directories.TryGetValue(file.File, out var directory))
            {
                continue;
            }

            var dirFileName = directory.Path;

#pragma warning disable CA2000 // Disposed by the caller
            var package = VpkReader.TryRead(file.Job, file.Path, dirFileName);
#pragma warning restore CA2000

            if (package == null)
            {
                run.FailedVpkPatterns.UnionWith(directory.VpkPatterns.Select(static p => p.Source));
                run.Fail();
                continue;
            }

            var matches = VpkReader.ListEntries(package, directory.VpkPatterns);

            foreach (var match in matches)
            {
                run.MatchCounts[match.Pattern.Source]++;
            }

            yield return (file.Job, dirFileName, package, matches);
        }
    }

    private static int Finish(Run run, bool downloads)
    {
        // A target whose manifests all failed fails as a whole, and the closing error names it. Its jobs aren't also
        // listed as skipped
        var failedWhole = new HashSet<ManifestJob>();

        foreach (var (source, jobs) in run.AllTargets)
        {
            if (jobs.All(static j => j.Manifest == null))
            {
                var noun = jobs[0].ItemID != null ? "item" : "depot";

                run.Fail($"{source} ({Log.Plural(jobs.Count, noun)})", jobs.All(static j => j.NoAccess));
                failedWhole.UnionWith(jobs);
            }
        }

        // Patterns can only be judged against manifests that were downloaded, and VPK patterns against directory files
        // that were read
        List<string> unmatched;

        var searched = run.Matches.Count + run.LegacyItems.Count;

        if (searched > 0)
        {
            unmatched = run.MatchCounts
                .Where(p => p.Value == 0 && !run.FailedVpkPatterns.Contains(p.Key))
                .Select(static p => p.Key)
                .ToList();
        }
        else
        {
            unmatched = [];
        }

        var skippedJobs = run.Jobs.Where(j => j.Optional && j.NoAccess && !failedWhole.Contains(j)).ToList();
        var skipped = skippedJobs.Select(static j => j.ItemID != null ? j.ToString() : j.DepotID.ToString(CultureInfo.InvariantCulture)).ToList();

        if (unmatched.Count > 0)
        {
            var skippedWhat = skippedJobs.Any(static j => j.ItemID != null) ? "item" : "depot";
            string? where = null;

            if (run.Matches.Count == 1 && searched == 1)
            {
                where = run.Matches.Keys.First().ToString();
            }
            else if (run.LegacyItems.Count == 1 && searched == 1)
            {
                where = run.LegacyItems[0].Label;
            }

            Log.Error(DescribeUnmatched(unmatched, searched, where, skipped.Count, skippedWhat, run.SearchAllHint));
        }

        // One closing line, so the outcome is clear without reading the whole log
        var notes = new List<string>();

        if (skipped.Count > 0)
        {
            notes.Add($"skipped without access: {Log.JoinFirst(skipped)}");
        }

        // A target without a match is fine, like a build from before the file existed. When no pattern matched at all, the error says so already
        var withoutMatches = run.Matches.Where(static m => m.Value.Count == 0 && !m.Key.Optional).Select(static m => m.Key.ToString()).ToList();

        withoutMatches.AddRange(run.LegacyItems.Where(static i => !i.Requested).Select(static i => i.Label));

        var someMatched = unmatched.Count == 0 || unmatched.Count < run.MatchCounts.Count;

        if (withoutMatches.Count > 0 && someMatched)
        {
            notes.Add($"nothing matched in {Log.JoinFirst(withoutMatches)}");
        }

        var counted = new List<string>();

        if (run.Jobs.Count > 0 || run.LegacyItems.Count == 0)
        {
            counted.Add(Log.PluralOf(run.Matches.Count, run.Jobs.Count, "manifest"));
        }

        if (run.LegacyItems.Count > 0)
        {
            counted.Add(Log.Plural(run.LegacyItems.Count, "legacy item"));
        }

        var manifests = string.Join(" and ", counted);

        string rows;

        if (run.HideRows)
        {
            rows = Log.Plural(run.Rows, "file");
        }
        else if (run.VpkOnly)
        {
            rows = Log.Plural(run.Rows, "VPK entry", "VPK entries");
        }
        else
        {
            rows = Log.Plural(run.Rows, "row");
        }

        var notesText = notes.Count > 0 ? $" ({string.Join("; ", notes)})" : "";
        var filesFolder = run.InstallFolder ?? run.OutputRoot;
        var outputNote = downloads ? $" Files are in {filesFolder}" : "";

        Log.Info($"Done: {rows} from {manifests}{notesText}.{outputNote}");

        // Another login only helps targets that failed for lack of access
        if (run.FailedForAccess)
        {
            Log.Info(Help.AccessHint(run.Session.IsAnonymous, AccountStore.GetUsernames()));
        }

        // The failed targets are named in the error that explains the exit code
        return ExitCodes.Report(new RunOutcome(run.Failed, run.FailedOtherwise, run.Session.FailureExitCode, unmatched.Count > 0, run.FailedTargets));
    }

    /// <summary>
    /// The error for patterns that matched nothing: in which manifests, how many were skipped without access, and how to
    /// search every depot. onlyManifest names the one manifest searched, if there was one, and skippedNoun is depot or item.
    /// </summary>
    internal static string DescribeUnmatched(
        IReadOnlyList<string> unmatched,
        int manifests,
        string? onlyManifest,
        int skipped,
        string skippedNoun,
        string? searchAllHint)
    {
        var where = onlyManifest ?? $"any of the {manifests} manifests";
        var skippedNote = skipped > 0 ? $" ({Log.Plural(skipped, skippedNoun)} skipped without access)" : "";
        var hint = searchAllHint != null ? $" {searchAllHint} {string.Join(' ', unmatched.Select(Help.QuoteArgument))}" : "";

        return $"{string.Join(", ", unmatched.Select(static p => $"\"{p}\""))} matched nothing in {where}{skippedNote}.{hint}";
    }
}
