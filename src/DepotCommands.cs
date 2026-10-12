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
internal static class DepotCommands
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
        public List<ManifestJob> Jobs = [];
        public Dictionary<ManifestJob, List<MatchedFile>> Matches = [];
        public Dictionary<string, int> MatchCounts = [];
        public List<string> FailedTargets = [];

        // The jobs of each target that names several manifests, like all, by the target's source. Its manifests fail
        // quietly one by one, but not all of them
        public Dictionary<string, List<ManifestJob>> AllTargets = [];
        public int Rows;
        public bool Failed;

        // Something failed other than Steam giving no access
        public bool FailedOtherwise;

        // Whole depots are thousands of files, which nobody asked to see one by one
        public bool HideRows;

        private readonly Dictionary<(uint DepotID, ulong ManifestID), ManifestJob> JobsByManifest = [];

        /// <summary>
        /// Adds the job of a manifest, or returns the one already added for it. Its files go to &lt;parentFolder&gt;/&lt;manifest&gt;.
        /// </summary>
        public ManifestJob AddJob(uint appID, uint depotID, ulong manifestID, string branch, bool optional, string parentFolder)
        {
            if (JobsByManifest.TryGetValue((depotID, manifestID), out var existing))
            {
                // Also asked for by an explicit target, so its failure counts
                existing.Optional &= optional;
                return existing;
            }

            var job = ManifestJob.Create(appID, depotID, manifestID, branch, parentFolder);
            job.Optional = optional;

            JobsByManifest[(depotID, manifestID)] = job;
            Jobs.Add(job);

            return job;
        }

        public void Fail(string? target = null, bool noAccess = false)
        {
            if (target != null)
            {
                FailedTargets.Add(target);
            }

            Failed = true;
            FailedOtherwise |= !noAccess;
        }

        public void WriteRow(ManifestJob job, params string[] columns)
        {
            Rows++;

            if (!HideRows)
            {
                Log.Result($"{job.DepotID}\t{job.ManifestID}\t{string.Join('\t', columns)}");
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
            if (DepotArgumentParser.ParseTarget(arg, arguments) is not { } target)
            {
                return ExitCodes.Failed;
            }

            targets.Add(target);
        }

        return await RunAsync(arguments.Options, arguments.Patterns, list: arguments.Command == "ls", run => ResolveJobsAsync(run, targets), cancellationToken);
    }

    /// <summary>
    /// Logs in, adds the run's jobs with resolveJobs, gets every manifest, and lists or downloads the matching files.
    /// </summary>
    internal static async Task<int> RunAsync(DepotOptions options, List<string> patterns, bool list, Func<Run, Task> resolveJobs, CancellationToken cancellationToken)
    {
        FileFilter filter;

        try
        {
            filter = FileFilter.FromPatterns(patterns);
        }
        catch (ArgumentException e)
        {
            Log.Error($"Invalid regex pattern: {e.Message}");
            return ExitCodes.Failed;
        }

        var (loggedOn, logOnExitCode) = await SteamSession.CreateLoggedOnAsync(options, takesTargets: true, cancellationToken);

        if (loggedOn == null)
        {
            return logOnExitCode;
        }

        using var session = loggedOn;

        var run = new Run
        {
            Filter = filter,
            Session = session,
            OutputRoot = Path.GetFullPath(options.Output ?? "depots"),
            HideRows = options.AllFiles,
        };

        // Manifests come from the CDN too, so its server list loads while the jobs resolve
        _ = session.EnsureCdnServersAsync();

        await resolveJobs(run);

        await session.DownloadManifestsAsync(run.Jobs);

        // Depots of all targets that the account can't access are expected, errors are not
        foreach (var job in run.Jobs.Where(static j => j.Manifest == null && !(j.Optional && j.NoAccess)))
        {
            run.Fail(job.ToString(), job.NoAccess);
        }

        // Files come from the CDN, which does not need the Steam connection
        session.Disconnect();

        MatchFiles(run);

        return list ? await ListFilesAsync(run, cancellationToken) : await GetFilesAsync(run, cancellationToken);
    }

    private static async Task<int> GetFilesAsync(Run run, CancellationToken cancellationToken)
    {
        using var downloader = new FileDownloader(run.Session, cancellationToken);

        var fileJobs = run.Matches
            .SelectMany(static m => m.Value.Select(f => new FileJob(m.Key, f.File)))
            .ToList();

        var (success, files) = await downloader.DownloadAllFiles(fileJobs);

        if (!success)
        {
            run.Fail();
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
            foreach (var file in files.Where(f => requested.Contains(f.File)).OrderBy(f => run.Jobs.IndexOf(f.Job)).ThenBy(static f => f.Path, StringComparer.Ordinal))
            {
                run.WriteRow(file.Job, file.Path);
            }
        }

        var directories = OpenVpkDirectories(run, files).ToList();
        var paths = files.Select(static f => (f.Job, f.Path)).ToList();

        try
        {
            // Directories extract in parallel, the shared chunk limit keeps the total in check
            var results = await downloader.RunWithProgress("directory files", directories, d => VpkReader.ExtractAsync(downloader, run.Jobs, d.Job, d.DirFileName, d.Package, d.Matches));

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
                Log.Info($"VPK entries: {results.Sum(static r => r.Paths.Count)} from {directories.Count} directory files.");
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

        OutputFiles.SetFolderTimes(paths);

        return Finish(run, downloads: true);
    }

    private static async Task<int> ListFilesAsync(Run run, CancellationToken cancellationToken)
    {
        foreach (var (job, files) in run.Matches)
        {
            foreach (var file in files.Where(static f => f.Requested).OrderBy(static f => f.Path, StringComparer.Ordinal))
            {
                run.WriteRow(job, Convert.ToHexStringLower(file.File.FileHash), file.File.TotalSize.ToString(CultureInfo.InvariantCulture), file.Path);
            }
        }

        // Directory files have to be downloaded to read their entries
        using var downloader = new FileDownloader(run.Session, cancellationToken);
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
                    run.WriteRow(job, match.Entry.CRC32.ToString("x8", CultureInfo.InvariantCulture), match.Entry.TotalLength.ToString(CultureInfo.InvariantCulture), $"{dirFileName}:{match.Path}");
                }
            }
        }

        OutputFiles.SetFolderTimes(dirFiles.Select(static f => (f.Job, f.Path)));

        return Finish(run, downloads: false);
    }

    private static async Task ResolveJobsAsync(Run run, List<Target> targets)
    {
        var appInfos = await run.Session.GetAppInfosAsync([.. targets.Where(static t => t.DepotID == null || t.ManifestID == null).Select(static t => t.AppID).Distinct()]);

        // Earlier runs of other manifests of the depot may already have identical files
        ManifestJob AddJob(Target target, uint depotID, ulong manifestID, string branch, bool optional) =>
            run.AddJob(target.AppID, depotID, manifestID, branch, optional, Path.Combine(run.OutputRoot, depotID.ToString(CultureInfo.InvariantCulture)));

        foreach (var target in targets)
        {
            if (target.DepotID != null && target.ManifestID != null)
            {
                AddJob(target, target.DepotID.Value, target.ManifestID.Value, target.Branch, optional: false);
                continue;
            }

            var depots = appInfos.GetValueOrDefault(target.AppID)?["depots"];

            if (depots == null)
            {
                run.Fail($"{target.Source} (no app info for {target.AppID})");
                continue;
            }

            if (!AppInfo.CheckBranch(depots, target.AppID, target.Branch))
            {
                run.Fail($"{target.Source} (branch {target.Branch})");
                continue;
            }

            if (target.DepotID == null)
            {
                var allJobs = new List<ManifestJob>();

                // Shared depots are listed under the app that owns them
                foreach (var (depotID, depot) in AppInfo.GetDepots(depots).Where(static d => d.Depot["depotfromapp"].Value == null))
                {
                    var branch = target.Branch;
                    var manifestID = AppInfo.GetManifestIdForBranch(depot, ref branch);

                    if (manifestID != 0)
                    {
                        allJobs.Add(AddJob(target, depotID, manifestID, branch, optional: true));
                    }
                }

                if (allJobs.Count == 0)
                {
                    Log.Error($"App {target.AppID} has no depots with a manifest on \"{target.Branch}\", see \"{Help.ExeName} depots {target.AppID}\".");
                    run.Fail($"{target.Source} (no depots)");
                }
                else
                {
                    Log.Info($"Target {target.Source}: {allJobs.Count} depots of app {target.AppID} with a manifest on \"{target.Branch}\".");
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

            var manifestBranch = target.Branch;
            var latestID = AppInfo.GetManifestIdForBranch(depotInfo, ref manifestBranch);

            if (latestID == 0)
            {
                var fromApp = depotInfo["depotfromapp"].Value;
                Log.Error(fromApp != null
                    ? $"Depot {target.DepotID} is shared from app {fromApp}, use {fromApp}/{target.DepotID} as the target instead."
                    : $"Depot {target.DepotID} has no manifest on branch \"{target.Branch}\" (it may be a DLC or another branch's depot), see \"{Help.ExeName} depots {target.AppID}\".");
                run.Fail($"{target.Source} (no manifest)");
                continue;
            }

            AddJob(target, target.DepotID.Value, latestID, manifestBranch, optional: false);
        }
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

        foreach (var file in files.OrderBy(f => run.Jobs.IndexOf(f.Job)))
        {
            if (!directories.TryGetValue(file.File, out var directory))
            {
                continue;
            }

            var dirFileName = directory.Path;

#pragma warning disable CA2000 // Disposed by the caller
            if (VpkReader.TryRead(file.Job, file.Path, dirFileName) is not { } package)
#pragma warning restore CA2000
            {
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
        foreach (var (source, jobs) in run.AllTargets)
        {
            if (jobs.All(static j => j.Manifest == null))
            {
                var noAccess = jobs.All(static j => j.NoAccess);
                var what = jobs[0].ItemID != null ? "items" : "depots";

                Log.Error(noAccess
                    ? $"None of the {jobs.Count} {what} of target {source} could be downloaded; this account likely has no access to them."
                    : $"None of the {jobs.Count} {what} of target {source} could be downloaded; see the errors above.");
                run.Fail(noAccess ? $"{source} (no access)" : source, noAccess);
            }
        }

        // Patterns can only be judged against manifests that were downloaded
        var unmatched = run.Matches.Count > 0 ? run.MatchCounts.Where(static p => p.Value == 0).Select(static p => p.Key).ToList() : [];
        var skipped = run.Jobs.Where(static j => j.Optional && j.NoAccess).Select(static j => j.ItemID != null ? j.ToString() : j.DepotID.ToString(CultureInfo.InvariantCulture)).ToList();

        foreach (var pattern in unmatched)
        {
            Log.Error($"Pattern \"{pattern}\" matched nothing in any target. Check the path with ls; the file may be in another depot.");
        }

        // One closing line, so the outcome is clear without reading the whole log
        var notes = new List<string>();

        if (skipped.Count > 0)
        {
            notes.Add($"skipped without access: {Log.JoinFirst(skipped)}");
        }

        // A target without a match is fine, like a build from before the file existed
        var withoutMatches = run.Matches.Where(static m => m.Value.Count == 0 && !m.Key.Optional).Select(static m => m.Key.ToString()).ToList();

        if (withoutMatches.Count > 0)
        {
            notes.Add($"nothing matched in {Log.JoinFirst(withoutMatches)}");
        }

        Log.Info($"Done: {run.Rows} {(run.HideRows ? "files" : "rows")} from {run.Matches.Count} of {run.Jobs.Count} manifests{(notes.Count > 0 ? $" ({string.Join("; ", notes)})" : "")}.{(downloads ? $" Files are in {run.OutputRoot}" : "")}");

        if (run.FailedTargets.Count > 0)
        {
            Log.Error($"Failed targets: {string.Join(", ", run.FailedTargets)}. They have no rows on stdout because they failed, not because their files don't exist; see the errors above.");

            var usernames = AccountStore.GetUsernames();

            if (usernames.Count > 1)
            {
                Log.Info($"If another saved login owns this content, pick it with --username ({string.Join(", ", usernames)}).");
            }
        }

        return ExitCodes.Report(run.Failed, run.FailedOtherwise, run.Session.FailureExitCode, unmatched.Count > 0);
    }
}
