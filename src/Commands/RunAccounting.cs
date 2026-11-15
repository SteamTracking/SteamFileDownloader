using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SteamKit2;

namespace SteamFileDownloader;

// A matching manifest file with its normalized path. Requested is false for directory VPKs only needed for their entries,
// and VpkPatterns are the VPK patterns that read entries from it
internal sealed record MatchedFile(
    DepotManifest.FileData File,
    string Path,
    bool Requested,
    IReadOnlyList<VpkPattern> VpkPatterns);

// The matching files of each manifest, and how often each pattern matched
internal sealed record MatchResult(Dictionary<ManifestJob, List<MatchedFile>> Matches, Dictionary<string, int> Counts);

// The closing error for patterns that matched nothing, or null when none did, and the notes of the closing line
internal sealed record RunSummary(string? UnmatchedError, List<string> Notes);

/// <summary>
/// The failures of a run: each one names the target it failed, if it was one, and whether only for lack of access.
/// </summary>
internal sealed class FailureLog
{
    private readonly List<(string? Target, bool NoAccess)> Failures = [];

    public bool Failed => Failures.Count > 0;

    // Something failed other than Steam giving no access
    public bool FailedOtherwise => Failures.Exists(static f => !f.NoAccess);

    // A target failed for lack of access, which another saved login may have
    public bool FailedForAccess => Failures.Exists(static f => f.Target != null && f.NoAccess);

    public List<string> FailedTargets => [.. Failures.Where(static f => f.Target != null).Select(static f => f.Target!)];

    public void Add(string? target = null, bool noAccess = false) => Failures.Add((target, noAccess));

    public RunOutcome ToOutcome(int sessionFailureExitCode, bool unmatched = false) =>
        new(Failed, FailedOtherwise, sessionFailureExitCode, unmatched, FailedTargets);
}

/// <summary>
/// Which files match the patterns of a run, which patterns matched nothing, and the text that closes the run.
/// </summary>
internal static class RunAccounting
{
    /// <summary>
    /// One pass over the downloaded manifests and the legacy items, whose Requested is set by the patterns that match their name.
    /// </summary>
    public static MatchResult MatchFiles(FileFilter filter, IEnumerable<ManifestJob> jobs, IReadOnlyList<LegacyItem> legacyItems)
    {
        var listAll = filter.Patterns.Count == 0 && filter.VpkPatterns.Count == 0;
        var matches = new Dictionary<ManifestJob, List<MatchedFile>>();
        var counts = new Dictionary<string, int>();

        foreach (var pattern in filter.Patterns.Select(static p => p.Source).Concat(filter.VpkPatterns.Select(static p => p.Source)))
        {
            counts[pattern] = 0;
        }

        foreach (var job in jobs.Where(static j => j.Manifest != null))
        {
            var matched = new List<MatchedFile>();

            foreach (var (file, path) in job.GetFiles())
            {
                var matching = filter.GetMatchingPatterns(path);
                var requested = listAll || matching.Count > 0;

                foreach (var pattern in matching)
                {
                    counts[pattern.Source]++;
                }

                var vpkPatterns = filter.GetVpkPatterns(path);

                if (requested || vpkPatterns.Count > 0)
                {
                    matched.Add(new MatchedFile(file, path, requested, vpkPatterns));
                }
            }

            matches[job] = matched;
        }

        // A legacy item has one file, which only patterns that match its name select
        foreach (var item in legacyItems)
        {
            var matching = filter.GetMatchingPatterns(item.FileName);

            foreach (var pattern in matching)
            {
                counts[pattern.Source]++;
            }

            item.Requested = listAll || matching.Count > 0;
        }

        return new MatchResult(matches, counts);
    }

    /// <summary>
    /// Fails each target that names several manifests when all of them failed, and builds the closing error and notes.
    /// Patterns can only be judged against manifests that were downloaded, and VPK patterns against directory files that were read.
    /// </summary>
    public static RunSummary Summarize(
        IReadOnlyList<ManifestJob> jobs,
        IReadOnlyDictionary<ManifestJob, List<MatchedFile>> matches,
        IReadOnlyList<LegacyItem> legacyItems,
        IReadOnlyDictionary<string, int> counts,
        IReadOnlySet<string> failedVpkPatterns,
        IReadOnlyDictionary<string, List<ManifestJob>> allTargets,
        FailureLog failures,
        string? searchAllHint)
    {
        // A target whose manifests all failed fails as a whole, and the closing error names it. Its jobs aren't also
        // listed as skipped
        var failedWhole = new HashSet<ManifestJob>();

        foreach (var (source, targetJobs) in allTargets)
        {
            if (targetJobs.All(static j => j.Manifest == null))
            {
                var noun = targetJobs[0].ItemID != null ? "item" : "depot";

                failures.Add($"{source} ({Log.Plural(targetJobs.Count, noun)})", targetJobs.All(static j => j.NoAccess));
                failedWhole.UnionWith(targetJobs);
            }
        }

        var searched = matches.Count + legacyItems.Count;
        var unmatched = new List<string>();

        if (searched > 0)
        {
            unmatched = counts.Where(p => p.Value == 0 && !failedVpkPatterns.Contains(p.Key)).Select(static p => p.Key).ToList();
        }

        var skippedJobs = jobs.Where(j => j.Optional && j.NoAccess && !failedWhole.Contains(j)).ToList();
        var skipped = skippedJobs.Select(static j => j.ItemID != null ? j.ToString() : j.DepotID.ToString(CultureInfo.InvariantCulture)).ToList();
        string? unmatchedError = null;

        if (unmatched.Count > 0)
        {
            var skippedWhat = skippedJobs.Any(static j => j.ItemID != null) ? "item" : "depot";
            string? where = null;

            if (matches.Count == 1 && searched == 1)
            {
                where = matches.Keys.First().ToString();
            }
            else if (legacyItems.Count == 1 && searched == 1)
            {
                where = legacyItems[0].Label;
            }

            unmatchedError = DescribeUnmatched(unmatched, searched, where, skipped.Count, skippedWhat, searchAllHint);
        }

        var notes = new List<string>();

        if (skipped.Count > 0)
        {
            notes.Add($"skipped without access: {Log.JoinFirst(skipped)}");
        }

        // A target without a match is fine, like a build from before the file existed. When no pattern matched at all, the error says so already
        var withoutMatches = matches.Where(static m => m.Value.Count == 0 && !m.Key.Optional).Select(static m => m.Key.ToString()).ToList();

        withoutMatches.AddRange(legacyItems.Where(static i => !i.Requested).Select(static i => i.Label));

        var someMatched = unmatched.Count == 0 || unmatched.Count < counts.Count;

        if (withoutMatches.Count > 0 && someMatched)
        {
            notes.Add($"nothing matched in {Log.JoinFirst(withoutMatches)}");
        }

        return new RunSummary(unmatchedError, notes);
    }

    /// <summary>
    /// The error for patterns that matched nothing: in which manifests, how many were skipped without access, and how to
    /// search every depot. onlyManifest names the one manifest searched, if there was one, and skippedNoun is depot or item.
    /// </summary>
    private static string DescribeUnmatched(
        IReadOnlyList<string> unmatched,
        int manifests,
        string? onlyManifest,
        int skipped,
        string skippedNoun,
        string? searchAllHint)
    {
        var where = onlyManifest ?? $"any of the {manifests} manifests";
        var skippedNote = skipped > 0 ? $" ({Log.Plural(skipped, skippedNoun)} skipped without access)" : "";
        var hint = searchAllHint != null ? $" {searchAllHint} {string.Join(' ', unmatched.Select(Help.QuoteArgument))}" : "";

        return $"{string.Join(", ", unmatched.Select(static p => $"\"{p}\""))} matched nothing in {where}{skippedNote}.{hint}";
    }

    /// <summary>
    /// The closing line: the rows from the manifests and legacy items, with the notes. Downloads add the folder of the files.
    /// </summary>
    public static string DescribeDone(
        int rows,
        bool hideRows,
        bool vpkOnly,
        int jobCount,
        int manifestCount,
        int legacyCount,
        IReadOnlyList<string> notes,
        string? filesFolder)
    {
        var counted = new List<string>();

        if (jobCount > 0 || legacyCount == 0)
        {
            counted.Add(Log.PluralOf(manifestCount, jobCount, "manifest"));
        }

        if (legacyCount > 0)
        {
            counted.Add(Log.Plural(legacyCount, "legacy item"));
        }

        var manifests = string.Join(" and ", counted);
        string rowsText;

        if (hideRows)
        {
            rowsText = Log.Plural(rows, "file");
        }
        else if (vpkOnly)
        {
            rowsText = Log.Plural(rows, "VPK entry", "VPK entries");
        }
        else
        {
            rowsText = Log.Plural(rows, "row");
        }

        var notesText = notes.Count > 0 ? $" ({string.Join("; ", notes)})" : "";
        var outputNote = filesFolder != null ? $" Files are in {filesFolder}" : "";

        return $"Done: {rowsText} from {manifests}{notesText}.{outputNote}";
    }
}
