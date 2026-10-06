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
/// Downloads the files listed in files.json from the latest manifests of a branch, used by GameTracking.
/// </summary>
internal static class TrackCommand
{
    public static async Task<int> RunAsync(
        uint appID,
        string output,
        string? username,
        string? password,
        string branch,
        bool saveManifest,
        bool saveAllManifests,
        CancellationToken cancellationToken)
    {
        saveManifest |= saveAllManifests;

        var outputPath = Path.GetFullPath(output);

        // Load the file mapping before logging in, so invalid configuration fails fast
        Dictionary<uint, FileFilter> filters;

        try
        {
            filters = FileFilter.LoadFilesJson("files.json");
        }
        catch (Exception e) when (e is IOException or JsonException or ArgumentException)
        {
            Log.Error($"Failed to load files.json from {Environment.CurrentDirectory}: {e.Message} The get and ls commands don't need it, see \"{Help.ExeName} --help\".");
            return ExitCodes.Failed;
        }

        var credentials = GetCredentials(username, password);

        if (credentials == null)
        {
            return ExitCodes.NoAccess;
        }

        using var session = new SteamSession(cancellationToken);
        var logOnExitCode = await session.LogOnAsync(credentials);

        if (logOnExitCode != ExitCodes.Ok)
        {
            return logOnExitCode;
        }

        // Manifests come from the CDN too, so its server list loads while the app info does
        _ = session.EnsureCdnServersAsync();

        var appInfo = await session.GetAppInfoAsync(appID);

        if (appInfo == null)
        {
            return ExitCodes.Failed;
        }

        var depots = appInfo["depots"];

        if (!SteamSession.GetDepots(depots).Any())
        {
            Log.Error($"App {appID} has no depots.");
            return ExitCodes.Failed;
        }

        // Falling back to public would save public files under this branch's build id
        if (!SteamSession.CheckBranch(depots, appID, branch))
        {
            return ExitCodes.Failed;
        }

        // Log build ID for this branch
        int? buildId = null;

        if (int.TryParse(depots["branches"][branch]["buildid"].Value, out var parsedBuildId))
        {
            buildId = parsedBuildId;
            Log.Info($"Branch \"{branch}\": build {parsedBuildId}");
        }

        // Find the tracked depots (in files.json), or all of them when saving all manifests
        var manifestJobs = new List<ManifestJob>();

        foreach (var (depotID, depot) in SteamSession.GetDepots(depots))
        {
            var filter = filters.GetValueOrDefault(depotID);

            if (filter == null && !saveAllManifests)
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

            var manifestBranch = branch;
            var manifestID = SteamSession.GetManifestIdForBranch(depot, ref manifestBranch);

            // Depots like DLC and other branches often have no manifest, which is only a problem for tracked ones
            if (manifestID == 0)
            {
                if (filter != null)
                {
                    Log.Warn($"No manifest found for depot {depotID} on branch \"{branch}\"");
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
            });

            Log.Info($"Found depot {depotID}: manifest {manifestID}{(filter != null ? "" : " (manifest only)")}");
        }

        foreach (var depotID in filters.Keys.Where(id => depots[id.ToString(CultureInfo.InvariantCulture)] == KeyValue.Invalid))
        {
            Log.Warn($"Depot {depotID} in files.json is not a depot of app {appID}.");
        }

        if (manifestJobs.All(static job => job.Optional))
        {
            Log.Error($"None of the depots in files.json have a manifest in app {appID} on branch \"{branch}\". Check the depot ids in files.json.");
            return ExitCodes.Failed;
        }

        await session.DownloadManifestsAsync(manifestJobs);

        var writeFailed = false;

        if (saveManifest)
        {
            foreach (var job in manifestJobs.Where(static job => job.Manifest != null))
            {
                writeFailed |= !TryWrite($"manifest of depot {job.DepotID}", () => ManifestWriter.DumpToTextFile(outputPath, job));
            }
        }

        // Done with Steam, disconnect before downloading files from CDN
        Log.Info("");
        Log.Info("Disconnecting from Steam...");
        session.Disconnect();

        var downloadTimer = Stopwatch.StartNew();
        // The output folder is a git repository, so partial files must not land in it
        using var fileDownloader = new FileDownloader(session, cancellationToken) { TempFolder = Path.GetTempPath() };
        var allFiles = CollectFiles(manifestJobs);
        var (allSucceeded, _) = await fileDownloader.DownloadAllFiles(allFiles);
        allSucceeded &= !writeFailed;

        // A tracked depot without its manifest is missing all of its files
        var failedDepots = manifestJobs.Where(static job => !job.Optional && job.Manifest == null).Select(static job => job.DepotID).ToList();

        if (failedDepots.Count > 0)
        {
            Log.Error($"Failed to get the manifests of tracked depots {string.Join(", ", failedDepots)}, so their files were not downloaded.");
            allSucceeded = false;
        }

        if (allSucceeded && buildId.HasValue)
        {
            allSucceeded = TryWrite("steam_buildid.txt", () => File.WriteAllText(Path.Combine(outputPath, "steam_buildid.txt"), buildId.Value.ToString(CultureInfo.InvariantCulture)));
        }

        Log.Info("");
        Log.Info($"Done in {downloadTimer.Elapsed:hh\\:mm\\:ss}.");

        if (allSucceeded)
        {
            return ExitCodes.Ok;
        }

        return session.FailureExitCode;
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
            Log.Error($"Failed to write the {what}: {e.Message}");
            return false;
        }
    }

    private static List<FileJob> CollectFiles(List<ManifestJob> jobs)
    {
        var fileJobs = new List<FileJob>();

        foreach (var job in jobs.Where(static job => job.Filter != null && job.Manifest != null))
        {
            var hasPakDir = false;

            foreach (var file in job.Manifest!.Files!)
            {
                if (!FileDownloader.IsDirectory(file) && job.Filter!.IsMatch(FileFilter.NormalizePath(file.FileName)))
                {
                    fileJobs.Add(new FileJob(job, file));
                    hasPakDir |= Path.GetFileName(file.FileName) == "pak01_dir.vpk";
                }
            }

            // The archives are found through the directory file
            if (job.Filter!.PakExtensions.Length > 0 && !hasPakDir)
            {
                Log.Warn($"Depot {job.DepotID} in files.json has a \"vpk:\" entry but no pak01_dir.vpk, so no archives are downloaded. List the pak01_dir.vpk too.");
            }
        }

        return fileJobs;
    }

    // GameTracking passes a username and password, locally a saved login is enough
    private static LogOnCredentials? GetCredentials(string? username, string? password)
    {
        if (username == "anonymous")
        {
            return LogOnCredentials.Anonymous;
        }

        if (username != null && password != null)
        {
            return new LogOnCredentials(username, password);
        }

        return AccountStore.LoadCredentials(username);
    }
}
