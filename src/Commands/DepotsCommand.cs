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

        var appInfos = await session.GetAppInfosAsync(appIDs);
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

            success &= ListAppDepots(appID, appInfo["depots"], options.Branch);
        }

        return success ? ExitCodes.Ok : ExitCodes.Failed;
    }

    // Returns false after an error
    internal static bool ListAppDepots(uint appID, KeyValue depots, string branchName)
    {
        if (!AppInfo.CheckBranch(depots, appID, branchName))
        {
            return false;
        }

        foreach (var branch in depots["branches"].Children)
        {
            var updated = branch["timeupdated"].AsLong();
            var updatedText = updated > 0 ? updated.ToString(CultureInfo.InvariantCulture) : "-";

            Log.Result($"{appID}\t{branch.Name}\t{branch["buildid"].Value}\t{updatedText}");
        }

        if (!AppInfo.GetDepots(depots).Any())
        {
            Log.Error($"App {appID} has no depots. DLC and tools often keep their files in depots of their parent app.");
            return false;
        }

        foreach (var (depotID, depot) in AppInfo.GetDepots(depots))
        {
            var (manifestID, fromPublic) = AppInfo.GetManifestIdForBranch(depot, branchName);
            var config = depot["config"];
            var fromApp = AppInfo.GetSharedInstallApp(depot);
            var notes = new List<string>();

            AddNote("name", depot["name"].Value);
            AddNote("os", config["oslist"].Value);
            AddNote("arch", config["osarch"].Value);
            AddNote("language", config["language"].Value);
            AddNote("dlc", depot["dlcappid"].Value);
            AddNote("shared from app", fromApp);

            if (fromPublic)
            {
                notes.Add("manifest from public");
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
