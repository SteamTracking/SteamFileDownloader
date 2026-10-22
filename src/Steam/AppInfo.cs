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
    /// Whether the app is a soundtrack, which is when its "common" type is music.
    /// </summary>
    public static bool IsMusic(KeyValue appInfo) =>
        string.Equals(appInfo["common"]["type"].Value, "music", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The app in the depot's "depotfromapp" when it is a shared install, which that app owns the manifests of, or null.
    /// Other depots with a "depotfromapp" are installed from the installing app's own manifests.
    /// </summary>
    public static string? GetSharedInstallApp(KeyValue depot)
    {
        if (depot["sharedinstall"].AsInteger() == 0)
        {
            return null;
        }

        return depot["depotfromapp"].Value;
    }

    /// <summary>
    /// The app that owns the manifests of a shared install depot when it is another app than the one being installed,
    /// or null for any other depot.
    /// </summary>
    public static uint? GetSharedInstallSourceApp(KeyValue depot, uint appID)
    {
        var sourceApp = GetSharedInstallApp(depot);

        if (!uint.TryParse(sourceApp, NumberStyles.None, CultureInfo.InvariantCulture, out var sourceAppID))
        {
            return null;
        }

        if (sourceAppID == appID)
        {
            return null;
        }

        return sourceAppID;
    }

    /// <summary>
    /// The app to request the depot's key with: the DLC that owns the depot when it has one, else the app being installed.
    /// </summary>
    public static uint GetKeyAppID(KeyValue? depot, uint appID)
    {
        var dlcAppID = depot?["dlcappid"].AsUnsignedInteger() ?? 0;

        return dlcAppID != 0 ? dlcAppID : appID;
    }

    /// <summary>
    /// The DLC apps of an app that keeps some of its depots in its DLC's own info, which is when its "hasdepotsindlc" is
    /// set. Empty otherwise.
    /// </summary>
    public static List<uint> GetDlcAppIDs(KeyValue appInfo)
    {
        var dlcAppIDs = new List<uint>();

        if (appInfo["depots"]["hasdepotsindlc"].AsInteger() == 0)
        {
            return dlcAppIDs;
        }

        var listOfDlc = appInfo["extended"]["listofdlc"].Value;

        if (string.IsNullOrEmpty(listOfDlc))
        {
            return dlcAppIDs;
        }

        var seen = new HashSet<uint>();

        foreach (var part in listOfDlc.Split(','))
        {
            if (uint.TryParse(part.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var dlcAppID) && seen.Add(dlcAppID))
            {
                dlcAppIDs.Add(dlcAppID);
            }
        }

        return dlcAppIDs;
    }

    /// <summary>
    /// Adds the depots of a DLC's own info that name a "dlcappid" to the app's depots, unless the app has them already.
    /// </summary>
    public static void MergeDlcDepots(KeyValue appInfo, KeyValue dlcInfo)
    {
        var depots = appInfo["depots"];

        if (depots == KeyValue.Invalid)
        {
            return;
        }

        foreach (var (_, depot) in GetDepots(dlcInfo["depots"]))
        {
            if (depot["dlcappid"].AsUnsignedInteger() != 0 && depots[depot.Name!] == KeyValue.Invalid)
            {
                depots.Children.Add(depot);
            }
        }
    }

    /// <summary>
    /// Merges the depot section that Steam gives for password protected branches into the app's info, adding the
    /// branches and their manifests to the public ones.
    /// </summary>
    public static void MergePrivateDepots(KeyValue appInfo, KeyValue privateDepots)
    {
        var depots = appInfo["depots"];

        if (depots == KeyValue.Invalid)
        {
            depots = new KeyValue("depots");
            appInfo.Children.Add(depots);
        }

        MergeInto(depots, privateDepots);
    }

    private static void MergeInto(KeyValue target, KeyValue source)
    {
        foreach (var child in source.Children)
        {
            var existing = target[child.Name!];

            if (existing == KeyValue.Invalid)
            {
                target.Children.Add(child);
            }
            else if (child.Children.Count == 0)
            {
                existing.Value = child.Value;
            }
            else
            {
                MergeInto(existing, child);
            }
        }
    }

    /// <summary>
    /// Whether the app has the branch and its manifests are visible, since otherwise every depot would silently
    /// fall back to public. Logs an error when not. With a branch password, unlockedBranches are the branches it unlocked.
    /// </summary>
    public static bool CheckBranch(KeyValue depots, uint appID, string branch, IReadOnlyCollection<string>? unlockedBranches = null)
    {
        if (string.Equals(branch, "public", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var branches = depots["branches"];
        var branchInfo = branches[branch];
        var unlockedNote = unlockedBranches != null ? $" {DescribeUnlockedBranches(unlockedBranches)}" : "";

        if (branchInfo == KeyValue.Invalid)
        {
            Log.Error($"App {appID} has no branch \"{branch}\". Its branches are: {string.Join(", ", branches.Children.Select(static b => b.Name))}.{unlockedNote}");
            return false;
        }

        // Password protected branches only list their manifests to accounts with access
        if (branchInfo["pwdrequired"].AsBoolean())
        {
            var hasVisibleManifest = GetDepots(depots).Any(d => d.Depot["manifests"][branch] != KeyValue.Invalid);

            if (!hasVisibleManifest && unlockedBranches == null)
            {
                Log.Error($"Branch \"{branch}\" of app {appID} is password protected. Give its password with --branch-password.");
                return false;
            }

            if (!hasVisibleManifest)
            {
                Log.Error($"Branch \"{branch}\" of app {appID} is password protected, and the branch password does not unlock it.{unlockedNote}");
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether the branch is only for a local content server, which Steam installs public's build instead of unless one is set up.
    /// </summary>
    public static bool RequiresLocalContentServer(KeyValue depots, string branch)
    {
        if (string.Equals(branch, "public", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return depots["branches"][branch]["lcsrequired"].AsBoolean();
    }

    /// <summary>
    /// Says which branches a branch password unlocked.
    /// </summary>
    public static string DescribeUnlockedBranches(IReadOnlyCollection<string> unlockedBranches)
    {
        if (unlockedBranches.Count == 0)
        {
            return "The branch password unlocks no branch.";
        }

        return $"The branch password unlocks: {string.Join(", ", unlockedBranches)}.";
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
