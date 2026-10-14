using System.Text.Json;
using SteamKit2;

namespace SteamFileDownloader.Tests;

public class TrackCommandTests
{
    private static FileFilter FilterOf(params string[] patterns)
    {
        using var temp = new TempDirectory();
        var path = temp.Write("files.json", $"{{ \"1\": {JsonSerializer.Serialize(patterns)} }}");

        return FileFilter.LoadFilesJson(path)[1];
    }

    private static ManifestJob TrackedJob(uint depotID, FileFilter? filter, params DepotManifest.FileData[] files)
    {
        var job = TestManifests.Job("out", depotID, files);
        job.Filter = filter;

        return job;
    }

    private static DepotManifest.FileData File(string name) => TestManifests.File(name);

    private static List<string> Names(List<FileJob> files) => [.. files.Select(static f => FileFilter.NormalizePath(f.File.FileName))];

    [Test]
    public async Task AnonymousWithAPasswordIsRejected()
    {
        await Assert.That(TrackCommand.ParseArguments(["--appid", "1", "--output", "out", "--username", "anonymous", "--password", "x"])).IsNull();
        await Assert.That(TrackCommand.ParseArguments(["--appid", "1", "--output", "out", "--username", "Anonymous", "--password", "x"])).IsNull();
        await Assert.That(TrackCommand.ParseArguments(["--appid", "1", "--output", "out", "--username", "anonymous"])?.Username).IsEqualTo("anonymous");
    }

    [Test]
    public async Task MatchingFilesOfTrackedDepotsAreSelected()
    {
        var job = TrackedJob(1, FilterOf("game/bin/server.dll", "game/*.txt"),
            File("game\\bin\\server.dll"),
            File("game\\bin\\client.dll"),
            File("game\\readme.txt"),
            File("game\\sub\\notes.txt"));

        var (files, vpkDirectories) = TrackCommand.CollectFiles([job]);

        await Assert.That(Names(files)).IsEquivalentTo(["game/bin/server.dll", "game/readme.txt"]);
        await Assert.That(files.All(f => f.Job == job)).IsTrue();
        await Assert.That(vpkDirectories).IsEmpty();
    }

    [Test]
    public async Task DirectoriesAreNeverSelected()
    {
        var job = TrackedJob(1, FilterOf("game/**"), TestManifests.Directory("game\\bin"), File("game\\bin\\server.dll"));

        var (files, _) = TrackCommand.CollectFiles([job]);

        await Assert.That(Names(files)).IsEquivalentTo(["game/bin/server.dll"]);
    }

    [Test]
    public async Task JobsWithoutAFilterOrManifestAreSkipped()
    {
        var untracked = TrackedJob(2, null, File("game\\bin\\server.dll"));
        var failed = TrackedJob(3, FilterOf("game/bin/server.dll"));
        failed.Manifest = null;

        var (files, vpkDirectories) = TrackCommand.CollectFiles([untracked, failed]);

        await Assert.That(files).IsEmpty();
        await Assert.That(vpkDirectories).IsEmpty();
    }

    [Test]
    public async Task VpkDirectoryFilesAreSelectedWithTheirPatterns()
    {
        var job = TrackedJob(1, FilterOf("pak01_dir.vpk:regex:\\.vmt$", "game/pak01_dir.vpk:models/**", "other_dir.vpk:x"),
            File("game\\pak01_dir.vpk"),
            File("game\\pak01_000.vpk"));

        var (files, vpkDirectories) = TrackCommand.CollectFiles([job]);

        await Assert.That(Names(files)).IsEquivalentTo(["game/pak01_dir.vpk"]);
        await Assert.That(vpkDirectories.Keys.Single()).IsSameReferenceAs(files[0].File);
        await Assert.That(vpkDirectories[files[0].File].Select(static p => p.Source)).IsEquivalentTo(["pak01_dir.vpk:regex:\\.vmt$", "game/pak01_dir.vpk:models/**"]);
    }

    [Test]
    public async Task FileMatchingAPatternAndAVpkPatternIsSelectedOnce()
    {
        var job = TrackedJob(1, FilterOf("game/*.vpk", "pak01_dir.vpk:x"),
            File("game\\pak01_dir.vpk"),
            File("game\\pak01_000.vpk"));

        var (files, vpkDirectories) = TrackCommand.CollectFiles([job]);

        await Assert.That(Names(files)).IsEquivalentTo(["game/pak01_dir.vpk", "game/pak01_000.vpk"]);
        await Assert.That(vpkDirectories.Count).IsEqualTo(1);
    }

    [Test]
    public async Task FileMatchingTwoPatternsIsSelectedOnceAndCountsForBoth()
    {
        var job = TrackedJob(1, FilterOf("game/bin/server.dll", "game/**/*.dll", "game/missing.dll"), File("game\\bin\\server.dll"));

        var (files, _) = TrackCommand.CollectFiles([job]);

        var stderr = TestContext.Current!.GetErrorOutput();
        await Assert.That(Names(files)).IsEquivalentTo(["game/bin/server.dll"]);
        await Assert.That(stderr).Contains("\"game/missing.dll\"");
        await Assert.That(stderr).DoesNotContain("\"game/bin/server.dll\"");
        await Assert.That(stderr).DoesNotContain("\"game/**/*.dll\"");
    }

    [Test]
    public async Task EachJobUsesItsOwnFilter()
    {
        var first = TrackedJob(1, FilterOf("a.dll"), File("a.dll"), File("b.dll"));
        var second = TrackedJob(2, FilterOf("b.dll"), File("a.dll"), File("b.dll"));

        var (files, _) = TrackCommand.CollectFiles([first, second]);

        await Assert.That(files.Select(static f => $"{f.Job.DepotID}/{f.File.FileName}")).IsEquivalentTo(["1/a.dll", "2/b.dll"]);
    }

    [Test]
    public async Task MissingOutputIsReported()
    {
        await Assert.That(TrackCommand.ParseArguments(["--appid", "730"])).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains("track needs --output <dir>");
    }

    [Test]
    public async Task MissingAppIdIsReported()
    {
        await Assert.That(TrackCommand.ParseArguments(["--output", "."])).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains("track needs --appid <app>");
    }

    [Test]
    public async Task NonNumericAppIdIsReported()
    {
        await Assert.That(TrackCommand.ParseArguments(["--appid", "cs2", "--output", "."])).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains("--appid needs a numeric app id, got \"cs2\".");
    }

    [Test]
    public async Task NumericPositionalIsPointedAtAppId()
    {
        await Assert.That(TrackCommand.ParseArguments(["730", "--output", "."])).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains("track takes the app as --appid, like: ").And.Contains("track --appid 730 --output .");
    }

    [Test]
    [Arguments(new uint[] { 2347771 }, true, false, "Failed (exit 1) after 00:01:02: tracked depot 2347771 has no manifest; steam_buildid.txt not written. Rerun to retry.")]
    [Arguments(new uint[] { 1, 2 }, false, false, "Failed (exit 1) after 00:01:02: tracked depots 1, 2 have no manifest. Rerun to retry.")]
    [Arguments(new uint[] { 1, 2, 3, 4, 5, 6, 7 }, false, false, "Failed (exit 1) after 00:01:02: tracked depots 1, 2, 3, 4, 5 and 2 more have no manifest. Rerun to retry.")]
    [Arguments(new uint[0], true, false, "Failed (exit 1) after 00:01:02: some manifests or files failed, see above; steam_buildid.txt not written.")]
    [Arguments(new uint[0], false, true, "Failed (exit 1) after 00:01:02: some manifests or files failed, see above. Rerun to retry.")]
    public async Task FailureIsOneLine(uint[] failedDepots, bool hasBuildId, bool writeFailed, string expected)
    {
        await Assert.That(TrackCommand.DescribeFailure("00:01:02", [.. failedDepots], hasBuildId, writeFailed)).IsEqualTo(expected);
    }
}
