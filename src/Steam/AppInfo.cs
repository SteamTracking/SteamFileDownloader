using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SteamKit2;

namespace SteamFileDownloader;

/// <summary>
/// Reads the depots, branches and manifests in an app's info.
/// </summary>
internal static class AppInfo
{
    /// <summary>
    /// The depots in an app's "depots" section, skipping its other keys like "branches".
    /// </summary>
    public static IEnumerable<(uint ID, KeyValue Depot)> GetDepots(KeyValue depots)
    {
        foreach (var depot in depots.Children)
        {
            if (uint.TryParse(depot.Name, out var depotID))
            {
                yield return (depotID, depot);
            }
        }
    }

    /// <summary>
    /// Whether the app has the branch and its manifests are visible, since otherwise every depot would silently
    /// fall back to public. Logs an error when not.
    /// </summary>
    public static bool CheckBranch(KeyValue depots, uint appID, string branch)
    {
        if (string.Equals(branch, "public", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var branches = depots["branches"];
        var branchInfo = branches[branch];

        if (branchInfo == KeyValue.Invalid)
        {
            Log.Error($"App {appID} has no branch \"{branch}\". Its branches are: {string.Join(", ", branches.Children.Select(static b => b.Name))}.");
            return false;
        }

        // Password protected branches only list their manifests to accounts with access
        if (branchInfo["pwdrequired"].AsBoolean())
        {
            var hasVisibleManifest = GetDepots(depots).Any(d => d.Depot["manifests"][branch] != KeyValue.Invalid);

            if (!hasVisibleManifest)
            {
                Log.Error($"Branch \"{branch}\" of app {appID} is password protected, which is not supported.");
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Gets the manifest id of a depot on a branch, or else public's, which FromPublic says. Returns 0 when there is none.
    /// </summary>
    public static (ulong ManifestID, bool FromPublic) GetManifestIdForBranch(KeyValue depot, string branch)
    {
        var manifests = depot["manifests"];
        var manifestID = ParseManifestId(manifests[branch]);

        if (manifestID != 0 || string.Equals(branch, "public", StringComparison.OrdinalIgnoreCase))
        {
            return (manifestID, false);
        }

        manifestID = ParseManifestId(manifests["public"]);

        return (manifestID, manifestID != 0);

        // Either the id itself, or a "gid" child
        static ulong ParseManifestId(KeyValue manifest)
        {
            var value = manifest.Value ?? manifest["gid"].Value;

            return ulong.TryParse(value, out var id) ? id : 0;
        }
    }

    /// <summary>
    /// Logs in one line which depots of an app use public's manifest, since they have none on the branch.
    /// </summary>
    public static void LogPublicFallbacks(uint appID, string branch, IReadOnlyCollection<uint> depotIDs)
    {
        if (depotIDs.Count == 0)
        {
            return;
        }

        if (depotIDs.Count == 1)
        {
            Log.Info($"Depot {depotIDs.First()} of app {appID} has no manifest on \"{branch}\", using the one on \"public\".");
            return;
        }

        List<string> sortedIDs = [.. depotIDs.Order().Select(static d => d.ToString(CultureInfo.InvariantCulture))];
        Log.Info($"{depotIDs.Count} depots of app {appID} have no manifest on \"{branch}\", using \"public\": {Log.JoinFirst(sortedIDs)}.");
    }
}

/// <summary>
/// Finds the manifests of depots on branches, and logs the depots that use public's since they have none on theirs,
/// in one line per app and branch.
/// </summary>
internal sealed class PublicFallbacks
{
    private readonly List<(uint AppID, string Branch, uint DepotID)> Depots = [];

    /// <summary>
    /// The manifest id of an app's depot on a branch, or 0 when there is none, and the branch it is on, which is public
    /// when the branch has none.
    /// </summary>
    public (ulong ManifestID, string Branch) Resolve(uint appID, uint depotID, KeyValue depot, string branch)
    {
        var (manifestID, fromPublic) = AppInfo.GetManifestIdForBranch(depot, branch);

        if (!fromPublic)
        {
            return (manifestID, branch);
        }

        Depots.Add((appID, branch, depotID));

        return (manifestID, "public");
    }

    public void Log()
    {
        foreach (var group in Depots.GroupBy(static d => (d.AppID, d.Branch)))
        {
            AppInfo.LogPublicFallbacks(group.Key.AppID, group.Key.Branch, [.. group.Select(static d => d.DepotID).Distinct()]);
        }
    }
}
