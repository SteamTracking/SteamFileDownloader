namespace SteamFileDownloader.Tests;

public class RunAccountingTests
{
    private static ManifestJob Job(uint depotID, params string[] names) =>
        TestManifests.Job("out", depotID, [.. names.Select(static n => TestManifests.File(n, new byte[] { 1 }))]);

    private static ManifestJob Failed(uint depotID, bool noAccess)
    {
        var job = Job(depotID);

        job.Manifest = null;
        job.NoAccess = noAccess;

        return job;
    }

    private static LegacyItem Legacy(string fileName) => new()
    {
        ItemID = 77,
        FileName = fileName,
        Url = new Uri("https://example.com/file"),
        Size = 1,
        TimeUpdated = 1,
        OutputFolder = "out",
    };

    private static RunSummary Summarize(
        FileFilter filter,
        List<ManifestJob> jobs,
        FailureLog failures,
        List<LegacyItem>? legacyItems = null,
        Dictionary<string, List<ManifestJob>>? allTargets = null,
        HashSet<string>? failedVpkPatterns = null,
        string? searchAllHint = null)
    {
        legacyItems ??= [];

        var matched = RunAccounting.MatchFiles(filter, jobs, legacyItems);

        return RunAccounting.Summarize(jobs, matched.Matches, legacyItems, matched.Counts, failedVpkPatterns ?? [], allTargets ?? [], failures, searchAllHint);
    }

    private static int ExitCodeOf(FailureLog failures, RunSummary summary) =>
        ExitCodes.GetExitCode(failures.ToOutcome(ExitCodes.Failed, summary.UnmatchedError != null)).ExitCode;

    [Test]
    public async Task CountsMatchesPerPattern()
    {
        var job = Job(1, "a.dll", "b.txt", "c.dll");
        var filter = FileFilter.FromPatterns(["*.dll", "*.exe"]);

        var matched = RunAccounting.MatchFiles(filter, [job], []);

        await Assert.That(matched.Counts["*.dll"]).IsEqualTo(2);
        await Assert.That(matched.Counts["*.exe"]).IsEqualTo(0);
        await Assert.That(matched.Matches[job].Select(static f => f.Path)).IsEquivalentTo(["a.dll", "c.dll"]);
        await Assert.That(matched.Matches[job].All(static f => f.Requested)).IsTrue();
    }

    [Test]
    public async Task NoPatternsRequestsEveryFile()
    {
        var job = Job(1, "a.dll", "b.txt");
        var failures = new FailureLog();

        var summary = Summarize(FileFilter.FromPatterns([]), [job], failures);

        await Assert.That(summary.UnmatchedError).IsNull();
        await Assert.That(ExitCodeOf(failures, summary)).IsEqualTo(ExitCodes.Ok);
    }

    [Test]
    public async Task VpkPatternSelectsDirectoryFileWithoutRequestingIt()
    {
        var job = Job(1, "pak01_dir.vpk", "other.txt");
        var filter = FileFilter.FromPatterns(["pak01_dir.vpk:regex:\\.txt$"]);

        var matched = RunAccounting.MatchFiles(filter, [job], []);

        var file = matched.Matches[job].Single();
        await Assert.That(file.Path).IsEqualTo("pak01_dir.vpk");
        await Assert.That(file.Requested).IsFalse();
        await Assert.That(file.VpkPatterns.Count).IsEqualTo(1);
    }

    [Test]
    public async Task UnmatchedPatternExitsWithNoMatch()
    {
        var failures = new FailureLog();

        var summary = Summarize(FileFilter.FromPatterns(["*.dll", "*.exe"]), [Job(1, "a.dll")], failures);

        await Assert.That(summary.UnmatchedError).Contains("\"*.exe\" matched nothing in 1:10");
        await Assert.That(ExitCodeOf(failures, summary)).IsEqualTo(ExitCodes.NoMatch);
    }

    [Test]
    public async Task UnmatchedErrorCountsManifestsAndSkippedDepotsAndHints()
    {
        var skipped = Failed(3, noAccess: true);

        skipped.Optional = true;

        var summary = Summarize(
            FileFilter.FromPatterns(["*.exe"]),
            [Job(1, "a.dll"), Job(2, "b.dll"), skipped],
            new FailureLog(),
            searchAllHint: "To search every depot: SFD ls 730 --");

        await Assert.That(summary.UnmatchedError).Contains("any of the 2 manifests (1 depot skipped without access)");
        await Assert.That(summary.UnmatchedError).Contains("To search every depot: SFD ls 730 -- '*.exe'");
        await Assert.That(summary.Notes).IsEquivalentTo(["skipped without access: 3"]);
    }

    [Test]
    public async Task PatternsAreNotJudgedWithoutDownloadedManifests()
    {
        var failures = new FailureLog();

        var summary = Summarize(FileFilter.FromPatterns(["*.dll"]), [Failed(1, noAccess: false)], failures);

        await Assert.That(summary.UnmatchedError).IsNull();
    }

    [Test]
    public async Task VpkPatternsThatFailedToReadAreNotUnmatched()
    {
        var filter = FileFilter.FromPatterns(["pak01_dir.vpk:regex:\\.txt$"]);
        var source = filter.VpkPatterns.Single().Source;
        var job = Job(1, "pak01_dir.vpk");

        await Assert.That(Summarize(filter, [job], new FailureLog()).UnmatchedError).Contains($"\"{source}\" matched nothing");
        await Assert.That(Summarize(filter, [job], new FailureLog(), failedVpkPatterns: [source]).UnmatchedError).IsNull();
    }

    [Test]
    public async Task LegacyItemIsRequestedByItsFileName()
    {
        var matching = Legacy("map.bsp");
        var other = Legacy("readme.txt");
        var failures = new FailureLog();

        var summary = Summarize(FileFilter.FromPatterns(["*.bsp"]), [], failures, [matching, other]);

        await Assert.That(matching.Requested).IsTrue();
        await Assert.That(other.Requested).IsFalse();
        await Assert.That(summary.UnmatchedError).IsNull();
        await Assert.That(summary.Notes).IsEquivalentTo(["nothing matched in 77:legacy"]);
    }

    [Test]
    public async Task TargetWhoseManifestsAllFailedFailsAsAWhole()
    {
        var first = Failed(1, noAccess: true);
        var second = Failed(2, noAccess: true);
        var failures = new FailureLog();

        var summary = Summarize(FileFilter.FromPatterns([]), [first, second], failures, allTargets: new() { ["app 730"] = [first, second] });

        await Assert.That(failures.FailedTargets).IsEquivalentTo(["app 730 (2 depots)"]);
        await Assert.That(failures.FailedOtherwise).IsFalse();
        await Assert.That(failures.FailedForAccess).IsTrue();
        await Assert.That(ExitCodeOf(failures, summary)).IsEqualTo(ExitCodes.NoDepotAccess);
    }

    [Test]
    public async Task TargetFailsOtherwiseWhenOneManifestFailedForAnotherReason()
    {
        var first = Failed(1, noAccess: true);
        var second = Failed(2, noAccess: false);
        var failures = new FailureLog();

        var summary = Summarize(FileFilter.FromPatterns([]), [first, second], failures, allTargets: new() { ["app 730"] = [first, second] });

        await Assert.That(failures.FailedOtherwise).IsTrue();
        await Assert.That(ExitCodeOf(failures, summary)).IsEqualTo(ExitCodes.Failed);
    }

    [Test]
    public async Task TargetWithADownloadedManifestDoesNotFail()
    {
        var failed = Failed(1, noAccess: true);
        var downloaded = Job(2, "a.dll");
        var failures = new FailureLog();

        failed.Optional = true;

        var summary = Summarize(FileFilter.FromPatterns([]), [failed, downloaded], failures, allTargets: new() { ["app 730"] = [failed, downloaded] });

        await Assert.That(failures.Failed).IsFalse();
        await Assert.That(summary.Notes).IsEquivalentTo(["skipped without access: 1"]);
        await Assert.That(ExitCodeOf(failures, summary)).IsEqualTo(ExitCodes.Ok);
    }

    [Test]
    public async Task WholeTargetFailureIsNotAlsoSkipped()
    {
        var first = Failed(1, noAccess: true);
        var second = Failed(2, noAccess: true);
        var failures = new FailureLog();

        first.Optional = true;
        second.Optional = true;

        var summary = Summarize(FileFilter.FromPatterns([]), [first, second], failures, allTargets: new() { ["app 730"] = [first, second] });

        await Assert.That(summary.Notes).IsEmpty();
        await Assert.That(failures.Failed).IsTrue();
    }

    [Test]
    public async Task FailureWithUnmatchedPatternKeepsTheFailureExitCode()
    {
        var failures = new FailureLog();

        failures.Add("1:10", noAccess: true);

        var summary = Summarize(FileFilter.FromPatterns(["*.exe"]), [Job(1, "a.dll")], failures);

        await Assert.That(summary.UnmatchedError).IsNotNull();
        await Assert.That(ExitCodeOf(failures, summary)).IsEqualTo(ExitCodes.NoDepotAccess);
    }

    [Test]
    public async Task FailureWithoutTargetIsAnotherFailure()
    {
        var failures = new FailureLog();

        failures.Add(noAccess: true);
        failures.Add();

        await Assert.That(failures.FailedOtherwise).IsTrue();
        await Assert.That(failures.FailedForAccess).IsFalse();
        await Assert.That(failures.FailedTargets).IsEmpty();
    }

    [Test]
    public async Task DoneLineCountsRowsByWhatTheyAre()
    {
        string[] notes = ["skipped without access: 3"];

        var files = RunAccounting.DescribeDone(5, hideRows: true, vpkOnly: false, jobCount: 2, manifestCount: 2, legacyCount: 0, [], "out");
        var entries = RunAccounting.DescribeDone(1, hideRows: false, vpkOnly: true, jobCount: 1, manifestCount: 1, legacyCount: 0, [], null);
        var rows = RunAccounting.DescribeDone(2, hideRows: false, vpkOnly: false, jobCount: 1, manifestCount: 1, legacyCount: 1, notes, null);

        await Assert.That(files).Contains("5 files").And.Contains("Files are in out");
        await Assert.That(entries).Contains("1 VPK entry").And.DoesNotContain("Files are in");
        await Assert.That(rows).Contains("2 rows").And.Contains("1 legacy item").And.Contains("skipped without access: 3");
    }
}
