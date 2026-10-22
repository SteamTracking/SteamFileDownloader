using SteamKit2;

namespace SteamFileDownloader.Tests;

public class ManifestJobTests
{
    private static string Folder(string parent, string name, DateTime lastWrite)
    {
        var path = Path.Combine(parent, name);
        Directory.CreateDirectory(path);
        Directory.SetLastWriteTimeUtc(path, lastWrite);

        return path;
    }

    [Test]
    public async Task ReuseFoldersAreTheOtherManifestsNewestFirst()
    {
        using var temp = new TempDirectory();
        var older = Folder(temp.Path, "111", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var newer = Folder(temp.Path, "222", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Folder(temp.Path, "333", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var job = ManifestJob.Create(730, 731, 333, "public", temp.Path);

        await Assert.That(job.OutputFolder).IsEqualTo(Path.Combine(temp.Path, "333"));
        await Assert.That(job.ReuseFolders).IsEquivalentTo([newer, older], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task NoReuseFoldersWithoutTheParentFolder()
    {
        using var temp = new TempDirectory();

        var job = ManifestJob.Create(730, 731, 333, "public", Path.Combine(temp.Path, "missing"));

        await Assert.That(job.ReuseFolders).IsEmpty();
    }

    [Test]
    public async Task ManifestAddedTwiceIsOneJobThatCountsWhenAnyTargetNamedIt()
    {
        using var temp = new TempDirectory();
        var run = new GetCommand.Run { Filter = FileFilter.FromPatterns([]), Session = null!, OutputRoot = temp.Path };

        var fromAll = run.AddJob(730, null, 731, 333, "public", optional: true, temp.Path);
        var explicitJob = run.AddJob(730, null, 731, 333, "public", optional: false, temp.Path);
        var fromAllAgain = run.AddJob(730, null, 731, 333, "public", optional: true, temp.Path);

        await Assert.That(explicitJob).IsSameReferenceAs(fromAll);
        await Assert.That(fromAllAgain).IsSameReferenceAs(fromAll);
        await Assert.That(run.Jobs.Count).IsEqualTo(1);
        await Assert.That(fromAll.Optional).IsFalse();
    }

    [Test]
    public async Task AddJobKeysADlcDepotWithItsDlc()
    {
        using var temp = new TempDirectory();
        var run = new GetCommand.Run { Filter = FileFilter.FromPatterns([]), Session = null!, OutputRoot = temp.Path };
        var depot = KeyValue.LoadFromString("\"4784\" { \"dlcappid\" \"4780\" }")!;

        var job = run.AddJob(4700, depot, 4784, 333, "public", optional: true, temp.Path);

        await Assert.That(job.AppID).IsEqualTo(4700u);
        await Assert.That(job.KeyAppID).IsEqualTo(4780u);
    }

    [Test]
    public async Task AddJobKeysADepotWithoutInfoWithTheApp()
    {
        using var temp = new TempDirectory();
        var run = new GetCommand.Run { Filter = FileFilter.FromPatterns([]), Session = null!, OutputRoot = temp.Path };

        var job = run.AddJob(4700, null, 4784, 333, "public", optional: true, temp.Path);

        await Assert.That(job.KeyAppID).IsEqualTo(4700u);
    }
}
