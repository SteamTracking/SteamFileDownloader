namespace SteamFileDownloader.Tests;

public class SharedProblemsTests
{
    private static ManifestJob Job(uint depotID, ulong manifestID, bool optional = false, bool failureIsFine = false)
    {
        var job = TestManifests.Job("out", depotID);
        job.ManifestID = manifestID;
        job.Optional = optional;
        job.FailureIsFine = failureIsFine;

        return job;
    }

    [Test]
    public async Task ProblemsAreReportedOncePerDepotAndOptionalOnesWithoutAccessAreSilent()
    {
        const string NoKey = "Steam gave no depot key in app 730 (AccessDenied): this account may not own the depot, or it belongs to another app.";

        SteamSession.ReportSharedProblems([
            (Job(1, 11), NoKey, true),
            (Job(1, 12), NoKey, true),
            (Job(3, 31, optional: true), NoKey, true),
            (Job(2, 21, failureIsFine: true), "Failed to get the depot key: timed out", false),
        ]);

        var lines = TestContext.Current!.GetErrorOutput().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        await Assert.That(lines).IsEquivalentTo([
            $"[ERROR] Depot 1 (2 manifests): {NoKey}",
            "[WARN] [2:21] Failed to get the depot key: timed out",
        ]);
    }
}
