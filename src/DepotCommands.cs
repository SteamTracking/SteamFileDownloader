using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;
using ValvePak;

namespace SteamFileDownloader;

/// <summary>
/// Options shared by the get, ls and depots commands.
/// </summary>
internal sealed record DepotOptions(
    uint App,
    string? Output = null,
    string Branch = "public",
    bool Anonymous = false,
    string? Username = null,
    bool AllFiles = false,
    bool Json = false);

/// <summary>
/// A parsed get, ls or depots command line.
/// </summary>
internal sealed record DepotArguments(
    DepotOptions Options,
    List<string> Targets,
    List<string> Patterns);

/// <summary>
/// The get, ls and depots commands: many depot manifests over one Steam login, with results on stdout.
/// </summary>
internal static partial class DepotCommands
{
    // DepotID null means every depot of the app, ManifestID null means the latest on the branch
    private sealed record Target(
        string Source,
        uint AppID,
        uint? DepotID,
        ulong? ManifestID,
        string Branch);

    // A matching manifest file with its normalized path. Requested is false for directory VPKs only needed for their entries,
    // and VpkPatterns are the VPK patterns that read entries from it
    private sealed record MatchedFile(
        DepotManifest.FileData File,
        string Path,
        bool Requested,
        IReadOnlyList<VpkPattern> VpkPatterns);

    private sealed class Run
    {
        public required FileFilter Filter;
        public required SteamSession Session;
        public required string OutputRoot;
        public List<ManifestJob> Jobs = [];
        public Dictionary<ManifestJob, List<MatchedFile>> Matches = [];
        public Dictionary<string, int> MatchCounts = [];
        public List<string> FailedTargets = [];

        // The jobs of each all target, whose depots fail quietly one by one, but not all of them
        public Dictionary<string, List<ManifestJob>> AllTargets = [];
        public int Rows;
        public bool Failed;

        // Whole depots are thousands of files, which nobody asked to see one by one
        public bool HideRows;

        public void Fail(string? target = null)
        {
            if (target != null)
            {
                FailedTargets.Add(target);
            }

            Failed = true;
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

    [GeneratedRegex(@"^(?:(?<app>\d+)/)?(?<depot>\d+|all)(?::(?<manifest>\d+|latest))?(?:@(?<branch>[^@/:]+))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TargetRegex();

    /// <summary>
    /// Parses "app [options] [targets] [-- patterns]" for get, ls and depots. Options can go anywhere before "--".
    /// Returns null after printing an error.
    /// </summary>
    public static DepotArguments? ParseArguments(string command, string[] args)
    {
        var parsed = ArgumentParser.Parse(command, args);

        if (parsed == null)
        {
            return null;
        }

        if (parsed.Positionals.Count == 0 || !uint.TryParse(parsed.Positionals[0], NumberStyles.None, CultureInfo.InvariantCulture, out var app))
        {
            Log.Error($"{command} needs an app id first, for example: {Help.ExeName} {command} 730 ... {Help.UsageHint(command)}");
            return null;
        }

        if (parsed.Options.ContainsKey("json") && parsed.Options.ContainsKey("branch"))
        {
            Log.Error("--json prints every branch, so it can't be combined with --branch.");
            return null;
        }

        var misplacedOption = parsed.Patterns.FirstOrDefault(static p => p.StartsWith("--", StringComparison.Ordinal));

        if (misplacedOption != null)
        {
            Log.Error($"\"{misplacedOption}\" is after --, which is only for patterns. Put options before --.");
            return null;
        }

        var options = new DepotOptions(
            app,
            parsed.Options.GetValueOrDefault("output"),
            parsed.Options.GetValueOrDefault("branch") ?? "public",
            parsed.Options.ContainsKey("anonymous"),
            parsed.Options.GetValueOrDefault("username"),
            parsed.Options.ContainsKey("all-files"),
            parsed.Options.ContainsKey("json"));

        return new DepotArguments(options, parsed.Positionals[1..], parsed.Patterns);
    }

    public static async Task<int> GetAsync(DepotArguments arguments, CancellationToken cancellationToken)
    {
        // Whole depots can be tens of gigabytes, so downloading every file has to be asked for
        if (arguments.Patterns.Count == 0 && !arguments.Options.AllFiles)
        {
            Log.Error($"get needs patterns after --, like: {Help.ExeName} get 730 2347771:latest -- server.dll. Use ls to see a manifest's files, or --all-files to download every file.");
            return ExitCodes.Failed;
        }

        if (arguments.Patterns.Count > 0 && arguments.Options.AllFiles)
        {
            Log.Error("--all-files downloads every file, so it can't be combined with patterns.");
            return ExitCodes.Failed;
        }

        var (run, exitCode) = await PrepareAsync(arguments, cancellationToken);

        if (run == null)
        {
            return exitCode;
        }

        using var session = run.Session;
        using var downloader = new FileDownloader(session, cancellationToken);

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

        try
        {
            // Directories extract in parallel, the shared chunk limit keeps the total in check
            var results = await Task.WhenAll(directories.Select(d => VpkReader.ExtractAsync(downloader, d.Job, d.DirFileName, d.Package, d.Matches)));

            for (var i = 0; i < directories.Count; i++)
            {
                if (!results[i].Success)
                {
                    run.Fail();
                }

                foreach (var path in results[i].Paths.Order(StringComparer.Ordinal))
                {
                    run.WriteRow(directories[i].Job, path);
                }
            }
        }
        finally
        {
            foreach (var directory in directories)
            {
                directory.Package.Dispose();
            }
        }

        return Finish(run, downloads: true);
    }

    public static async Task<int> ListAsync(DepotArguments arguments, CancellationToken cancellationToken)
    {
        var (run, exitCode) = await PrepareAsync(arguments, cancellationToken);

        if (run == null)
        {
            return exitCode;
        }

        using var session = run.Session;

        foreach (var (job, files) in run.Matches)
        {
            foreach (var file in files.Where(static f => f.Requested).OrderBy(static f => f.Path, StringComparer.Ordinal))
            {
                run.WriteRow(job, Convert.ToHexStringLower(file.File.FileHash), file.File.TotalSize.ToString(CultureInfo.InvariantCulture), file.Path);
            }
        }

        // Directory files have to be downloaded to read their entries
        using var downloader = new FileDownloader(session, cancellationToken);
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

        return Finish(run, downloads: false);
    }

    /// <summary>
    /// Lists the depots of apps with their manifest on a branch, and the apps' branches on stderr.
    /// With --json, prints everything Steam has on the apps' depots and branches instead.
    /// </summary>
    public static async Task<int> ListDepotsAsync(DepotArguments arguments, CancellationToken cancellationToken)
    {
        var options = arguments.Options;
        var appIDs = new List<uint> { options.App };

        // The other arguments are more apps
        foreach (var arg in arguments.Targets)
        {
            if (!uint.TryParse(arg, NumberStyles.None, CultureInfo.InvariantCulture, out var appID))
            {
                Log.Error($"Unexpected argument \"{arg}\", depots takes app ids. {Help.UsageHint("depots")}");
                return ExitCodes.Failed;
            }

            if (!appIDs.Contains(appID))
            {
                appIDs.Add(appID);
            }
        }

        var (loggedOn, logOnExitCode) = await LogOnAsync(options, cancellationToken);

        if (loggedOn == null)
        {
            return logOnExitCode;
        }

        using var session = loggedOn;

        var appInfos = await session.GetAppInfosAsync(appIDs);
        session.Disconnect();

        // Apps without info were already reported
        var depotsByApp = appIDs.Where(appInfos.ContainsKey).Select(appID => (AppID: appID, Depots: appInfos[appID]["depots"])).ToList();
        var success = depotsByApp.Count == appIDs.Count;

        if (options.Json)
        {
            Log.Result(ToJson(depotsByApp));
        }
        else
        {
            foreach (var (appID, depots) in depotsByApp)
            {
                success &= ListAppDepots(appID, depots, options.Branch);
            }
        }

        return success ? ExitCodes.Ok : ExitCodes.Failed;
    }

    // Returns false after an error
    private static bool ListAppDepots(uint appID, KeyValue depots, string branchName)
    {
        if (!SteamSession.CheckBranch(depots, appID, branchName))
        {
            return false;
        }

        var branches = depots["branches"].Children
            .Select(static b => $"{b.Name} (build {b["buildid"].Value}{(b["pwdrequired"].AsBoolean() ? ", password" : "")}{FormatTime(b["timeupdated"].AsLong())})");

        Log.Info("");
        Log.Info($"App {appID} branches: {string.Join(", ", branches)}");

        if (!SteamSession.GetDepots(depots).Any())
        {
            Log.Error($"App {appID} has no depots. DLC and tools often keep their files in depots of their parent app.");
            return false;
        }

        foreach (var (depotID, depot) in SteamSession.GetDepots(depots))
        {
            var branch = branchName;
            var manifestID = SteamSession.GetManifestIdForBranch(depot, ref branch);
            var config = depot["config"];
            var notes = new List<string>();

            AddNote("name", depot["name"].Value);
            AddNote("os", config["oslist"].Value);
            AddNote("arch", config["osarch"].Value);
            AddNote("language", config["language"].Value);
            AddNote("dlc", depot["dlcappid"].Value);
            AddNote("shared from app", depot["depotfromapp"].Value);

            if (branch != branchName)
            {
                notes.Add($"manifest from {branch}");
            }

            Log.Result($"{appID}\t{depotID}\t{(manifestID == 0 ? "-" : manifestID.ToString(CultureInfo.InvariantCulture))}\t{depot["maxsize"].Value ?? "-"}\t{string.Join(", ", notes)}");

            void AddNote(string name, string? value)
            {
                if (!string.IsNullOrEmpty(value))
                {
                    notes.Add($"{name} {value}");
                }
            }
        }

        return true;

        static string FormatTime(long unixTime) => unixTime > 0 ? $", {DateTimeOffset.FromUnixTimeSeconds(unixTime):yyyy-MM-dd}" : "";
    }

    // An object per app with its whole depots section. Values are strings as Steam sends them, keys with children are objects
    private static string ToJson(List<(uint AppID, KeyValue Depots)> depotsByApp)
    {
        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();

            foreach (var (appID, depots) in depotsByApp)
            {
                writer.WritePropertyName(appID.ToString(CultureInfo.InvariantCulture));
                WriteObject(writer, depots);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());

        static void WriteObject(Utf8JsonWriter writer, KeyValue keyValue)
        {
            writer.WriteStartObject();

            foreach (var child in keyValue.Children)
            {
                if (child.Value == null)
                {
                    writer.WritePropertyName(child.Name ?? "");
                    WriteObject(writer, child);
                }
                else
                {
                    writer.WriteString(child.Name ?? "", child.Value);
                }
            }

            writer.WriteEndObject();
        }
    }

    /// <summary>
    /// Parses targets and patterns, logs in, and gets every manifest. Returns a null run with the exit code after printing an error.
    /// </summary>
    private static async Task<(Run? Run, int ExitCode)> PrepareAsync(DepotArguments arguments, CancellationToken cancellationToken)
    {
        var options = arguments.Options;
        var targets = new List<Target>();

        foreach (var arg in arguments.Targets)
        {
            var target = ParseTarget(arg, options);

            if (target == null)
            {
                Log.Error($"Invalid target \"{arg}\", expected [<app>/]<depot>[:<manifest>|:latest][@<branch>] or all, like 2347771:8344780363095656278 or 570/373303@beta. Patterns go after --.");
                return (null, ExitCodes.Failed);
            }

            targets.Add(target);
        }

        if (targets.Count == 0)
        {
            Log.Error($"No targets, expected <app> <target>..., like: {Help.ExeName} ls 730 2347771:latest");
            return (null, ExitCodes.Failed);
        }

        FileFilter filter;

        try
        {
            filter = FileFilter.FromPatterns(arguments.Patterns);
        }
        catch (ArgumentException e)
        {
            Log.Error($"Invalid regex pattern: {e.Message}");
            return (null, ExitCodes.Failed);
        }

        var (session, logOnExitCode) = await LogOnAsync(options, cancellationToken);

        if (session == null)
        {
            return (null, logOnExitCode);
        }

        var run = new Run
        {
            Filter = filter,
            Session = session,
            OutputRoot = Path.GetFullPath(options.Output ?? "depots"),
            HideRows = options.AllFiles,
        };

        // Manifests come from the CDN too, so its server list loads while the app info does
        _ = session.EnsureCdnServersAsync();

        await ResolveJobsAsync(run, targets);

        await session.DownloadManifestsAsync(run.Jobs);

        // Depots of all targets that the account can't access are expected, errors are not
        foreach (var job in run.Jobs.Where(static j => j.Manifest == null && !(j.Optional && j.NoAccess)))
        {
            run.Fail(job.ToString());
        }

        // Files come from the CDN, which does not need the Steam connection
        session.Disconnect();

        MatchFiles(run);

        return (run, ExitCodes.Ok);
    }

    // Returns a logged on session, or null with the exit code after explaining why
    private static async Task<(SteamSession? Session, int ExitCode)> LogOnAsync(DepotOptions options, CancellationToken cancellationToken)
    {
        var credentials = options.Anonymous ? LogOnCredentials.Anonymous : AccountStore.LoadCredentials(options.Username);

        if (credentials == null)
        {
            return (null, ExitCodes.NoAccess);
        }

        var session = new SteamSession(cancellationToken) { CacheDepotKeys = true };
        var exitCode = await session.LogOnAsync(credentials);

        if (exitCode != ExitCodes.Ok)
        {
            session.Dispose();
            return (null, exitCode);
        }

        return (session, ExitCodes.Ok);
    }

    private static async Task ResolveJobsAsync(Run run, List<Target> targets)
    {
        var appInfos = await run.Session.GetAppInfosAsync([.. targets.Where(static t => t.DepotID == null || t.ManifestID == null).Select(static t => t.AppID).Distinct()]);

        var jobs = new Dictionary<(uint, ulong), ManifestJob>();

        ManifestJob AddJob(Target target, uint depotID, ulong manifestID, string branch, bool optional)
        {
            if (jobs.TryGetValue((depotID, manifestID), out var existing))
            {
                // Also asked for by an explicit target, so its failure counts
                existing.Optional &= optional;
                return existing;
            }

            var depotFolder = Path.Combine(run.OutputRoot, depotID.ToString(CultureInfo.InvariantCulture));
            var outputFolder = Path.Combine(depotFolder, manifestID.ToString(CultureInfo.InvariantCulture));

            var job = new ManifestJob
            {
                AppID = target.AppID,
                DepotID = depotID,
                ManifestID = manifestID,
                Branch = branch,
                OutputFolder = outputFolder,
                Optional = optional,

                // Earlier runs of other manifests of this depot may already have identical files, recently written ones first
                ReuseFolders = Directory.Exists(depotFolder)
                    ? [.. new DirectoryInfo(depotFolder).GetDirectories()
                        .Where(d => !string.Equals(d.FullName, outputFolder, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(static d => d.LastWriteTimeUtc)
                        .Select(static d => d.FullName)]
                    : [],
            };

            jobs[(depotID, manifestID)] = job;
            run.Jobs.Add(job);

            return job;
        }

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

            if (!SteamSession.CheckBranch(depots, target.AppID, target.Branch))
            {
                run.Fail($"{target.Source} (branch {target.Branch})");
                continue;
            }

            if (target.DepotID == null)
            {
                var allJobs = new List<ManifestJob>();

                // Shared depots are listed under the app that owns them
                foreach (var (depotID, depot) in SteamSession.GetDepots(depots).Where(static d => d.Depot["depotfromapp"].Value == null))
                {
                    var branch = target.Branch;
                    var manifestID = SteamSession.GetManifestIdForBranch(depot, ref branch);

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
                    Log.Info($"Target {target.Source}: {allJobs.Count} depots of app {target.AppID} with a manifest on \"{target.Branch}\"");
                    run.AllTargets[target.Source] = allJobs;
                }

                continue;
            }

            var depotInfo = depots[target.DepotID.Value.ToString(CultureInfo.InvariantCulture)];

            if (depotInfo == KeyValue.Invalid)
            {
                Log.Error($"Depot {target.DepotID} is not in app {target.AppID}, so its latest manifest is unknown. Check with \"{Help.ExeName} depots {target.AppID}\", or give <depot>:<manifest>.");
                run.Fail($"{target.Source} (not in app {target.AppID})");
                continue;
            }

            var manifestBranch = target.Branch;
            var latestID = SteamSession.GetManifestIdForBranch(depotInfo, ref manifestBranch);

            if (latestID == 0)
            {
                var fromApp = depotInfo["depotfromapp"].Value;
                Log.Error(fromApp != null
                    ? $"Depot {target.DepotID} is shared from app {fromApp}, use {fromApp}/{target.DepotID} as the target instead."
                    : $"Depot {target.DepotID} has no manifest on branch \"{target.Branch}\" (it may be a DLC or another branch's depot), see \"{Help.ExeName} depots {target.AppID}\".");
                run.Fail($"{target.Source} (no manifest)");
                continue;
            }

            Log.Info($"Depot {target.DepotID}: latest manifest on \"{manifestBranch}\" is {latestID}");
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

            foreach (var file in job.Manifest!.Files!)
            {
                if (FileDownloader.IsDirectory(file))
                {
                    continue;
                }

                var path = FileFilter.NormalizePath(file.FileName);
                var requested = listAll;

                foreach (var pattern in filter.Patterns)
                {
                    if (pattern.Regex.IsMatch(path))
                    {
                        run.MatchCounts[pattern.Source]++;
                        requested = true;
                    }
                }

                var vpkPatterns = filter.GetVpkPatterns(path);

                if (requested || vpkPatterns.Count > 0)
                {
                    matches.Add(new MatchedFile(file, path, requested, vpkPatterns));
                }
            }

            if (matches.Count == 0 && !job.Optional)
            {
                Log.Info($"[{job}] No files matched");
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
                Log.Error($"None of the {jobs.Count} depots of {source} could be downloaded; this account likely has no access to them.");
                run.Fail($"{source} (no access)");
            }
        }

        // Patterns can only be judged against manifests that were downloaded
        var unmatched = run.Matches.Count > 0 ? run.MatchCounts.Where(static p => p.Value == 0).Select(static p => p.Key).ToList() : [];

        foreach (var pattern in unmatched)
        {
            Log.Error($"Pattern \"{pattern}\" matched nothing in any target. Check the path with ls; the file may be in another depot.");
        }

        // One closing line, so the outcome is clear without reading the whole log
        var skipped = run.Jobs.Count(static j => j.Optional && j.NoAccess);

        Log.Info($"Done: {run.Rows} {(run.HideRows ? "files" : "results")} from {run.Matches.Count} of {run.Jobs.Count} manifests{(skipped > 0 ? $" ({skipped} depots without access skipped)" : "")}.{(downloads ? $" Files are in {run.OutputRoot}" : "")}");

        if (run.FailedTargets.Count > 0)
        {
            Log.Error($"Failed targets: {string.Join(", ", run.FailedTargets)}. They have no rows on stdout because they failed, not because their files don't exist. See the errors above.");

            var usernames = AccountStore.GetUsernames();

            if (usernames.Count > 1)
            {
                Log.Info($"If another account owns this content, switch with --username ({string.Join(", ", usernames)}).");
            }
        }

        if (run.Failed && run.Session.FailureExitCode != ExitCodes.Failed)
        {
            Log.Error($"Exit code {run.Session.FailureExitCode}: logging in again after a lost connection failed (see above), so stdout is partial.");
            return run.Session.FailureExitCode;
        }

        if (run.Failed)
        {
            Log.Error($"Exit code 1: some targets or files failed, so stdout is partial{(unmatched.Count > 0 ? ", and a pattern matched nothing" : "")}.");
            return ExitCodes.Failed;
        }

        if (unmatched.Count > 0)
        {
            Log.Error("Exit code 2: a pattern matched nothing.");
            return ExitCodes.NoMatch;
        }

        return ExitCodes.Ok;
    }

    private static Target? ParseTarget(string arg, DepotOptions options)
    {
        var match = TargetRegex().Match(arg);

        if (!match.Success)
        {
            return null;
        }

        var appID = options.App;

        if (match.Groups["app"].Success && !uint.TryParse(match.Groups["app"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out appID))
        {
            return null;
        }

        uint? depotID = null;
        ulong? manifestID = null;
        var depot = match.Groups["depot"].Value;
        var manifest = match.Groups["manifest"].Value;

        if (!depot.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            if (!uint.TryParse(depot, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedDepot))
            {
                return null;
            }

            depotID = parsedDepot;
        }

        if (manifest.Length > 0 && !manifest.Equals("latest", StringComparison.OrdinalIgnoreCase))
        {
            // A manifest id only makes sense for one depot
            if (depotID == null || !ulong.TryParse(manifest, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedManifest))
            {
                return null;
            }

            manifestID = parsedManifest;
        }

        var branch = match.Groups["branch"].Success ? match.Groups["branch"].Value : options.Branch;

        return new Target(arg, appID, depotID, manifestID, branch);
    }
}
