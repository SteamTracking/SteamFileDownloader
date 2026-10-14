using SteamKit2;
using ValvePak;

namespace SteamFileDownloader.Tests;

public class VpkReaderTests
{
    private const uint ChunkSize = 100;

    private static DepotManifest.ChunkData Chunk(ulong offset, uint length = ChunkSize) =>
        new([(byte)(offset / ChunkSize)], 0, offset, length, length);

    private static DepotManifest.FileData ArchiveOf(params DepotManifest.ChunkData[] chunks)
    {
        var file = new DepotManifest.FileData("pak01_000.vpk", [], EDepotFileFlag.UserConfig, (ulong)chunks.Sum(static c => c.UncompressedLength), [], "", false, chunks.Length);
        file.Chunks.AddRange(chunks);

        return file;
    }

    private static PackageEntry Entry(uint offset, uint length) => new() { TypeName = "t", DirectoryName = "d", FileName = "f", Offset = offset, Length = length };

    private static DepotManifest.FileData FourChunks() => ArchiveOf(Chunk(0), Chunk(100), Chunk(200), Chunk(300));

    private static async Task AssertOffsets(List<DepotManifest.ChunkData> chunks, params ulong[] offsets) =>
        await Assert.That(chunks.Select(static c => c.Offset)).IsEquivalentTo(offsets);

    [Test]
    public async Task EntryInsideOneChunkNeedsOnlyThatChunk()
    {
        await AssertOffsets(VpkReader.FindChunks(FourChunks(), [Entry(110, 30)]), 100);
    }

    [Test]
    public async Task EntrySpanningChunksNeedsEachOfThem()
    {
        await AssertOffsets(VpkReader.FindChunks(FourChunks(), [Entry(150, 200)]), 100, 200, 300);
    }

    [Test]
    public async Task EntryStartingAtAChunkBoundarySkipsThePreviousChunk()
    {
        await AssertOffsets(VpkReader.FindChunks(FourChunks(), [Entry(200, 10)]), 200);
    }

    [Test]
    public async Task EntryEndingAtAChunkBoundaryDoesNotNeedTheNextChunk()
    {
        await AssertOffsets(VpkReader.FindChunks(FourChunks(), [Entry(150, 50)]), 100);
    }

    [Test]
    public async Task EntryCoveringWholeChunksNeedsExactlyThose()
    {
        await AssertOffsets(VpkReader.FindChunks(FourChunks(), [Entry(100, 200)]), 100, 200);
    }

    [Test]
    public async Task UnsortedChunksAreReturnedByOffset()
    {
        var archive = ArchiveOf(Chunk(300), Chunk(100), Chunk(0), Chunk(200));

        await AssertOffsets(VpkReader.FindChunks(archive, [Entry(50, 300)]), 0, 100, 200, 300);
    }

    [Test]
    public async Task EntriesSharingAChunkDoNotDuplicateIt()
    {
        await AssertOffsets(VpkReader.FindChunks(FourChunks(), [Entry(110, 10), Entry(150, 10), Entry(250, 100)]), 100, 200, 300);
    }

    [Test]
    public async Task EntriesInDifferentChunksNeedBoth()
    {
        await AssertOffsets(VpkReader.FindChunks(FourChunks(), [Entry(310, 10), Entry(10, 10)]), 0, 300);
    }

    [Test]
    public async Task ZeroLengthEntryNeedsAtMostTheChunkItIsIn()
    {
        await AssertOffsets(VpkReader.FindChunks(FourChunks(), [Entry(150, 0)]), 100);
        await AssertOffsets(VpkReader.FindChunks(FourChunks(), [Entry(200, 0)]));
    }

    [Test]
    public async Task ChunksOfUnevenSizeAreMatchedByTheirEnds()
    {
        var archive = ArchiveOf(Chunk(0, 10), Chunk(10, 500), Chunk(510, 5));

        await AssertOffsets(VpkReader.FindChunks(archive, [Entry(505, 8)]), 10, 510);
    }

    [Test]
    [Arguments(0ul, 0)]
    [Arguments(99ul, 0)]
    [Arguments(100ul, 1)]
    [Arguments(250ul, 2)]
    [Arguments(399ul, 3)]
    [Arguments(400ul, 4)]
    [Arguments(5000ul, 4)]
    public async Task FirstChunkEndingAfterTheOffsetIsFound(ulong offset, int expected)
    {
        DepotManifest.ChunkData[] chunks = [Chunk(0), Chunk(100), Chunk(200), Chunk(300)];

        await Assert.That(VpkReader.FindFirstChunkEnding(chunks, offset, static c => c)).IsEqualTo(expected);
    }

    [Test]
    public async Task FirstChunkEndingOfNoItemsIsZero()
    {
        await Assert.That(VpkReader.FindFirstChunkEnding([], 10ul, static (DepotManifest.ChunkData c) => c)).IsEqualTo(0);
    }

    private static Package PackageWith(params string[] paths)
    {
        var package = new Package();

        foreach (var path in paths)
        {
            package.AddFile(path, [1, 2, 3]);
        }

        return package;
    }

    [Test]
    public async Task ListEntriesMatchesEachPatternOfTheDirectoryFile()
    {
        using var package = PackageWith("materials/a.vmt", "materials/b.vtex_c", "models/c.vmdl_c");
        var patterns = FileFilter.FromPatterns(["pak01_dir.vpk:materials/**", "pak01_dir.vpk:regex:\\.v[mt]"]).VpkPatterns;

        var matches = VpkReader.ListEntries(package, patterns);

        await Assert.That(matches.Select(static m => $"{m.Pattern.Source} {m.Path}")).IsEquivalentTo([
            "pak01_dir.vpk:materials/** materials/a.vmt",
            "pak01_dir.vpk:materials/** materials/b.vtex_c",
            "pak01_dir.vpk:regex:\\.v[mt] materials/a.vmt",
            "pak01_dir.vpk:regex:\\.v[mt] materials/b.vtex_c",
            "pak01_dir.vpk:regex:\\.v[mt] models/c.vmdl_c",
        ]);
    }

    [Test]
    public async Task TryReadReadsAWrittenVpkAndRejectsOtherFiles()
    {
        using var temp = new TempDirectory();
        var job = TestManifests.Job(temp.Path, 1);
        var vpkPath = Path.Combine(temp.Path, "pak01_dir.vpk");

        using (var written = PackageWith("materials/a.vmt"))
        {
            written.Write(vpkPath);
        }

        using var package = VpkReader.TryRead(job, vpkPath, "pak01_dir.vpk");
        using var invalid = VpkReader.TryRead(job, temp.Write("bad_dir.vpk", "not a vpk"), "bad_dir.vpk");

        await Assert.That(package!.FindEntry("materials/a.vmt")).IsNotNull();
        await Assert.That(invalid).IsNull();
    }

    private static ManifestJob JobWithArchives(uint appID, uint depotID, double daysFromNow, params string[] names)
    {
        var job = TestManifests.Job("out", depotID, [.. names.Select(static n => TestManifests.File(n))]);
        job.AppID = appID;
        job.Manifest!.CreationTime = TestManifests.CreationTime.AddDays(daysFromNow);

        return job;
    }

    [Test]
    public async Task ArchiveInTheDirectoryFilesOwnManifestIsPreferred()
    {
        var dirJob = JobWithArchives(1, 1, 0, "game\\pak01_dir.vpk", "game\\pak01_000.vpk");
        var other = JobWithArchives(1, 2, 0, "game\\pak01_000.vpk");

        var found = VpkReader.TryFindArchive([other, dirJob], dirJob, "game\\pak01_dir.vpk", 0, out var archiveJob, out var archiveFile);

        await Assert.That(found).IsTrue();
        await Assert.That(archiveJob!).IsSameReferenceAs(dirJob);
        await Assert.That(archiveFile!.FileName).IsEqualTo("game\\pak01_000.vpk");
    }

    [Test]
    [Arguments(new double[] { -10, -1, 0.1 }, 1)]
    [Arguments(new double[] { -10, 0.1, 5 }, 0)]
    [Arguments(new double[] { 3, 0.5, 5 }, 1)]
    [Arguments(new double[] { 0, 2 }, 0)]
    public async Task ArchiveOfAnotherDepotComesFromTheClosestManifestNotNewerThanTheDirectoryFile(double[] days, int expected)
    {
        var dirJob = JobWithArchives(1, 1, 0, "pak01_dir.vpk");
        var others = days.Select((d, i) => JobWithArchives(1, (uint)(i + 2), d, "pak01_002.vpk")).ToList();

        var found = VpkReader.TryFindArchive([dirJob, .. others], dirJob, "pak01_dir.vpk", 2, out var archiveJob, out _);

        await Assert.That(found).IsTrue();
        await Assert.That(archiveJob!).IsSameReferenceAs(others[expected]);
    }

    [Test]
    public async Task ArchiveIsNotTakenFromOtherAppsOrJobsWithoutManifest()
    {
        var dirJob = JobWithArchives(1, 1, 0, "pak01_dir.vpk");
        var otherApp = JobWithArchives(2, 2, 0, "pak01_000.vpk");
        var noManifest = JobWithArchives(1, 3, 0, "pak01_000.vpk");
        noManifest.Manifest = null;

        await Assert.That(VpkReader.TryFindArchive([dirJob, otherApp, noManifest], dirJob, "pak01_dir.vpk", 0, out _, out _)).IsFalse();
    }

    private static PackageEntry InArchive(ushort archive, uint offset, uint length) =>
        new() { TypeName = "t", DirectoryName = "d", FileName = $"{archive}_{offset}", ArchiveIndex = archive, Offset = offset, Length = length };

    private static ManifestJob TwoChunkArchiveJob() =>
        TestManifests.Job("out", 1, TestManifests.File("pak01_dir.vpk"), TestManifests.File("pak01_000.vpk", new byte[100], new byte[100]));

    private static async Task AssertArchiveChunks(List<PartialFile> archives, params ulong[] offsets)
    {
        await Assert.That(archives.Count).IsEqualTo(1);
        await Assert.That(archives[0].File.FileName).IsEqualTo("pak01_000.vpk");
        await Assert.That(archives[0].Chunks.Select(static c => c.Offset)).IsEquivalentTo(offsets);
    }

    [Test]
    public async Task ArchiveChunksSkipDirectoryAndEmptyEntries()
    {
        var dirJob = TwoChunkArchiveJob();

        var (success, archives) = VpkReader.FindArchiveChunks([dirJob], dirJob, "pak01_dir.vpk", [InArchive(0, 120, 10), InArchive(0, 0, 0), InArchive(0x7FFF, 0, 10)]);

        await Assert.That(success).IsTrue();
        await AssertArchiveChunks(archives, 100);
    }

    [Test]
    public async Task ArchiveChunksFailForAMissingArchiveAndKeepTheOthers()
    {
        var dirJob = TwoChunkArchiveJob();

        var (success, archives) = VpkReader.FindArchiveChunks([dirJob], dirJob, "pak01_dir.vpk", [InArchive(0, 120, 10), InArchive(5, 0, 10)]);

        await Assert.That(success).IsFalse();
        await AssertArchiveChunks(archives, 100);
    }

    [Test]
    public async Task ArchiveChunksFailForAnEntryPastTheEndAndKeepTheOthers()
    {
        var dirJob = TwoChunkArchiveJob();

        var (success, archives) = VpkReader.FindArchiveChunks([dirJob], dirJob, "pak01_dir.vpk", [InArchive(0, 120, 10), InArchive(0, 150, 100)]);

        await Assert.That(success).IsFalse();
        await AssertArchiveChunks(archives, 100);
    }

    [Test]
    public async Task EntryEndingAtTheEndOfTheArchiveIsIncluded()
    {
        var dirJob = TwoChunkArchiveJob();

        var (success, archives) = VpkReader.FindArchiveChunks([dirJob], dirJob, "pak01_dir.vpk", [InArchive(0, 150, 50)]);

        await Assert.That(success).IsTrue();
        await AssertArchiveChunks(archives, 100);
    }

    [Test]
    public async Task EntryEndingOneBytePastTheArchiveFails()
    {
        var dirJob = TwoChunkArchiveJob();

        var (success, archives) = VpkReader.FindArchiveChunks([dirJob], dirJob, "pak01_dir.vpk", [InArchive(0, 150, 51)]);

        await Assert.That(success).IsFalse();
        await Assert.That(archives).IsEmpty();
    }

    private const string DirFileName = "game/test_dir.vpk";
    private const string RequestedEntry = "steammessages_base.proto";

    private static readonly string FixtureFolder = Path.Combine(AppContext.BaseDirectory, "Files");

    private sealed record SplitVpk(ManifestJob Job, DepotManifest.FileData Dir, DepotManifest.FileData Archive, FakeChunkSource Source);

    // A depot with the fixture's directory file as one chunk and its archive in chunks of 4 KiB
    private static SplitVpk SplitVpkDepot(string outputFolder)
    {
        var dirData = File.ReadAllBytes(Path.Combine(FixtureFolder, "test_dir.vpk"));
        var archiveChunks = File.ReadAllBytes(Path.Combine(FixtureFolder, "test_000.vpk")).Chunk(4096).ToArray();
        var dir = TestManifests.File("game\\test_dir.vpk", dirData);
        var archive = TestManifests.File("game\\test_000.vpk", archiveChunks);
        var source = new FakeChunkSource();
        source.Add([dirData, .. archiveChunks]);

        return new SplitVpk(TestManifests.Job(outputFolder, 1, dir, archive), dir, archive, source);
    }

    private static byte[] FixtureEntry(string path)
    {
        using var package = new Package();
        package.Read(Path.Combine(FixtureFolder, "test_dir.vpk"));
        package.ReadEntry(package.FindEntry(path)!, out var data);

        return data;
    }

    private static List<DepotManifest.ChunkData> ChunksOverlapping(DepotManifest.FileData archive, PackageEntry entry) =>
        [.. archive.Chunks.Where(c => c.Offset < (ulong)entry.Offset + entry.Length && entry.Offset < c.Offset + c.UncompressedLength)];

    [Test]
    public async Task ExtractingAnEntryOfASplitVpkDownloadsOnlyItsChunksAndARerunNothing()
    {
        using var temp = new TempDirectory();
        var (job, dir, archive, source) = SplitVpkDepot(temp.Path);
        using var downloader = new FileDownloader(source, CancellationToken.None);
        var (_, dirFiles) = await downloader.DownloadAllFiles([new FileJob(job, dir)]);
        using var package = VpkReader.TryRead(job, dirFiles[0].Path, DirFileName)!;
        var matches = VpkReader.ListEntries(package, FileFilter.FromPatterns([$"{DirFileName}:{RequestedEntry}"]).VpkPatterns);
        var callsBefore = source.Calls.Count;

        var (success, paths) = await VpkReader.ExtractAsync(downloader, [job], job, DirFileName, package, matches);

        var path = Path.Combine(temp.Path, "game", "test_dir", RequestedEntry);
        var downloaded = source.Calls.Skip(callsBefore).Select(static c => c.Chunk).ToList();
        await Assert.That(success).IsTrue();
        await Assert.That(paths).IsEquivalentTo([path]);
        await Assert.That(await File.ReadAllBytesAsync(path)).IsEquivalentTo(FixtureEntry(RequestedEntry));
        await Assert.That(File.GetLastWriteTimeUtc(path)).IsEqualTo(TestManifests.CreationTime);
        await Assert.That(downloaded).IsEquivalentTo(ChunksOverlapping(archive, matches.Single().Entry));
        await Assert.That(downloaded.Count).IsLessThan(archive.Chunks.Count);
        await Assert.That(Directory.GetFiles(temp.Path, "*", SearchOption.AllDirectories)).IsEquivalentTo([dirFiles[0].Path, path]);

        var callsAfter = source.Calls.Count;
        var (rerunSuccess, rerunPaths) = await VpkReader.ExtractAsync(downloader, [job], job, DirFileName, package, matches);

        await Assert.That(rerunSuccess).IsTrue();
        await Assert.That(rerunPaths).IsEquivalentTo([path]);
        await Assert.That(source.Calls.Count).IsEqualTo(callsAfter);
    }

    [Test]
    public async Task PartialArchiveOfASplitVpkHoldsOnlyTheRequestedEntries()
    {
        using var temp = new TempDirectory();
        var (job, dir, archive, source) = SplitVpkDepot(temp.Path);
        using var downloader = new FileDownloader(source, CancellationToken.None);
        var (_, dirFiles) = await downloader.DownloadAllFiles([new FileJob(job, dir)]);
        List<PartialFile> archives;
        PackageEntry entry;

        using (var package = VpkReader.TryRead(job, dirFiles[0].Path, DirFileName)!)
        {
            entry = package.FindEntry(RequestedEntry)!;
            (var found, archives) = VpkReader.FindArchiveChunks([job], job, DirFileName, [entry]);
            await Assert.That(found).IsTrue();
        }

        var callsBefore = source.Calls.Count;
        var success = await downloader.DownloadPartialFiles(archives);
        var downloaded = source.Calls.Skip(callsBefore).Select(static c => c.Chunk).ToList();

        await Assert.That(success).IsTrue();
        await Assert.That(downloaded).IsEquivalentTo(ChunksOverlapping(archive, entry));

        // Read like VRF does, from the directory file next to the partial archive
        using (var partial = new Package())
        {
            partial.Read(dirFiles[0].Path);
            partial.ReadEntry(partial.FindEntry(RequestedEntry)!, out var data);

            await Assert.That(data).IsEquivalentTo(FixtureEntry(RequestedEntry));
            await Assert.That(() => partial.ReadEntry(partial.FindEntry("kitten.jpg")!, out _)).Throws<InvalidDataException>();
            await Assert.That(() => partial.ReadEntry(partial.FindEntry("steammessages_clientserver.proto")!, out _)).Throws<InvalidDataException>();
        }

        var callsAfter = source.Calls.Count;

        await Assert.That(await downloader.DownloadPartialFiles(archives)).IsTrue();
        await Assert.That(source.Calls.Count).IsEqualTo(callsAfter);
    }

    [Test]
    public async Task ArchiveChunksOfValidEntriesSucceed()
    {
        var dirJob = TestManifests.Job("out", 1, TestManifests.File("pak01_dir.vpk"), TestManifests.File("pak01_000.vpk", new byte[100], new byte[100]), TestManifests.File("pak01_001.vpk", new byte[50]));

        var (success, archives) = VpkReader.FindArchiveChunks([dirJob], dirJob, "pak01_dir.vpk", [InArchive(0, 90, 20), InArchive(1, 0, 50)]);

        await Assert.That(success).IsTrue();
        await Assert.That(archives.Select(static a => $"{a.File.FileName} {string.Join(",", a.Chunks.Select(static c => c.Offset))}")).IsEquivalentTo(["pak01_000.vpk 0,100", "pak01_001.vpk 0"]);
    }
}
