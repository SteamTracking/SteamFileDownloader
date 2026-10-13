using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
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

    public static Task<int> InfoAsync(WorkshopArguments arguments, CancellationToken cancellationToken) =>
        RunWithSessionAsync(arguments.Options, takesTargets: true, "Failed to get workshop items from Steam", (session, outcome) => WriteInfoAsync(session, arguments, outcome), cancellationToken);

    public static Task<int> SearchAsync(WorkshopArguments arguments, CancellationToken cancellationToken) =>
        RunWithSessionAsync(arguments.Options, takesTargets: false, "Failed to search the workshop", (session, _) => WriteSearchAsync(session, arguments), cancellationToken);

    /// <summary>
    /// Logs in, runs write, and returns the exit code of its outcome. A failed Steam request fails the run with failure.
    /// </summary>
    private static async Task<int> RunWithSessionAsync(DepotOptions options, bool takesTargets, string failure, Func<SteamSession, Outcome, Task> write, CancellationToken cancellationToken)
    {
        var (loggedOn, logOnExitCode) = await SteamSession.CreateLoggedOnAsync(options, takesTargets, cancellationToken);

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

        return ExitCodes.Report(outcome.Failed, outcome.FailedOtherwise, session.FailureExitCode);
    }

    /// <summary>
    /// Writes the rows of info, or its JSON. Items without details fail the outcome.
    /// </summary>
    internal static async Task WriteInfoAsync(IWorkshopSource source, WorkshopArguments arguments, Outcome outcome)
    {
        var details = await Workshop.GetDetailsAsync(source, arguments.Targets.Select(static t => t.ItemID), full: arguments.Options.Json);
        var items = new List<PublishedFileDetails>();

        foreach (var target in arguments.Targets.DistinctBy(static t => t.ItemID))
        {
            var item = details.GetValueOrDefault(target.ItemID);

            if (!Workshop.IsAvailable(item))
            {
                var (problem, noAccess) = Workshop.GetProblem(target.ItemID, item)!.Value;
                Log.Error(problem);
                outcome.Fail(noAccess);
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

            foreach (var childID in Workshop.GetCollectionItems(item, details))
            {
                var child = details.GetValueOrDefault(childID);

                if (Workshop.IsAvailable(child))
                {
                    items.Add(child);
                }
                else
                {
                    Log.Info($"Skipping item {childID} of collection {target.ItemID}: {Workshop.GetProblem(childID, child)!.Value.Problem}");
                }
            }
        }

        if (arguments.History)
        {
            var histories = await Task.WhenAll(items.Select(item => Workshop.GetHistoryAsync(source, item.publishedfileid, arguments.Limit)));

            if (arguments.Options.Json)
            {
                WriteInfoJson(items, histories);
                return;
            }

            for (var i = 0; i < items.Count; i++)
            {
                WriteHistory(items[i], histories[i]);
            }

            return;
        }

        if (arguments.Options.Json)
        {
            WriteInfoJson(items, null);
            return;
        }

        var appInfosTask = source.GetAppInfosAsync([.. items.Where(static d => !Workshop.IsCollection(d)).Select(static d => d.consumer_appid).Where(static a => a != 0).Distinct()]);
        var withoutManifest = items.Where(static d => Workshop.GetManifestColumn(d) == null).Select(static d => d.publishedfileid).Distinct().ToList();
        var newest = await Task.WhenAll(withoutManifest.Select(id => Workshop.GetNewestManifestAsync(source, id)));
        var newestByItem = withoutManifest.Zip(newest).ToDictionary(static p => p.First, static p => p.Second);
        var appInfos = await appInfosTask;

        foreach (var item in items)
        {
            var depotID = Workshop.IsCollection(item) ? 0 : Workshop.GetWorkshopDepot(appInfos.GetValueOrDefault(item.consumer_appid));
            var manifest = Workshop.GetManifestColumn(item)
                ?? (newestByItem[item.publishedfileid] is var newestID and not 0 ? newestID.ToString(CultureInfo.InvariantCulture) : "-");

            Log.Result($"{Workshop.FormatColumns(item, depotID, manifest)}\t{Workshop.CleanText(item.title)}");
        }
    }

    private static void WriteHistory(PublishedFileDetails item, (uint Total, List<CPublishedFile_GetChangeHistory_Response.ChangeLog> Changes) history)
    {
        var itemID = item.publishedfileid;
        var (total, changes) = history;
        var versions = Workshop.CollapseHistory(changes);
        var more = changes.Count < total;

        Log.Info($"Item {itemID} ({Workshop.CleanText(item.title)}): {versions.Count} {(more ? "newest " : "")}versions from {changes.Count} of its {total} changes{(more ? ", --limit lists more" : "")}.");

        if (Workshop.IsLegacy(item))
        {
            Log.Warn($"Item {itemID} is a legacy workshop item, so its versions are legacy files, not manifests that workshop ls and get can use.");
        }

        foreach (var version in versions)
        {
            Log.Result($"{itemID}:{version.ManifestID}\t{Workshop.FormatTime(version.Timestamp)}\t{Workshop.CleanText(version.Note)}");
        }
    }

    // An object per item by its id, with its details as Steam sent them, and its changes with --history
    private static void WriteInfoJson(List<PublishedFileDetails> items, (uint Total, List<CPublishedFile_GetChangeHistory_Response.ChangeLog> Changes)[]? histories)
    {
        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();

            for (var i = 0; i < items.Count; i++)
            {
                writer.WriteStartObject(items[i].publishedfileid.ToString(CultureInfo.InvariantCulture));
                writer.WritePropertyName("details");
                Workshop.WriteJson(writer, items[i]);

                if (histories != null)
                {
                    writer.WriteStartObject("history");
                    writer.WriteNumber("total", histories[i].Total);
                    writer.WriteStartArray("changes");

                    foreach (var change in histories[i].Changes)
                    {
                        Workshop.WriteJson(writer, change);
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        Log.Result(Encoding.UTF8.GetString(stream.ToArray()));
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

                Log.Result($"{Workshop.FormatColumns(item, depotID, manifest)}\t{item.subscriptions.ToString(CultureInfo.InvariantCulture)}\t{Workshop.CleanText(item.title)}");
                listed++;
            }

            if (page.publishedfiledetails.Count == 0 || string.IsNullOrEmpty(page.next_cursor) || page.next_cursor == cursor)
            {
                break;
            }

            cursor = page.next_cursor;
        }

        Log.Info($"{listed} of {total} items of app {appID} match \"{arguments.SearchText}\"{(listed < total ? ", --limit lists more" : "")}.");
    }

    /// <summary>
    /// Runs workshop ls or get: the items' manifests go through the get and ls pipeline.
    /// </summary>
    public static Task<int> RunAsync(WorkshopArguments arguments, CancellationToken cancellationToken) =>
        GetCommand.RunAsync(arguments.Options, arguments.Patterns, list: arguments.Command == "workshop ls", async run =>
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

    /// <summary>
    /// Adds a job per item, with collections expanded. Items of collections are like the depots of an "all" target:
    /// skipped with a note when they have no files, and the collection fails only when none of them can be downloaded.
    /// </summary>
    internal static async Task ResolveJobsAsync(GetCommand.Run run, IWorkshopSource source, List<WorkshopTarget> targets, CancellationToken cancellationToken)
    {
        var details = await Workshop.GetDetailsAsync(source, targets.Select(static t => t.ItemID));
        var candidates = new List<Candidate>();
        var collections = new Dictionary<string, (ulong ItemID, int Items, HashSet<ManifestJob> Jobs)>();

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
                collections.TryAdd(target.Source, (target.ItemID, items.Count, []));

                foreach (var childID in items)
                {
                    var child = details.GetValueOrDefault(childID);

                    if (Workshop.GetProblem(childID, child) is { } childProblem)
                    {
                        Log.Info($"Skipping item {childID} of collection {target.ItemID}: {childProblem.Problem}");
                    }
                    else
                    {
                        candidates.Add(new Candidate(target, child!, FromCollection: true));
                    }
                }

                continue;
            }

            if (Workshop.GetProblem(target.ItemID, item) is { } problem)
            {
                Log.Error(problem.Problem);
                run.Fail(target.Source, problem.NoAccess);
                continue;
            }

            candidates.Add(new Candidate(target, item!, FromCollection: false));
        }

        // An item given twice, or both directly and in a collection, is looked up once
        var appInfosTask = source.GetAppInfosAsync([.. candidates.Select(static c => c.Details.consumer_appid).Distinct()]);
        var lookups = new Dictionary<(ulong ItemID, ulong? ManifestID), Task<(ulong ManifestID, string? Problem)>>();
        var manifests = await Task.WhenAll(candidates.Select(c =>
            lookups.TryGetValue((c.ItemID, c.Target.ManifestID), out var lookup) ? lookup : lookups[(c.ItemID, c.Target.ManifestID)] = GetManifestAsync(source, c, cancellationToken)));
        var appInfos = await appInfosTask;

        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            var appID = candidate.Details.consumer_appid;
            var depotID = Workshop.GetWorkshopDepot(appInfos.GetValueOrDefault(appID));
            var (manifestID, problem) = manifests[i];

            if (depotID == 0)
            {
                problem = $"App {appID} has no workshop depot, so item {candidate.ItemID} has no files to download.";
            }

            if (problem != null)
            {
                if (candidate.FromCollection)
                {
                    Log.Info($"Skipping item {candidate.ItemID} of collection {candidate.Target.ItemID}: {problem}");
                }
                else
                {
                    Log.Error(problem);
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

        foreach (var (targetSource, (itemID, items, jobs)) in collections)
        {
            if (jobs.Count == 0)
            {
                Log.Error($"None of the {items} items of collection {itemID} have files to download, see above.");
                run.Fail(targetSource);
            }
            else
            {
                Log.Info($"Target {targetSource}: {jobs.Count} of the {items} items of collection {itemID}.");
                run.AllTargets[targetSource] = [.. jobs];
            }
        }
    }

    /// <summary>
    /// The manifest of an item to download: the one asked for, the one in its details, or the newest in its history.
    /// </summary>
    private static async Task<(ulong ManifestID, string? Problem)> GetManifestAsync(IWorkshopSource source, Candidate candidate, CancellationToken cancellationToken)
    {
        var itemID = candidate.ItemID;
        var current = candidate.Details.hcontent_file;

        try
        {
            if (candidate.Target.ManifestID is { } pinned)
            {
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

            return newest != 0 ? (newest, null) : (0, $"Item {itemID} has no manifest in its details or change history.");
        }
        catch (Exception e) when (SteamSession.IsError(e, cancellationToken))
        {
            return (0, $"Failed to get the change history of item {itemID}: {SteamSession.DescribeError(e)}");
        }
    }
}
