using System;
using System.Collections.Generic;
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
        if (branchInfo["pwdrequired"].AsBoolean() && !GetDepots(depots).Any(d => d.Depot["manifests"][branch] != KeyValue.Invalid))
        {
            Log.Error($"Branch \"{branch}\" of app {appID} is password protected, which is not supported.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Gets the manifest id of a depot on a branch, falling back to public. Returns 0 when there is none.
    /// </summary>
    public static ulong GetManifestIdForBranch(KeyValue depot, ref string branch)
    {
        var manifests = depot["manifests"];
        var manifestID = ParseManifestId(manifests[branch]);

        if (manifestID == 0 && !string.Equals(branch, "public", StringComparison.OrdinalIgnoreCase))
        {
            manifestID = ParseManifestId(manifests["public"]);

            if (manifestID != 0)
            {
                Log.Info($"Depot {depot.Name} has no manifest on branch \"{branch}\", using the one on \"public\".");
                branch = "public";
            }
        }

        return manifestID;

        // Either the id itself, or a "gid" child
        static ulong ParseManifestId(KeyValue manifest) => ulong.TryParse(manifest.Value ?? manifest["gid"].Value, out var id) ? id : 0;
    }
}
