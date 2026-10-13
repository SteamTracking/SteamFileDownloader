namespace SteamFileDownloader.Tests;

public class ManifestWriterTests
{
    private const string Label = "Manifest ID / date     : ";

    private static string ManifestText(uint depotID, string idText) =>
        $"Content Manifest for Depot {depotID} \n\n{Label}{idText} / 10/12/2026 12:00:00 \nTotal number of files  : 1 \n";

    [Test]
    public async Task ReadsIdsOfEachDepot()
    {
        using var temp = new TempDirectory();
        temp.Write("manifest_228990.txt", ManifestText(228990, "7617088375292372759"));
        temp.Write("manifest_228991.txt", ManifestText(228991, "42"));

        var ids = ManifestWriter.ReadManifestIds(temp.Path);

        await Assert.That(ids.Count).IsEqualTo(2);
        await Assert.That(ids[228990]).IsEqualTo(7617088375292372759ul);
        await Assert.That(ids[228991]).IsEqualTo(42ul);
    }

    [Test]
    public async Task ReadsTheIdsOfWrittenManifests()
    {
        using var temp = new TempDirectory();
        ManifestWriter.DumpToTextFile(temp.Path, TestManifests.Job(temp.Path, 228990, TestManifests.File("a.dll", [1, 2, 3])));
        ManifestWriter.DumpToTextFile(temp.Path, TestManifests.Job(temp.Path, 228991, TestManifests.File("game\\b.dll", [4]), TestManifests.File("c.txt")));

        var ids = ManifestWriter.ReadManifestIds(Path.Combine(temp.Path, "manifests"));

        await Assert.That(ids).IsEquivalentTo(new Dictionary<uint, ulong> { [228990] = 2289900, [228991] = 2289910 });
    }

    [Test]
    public async Task ReadsIdWithWindowsLineEndings()
    {
        using var temp = new TempDirectory();
        temp.Write("manifest_1.txt", ManifestText(1, "123").Replace("\n", "\r\n"));

        await Assert.That(ManifestWriter.ReadManifestIds(temp.Path)[1]).IsEqualTo(123ul);
    }

    [Test]
    [Arguments("manifest_abc.txt")]
    [Arguments("manifest_.txt")]
    [Arguments("manifest_-1.txt")]
    [Arguments("manifest_1.bak.txt")]
    [Arguments("other_5.txt")]
    [Arguments("manifest_5.json")]
    public async Task IgnoresFilesThatAreNotDepotManifests(string name)
    {
        using var temp = new TempDirectory();
        temp.Write("manifest_7.txt", ManifestText(7, "70"));
        temp.Write(name, "not a manifest");

        var ids = ManifestWriter.ReadManifestIds(temp.Path);

        await Assert.That(ids.Keys).IsEquivalentTo([7u]);
    }

    [Test]
    public async Task ThrowsOnManifestWithoutIdLine()
    {
        using var temp = new TempDirectory();
        temp.Write("manifest_7.txt", "Content Manifest for Depot 7 \n\nTotal number of files  : 1 \n");

        await Assert.That(() => ManifestWriter.ReadManifestIds(temp.Path)).Throws<InvalidDataException>();
    }

    [Test]
    [Arguments("0")]
    [Arguments("abc")]
    [Arguments("-5")]
    [Arguments("")]
    public async Task ThrowsOnInvalidId(string idText)
    {
        using var temp = new TempDirectory();
        temp.Write("manifest_7.txt", ManifestText(7, idText));

        await Assert.That(() => ManifestWriter.ReadManifestIds(temp.Path)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task ThrowsOnFolderWithoutManifests()
    {
        using var temp = new TempDirectory();

        await Assert.That(() => ManifestWriter.ReadManifestIds(temp.Path)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task ThrowsOnFolderWithOnlyIgnoredFiles()
    {
        using var temp = new TempDirectory();
        temp.Write("manifest_abc.txt", ManifestText(1, "5"));

        await Assert.That(() => ManifestWriter.ReadManifestIds(temp.Path)).Throws<InvalidDataException>();
    }
}
