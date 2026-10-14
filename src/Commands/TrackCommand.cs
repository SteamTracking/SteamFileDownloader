using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;

namespace SteamFileDownloader;

/// <summary>
/// A parsed track command line.
/// </summary>
internal sealed record TrackArguments(
    uint AppID,
    string Output,
    string? Username,
    string? Password,
    string Branch,
    string? ManifestsFolder,
    bool SaveManifest,
    bool SaveAllManifests);

/// <summary>
/// Downloads the files listed in files.json from the latest manifests of a branch, used by GameTracking.
/// </summary>
internal static class TrackCommand
{
    /// <summary>
    /// Parses the options of track. Returns null after printing an error.
    /// </summary>
    public static TrackArguments? ParseArguments(string[] args)
    {
        var parsed = ArgumentParser.Parse("track", args);

        if (parsed == null)
        {
            return null;
        }

        if (parsed.Positionals.Count > 0 && uint.TryParse(parsed.Positionals[0], NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            Log.Error($"track takes the app as --appid, like: {Help.ExeName} track --appid {parsed.Positionals[0]} --output . {Help.UsageHint("track")}");
            return null;
        }

        if (!ArgumentParser.NoPositionals(parsed, "track"))
        {
            return null;
        }

        var options = parsed.Options;

        if (!options.TryGetValue("output", out var output))
        {
            Log.Error($"track needs --output <dir>, like: {Help.ExeName} track --appid 730 --output . {Help.UsageHint("track")}");
            return null;
        }

        if (!options.TryGetValue("appid", out var appText))
        {
            Log.Error($"track needs --appid <app>, like: {Help.ExeName} track --appid 730 --output . {Help.UsageHint("track")}");
            return null;
        }

        if (!uint.TryParse(appText, NumberStyles.None, CultureInfo.InvariantCulture, out var appID))
        {
            Log.Error($"--appid needs a numeric app id, got \"{appText}\". {Help.UsageHint("track")}");
            return null;
        }

        if (options.ContainsKey("password") && !options.ContainsKey("username"))
        {
            Log.Error($"--password needs --username. Without both, the last saved login is used. {Help.UsageHint("track")}");
            return null;
        }

        if (options.ContainsKey("password") && LogOnCredentials.IsAnonymousName(options["username"]))
        {
            Log.Error($"--password can't be combined with --username anonymous. {Help.UsageHint("track")}");
            return null;
        }

        return new TrackArguments(
            appID,
            output,
            options.GetValueOrDefault("username"),
            options.GetValueOrDefault("password"),
            options.GetValueOrDefault("branch") ?? "public",
            options.GetValueOrDefault("manifests"),
            options.ContainsKey("save-manifest"),
            options.ContainsKey("save-all-manifests"));
    }

    public static async Task<int> RunAsync(TrackArguments arguments, CancellationToken cancellationToken)
    {
        var (appID, output, username, password, branch, manifestsFolder, saveManifest, saveAllManifests) = arguments;
        saveManifest |= saveAllManifests;

        var outputPath = Path.GetFullPath(output);

        // Load the file mapping before logging in, so invalid configuration fails fast
        Dictionary<uint, FileFilter> filters;

        try
        {
            filters = FileFilter.LoadFilesJson("files.json");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or JsonException or ArgumentException)
        {
            Log.Error($"Failed to load files.json from {Environment.CurrentDirectory}: {e.Message} The get and ls commands don't need it, see \"{Help.ExeName} --help\".");
            return ExitCodes.Failed;
        }

        // Manifest ids of an older build, from the manifests/ folder of a previous run
        Dictionary<uint, ulong>? manifestIds = null;

        if (manifestsFolder != null)
        {
            try
            {
                manifestIds = ManifestWriter.ReadManifestIds(manifestsFolder);
            }
            catch (Exception e) when (OutputFiles.IsFileError(e))
            {
                Log.Error($"Failed to read the manifests in {manifestsFolder}: {e.Message}");
                return ExitCodes.Failed;
            }
        }

        var (loggedOn, logOnExitCode) = await SteamSession.CreateLoggedOnAsync(GetCredentials(username, password), cacheDepotKeys: false, takesTargets: false, cancellationToken);

        if (loggedOn == null)
        {
            return logOnExitCode;
        }

        using var session = loggedOn;

        // Manifests come from the CDN too, so its server list loads while the app info does
        _ = session.EnsureCdnServersAsync();

        var appInfo = await session.GetAppInfoAsync(appID);

        if (appInfo == null)
        {
            return ExitCodes.Failed;
        }

        var depots = appInfo["depots"];

        if (!AppInfo.GetDepots(depots).Any())
        {
            Log.Error($"App {appID} has no depots.");
            return ExitCodes.Failed;
        }

        // Falling back to public would save public files under this branch's build id
        if (!AppInfo.CheckBranch(depots, appID, branch))
        {
            return ExitCodes.Failed;
        }

        // Log build ID for this branch, which an older build's manifests don't have
        int? buildId = null;
        var manifestSource = manifestIds != null ? manifestsFolder : $"app {appID} on branch \"{branch}\"";

        if (manifestIds != null)
        {
            Log.Info($"Using the manifest ids in {manifestsFolder}, so steam_buildid.txt won't be written.");
        }
        else if (int.TryParse(depots["branches"][branch]["buildid"].Value, out var parsedBuildId))
        {
            buildId = parsedBuildId;
            Log.Info($"Branch \"{branch}\" is on build {parsedBuildId}.");
        }

        // Archives of VPKs can be in other depots than their directory file, so VPK entries need every manifest
        var allManifests = saveAllManifests || filters.Values.Any(static f => f.VpkPatterns.Count > 0);

        // Find the tracked depots (in files.json), or all of them when saving all manifests or for VPK entries
        var manifestJobs = new List<ManifestJob>();
        var publicFallbacks = new PublicFallbacks();

        foreach (var (depotID, depot) in AppInfo.GetDepots(depots))
        {
            var filter = filters.GetValueOrDefault(depotID);

            if (filter == null && !allManifests)
            {
                continue;
            }

            // Shared depots are tracked in the app that owns them
            if (depot["depotfromapp"].Value is { } fromApp)
            {
                if (filter != null)
                {
                    Log.Warn($"Depot {depotID} in files.json is shared from app {fromApp}, track it there instead.");
                }

                continue;
            }

            var (manifestID, manifestBranch) = manifestIds != null
                ? (manifestIds.GetValueOrDefault(depotID), branch)
                : publicFallbacks.Resolve(appID, depotID, depot, branch);

            // Depots like DLC and other branches often have no manifest, which is only a problem for tracked ones
            if (manifestID == 0)
            {
                if (filter != null)
                {
                    Log.Warn($"Depot {depotID} in files.json has no manifest in {manifestSource}, so it is skipped.");
                }

                continue;
            }

            manifestJobs.Add(new ManifestJob
            {
                AppID = appID,
                DepotID = depotID,
                ManifestID = manifestID,
                Branch = manifestBranch,
                OutputFolder = outputPath,
                Filter = filter,
                Optional = filter == null,
                FailureIsFine = filter == null,
            });
        }

        publicFallbacks.Log();

        foreach (var depotID in filters.Keys.Where(id => depots[id.ToString(CultureInfo.InvariantCulture)] == KeyValue.Invalid))
        {
            Log.Warn($"Depot {depotID} in files.json is not a depot of app {appID}.");
        }

        if (manifestJobs.All(static job => job.Optional))
        {
            Log.Error($"None of the depots in files.json have a manifest in {manifestSource}. Check the depot ids in files.json.");
            return ExitCodes.Failed;
        }

        await session.DownloadManifestsAsync(manifestJobs);

        var writeFailed = false;

        if (saveManifest)
        {
            foreach (var job in manifestJobs.Where(job => job.Manifest != null && (job.Filter != null || saveAllManifests)))
            {
                writeFailed |= !TryWrite($"the manifest of depot {job.DepotID}", () => ManifestWriter.DumpToTextFile(outputPath, job));
            }
        }

        // Done with Steam, disconnect before downloading files from CDN
        session.Disconnect();

        var downloadTimer = Stopwatch.StartNew();
        // Whole files download into the temp folder, since the output folder is a git repository. Partial VPK archives
        // are written in the output folder on purpose: *.vpk is gitignored there, and a rerun checks their chunks again
        using var fileDownloader = new FileDownloader(session, cancellationToken) { TempFolder = Path.GetTempPath() };
        var (allFiles, vpkDirectories) = CollectFiles(manifestJobs);
        var (allSucceeded, files) = await fileDownloader.DownloadAllFiles(allFiles);
        allSucceeded &= await DownloadVpkEntries(fileDownloader, manifestJobs, files, vpkDirectories);
        allSucceeded &= !writeFailed;

        // A tracked depot without its manifest is missing all of its files
        var failedDepots = manifestJobs.Where(static job => !job.Optional && job.Manifest == null).Select(static job => job.DepotID).ToList();
        allSucceeded &= failedDepots.Count == 0;

        if (allSucceeded && buildId.HasValue)
        {
            allSucceeded = TryWrite("steam_buildid.txt", () => File.WriteAllText(Path.Combine(outputPath, "steam_buildid.txt"), buildId.Value.ToString(CultureInfo.InvariantCulture)));
        }

        var elapsed = downloadTimer.Elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);

        if (allSucceeded)
        {
            Log.Info($"Done in {elapsed}.");
            return ExitCodes.Ok;
        }

        // Running again right away would hit the same rate limit or rejected login
        if (session.FailureExitCode != ExitCodes.Failed)
        {
            Log.Error($"Failed (exit {session.FailureExitCode}) after {elapsed}: logging in again after a lost connection failed (see above), so some manifests and their files are missing.");
            return session.FailureExitCode;
        }

        Log.Error(DescribeFailure(elapsed, failedDepots, buildId.HasValue, writeFailed));

        return ExitCodes.Failed;
    }

    /// <summary>
    /// The closing error of a failed run. Failed files were already listed with how to retry them, but tracked depots
    /// without a manifest and failed writes were not.
    /// </summary>
    internal static string DescribeFailure(string elapsed, List<uint> failedDepots, bool hasBuildId, bool writeFailed)
    {
        var depots = Log.JoinFirst([.. failedDepots.Select(static d => d.ToString(CultureInfo.InvariantCulture))]);
        var what = failedDepots.Count > 0
            ? $"tracked {(failedDepots.Count == 1 ? $"depot {depots} has" : $"depots {depots} have")} no manifest"
            : "some manifests or files failed, see above";
        var buildIdNote = hasBuildId ? "; steam_buildid.txt not written" : "";
        var rerun = failedDepots.Count > 0 || writeFailed ? ". Rerun to retry" : "";

        return $"Failed (exit 1) after {elapsed}: {what}{buildIdNote}{rerun}.";
    }

    // Like a full disk or a file locked by another program
    private static bool TryWrite(string what, Action write)
    {
        try
        {
            write();
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Failed to write {what}: {e.Message}");
            return false;
        }
    }

    // The files to download, and the VPK patterns of the directory files among them
    internal static (List<FileJob> Files, Dictionary<DepotManifest.FileData, IReadOnlyList<VpkPattern>> VpkDirectories) CollectFiles(List<ManifestJob> jobs)
    {
        var fileJobs = new List<FileJob>();
        var vpkDirectories = new Dictionary<DepotManifest.FileData, IReadOnlyList<VpkPattern>>();

        foreach (var job in jobs.Where(static job => job.Filter != null && job.Manifest != null))
        {
            var filter = job.Filter!;
            var matchedDirs = new HashSet<VpkPattern>();
            var matchedPatterns = new HashSet<FilePattern>();

            foreach (var (file, path) in job.GetFiles())
            {
                var vpkPatterns = filter.GetVpkPatterns(path);

                // Directory files of VPK entries are downloaded whole, for VRF to read the entries with
                if (vpkPatterns.Count > 0)
                {
                    vpkDirectories.Add(file, vpkPatterns);
                    matchedDirs.UnionWith(vpkPatterns);
                }

                var matching = filter.GetMatchingPatterns(path);
                matchedPatterns.UnionWith(matching);

                if (vpkPatterns.Count > 0 || matching.Count > 0)
                {
                    fileJobs.Add(new FileJob(job, file));
                }
            }

            // Like a typo, a file that moved to another depot, or one that an older build doesn't have yet
            foreach (var pattern in filter.Patterns.Except(matchedPatterns))
            {
                Log.Warn($"\"{pattern.Source}\" in files.json matched no file in depot {job.DepotID}.");
            }

            foreach (var pattern in filter.VpkPatterns.Except(matchedDirs))
            {
                Log.Warn($"\"{pattern.Source}\" in files.json matched no _dir.vpk in depot {job.DepotID}.");
            }
        }

        return (fileJobs, vpkDirectories);
    }

    /// <summary>
    /// Downloads the chunks of the archives next to downloaded directory files that the files.json VPK entries are in,
    /// at their offsets in otherwise empty archives, so VRF reads those entries from the directory file like from a full VPK.
    /// Returns false when anything failed.
    /// </summary>
    private static async Task<bool> DownloadVpkEntries(
        FileDownloader fileDownloader,
        List<ManifestJob> jobs,
        List<DownloadedFile> files,
        Dictionary<DepotManifest.FileData, IReadOnlyList<VpkPattern>> vpkDirectories)
    {
        var success = true;
        var archives = new List<PartialFile>();

        // Patterns of the directory files that were read, ones that failed to download or read already logged an error
        var readPatterns = new HashSet<VpkPattern>();
        var matchedPatterns = new HashSet<VpkPattern>();

        foreach (var (job, file, filePath) in files.OrderBy(static f => f.File.FileName, StringComparer.Ordinal))
        {
            if (!vpkDirectories.TryGetValue(file, out var patterns))
            {
                continue;
            }

            var dirFileName = FileFilter.NormalizePath(file.FileName);
            using var package = VpkReader.TryRead(job, filePath, dirFileName);

            if (package == null)
            {
                success = false;
                continue;
            }

            var matches = VpkReader.ListEntries(package, patterns);
            readPatterns.UnionWith(patterns);
            matchedPatterns.UnionWith(matches.Select(static m => m.Pattern));

            var entries = matches.Select(static m => m.Entry).Distinct().ToList();
            var (found, dirArchives) = VpkReader.FindArchiveChunks(jobs, job, dirFileName, entries);
            success &= found;
            archives.AddRange(dirArchives);

            Log.Info($"[{job}] {dirFileName}: {entries.Count} entries in {dirArchives.Count} archives.");
        }

        // Like a typo, or entries that were moved or removed
        foreach (var pattern in readPatterns.Except(matchedPatterns))
        {
            Log.Warn($"\"{pattern.Source}\" in files.json matched no VPK entry.");
        }

        return await fileDownloader.DownloadPartialFiles(archives) && success;
    }

    // GameTracking passes a username and password, locally a saved login is enough
    private static LogOnCredentials? GetCredentials(string? username, string? password)
    {
        if (username != null && password != null)
        {
            return new LogOnCredentials(username, password);
        }

        return AccountStore.LoadCredentials(username);
    }
}
