using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SteamKit2;

namespace SteamFileDownloader;

internal sealed record ResolvedDepot(uint AppID, uint DepotID, uint KeyAppID, ulong ManifestID, string Branch, bool Optional);

internal sealed record ResolvedTarget(Target Target, bool WholeApp, List<ResolvedDepot> Depots);

internal delegate (ulong ManifestID, string Branch) ManifestPicker(uint appID, uint depotID, KeyValue depot, string branch);

internal static class JobResolver
{
    public const string SharedReason = "shared from app";
    public const string NoManifestReason = "no manifest";

    public static List<ResolvedDepot> ResolveDepots(
        uint appID,
        string branch,
        IEnumerable<(uint ID, KeyValue Depot)> depots,
        ManifestPicker pickManifest,
        List<SkippedDepot> skipped,
        bool optional,
        Dictionary<uint, KeyValue>? sharedSourceInfos = null)
    {
        var resolved = new List<ResolvedDepot>();

        foreach (var (depotID, depot) in depots)
        {
            var jobAppID = appID;
            var jobDepot = depot;

            if (sharedSourceInfos == null)
            {
                var fromApp = AppInfo.GetSharedInstallApp(depot);

                if (fromApp != null)
                {
                    skipped.Add(new SkippedDepot(depotID, SharedReason, fromApp));
                    continue;
                }
            }
            else if (AppInfo.GetSharedInstallSourceApp(depot, appID) is { } sourceAppID)
            {
                var sourceDepot = AppInfo.GetSourceDepot(sharedSourceInfos, sourceAppID, depotID);

                if (sourceDepot == null)
                {
                    skipped.Add(new SkippedDepot(depotID, $"app {sourceAppID} unavailable"));
                    continue;
                }

                jobAppID = sourceAppID;
                jobDepot = sourceDepot;
            }

            var (manifestID, manifestBranch) = pickManifest(jobAppID, depotID, jobDepot, branch);

            if (manifestID == 0)
            {
                skipped.Add(new SkippedDepot(depotID, NoManifestReason));
                continue;
            }

            var keyAppID = AppInfo.GetKeyAppID(jobDepot, jobAppID);

            resolved.Add(new ResolvedDepot(jobAppID, depotID, keyAppID, manifestID, manifestBranch, optional));
        }

        return resolved;
    }

    public static List<ResolvedTarget> ResolveTargets(
        IReadOnlyList<Target> targets,
        Dictionary<uint, KeyValue> appInfos,
        Dictionary<uint, List<string>> unlockedBranches,
        FailureLog failures)
    {
        var resolved = new List<ResolvedTarget>();

        // Depots without a manifest on the target's branch that use public's instead, said once per app and branch
        var publicFallbacks = new PublicFallbacks();

        foreach (var target in targets)
        {
            if (target.DepotID != null && target.ManifestID != null)
            {
                // The depot may be missing from the app's info, like a removed one, which is fine for a known manifest
                var depotName = target.DepotID.Value.ToString(CultureInfo.InvariantCulture);
                var explicitDepot = appInfos.GetValueOrDefault(target.AppID)?["depots"][depotName];
                var explicitKeyAppID = AppInfo.GetKeyAppID(explicitDepot, target.AppID);
                var explicitJob = new ResolvedDepot(target.AppID, target.DepotID.Value, explicitKeyAppID, target.ManifestID.Value, target.Branch, Optional: false);

                resolved.Add(new ResolvedTarget(target, WholeApp: false, [explicitJob]));
                continue;
            }

            var depots = appInfos.GetValueOrDefault(target.AppID)?["depots"];

            if (depots == null)
            {
                // A lone depot id reads as the app id
                if (target.DepotID == null)
                {
                    Log.Info($"If {target.AppID} is a depot, put its app id before it, like <app> {target.AppID}.");
                }

                failures.Add($"{target.Source} (no app info for {target.AppID})");
                continue;
            }

            if (target.DepotID == null && !AppInfo.GetDepots(depots).Any())
            {
                Log.Error(AppInfo.DescribeNoDepots(target.AppID));
                failures.Add($"{target.Source} (no depots)");
                continue;
            }

            if (!AppInfo.CheckBranch(depots, target.AppID, target.Branch, unlockedBranches.GetValueOrDefault(target.AppID)))
            {
                failures.Add($"{target.Source} (branch {target.Branch})");
                continue;
            }

            if (target.DepotID == null)
            {
                // Shared installs are listed under the app that owns them
                var wholeApp = ResolveDepots(target.AppID, target.Branch, AppInfo.GetDepots(depots), publicFallbacks.Resolve, [], optional: true);

                if (wholeApp.Count == 0)
                {
                    Log.Error($"App {target.AppID} has no depots with a manifest on \"{target.Branch}\", see \"{Help.ExeName} depots {target.AppID}\".");
                    failures.Add($"{target.Source} (no depots)");
                }
                else
                {
                    Log.Info($"App {target.AppID}: {wholeApp.Count} depots with a manifest on \"{target.Branch}\".");
                    resolved.Add(new ResolvedTarget(target, WholeApp: true, wholeApp));
                }

                continue;
            }

            var depotInfo = depots[target.DepotID.Value.ToString(CultureInfo.InvariantCulture)];

            if (depotInfo == KeyValue.Invalid && Workshop.GetWorkshopDepot(appInfos[target.AppID]) == target.DepotID)
            {
                Log.Error($"Depot {target.DepotID} is the workshop depot of app {target.AppID}, whose manifests are workshop items. Use \"{Help.ExeName} workshop ls <item>\" or \"workshop get\", or give <depot>:<manifest>.");
                failures.Add($"{target.Source} (workshop depot)");
                continue;
            }

            if (depotInfo == KeyValue.Invalid)
            {
                Log.Error($"Depot {target.DepotID} is not in app {target.AppID}, so its latest manifest is unknown. Check with \"{Help.ExeName} depots {target.AppID}\", name its app as <app>/<depot>, or give <depot>:<manifest>.");
                failures.Add($"{target.Source} (not in app {target.AppID})");
                continue;
            }

            var (latestID, manifestBranch) = publicFallbacks.Resolve(target.AppID, target.DepotID.Value, depotInfo, target.Branch);

            if (latestID == 0)
            {
                var fromApp = AppInfo.GetSharedInstallApp(depotInfo);

                if (fromApp != null)
                {
                    Log.Error($"Depot {target.DepotID} is shared from app {fromApp}, use {fromApp}/{target.DepotID} as the target instead.");
                }
                else
                {
                    Log.Error($"Depot {target.DepotID} has no manifest on branch \"{target.Branch}\" (it may be a DLC or another branch's depot), see \"{Help.ExeName} depots {target.AppID}\".");
                }

                failures.Add($"{target.Source} (no manifest)");
                continue;
            }

            var latestKeyAppID = AppInfo.GetKeyAppID(depotInfo, target.AppID);
            var latestJob = new ResolvedDepot(target.AppID, target.DepotID.Value, latestKeyAppID, latestID, manifestBranch, Optional: false);

            resolved.Add(new ResolvedTarget(target, WholeApp: false, [latestJob]));
        }

        publicFallbacks.Log();

        return resolved;
    }
}
