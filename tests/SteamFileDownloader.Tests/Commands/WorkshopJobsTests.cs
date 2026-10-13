using SteamKit2;
using static SteamFileDownloader.Tests.FakeWorkshopSource;

namespace SteamFileDownloader.Tests;

public class WorkshopJobsTests
{
    private static async Task<GetCommand.Run> Resolve(FakeWorkshopSource source, params string[] items)
    {
        // A folder that doesn't exist, so no earlier downloads are found to reuse
        var outputRoot = Path.Combine(Path.GetTempPath(), "SteamFileDownloader.Tests", Guid.NewGuid().ToString("N"));
        var run = new GetCommand.Run { Filter = FileFilter.FromPatterns([]), Session = null!, OutputRoot = outputRoot };
        var targets = items.Select(static i => WorkshopArgumentParser.ParseTarget(i)!).ToList();

        await WorkshopCommands.ResolveJobsAsync(run, source, targets, CancellationToken.None);

        return run;
    }

    private static ManifestJob Job(GetCommand.Run run, ulong itemID) => run.Jobs.Single(j => j.ItemID == itemID);

    [Test]
    public async Task ItemIsTheManifestInTheAppsWorkshopDepot()
    {
        var source = new FakeWorkshopSource().Add(Item(10, manifest: 1000, app: 730)).AddApp(730, 731);

        var run = await Resolve(source, "10");

        var job = run.Jobs.Single();
        await Assert.That(job.AppID).IsEqualTo(730u);
        await Assert.That(job.DepotID).IsEqualTo(731u);
        await Assert.That(job.ManifestID).IsEqualTo(1000ul);
        await Assert.That(job.Branch).IsEqualTo("public");
        await Assert.That(job.ToString()).IsEqualTo("10:1000");
        await Assert.That(job.OutputFolder).IsEqualTo(Path.Combine(run.OutputRoot, "workshop", "10", "1000"));
        await Assert.That(job.Optional).IsFalse();
        await Assert.That(run.Failed).IsFalse();
    }

    [Test]
    public async Task ItemWithoutAManifestInItsDetailsUsesTheNewestInItsHistory()
    {
        var source = new FakeWorkshopSource().Add(Item(10, manifest: 0)).AddApp(730, 730);
        source.Histories[10] = [Change(1002, 20), Change(1001, 10)];

        var run = await Resolve(source, "10");

        await Assert.That(run.Jobs.Single().ManifestID).IsEqualTo(1002ul);
    }

    [Test]
    public async Task OlderVersionIsTheManifestAskedFor()
    {
        var source = new FakeWorkshopSource().Add(Item(10, manifest: 1002)).AddApp(730, 730);
        source.Histories[10] = [Change(1002, 20), Change(1001, 10)];

        var run = await Resolve(source, "10:1001", "10:latest");

        await Assert.That(run.Jobs.Select(static j => j.ManifestID)).IsEquivalentTo([1001ul, 1002ul], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task ManifestOutsideTheHistoryIsStillTried()
    {
        var source = new FakeWorkshopSource().Add(Item(10, manifest: 1002)).AddApp(730, 730);
        source.Histories[10] = [Change(1002, 20)];

        var run = await Resolve(source, "10:555");

        await Assert.That(run.Jobs.Single().ManifestID).IsEqualTo(555ul);
        await Assert.That(TestContext.Current!.GetStandardOutput() + TestContext.Current.GetErrorOutput()).Contains("not in the newest changes of item 10");
    }

    [Test]
    public async Task CollectionItemsAreOptionalAndGroupedUnderTheCollection()
    {
        var source = new FakeWorkshopSource()
            .Add(
                Collection(1, (10, 1), (11, 2), (12, 3), (13, 4)),
                Item(10, manifest: 1000),
                Unavailable(11, EResult.AccessDenied),
                Item(12, manifest: 1200, flags: 0),
                Item(13, manifest: 1300))
            .AddApp(730, 730);

        var run = await Resolve(source, "1");

        await Assert.That(run.Jobs.Select(static j => j.ToString())).IsEquivalentTo(["10:1000", "13:1300"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(run.Jobs.All(static j => j.Optional)).IsTrue();
        await Assert.That(run.AllTargets["1"].Select(static j => j.ToString())).IsEquivalentTo(run.Jobs.Select(static j => j.ToString()), TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(run.Failed).IsFalse();
    }

    [Test]
    public async Task ItemGivenDirectlyAndInACollectionIsOneJobThatMustSucceed()
    {
        var source = new FakeWorkshopSource().Add(Collection(1, (10, 1), (13, 2)), Item(10, manifest: 1000), Item(13, manifest: 1300)).AddApp(730, 730);

        var run = await Resolve(source, "1", "10");

        await Assert.That(run.Jobs.Count).IsEqualTo(2);
        await Assert.That(Job(run, 10).Optional).IsFalse();
        await Assert.That(Job(run, 13).Optional).IsTrue();
        await Assert.That(run.AllTargets["1"].Count).IsEqualTo(2);
    }

    [Test]
    public async Task CollectionWithoutDownloadableItemsFails()
    {
        var source = new FakeWorkshopSource().Add(Collection(1, (11, 1), (12, 2)), Unavailable(11, EResult.AccessDenied), Item(12, manifest: 1200, flags: 0));

        var run = await Resolve(source, "1");

        await Assert.That(run.Jobs).IsEmpty();
        await Assert.That(run.FailedTargets).IsEquivalentTo(["1"]);
    }

    [Test]
    public async Task CollectionWithAManifestFails()
    {
        var source = new FakeWorkshopSource().Add(Collection(1, (10, 1)), Item(10, manifest: 1000)).AddApp(730, 730);

        var run = await Resolve(source, "1:1000");

        await Assert.That(run.Jobs).IsEmpty();
        await Assert.That(run.FailedTargets).IsEquivalentTo(["1:1000"]);
    }

    [Test]
    public async Task PrivateItemFailsForLackOfAccessOnly()
    {
        var source = new FakeWorkshopSource().Add(Unavailable(11, EResult.AccessDenied));

        var run = await Resolve(source, "11");

        await Assert.That(run.FailedTargets).IsEquivalentTo(["11"]);
        await Assert.That(run.FailedOtherwise).IsFalse();
    }

    [Test]
    [Arguments("12")]
    [Arguments("99")]
    [Arguments("20")]
    public async Task LegacyMissingAndDepotlessItemsFail(string item)
    {
        var source = new FakeWorkshopSource().Add(Item(12, manifest: 1200, flags: 0), Item(20, manifest: 2000, app: 550)).AddApp(730, 730);

        var run = await Resolve(source, item);

        await Assert.That(run.Jobs).IsEmpty();
        await Assert.That(run.FailedTargets).IsEquivalentTo([item]);
        await Assert.That(run.FailedOtherwise).IsTrue();
    }
}
