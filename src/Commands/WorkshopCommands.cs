using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2.Internal;

namespace SteamFileDownloader;

/// <summary>
/// The workshop subcommands, with results on stdout.
/// </summary>
internal static class WorkshopCommands
{
    // Steam returns up to this many items per search request
    private const int SearchPageSize = 100;

    /// <summary>
    /// Whether a run failed, and whether for something other than lack of access.
    /// </summary>
    internal sealed class Outcome
    {
        public bool Failed;
        public bool FailedOtherwise;

        public void Fail(bool noAccess = false)
        {
            Failed = true;
            FailedOtherwise |= !noAccess;
        }
    }

    public static Task<int> InfoAsync(WorkshopArguments arguments, CancellationToken cancellationToken) => RunWithSessionAsync(
        arguments.Options,
        takesTargets: true,
        "Failed to get workshop items from Steam",
        (session, outcome) => WriteInfoAsync(session, arguments, outcome),
        cancellationToken);

    public static Task<int> SearchAsync(WorkshopArguments arguments, CancellationToken cancellationToken) => RunWithSessionAsync(
        arguments.Options,
        takesTargets: false,
        "Failed to search the workshop",
        (session, _) => WriteSearchAsync(session, arguments),
        cancellationToken);

    /// <summary>
    /// Logs in, runs write, and returns the exit code of its outcome. A failed Steam request fails the run with failure.
    /// </summary>
    private static async Task<int> RunWithSessionAsync(
        DepotOptions options,
        bool takesTargets,
        string failure,
        Func<SteamSession, Outcome, Task> write,
        CancellationToken cancellationToken)
    {
        var (loggedOn, logOnExitCode) = await SteamSession.CreateLoggedOnAsync(options.Login, takesTargets, cancellationToken);

        if (loggedOn == null)
        {
            return logOnExitCode;
        }

        using var session = loggedOn;
        var outcome = new Outcome();

        try
        {
            await write(session, outcome);
        }
        catch (Exception e) when (SteamSession.IsError(e, cancellationToken))
        {
            Log.Error($"{failure}: {SteamSession.DescribeError(e)}. Retry later.");
            outcome.Fail();
        }

        session.Disconnect();

        return ExitCodes.Report(new RunOutcome(outcome.Failed, outcome.FailedOtherwise, session.FailureExitCode));
    }

    /// <summary>
    /// Writes the rows of info. Items without details fail the outcome.
    /// </summary>
    internal static async Task WriteInfoAsync(IWorkshopSource source, WorkshopArguments arguments, Outcome outcome)
    {
        var details = await Workshop.GetDetailsAsync(source, arguments.Targets.Select(static t => t.ItemID));
        var items = new List<PublishedFileDetails>();

        foreach (var target in arguments.Targets.DistinctBy(static t => t.ItemID))
        {
            var item = details.GetValueOrDefault(target.ItemID);

            if (!Workshop.IsAvailable(item))
            {
                var problem = Workshop.GetProblem(target.ItemID, item)!;
                Log.Error(problem.Message);
                outcome.Fail(problem.NoAccess);
                continue;
            }

            if (!Workshop.IsCollection(item))
            {
                items.Add(item);
                continue;
            }

            if (arguments.History)
            {
                Log.Error($"Item {target.ItemID} is a collection, which has no versions of its own; give its items to --history instead.");
                outcome.Fail();
                continue;
            }

            // A collection's row comes first, then its items' rows
            items.Add(item);

            var children = Workshop.GetCollectionItems(item, details);
            var skipped = new List<(string Reason, ulong ItemID)>();

            foreach (var childID in children)
            {
                var child = details.GetValueOrDefault(childID);

                if (Workshop.IsAvailable(child))
                {
                    items.Add(child);
                }
                else
                {
                    skipped.Add((Workshop.GetProblem(childID, child)!.SkipReason, childID));
                }
            }

            if (skipped.Count > 0)
            {
                Log.Info($"Collection {target.ItemID}: {Log.Plural(children.Count, "item")}, {FormatSkipped(skipped)}.");
            }
        }

        if (arguments.History)
        {
            var histories = await Task.WhenAll(items.Select(item => Workshop.GetHistoryAsync(source, item.publishedfileid, arguments.Limit)));

            for (var i = 0; i < items.Count; i++)
            {
                WriteHistory(items[i], histories[i]);
            }

            return;
        }

        var appIDs = items
            .Where(static d => !Workshop.IsCollection(d))
            .Select(static d => d.consumer_appid)
            .Where(static a => a != 0)
            .Distinct()
            .ToList();
        var appInfosTask = source.GetAppInfosAsync(appIDs);
        var withoutManifest = items
            .Where(static d => Workshop.GetManifestColumn(d) == null)
            .Select(static d => d.publishedfileid)
            .Distinct()
            .ToList();
        var newest = await Task.WhenAll(withoutManifest.Select(id => Workshop.GetNewestManifestAsync(source, id)));
        var newestByItem = new Dictionary<ulong, ulong>();

        for (var i = 0; i < withoutManifest.Count; i++)
        {
            newestByItem.Add(withoutManifest[i], newest[i]);
        }

        var appInfos = await appInfosTask;

        foreach (var item in items)
        {
            var depotID = Workshop.IsCollection(item) ? 0 : Workshop.GetWorkshopDepot(appInfos.GetValueOrDefault(item.consumer_appid));
            var manifest = Workshop.GetManifestColumn(item);

            if (manifest == null)
            {
                var newestID = newestByItem[item.publishedfileid];
                manifest = newestID != 0 ? newestID.ToString(CultureInfo.InvariantCulture) : "-";
            }

            Log.Result($"{Workshop.FormatColumns(item, depotID, manifest)}\t{Workshop.CleanText(item.title)}");
        }
    }

    private static void WriteHistory(PublishedFileDetails item, (uint Total, List<CPublishedFile_GetChangeHistory_Response.ChangeLog> Changes) history)
    {
        var itemID = item.publishedfileid;
        var (total, changes) = history;
        var versions = Workshop.CollapseHistory(changes);
        var more = changes.Count < total;
        var versionsText = more ? "newest versions" : "versions";
        var limitHint = more ? ", --limit lists more" : "";

        Log.Info($"Item {itemID} ({Workshop.CleanText(item.title)}): {versions.Count} {versionsText} from {changes.Count} of its {total} changes{limitHint}.");

        if (Workshop.IsLegacy(item))
        {
            Log.Warn($"Item {itemID} is a legacy workshop item, so its versions are legacy files, not manifests that workshop ls and get can use.");
        }

        foreach (var version in versions)
        {
            Log.Result($"{itemID}:{version.ManifestID}\t{Workshop.FormatTime(version.Timestamp)}\t{Workshop.CleanText(version.Note)}");
        }
    }

    /// <summary>
    /// Writes a row per matching item, best match first, up to the limit.
    /// </summary>
    internal static async Task WriteSearchAsync(IWorkshopSource source, WorkshopArguments arguments)
    {
        var appID = arguments.SearchApp;
        var appInfosTask = source.GetAppInfosAsync([appID]);
        var listed = 0;
        uint total = 0;

        // Cursors page past the page numbers Steam allows
        var cursor = "*";

        while (listed < arguments.Limit)
        {
            var page = await source.QueryFilesAsync(new CPublishedFile_QueryFiles_Request
            {
                appid = appID,
                query_type = (uint)SteamKit2.EPublishedFileQueryType.RankedByTextSearch,
                search_text = arguments.SearchText,
                cursor = cursor,
                numperpage = (uint)Math.Min(SearchPageSize, arguments.Limit - listed),

                // Without it results are only ids
                return_details = true,
            });

            var appInfos = await appInfosTask;
            total = page.total;

            foreach (var item in page.publishedfiledetails.Take(arguments.Limit - listed))
            {
                var depotID = Workshop.GetWorkshopDepot(appInfos.GetValueOrDefault(item.consumer_appid));
                var manifest = Workshop.GetManifestColumn(item) ?? "-";
                var subscriptions = item.subscriptions.ToString(CultureInfo.InvariantCulture);

                Log.Result($"{Workshop.FormatColumns(item, depotID, manifest)}\t{subscriptions}\t{Workshop.CleanText(item.title)}");
                listed++;
            }

            var pageWasEmpty = page.publishedfiledetails.Count == 0;
            var cursorStopped = string.IsNullOrEmpty(page.next_cursor) || page.next_cursor == cursor;

            if (pageWasEmpty || cursorStopped)
            {
                break;
            }

            cursor = page.next_cursor;
        }

        var limitHint = listed < total ? ", --limit lists more" : "";

        Log.Info($"{listed} of {total} items of app {appID} match \"{arguments.SearchText}\"{limitHint}.");
    }

    /// <summary>
    /// Runs workshop ls or get: the items' manifests go through the get and ls pipeline.
    /// </summary>
    public static Task<int> RunAsync(WorkshopArguments arguments, CancellationToken cancellationToken) =>
        GetCommand.RunAsync(arguments.Command, arguments.Options, arguments.Patterns, async run =>
        {
            try
            {
                await ResolveJobsAsync(run, run.Session, arguments.Targets, cancellationToken);
            }
            catch (Exception e) when (SteamSession.IsError(e, cancellationToken))
            {
                Log.Error($"Failed to get workshop items from Steam: {SteamSession.DescribeError(e)}. Retry later.");
                run.Fail();
            }
        }, cancellationToken);

    // An item to download, from a target or the collection it names
    private sealed record Candidate(WorkshopTarget Target, PublishedFileDetails Details, bool FromCollection)
    {
        public ulong ItemID => Details.publishedfileid;
    }

    // A collection given as a target, with the jobs of its items and the ones skipped
    private sealed record CollectionTarget(ulong ItemID, int Items)
    {
        public HashSet<ManifestJob> Jobs { get; } = [];

        public List<(string Reason, ulong ItemID)> Skipped { get; } = [];
    }

    /// <summary>
    /// Adds a job per item, with collections expanded. Items of collections are like every depot of an app:
    /// skipped with a note when they have no files, and the collection fails only when none of them can be downloaded.
    /// </summary>
    internal static async Task ResolveJobsAsync(
        GetCommand.Run run,
        IWorkshopSource source,
        List<WorkshopTarget> targets,
        CancellationToken cancellationToken)
    {
        var details = await Workshop.GetDetailsAsync(source, targets.Select(static t => t.ItemID));
        var candidates = new List<Candidate>();
        var collections = new Dictionary<string, CollectionTarget>();

        foreach (var target in targets)
        {
            var item = details.GetValueOrDefault(target.ItemID);

            if (Workshop.IsAvailable(item) && Workshop.IsCollection(item))
            {
                if (target.ManifestID != null)
                {
                    Log.Error($"Item {target.ItemID} is a collection, which has no manifest of its own; give it without one to get its items.");
                    run.Fail(target.Source);
                    continue;
                }

                var items = Workshop.GetCollectionItems(item, details);
                var collection = new CollectionTarget(target.ItemID, items.Count);

                if (!collections.TryAdd(target.Source, collection))
                {
                    continue;
                }

                foreach (var childID in items)
                {
                    var child = details.GetValueOrDefault(childID);
                    var problem = Workshop.GetProblem(childID, child);

                    if (problem != null)
                    {
                        collection.Skipped.Add((problem.SkipReason, childID));
                    }
                    else
                    {
                        candidates.Add(new Candidate(target, child!, FromCollection: true));
                    }
                }

                continue;
            }

            var itemProblem = Workshop.GetProblem(target.ItemID, item);

            if (itemProblem != null)
            {
                Log.Error(itemProblem.Message);
                run.Fail(target.Source, itemProblem.NoAccess);
                continue;
            }

            candidates.Add(new Candidate(target, item!, FromCollection: false));
        }

        var appIDs = candidates.Select(static c => c.Details.consumer_appid).Distinct().ToList();
        var appInfosTask = source.GetAppInfosAsync(appIDs);

        // An item given twice, or both directly and in a collection, is looked up once
        var lookups = new Dictionary<(ulong ItemID, ulong? ManifestID), Task<(ulong ManifestID, ItemProblem? Problem)>>();
        var manifestTasks = new List<Task<(ulong ManifestID, ItemProblem? Problem)>>(candidates.Count);

        foreach (var candidate in candidates)
        {
            var key = (candidate.ItemID, candidate.Target.ManifestID);

            if (!lookups.TryGetValue(key, out var lookup))
            {
                lookup = GetManifestAsync(source, candidate, cancellationToken);
                lookups[key] = lookup;
            }

            manifestTasks.Add(lookup);
        }

        var manifests = await Task.WhenAll(manifestTasks);
        var appInfos = await appInfosTask;

        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            var appID = candidate.Details.consumer_appid;
            var depotID = Workshop.GetWorkshopDepot(appInfos.GetValueOrDefault(appID));
            var (manifestID, problem) = manifests[i];

            // Without a workshop depot, how the manifest lookup went doesn't matter
            if (depotID == 0)
            {
                problem = new ItemProblem($"App {appID} has no workshop depot, so item {candidate.ItemID} has no files to download.", "without files");
            }

            if (problem != null)
            {
                if (candidate.FromCollection)
                {
                    collections[candidate.Target.Source].Skipped.Add((problem.SkipReason, candidate.ItemID));
                }
                else
                {
                    Log.Error(problem.Message);
                    run.Fail(candidate.Target.Source);
                }

                continue;
            }

            var itemFolder = Path.Combine(run.OutputRoot, "workshop", candidate.ItemID.ToString(CultureInfo.InvariantCulture));
            var jobCount = run.Jobs.Count;
            var job = run.AddJob(appID, depotID, manifestID, "public", candidate.FromCollection, itemFolder);
            job.ItemID = candidate.ItemID;

            if (run.Jobs.Count > jobCount)
            {
                Log.Info($"Item {candidate.ItemID}: manifest {manifestID}, {Workshop.CleanText(candidate.Details.title)}");
            }

            if (candidate.FromCollection)
            {
                collections[candidate.Target.Source].Jobs.Add(job);
            }
        }

        foreach (var (targetSource, collection) in collections)
        {
            var skippedNote = collection.Skipped.Count > 0 ? $" ({FormatSkipped(collection.Skipped)})" : "";

            if (collection.Jobs.Count == 0)
            {
                Log.Error($"None of the {collection.Items} items of collection {collection.ItemID} have files to download{skippedNote}.");
                run.Fail(targetSource);
            }
            else
            {
                Log.Info($"Collection {collection.ItemID}: {collection.Jobs.Count} of {Log.Plural(collection.Items, "item")}{skippedNote}.");
                run.AllTargets[targetSource] = [.. collection.Jobs];
            }
        }
    }

    /// <summary>
    /// How many items of a collection were skipped, by reason in the order they first came up, like "12 skipped: 8
    /// removed, 4 without files". The ids are listed only when there are few.
    /// </summary>
    internal static string FormatSkipped(IReadOnlyCollection<(string Reason, ulong ItemID)> skipped)
    {
        var groups = skipped.GroupBy(static s => s.Reason);
        IEnumerable<string> reasons;

        if (skipped.Count <= 5)
        {
            reasons = groups.Select(static g => $"{string.Join(", ", g.Select(static s => s.ItemID))} {g.Key}");
        }
        else
        {
            reasons = groups.Select(static g => $"{g.Count()} {g.Key}");
        }

        return $"{skipped.Count} skipped: {string.Join(", ", reasons)}";
    }

    /// <summary>
    /// The manifest of an item to download: the one asked for, the one in its details, or the newest in its history.
    /// Without one, the problem says why.
    /// </summary>
    private static async Task<(ulong ManifestID, ItemProblem? Problem)> GetManifestAsync(
        IWorkshopSource source,
        Candidate candidate,
        CancellationToken cancellationToken)
    {
        var itemID = candidate.ItemID;
        var current = candidate.Details.hcontent_file;

        try
        {
            if (candidate.Target.ManifestID != null)
            {
                var pinned = candidate.Target.ManifestID.Value;

                if (pinned != current)
                {
                    var history = await source.GetChangeHistoryAsync(itemID, 0, Workshop.HistoryPageSize);

                    if (!history.changes.Any(c => c.manifest_id == pinned))
                    {
                        Log.Warn($"Manifest {pinned} is not in the newest changes of item {itemID}, so it may not be one of its versions; see \"{Help.ExeName} workshop info {itemID} --history\".");
                    }
                }

                return (pinned, null);
            }

            if (current != 0)
            {
                return (current, null);
            }

            var newest = await Workshop.GetNewestManifestAsync(source, itemID);

            if (newest == 0)
            {
                return (0, new ItemProblem($"Item {itemID} has no manifest in its details or change history.", "without files"));
            }

            return (newest, null);
        }
        catch (Exception e) when (SteamSession.IsError(e, cancellationToken))
        {
            return (0, new ItemProblem($"Failed to get the change history of item {itemID}: {SteamSession.DescribeError(e)}", "failed to look up"));
        }
    }
}
