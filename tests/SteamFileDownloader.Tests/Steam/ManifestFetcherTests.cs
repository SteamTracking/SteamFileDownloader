using SteamKit2;

namespace SteamFileDownloader.Tests;

public class ManifestFetcherTests
{
    private static ManifestFetcher CreateFetcher(FakeManifestSource source, TempDirectory temp) =>
        new(source, new DepotCache(Path.Combine(temp.Path, "depot_keys.json")), CancellationToken.None);

    private static ManifestJob Job(TempDirectory temp, uint depotID = 7, ulong manifestID = 70) =>
        TestManifests.PendingJob(temp.Path, depotID, manifestID);

    [Test]
    public async Task SavedManifestNeedsNoRequestCode()
    {
        using var temp = new TempDirectory();

        var firstSource = new FakeManifestSource();
        var firstJob = Job(temp);
        await CreateFetcher(firstSource, temp).GetManifestsAsync([firstJob]);

        await Assert.That(firstJob.Manifest).IsNotNull();
        await Assert.That(firstSource.Downloads).Count().IsEqualTo(1);

        var source = new FakeManifestSource();
        var job = Job(temp);
        await CreateFetcher(source, temp).GetManifestsAsync([job]);

        await Assert.That(job.Manifest).IsNotNull();
        await Assert.That(source.CodeRequests).IsEmpty();
        await Assert.That(source.Downloads).IsEmpty();
    }

    [Test]
    public async Task RefusalOnceGetsAFreshRequestCodeFromAnotherServer()
    {
        using var temp = new TempDirectory();
        var source = new FakeManifestSource
        {
            Download = static (count, job) => count == 0 ? throw FakeManifestSource.Refusal() : FakeManifestSource.ServeManifest(job),
        };
        var job = Job(temp);

        await CreateFetcher(source, temp).GetManifestsAsync([job]);

        await Assert.That(job.Manifest).IsNotNull();
        await Assert.That(source.CodeRequests).Count().IsEqualTo(2);
        await Assert.That(source.Downloads.Select(static d => d.Code)).IsEquivalentTo([1UL, 2UL]);
        await Assert.That(source.Downloads[1].Server).IsNotEqualTo(source.Downloads[0].Server);
    }

    [Test]
    public async Task RefusalByASecondServerMeansTheCdnDoesNotServeIt()
    {
        using var temp = new TempDirectory();
        var source = new FakeManifestSource
        {
            Download = static (_, _) => throw FakeManifestSource.Refusal(),
        };
        var job = Job(temp);

        await CreateFetcher(source, temp).GetManifestsAsync([job]);

        await Assert.That(job.Manifest).IsNull();
        await Assert.That(source.Downloads).Count().IsEqualTo(2);
        await Assert.That(source.CodeRequests).Count().IsEqualTo(2);
        await Assert.That(TestLog.GetProblemLines().Any(static l => l.Contains("no longer served", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task DeniedDepotKeyLeavesNoManifestAndAProblemSharedWithTheDepot()
    {
        using var temp = new TempDirectory();
        var source = new FakeManifestSource { KeyAnswer = new DepotKeyAnswer(EResult.AccessDenied, []) };
        var jobs = new List<ManifestJob> { Job(temp, 7, 70), Job(temp, 7, 71) };

        await CreateFetcher(source, temp).GetManifestsAsync(jobs);

        await Assert.That(jobs.All(static j => j.NoAccess)).IsTrue();
        await Assert.That(jobs.All(static j => j.Manifest == null)).IsTrue();
        await Assert.That(source.CodeRequests).IsEmpty();
        await Assert.That(source.Downloads).IsEmpty();
        await Assert.That(TestLog.GetProblemLines()).Count().IsEqualTo(1);
    }

    [Test]
    public async Task OptionalJobWithoutAccessIsSilent()
    {
        using var temp = new TempDirectory();
        var source = new FakeManifestSource { KeyAnswer = new DepotKeyAnswer(EResult.AccessDenied, []) };
        var job = Job(temp);
        job.Optional = true;

        await CreateFetcher(source, temp).GetManifestsAsync([job]);

        await Assert.That(job.Manifest).IsNull();
        await Assert.That(job.NoAccess).IsTrue();
        await Assert.That(TestLog.GetProblemLines()).IsEmpty();
    }

    [Test]
    public async Task JobsOfOneDepotRequestTheKeyOnce()
    {
        using var temp = new TempDirectory();
        var source = new FakeManifestSource();
        var jobs = new List<ManifestJob> { Job(temp, 7, 70), Job(temp, 7, 71) };

        await CreateFetcher(source, temp).GetManifestsAsync(jobs);

        await Assert.That(source.KeyRequests).Count().IsEqualTo(1);
        await Assert.That(jobs.All(static j => j.Manifest != null)).IsTrue();
    }
}
