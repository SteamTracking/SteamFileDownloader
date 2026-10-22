using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;

namespace SteamFileDownloader;

/// <summary>
/// The depots command: the depots and branches of apps, with results on stdout.
/// </summary>
internal static class DepotsCommand
{
    /// <summary>
    /// Lists the branches of apps with their build ids, and their depots with their manifest on a branch.
    /// </summary>
    public static async Task<int> ListDepotsAsync(DepotArguments arguments, CancellationToken cancellationToken)
    {
        var options = arguments.Options;
        var appIDs = new List<uint> { options.App!.Value };

        // The other arguments are more apps
        foreach (var arg in arguments.Targets)
        {
            if (!uint.TryParse(arg, NumberStyles.None, CultureInfo.InvariantCulture, out var appID))
            {
                Log.Error($"Unexpected argument \"{arg}\", depots takes app ids. {Help.UsageHint("depots")}");
                return ExitCodes.ForUsageError("depots");
            }

            if (!appIDs.Contains(appID))
            {
                appIDs.Add(appID);
            }
        }

        var (loggedOn, logOnExitCode) = await SteamSession.CreateLoggedOnAsync(options.Login, takesTargets: false, cancellationToken);

        if (loggedOn == null)
        {
            return logOnExitCode;
        }

        using var session = loggedOn;

        var (appInfos, unlockedBranches, exitCode) = await session.GetAppInfosForDepotsAsync("depots", appIDs, options.BranchPassword);

        if (exitCode != ExitCodes.Ok)
        {
            return exitCode;
        }

        var sourceInfos = await session.GetAppInfosAsync(GetSharedInstallSourceApps(appInfos), logFailures: false);

        session.Disconnect();

        var success = true;

        foreach (var appID in appIDs)
        {
            // Apps without info were already reported
            if (!appInfos.TryGetValue(appID, out var appInfo))
            {
                success = false;
                continue;
            }

            var selection = DepotFilter.SelectForApp(appInfo, new InstallOptions());

            success &= ListAppDepots(appID, appInfo["depots"], options.Branch, unlockedBranches.GetValueOrDefault(appID), selection, sourceInfos);
        }

        return success ? ExitCodes.Ok : ExitCodes.Failed;
    }

    // The other apps that depots of the apps are shared from, which have the manifests of those depots
    private static List<uint> GetSharedInstallSourceApps(Dictionary<uint, KeyValue> appInfos)
    {
        var sourceAppIDs = new List<uint>();

        foreach (var (appID, appInfo) in appInfos)
        {
            AppInfo.AddSharedInstallSourceApps(sourceAppIDs, appID, AppInfo.GetDepots(appInfo["depots"]));
        }

        return sourceAppIDs;
    }

    // Returns false after an error. The depots that the selection skips, which is what download skips by default, get a
    // note with the reason. Shared installs show the manifest of the app they come from, when its info is in sourceInfos
    internal static bool ListAppDepots(uint appID, KeyValue depots, string branchName, IReadOnlyCollection<string>? unlockedBranches, DepotSelection selection,
        IReadOnlyDictionary<uint, KeyValue> sourceInfos)
    {
        if (!AppInfo.CheckBranch(depots, appID, branchName, unlockedBranches))
        {
            return false;
        }

        foreach (var branch in depots["branches"].Children)
        {
            var updated = branch["timeupdated"].AsLong();

            // Password protected branches have their own name for it
            if (updated == 0)
            {
                updated = branch["timebuildupdated"].AsLong();
            }

            var updatedText = updated > 0 ? updated.ToString(CultureInfo.InvariantCulture) : "-";

            Log.Result($"{appID}\t{branch.Name}\t{branch["buildid"].Value}\t{updatedText}");
        }

        if (!AppInfo.GetDepots(depots).Any())
        {
            Log.Error($"App {appID} has no depots. DLC and tools often keep their files in depots of their parent app.");
            return false;
        }

        var skipped = selection.Skipped.ToDictionary(static s => s.ID);

        foreach (var (depotID, depot) in AppInfo.GetDepots(depots))
        {
            var manifestDepot = depot;

            if (AppInfo.GetSharedInstallSourceApp(depot, appID) is { } sourceAppID)
            {
                manifestDepot = AppInfo.GetSourceDepot(sourceInfos, sourceAppID, depotID) ?? KeyValue.Invalid;
            }

            var (manifestID, fromPublic) = AppInfo.GetManifestIdForBranch(manifestDepot, branchName);
            var config = depot["config"];
            var fromApp = AppInfo.GetSharedInstallApp(depot);
            var notes = new List<string>();

            AddNote("os", config["oslist"].Value);
            AddNote("arch", config["osarch"].Value);
            AddNote("language", config["language"].Value);
            AddNote("dlc", depot["dlcappid"].Value);
            AddNote("shared from app", fromApp);

            if (fromPublic)
            {
                notes.Add("manifest from public");
            }

            if (skipped.TryGetValue(depotID, out var skippedDepot))
            {
                // With the depot's own value of the config key, like "mod czero", when the reason is a key
                var value = skippedDepot.Value;

                notes.Add(string.IsNullOrEmpty(value) ? $"skipped: {skippedDepot.Reason}" : $"skipped: {skippedDepot.Reason} {value}");
            }

            // A target for ls and get. Shared installs are downloaded through the app that owns them
            var manifest = manifestID == 0 ? "" : $":{manifestID}";
            var targetApp = AppInfo.GetSharedInstallApp(depot) ?? appID.ToString(CultureInfo.InvariantCulture);
            var target = $"{targetApp}/{depotID}{manifest}";

            Log.Result($"{target}\t{depot["maxsize"].Value ?? "-"}\t{string.Join(", ", notes)}");

            void AddNote(string name, string? value)
            {
                if (!string.IsNullOrEmpty(value))
                {
                    notes.Add($"{name} {value}");
                }
            }
        }

        return true;
    }
}
