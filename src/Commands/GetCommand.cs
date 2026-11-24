using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;
using ValvePak;

namespace SteamFileDownloader;

/// <summary>
/// The get and ls commands: many depot manifests over one Steam login, with results on stdout. The download command
/// shares their run, with the files of every manifest in one folder.
/// </summary>
internal static class GetCommand
{
    internal sealed class Run
    {
        public required FileFilter Filter;
        public required SteamSession Session;
        public required string OutputRoot;

        // The folder of the download command, which every manifest's files go to directly instead of a folder per manifest
        public string? InstallFolder;

        // Whether symbolic links are left out, since the files are for another operating system than this one
        public bool SkipSymlinks;

        // The command's name, which decides the exit code of a usage error
        public string Command = "get";

        // The options of the command line, with the branch password and the hints for ids that may be of the other kind
        public DepotOptions Options = new(App: null);

        // The branches that the branch password unlocked, per app
        public Dictionary<uint, List<string>> UnlockedBranches = [];

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

        public readonly FailureLog Failures = new();

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
        public ManifestJob AddJob(uint appID, uint keyAppID, uint depotID, ulong manifestID, string branch, bool optional, string parentFolder)
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

            job.KeyAppID = keyAppID;
            job.Optional = optional;
            job.ManifestFile = ManifestCache.GetPath(Path.Combine(OutputRoot, "manifests"), depotID, manifestID);

            JobsByManifest[(depotID, manifestID)] = job;
            Jobs.Add(job);

            return job;
        }

        /// <summary>
        /// Adds the jobs of resolved depots, whose files go to each depot's folder in the output root.
        /// </summary>
        public List<ManifestJob> AddJobs(List<ResolvedDepot> depots)
        {
            var jobs = new List<ManifestJob>(depots.Count);

            foreach (var depot in depots)
            {
                var parentFolder = Path.Combine(OutputRoot, depot.DepotID.ToString(CultureInfo.InvariantCulture));

                jobs.Add(AddJob(depot.AppID, depot.KeyAppID, depot.DepotID, depot.ManifestID, depot.Branch, depot.Optional, parentFolder));
            }

            return jobs;
        }

        public void AddTargets(List<ResolvedTarget> targets)
        {
            foreach (var resolved in targets)
            {
                var jobs = AddJobs(resolved.Depots);

                if (resolved.WholeApp)
                {
                    AllTargets[resolved.Target.Source] = jobs;
                }
            }
        }

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
                Log.ResultRow(first, manifest, columns);
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
    /// Runs get, ls or download, or workshop get or ls: logs in, adds the run's jobs with resolveJobs, gets every manifest,
    /// and lists or downloads the matching files. The configure callback sets up the run before its jobs are resolved.
    /// </summary>
    internal static async Task<int> RunAsync(
        string command,
        DepotOptions options,
        List<string> patterns,
        Func<Run, Task> resolveJobs,
        CancellationToken cancellationToken,
        Action<Run>? configure = null)
    {
        FileFilter filter;

        try
        {
            filter = FileFilter.FromPatterns(patterns);
        }
        catch (RegexParseException e)
        {
            Log.Error($"Invalid regex pattern: {e.Message}");
            return ExitCodes.ForUsageError(command);
        }
        catch (ArgumentException e)
        {
            Log.Error(e.Message);
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

        var downloads = command is "get" or "download" or "workshop get";
        var run = new Run
        {
            Command = command,
            Options = options,
            Filter = filter,
            Session = session,
            OutputRoot = outputRoot,
            HideRows = downloads && patterns.Count == 0,
        };

        configure?.Invoke(run);

        // Manifests come from the CDN too, so its server list loads while the jobs resolve
        _ = session.EnsureCdnServersAsync();

        await resolveJobs(run);

        if (run.AbortExitCode != null)
        {
            return run.AbortExitCode.Value;
        }

        // Without patterns every file and directory is wanted
        var wantsAll = run.Filter.Patterns.Count == 0 && run.Filter.VpkPatterns.Count == 0;

        await session.Manifests.GetManifestsAsync(run.Jobs, wantsAll ? null : run.Filter);

        // Depots of all targets that the account can't access are expected, errors are not
        foreach (var job in run.Jobs.Where(static j => j.Manifest == null && !(j.Optional && j.NoAccess)))
        {
            run.Failures.Add(job.ToString(), job.NoAccess);
        }

        if (options.ManifestNotFoundHint != null && run.Jobs.Exists(static j => j.Manifest == null && j.NoAccess))
        {
            Log.Info(options.ManifestNotFoundHint);
        }

        // Files come from the CDN, which does not need the Steam connection
        session.Disconnect();

        var matched = RunAccounting.MatchFiles(run.Filter, run.Jobs, run.LegacyItems);
        run.Matches = matched.Matches;
        run.MatchCounts = matched.Counts;

        return downloads ? await GetFilesAsync(run, cancellationToken) : await ListFilesAsync(run, cancellationToken);
    }

    private static async Task<int> GetFilesAsync(Run run, CancellationToken cancellationToken)
    {
        // With only VPK patterns, the files are directory files to read entries from
        using var downloader = new FileDownloader(run.Session, cancellationToken) { LogFilesSummary = !run.VpkOnly };

        // In the order of the jobs, which decides the file that stays at a path that several manifests have
        var fileJobs = run.Jobs
            .Where(run.Matches.ContainsKey)
            .SelectMany(job => run.Matches[job].Select(f => new FileJob(job, f.File)))
            .ToList();

        if (run.SkipSymlinks)
        {
            var linkCount = fileJobs.RemoveAll(static f => (f.File.Flags & EDepotFileFlag.Symlink) != 0);

            if (linkCount > 0)
            {
                Log.Info($"Skipping {Log.Plural(linkCount, "symbolic link")}, since the files are for another operating system.");
            }
        }

        if (run.InstallFolder != null)
        {
            // A file is written whole even when an earlier run left an older version of it, which a later version could patch
            // or move chunks of in place instead. No state file lists what an earlier run installed, so files that are no
            // longer in the manifests stay in the folder
            fileJobs = KeepLastAtEachPath(fileJobs);
        }

        var (success, files) = await downloader.DownloadAllFiles(fileJobs);

        if (!success)
        {
            run.Failures.Add();
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
                    run.Failures.Add();
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
                    run.Failures.Add();
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
            run.Failures.Add();
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
            run.Failures.Add();
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

    /// <summary>
    /// Gets the info of the apps with their DLC depots merged in, and unlocks the branches that the run's branch password
    /// is for. Returns null after setting the exit code to abort the run with.
    /// </summary>
    internal static async Task<Dictionary<uint, KeyValue>?> GetAppInfosAsync(Run run, List<uint> appIDs)
    {
        var (appInfos, unlockedBranches, exitCode) = await run.Session.GetAppInfosForDepotsAsync(run.Command, appIDs, run.Options.BranchPassword);

        if (exitCode != ExitCodes.Ok)
        {
            run.AbortExitCode = exitCode;
            return null;
        }

        foreach (var (appID, unlocked) in unlockedBranches)
        {
            run.UnlockedBranches[appID] = unlocked;
        }

        return appInfos;
    }

    /// <summary>
    /// Adds the jobs of the targets, which are depots, or every depot of an app when they don't name one.
    /// </summary>
    internal static async Task ResolveJobsAsync(Run run, List<Target> targets)
    {
        var appIDs = targets
            .Where(static t => t.DepotID == null || t.ManifestID == null)
            .Select(static t => t.AppID)
            .Distinct()
            .ToList();
        var appInfos = await GetAppInfosAsync(run, appIDs);

        if (appInfos == null)
        {
            return;
        }

        // Explicit targets only need their app to find the depot's DLC for its key, so an app that can't be found is no error
        var explicitAppIDs = targets.Select(static t => t.AppID).Distinct().Except(appIDs).ToList();
        var explicitAppInfos = await run.Session.GetAppInfosAsync(explicitAppIDs, logFailures: false);

        foreach (var (appID, appInfo) in explicitAppInfos)
        {
            appInfos[appID] = appInfo;
        }

        run.SearchAllHint = GetSearchAllHint(targets);

        run.AddTargets(JobResolver.ResolveTargets(targets, appInfos, run.UnlockedBranches, run.Failures));
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
                run.Failures.Add();
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
        var summary = RunAccounting.Summarize(
            run.Jobs,
            run.Matches,
            run.LegacyItems,
            run.MatchCounts,
            run.FailedVpkPatterns,
            run.AllTargets,
            run.Failures,
            run.SearchAllHint);

        if (summary.UnmatchedError != null)
        {
            Log.Error(summary.UnmatchedError);
        }

        // One closing line, so the outcome is clear without reading the whole log
        string? filesFolder = null;

        if (downloads)
        {
            filesFolder = run.InstallFolder ?? run.OutputRoot;
        }

        Log.Info(RunAccounting.DescribeDone(
            run.Rows,
            run.HideRows,
            run.VpkOnly,
            jobCount: run.Jobs.Count,
            manifestCount: run.Matches.Count,
            legacyCount: run.LegacyItems.Count,
            summary.Notes,
            filesFolder));

        // Another login only helps targets that failed for lack of access
        if (run.Failures.FailedForAccess)
        {
            Log.Info(Help.AccessHint(run.Session.IsAnonymous, AccountStore.GetUsernames()));
        }

        // The failed targets are named in the error that explains the exit code
        return ExitCodes.Report(run.Failures.ToOutcome(run.Session.FailureExitCode, summary.UnmatchedError != null));
    }
}
