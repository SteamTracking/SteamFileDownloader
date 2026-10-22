using System.Net;
using SteamKit2;

namespace SteamFileDownloader.Tests;

public class FileDownloaderTests
{
    private static readonly byte[] ChunkA = Bytes(1, 100);
    private static readonly byte[] ChunkB = Bytes(2, 60);
    private static readonly byte[] ChunkC = Bytes(3, 100);

    private static byte[] Bytes(int seed, int length) => [.. Enumerable.Range(0, length).Select(i => (byte)((i * 31) + (seed * 17)))];

    private static FileDownloader Downloader(FakeChunkSource source, CancellationToken cancellationToken = default) => new(source, cancellationToken) { TimeProvider = new InstantTimeProvider() };

    // Chunks that keep failing end it after a few readings of the clock, instead of after the outage timeout
    private static FileDownloader OutageDownloader(FakeChunkSource source, string? tempFolder = null) =>
        new(source, CancellationToken.None) { TimeProvider = new SteppingTimeProvider(TimeSpan.FromMinutes(5)), TempFolder = tempFolder };

    private static string[] FilesIn(string folder) => Directory.GetFiles(folder, "*", SearchOption.AllDirectories);

    private static FakeChunkSource SourceOf(params byte[][] chunks)
    {
        var source = new FakeChunkSource();
        source.Add(chunks);

        return source;
    }

    private static Exception Refusal() => new SteamKitWebRequestException("refused", new HttpResponseMessage(HttpStatusCode.NotFound));

    private static void WriteFile(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    [Test]
    public async Task FreshDownloadWritesTheFileDatedToTheManifest()
    {
        using var temp = new TempDirectory();
        var file = TestManifests.File("game\\bin\\server.dll", ChunkA, ChunkB);
        var job = TestManifests.Job(temp.Path, 1, file);
        var source = SourceOf(ChunkA, ChunkB);
        using var downloader = Downloader(source);

        var (success, files) = await downloader.DownloadAllFiles([new FileJob(job, file)]);

        var path = Path.Combine(temp.Path, "game", "bin", "server.dll");
        await Assert.That(success).IsTrue();
        await Assert.That(files.Select(static f => f.Path)).IsEquivalentTo([path]);
        await Assert.That(await File.ReadAllBytesAsync(path)).IsEquivalentTo([.. ChunkA, .. ChunkB]);
        await Assert.That(File.GetLastWriteTimeUtc(path)).IsEqualTo(TestManifests.CreationTime);
        await Assert.That(source.Calls.Count).IsEqualTo(2);

        // Zero counts are left out of the summary
        var output = TestContext.Current!.GetStandardOutput() + TestContext.Current.GetErrorOutput();
        await Assert.That(output).Contains("Downloading 1 file (").And.Contains("Files: 1 downloaded (").And.DoesNotContain("copied").And.DoesNotContain("already present");
        await Assert.That(Directory.GetFiles(temp.Path, "*", SearchOption.AllDirectories)).IsEquivalentTo([path]);
    }

    [Test]
    public async Task EmptyFileIsWrittenWithoutDownloading()
    {
        using var temp = new TempDirectory();
        var file = TestManifests.File("empty.txt");
        var job = TestManifests.Job(temp.Path, 1, file);
        var source = SourceOf();
        using var downloader = Downloader(source);

        var (success, _) = await downloader.DownloadAllFiles([new FileJob(job, file)]);

        await Assert.That(success).IsTrue();
        await Assert.That(new FileInfo(Path.Combine(temp.Path, "empty.txt")).Length).IsEqualTo(0L);
        await Assert.That(source.Calls).IsEmpty();
    }

    [Test]
    public async Task FileAlreadyOnDiskWithTheRightHashIsKept()
    {
        using var temp = new TempDirectory();
        var file = TestManifests.File("a.bin", ChunkA, ChunkB);
        var job = TestManifests.Job(temp.Path, 1, file);
        var path = Path.Combine(temp.Path, "a.bin");
        WriteFile(path, [.. ChunkA, .. ChunkB]);
        var source = SourceOf(ChunkA, ChunkB);
        using var downloader = Downloader(source);

        var (success, files) = await downloader.DownloadAllFiles([new FileJob(job, file)]);

        await Assert.That(success).IsTrue();
        await Assert.That(files.Select(static f => f.Path)).IsEquivalentTo([path]);
        await Assert.That(source.Calls).IsEmpty();
        await Assert.That(TestContext.Current!.GetStandardOutput() + TestContext.Current.GetErrorOutput()).Contains("Files: 1 already present.");
    }

    [Test]
    public async Task SummaryCountsEachOutcomeAndCanBeLeftOut()
    {
        using var temp = new TempDirectory();
        var present = TestManifests.File("present.bin", ChunkC);
        var first = TestManifests.File("a/same.bin", ChunkA);
        var second = TestManifests.File("b/same.bin", ChunkA);
        var job = TestManifests.Job(temp.Path, 1, present, first, second);
        WriteFile(Path.Combine(temp.Path, "present.bin"), ChunkC);

        using (var downloader = Downloader(SourceOf(ChunkA)))
        {
            await downloader.DownloadAllFiles([new FileJob(job, present), new FileJob(job, first), new FileJob(job, second)]);
        }

        var output = TestContext.Current!.GetStandardOutput() + TestContext.Current.GetErrorOutput();
        await Assert.That(output).Contains("Files: 1 downloaded (").And.Contains("), 1 copied from identical files, 1 already present.");

        using var quiet = new FileDownloader(SourceOf(ChunkA), default) { TimeProvider = new InstantTimeProvider(), LogFilesSummary = false };
        var (success, _) = await quiet.DownloadAllFiles([new FileJob(job, present)]);

        await Assert.That(success).IsTrue();

        // Only the first download's line
        var after = TestContext.Current.GetStandardOutput() + TestContext.Current.GetErrorOutput();
        await Assert.That(after.Split("Files:").Length).IsEqualTo(2);
    }

    [Test]
    [Arguments(10)]
    [Arguments(160)]
    [Arguments(500)]
    public async Task ExistingFileOfOtherContentIsDownloadedAgain(int existingLength)
    {
        using var temp = new TempDirectory();
        var file = TestManifests.File("a.bin", ChunkA, ChunkB);
        var job = TestManifests.Job(temp.Path, 1, file);
        var path = Path.Combine(temp.Path, "a.bin");
        WriteFile(path, Bytes(9, existingLength));
        var source = SourceOf(ChunkA, ChunkB);
        using var downloader = Downloader(source);

        var (success, _) = await downloader.DownloadAllFiles([new FileJob(job, file)]);

        await Assert.That(success).IsTrue();
        await Assert.That(await File.ReadAllBytesAsync(path)).IsEquivalentTo([.. ChunkA, .. ChunkB]);
        await Assert.That(source.Calls.Count).IsEqualTo(2);
    }

    [Test]
    public async Task IdenticalFileInAReuseFolderIsCopied()
    {
        using var temp = new TempDirectory();
        var file = TestManifests.File("game\\a.bin", ChunkA, ChunkB);
        var job = TestManifests.Job(Path.Combine(temp.Path, "out"), 1, file);
        var otherVersion = Path.Combine(temp.Path, "old1");
        var sameVersion = Path.Combine(temp.Path, "old2");
        WriteFile(Path.Combine(otherVersion, "game", "a.bin"), Bytes(9, 160));
        WriteFile(Path.Combine(sameVersion, "game", "a.bin"), [.. ChunkA, .. ChunkB]);
        job.ReuseFolders = [otherVersion, Path.Combine(temp.Path, "missing"), sameVersion];
        var source = SourceOf(ChunkA, ChunkB);
        using var downloader = Downloader(source);

        var (success, files) = await downloader.DownloadAllFiles([new FileJob(job, file)]);

        var path = Path.Combine(temp.Path, "out", "game", "a.bin");
        await Assert.That(success).IsTrue();
        await Assert.That(files.Select(static f => f.Path)).IsEquivalentTo([path]);
        await Assert.That(await File.ReadAllBytesAsync(path)).IsEquivalentTo([.. ChunkA, .. ChunkB]);
        await Assert.That(File.GetLastWriteTimeUtc(path)).IsEqualTo(TestManifests.CreationTime);
        await Assert.That(source.Calls).IsEmpty();
    }

    [Test]
    public async Task ReuseFolderWithoutAnIdenticalFileIsNotUsed()
    {
        using var temp = new TempDirectory();
        var file = TestManifests.File("a.bin", ChunkA, ChunkB);
        var job = TestManifests.Job(Path.Combine(temp.Path, "out"), 1, file);
        var otherVersion = Path.Combine(temp.Path, "old");
        WriteFile(Path.Combine(otherVersion, "a.bin"), Bytes(9, 160));
        job.ReuseFolders = [otherVersion];
        var source = SourceOf(ChunkA, ChunkB);
        using var downloader = Downloader(source);

        await downloader.DownloadAllFiles([new FileJob(job, file)]);

        await Assert.That(await File.ReadAllBytesAsync(Path.Combine(temp.Path, "out", "a.bin"))).IsEquivalentTo([.. ChunkA, .. ChunkB]);
        await Assert.That(source.Calls.Count).IsEqualTo(2);
    }

    [Test]
    public async Task FilesWithTheSameContentAreDownloadedOnce()
    {
        using var temp = new TempDirectory();
        var first = TestManifests.File("a.bin", ChunkA, ChunkB);
        var second = TestManifests.File("b\\copy.bin", ChunkA, ChunkB);
        var firstJob = TestManifests.Job(Path.Combine(temp.Path, "1"), 1, first);
        var secondJob = TestManifests.Job(Path.Combine(temp.Path, "2"), 2, second);
        var source = SourceOf(ChunkA, ChunkB);
        using var downloader = Downloader(source);

        var (success, files) = await downloader.DownloadAllFiles([new FileJob(firstJob, first), new FileJob(secondJob, second)]);

        await Assert.That(success).IsTrue();
        await Assert.That(files.Count).IsEqualTo(2);
        await Assert.That(await File.ReadAllBytesAsync(Path.Combine(temp.Path, "2", "b", "copy.bin"))).IsEquivalentTo([.. ChunkA, .. ChunkB]);
        await Assert.That(source.Calls.Count).IsEqualTo(2);
    }

    [Test]
    public async Task FailedChunkIsRetriedOnAnotherServer()
    {
        using var temp = new TempDirectory();
        var file = TestManifests.File("a.bin", ChunkA);
        var job = TestManifests.Job(temp.Path, 1, file);
        var source = SourceOf(ChunkA);
        source.Serve = static (attempt, data) => attempt == 0 ? throw new HttpRequestException("reset") : data;
        using var downloader = Downloader(source);

        var (success, _) = await downloader.DownloadAllFiles([new FileJob(job, file)]);

        var calls = source.Calls;
        await Assert.That(success).IsTrue();
        await Assert.That(await File.ReadAllBytesAsync(Path.Combine(temp.Path, "a.bin"))).IsEquivalentTo(ChunkA);
        await Assert.That(calls.Count).IsEqualTo(2);
        await Assert.That(calls[1].Server).IsNotEqualTo(calls[0].Server);
    }

    [Test]
    public async Task CorruptChunkIsRetried()
    {
        using var temp = new TempDirectory();
        var file = TestManifests.File("a.bin", ChunkA, ChunkB);
        var job = TestManifests.Job(temp.Path, 1, file);
        var source = SourceOf(ChunkA, ChunkB);
        source.Serve = static (attempt, data) => attempt == 0 ? [.. data.Reverse()] : data;
        using var downloader = Downloader(source);

        var (success, _) = await downloader.DownloadAllFiles([new FileJob(job, file)]);

        await Assert.That(success).IsTrue();
        await Assert.That(await File.ReadAllBytesAsync(Path.Combine(temp.Path, "a.bin"))).IsEquivalentTo([.. ChunkA, .. ChunkB]);
        await Assert.That(source.Calls.Count).IsEqualTo(4);
    }

    [Test]
    public async Task ChunkThatKeepsFailingFailsOnlyItsFileAndLeavesNothingBehind()
    {
        using var temp = new TempDirectory();
        var bad = TestManifests.File("bad.bin", ChunkA, ChunkB);
        var good = TestManifests.File("good.bin", ChunkC);
        var job = TestManifests.Job(temp.Path, 1, bad, good);
        var source = SourceOf(ChunkA, ChunkB, ChunkC);
        source.Serve = (_, data) => data == ChunkA ? throw new HttpRequestException("reset") : data;
        using var downloader = OutageDownloader(source);

        var (success, files) = await downloader.DownloadAllFiles([new FileJob(job, bad), new FileJob(job, good)]);

        var goodPath = Path.Combine(temp.Path, "good.bin");
        await Assert.That(success).IsFalse();
        await Assert.That(files.Select(static f => f.Path)).IsEquivalentTo([goodPath]);
        await Assert.That(Directory.GetFiles(temp.Path)).IsEquivalentTo([goodPath]);

        // It was retried, each time on another server, until no chunk had downloaded for the outage timeout
        var servers = source.Calls.Where(c => c.Chunk == bad.Chunks[0]).Select(static c => c.Server).ToList();
        await Assert.That(servers.Count).IsGreaterThan(1);
        await Assert.That(servers.Zip(servers.Skip(1)).All(static p => p.First != p.Second)).IsTrue();
    }

    [Test]
    public async Task ChunkIsRetriedUntilItDownloads()
    {
        using var temp = new TempDirectory();
        var file = TestManifests.File("a.bin", ChunkA);
        var job = TestManifests.Job(temp.Path, 1, file);
        var source = SourceOf(ChunkA);
        source.Serve = static (attempt, data) => attempt < 20 ? throw new HttpRequestException("reset") : data;
        using var downloader = Downloader(source);

        var (success, _) = await downloader.DownloadAllFiles([new FileJob(job, file)]);

        await Assert.That(success).IsTrue();
        await Assert.That(await File.ReadAllBytesAsync(Path.Combine(temp.Path, "a.bin"))).IsEquivalentTo(ChunkA);
        await Assert.That(source.Calls.Count).IsEqualTo(21);
    }

    [Test]
    public async Task IdenticalFileDownloadsOnItsOwnWhenTheFirstOneFails()
    {
        using var temp = new TempDirectory();
        var first = TestManifests.File("a.bin", ChunkA);
        var second = TestManifests.File("b.bin", ChunkA);
        var job = TestManifests.Job(temp.Path, 1, first, second);
        var source = SourceOf(ChunkA);
        source.Broken.Add(first.Chunks[0]);
        using var downloader = OutageDownloader(source);

        var (success, files) = await downloader.DownloadAllFiles([new FileJob(job, first), new FileJob(job, second)]);

        var secondPath = Path.Combine(temp.Path, "b.bin");
        await Assert.That(success).IsFalse();
        await Assert.That(files.Select(static f => f.Path)).IsEquivalentTo([secondPath]);
        await Assert.That(await File.ReadAllBytesAsync(secondPath)).IsEquivalentTo(ChunkA);
        await Assert.That(FilesIn(temp.Path)).IsEquivalentTo([secondPath]);
        await Assert.That(source.Calls.Count(c => c.Chunk == second.Chunks[0])).IsEqualTo(1);
    }

    [Test]
    public async Task CancellationMidDownloadThrowsAndLeavesNothingBehind()
    {
        using var temp = new TempDirectory();
        using var cancellation = new CancellationTokenSource();
        var file = TestManifests.File("a.bin", ChunkA, ChunkB);
        var job = TestManifests.Job(temp.Path, 1, file);
        var source = SourceOf(ChunkA, ChunkB);
        source.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var downloader = Downloader(source, cancellation.Token);

        var download = downloader.DownloadAllFiles([new FileJob(job, file)]);
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancellation.CancelAsync();
        source.Gate.SetResult();

        await Assert.That(async () => await download).Throws<OperationCanceledException>();
        await Assert.That(FilesIn(temp.Path)).IsEmpty();
    }

    [Test]
    public async Task TempFolderHoldsPartialFilesAndIsLeftEmpty()
    {
        using var temp = new TempDirectory();
        var output = Path.Combine(temp.Path, "out");
        var tempFolder = Path.Combine(temp.Path, "tmp");
        Directory.CreateDirectory(tempFolder);
        var good = TestManifests.File("good.bin", ChunkA, ChunkB);
        var bad = TestManifests.File("bad.bin", ChunkC);
        var job = TestManifests.Job(output, 1, good, bad);
        var source = SourceOf(ChunkA, ChunkB, ChunkC);
        source.Broken.Add(bad.Chunks[0]);
        var duringDownload = new System.Collections.Concurrent.ConcurrentBag<(int Output, int Temp)>();
        source.Serve = (_, data) =>
        {
            duringDownload.Add((FilesIn(output).Length, FilesIn(tempFolder).Length));
            return data;
        };
        using var downloader = OutageDownloader(source, tempFolder);

        var (success, files) = await downloader.DownloadAllFiles([new FileJob(job, good), new FileJob(job, bad)]);

        var goodPath = Path.Combine(output, "good.bin");
        await Assert.That(success).IsFalse();
        await Assert.That(files.Select(static f => f.Path)).IsEquivalentTo([goodPath]);
        await Assert.That(duringDownload.All(static c => c.Output == 0 && c.Temp > 0)).IsTrue();
        await Assert.That(duringDownload).IsNotEmpty();
        await Assert.That(FilesIn(output)).IsEquivalentTo([goodPath]);
        await Assert.That(FilesIn(tempFolder)).IsEmpty();
        await Assert.That(File.GetLastWriteTimeUtc(goodPath)).IsEqualTo(TestManifests.CreationTime);
    }

    [Test]
    public async Task FileWhosePathEscapesTheOutputFolderFails()
    {
        using var temp = new TempDirectory();
        var output = Path.Combine(temp.Path, "out");
        var file = TestManifests.File("../x", ChunkA);
        var job = TestManifests.Job(output, 1, file);
        var source = SourceOf(ChunkA);
        using var downloader = Downloader(source);

        var (success, files) = await downloader.DownloadAllFiles([new FileJob(job, file)]);

        await Assert.That(success).IsFalse();
        await Assert.That(files).IsEmpty();
        await Assert.That(FilesIn(temp.Path)).IsEmpty();
        await Assert.That(source.Calls).IsEmpty();
    }

    [Test]
    public async Task ChunksOutOfOffsetOrderLandAtTheirOffsets()
    {
        using var temp = new TempDirectory();
        var file = TestManifests.File("a.bin", ChunkA, ChunkB, ChunkC);
        file.Chunks.Reverse();
        var job = TestManifests.Job(temp.Path, 1, file);
        using var downloader = Downloader(SourceOf(ChunkA, ChunkB, ChunkC));

        var (success, _) = await downloader.DownloadAllFiles([new FileJob(job, file)]);

        await Assert.That(success).IsTrue();
        await Assert.That(await File.ReadAllBytesAsync(Path.Combine(temp.Path, "a.bin"))).IsEquivalentTo([.. ChunkA, .. ChunkB, .. ChunkC]);
    }

    [Test]
    public async Task ChunkRefusedByTwoServersFailsWithoutMoreAttempts()
    {
        using var temp = new TempDirectory();
        var file = TestManifests.File("a.bin", ChunkA);
        var job = TestManifests.Job(temp.Path, 1, file);
        var source = SourceOf(ChunkA);
        source.Serve = static (_, _) => throw Refusal();
        using var downloader = Downloader(source);

        var (success, files) = await downloader.DownloadAllFiles([new FileJob(job, file)]);

        var calls = source.Calls;
        await Assert.That(success).IsFalse();
        await Assert.That(files).IsEmpty();
        await Assert.That(calls.Count).IsEqualTo(2);
        await Assert.That(calls[1].Server).IsNotEqualTo(calls[0].Server);
        await Assert.That(Directory.GetFiles(temp.Path)).IsEmpty();
    }

    [Test]
    public async Task ChunkRefusedByOneServerComesFromAnother()
    {
        using var temp = new TempDirectory();
        var file = TestManifests.File("a.bin", ChunkA);
        var job = TestManifests.Job(temp.Path, 1, file);
        var source = SourceOf(ChunkA);
        source.Serve = static (attempt, data) => attempt == 0 ? throw Refusal() : data;
        using var downloader = Downloader(source);

        var (success, _) = await downloader.DownloadAllFiles([new FileJob(job, file)]);

        await Assert.That(success).IsTrue();
        await Assert.That(await File.ReadAllBytesAsync(Path.Combine(temp.Path, "a.bin"))).IsEquivalentTo(ChunkA);
    }

    [Test]
    public async Task PartialFileGetsOnlyTheRequestedChunksAndKeepsTheRest()
    {
        using var temp = new TempDirectory();
        var archive = TestManifests.File("pak01_000.vpk", ChunkA, ChunkB, ChunkC);
        var job = TestManifests.Job(temp.Path, 1, archive);
        var path = Path.Combine(temp.Path, "pak01_000.vpk");
        var existing = Enumerable.Repeat((byte)0xEE, 260).ToArray();
        WriteFile(path, existing);
        var source = SourceOf(ChunkA, ChunkB, ChunkC);
        using var downloader = Downloader(source);

        var success = await downloader.DownloadPartialFiles([new PartialFile(job, archive, [archive.Chunks[1]])]);

        await Assert.That(success).IsTrue();
        await Assert.That(await File.ReadAllBytesAsync(path)).IsEquivalentTo([.. existing[..100], .. ChunkB, .. existing[160..]]);
        await Assert.That(source.Calls.Count).IsEqualTo(1);
    }

    [Test]
    public async Task PartialFileIsCreatedAtFullLengthAndChunksInItAreNotDownloadedAgain()
    {
        using var temp = new TempDirectory();
        var archive = TestManifests.File("pak01_000.vpk", ChunkA, ChunkB, ChunkC);
        var job = TestManifests.Job(temp.Path, 1, archive);
        var path = Path.Combine(temp.Path, "pak01_000.vpk");
        var source = SourceOf(ChunkA, ChunkB, ChunkC);
        using var downloader = Downloader(source);

        await downloader.DownloadPartialFiles([new PartialFile(job, archive, [archive.Chunks[2]])]);
        var success = await downloader.DownloadPartialFiles([new PartialFile(job, archive, [archive.Chunks[0], archive.Chunks[2]])]);

        await Assert.That(success).IsTrue();
        await Assert.That(await File.ReadAllBytesAsync(path)).IsEquivalentTo([.. ChunkA, .. new byte[60], .. ChunkC]);
        await Assert.That(source.Calls.Select(static c => c.Chunk.Offset)).IsEquivalentTo([160ul, 0ul]);
    }

    [Test]
    public async Task PartialFileFailsWhenAChunkFailsAndARerunGetsOnlyThatChunk()
    {
        using var temp = new TempDirectory();
        var archive = TestManifests.File("pak01_000.vpk", ChunkA, ChunkB, ChunkC);
        var job = TestManifests.Job(temp.Path, 1, archive);
        var path = Path.Combine(temp.Path, "pak01_000.vpk");
        var source = SourceOf(ChunkA, ChunkB, ChunkC);
        using var aServed = new ManualResetEventSlim();

        // B fails only once A is on its way to the file, which a failed chunk would otherwise stop
        source.Serve = (_, data) =>
        {
            if (data == ChunkA)
            {
                aServed.Set();
                return data;
            }

            aServed.Wait(TimeSpan.FromSeconds(10));
            throw Refusal();
        };
        using var downloader = Downloader(source);
        PartialFile[] request = [new PartialFile(job, archive, [archive.Chunks[0], archive.Chunks[1]])];

        var failed = await downloader.DownloadPartialFiles([.. request]);
        var afterFailure = await File.ReadAllBytesAsync(path);

        await Assert.That(failed).IsFalse();
        await Assert.That(afterFailure.Length).IsEqualTo(260);
        await Assert.That(afterFailure[..100]).IsEquivalentTo(ChunkA);
        await Assert.That(afterFailure[100..160].All(static b => b == 0)).IsTrue();

        source.Serve = static (_, data) => data;
        var callsBefore = source.Calls.Count;

        var success = await downloader.DownloadPartialFiles([.. request]);

        await Assert.That(success).IsTrue();
        await Assert.That(source.Calls.Skip(callsBefore).Select(static c => c.Chunk)).IsEquivalentTo([archive.Chunks[1]]);
        await Assert.That(await File.ReadAllBytesAsync(path)).IsEquivalentTo([.. ChunkA, .. ChunkB, .. new byte[100]]);
    }

    [Test]
    public async Task SameArchiveRequestedTwiceIsDownloadedOnce()
    {
        using var temp = new TempDirectory();
        var archive = TestManifests.File("pak01_000.vpk", ChunkA, ChunkB, ChunkC);
        var job = TestManifests.Job(temp.Path, 1, archive);
        var source = SourceOf(ChunkA, ChunkB, ChunkC);
        using var downloader = Downloader(source);

        var success = await downloader.DownloadPartialFiles([
            new PartialFile(job, archive, [archive.Chunks[0]]),
            new PartialFile(job, archive, [archive.Chunks[0], archive.Chunks[2]]),
        ]);

        await Assert.That(success).IsTrue();
        await Assert.That(await File.ReadAllBytesAsync(Path.Combine(temp.Path, "pak01_000.vpk"))).IsEquivalentTo([.. ChunkA, .. new byte[60], .. ChunkC]);
        await Assert.That(source.Calls.Select(static c => c.Chunk.Offset)).IsEquivalentTo([0ul, 160ul]);
    }

    [Test]
    public async Task PartialFileWithoutChunksSucceedsWithoutDownloading()
    {
        using var temp = new TempDirectory();
        var archive = TestManifests.File("pak01_000.vpk", ChunkA);
        var job = TestManifests.Job(temp.Path, 1, archive);
        var source = SourceOf(ChunkA);
        using var downloader = Downloader(source);

        await Assert.That(await downloader.DownloadPartialFiles([new PartialFile(job, archive, [])])).IsTrue();
        await Assert.That(source.Calls).IsEmpty();
    }

    [Test]
    public async Task LongerPartialFileIsCutKeepingItsChunks()
    {
        using var temp = new TempDirectory();
        var archive = TestManifests.File("pak01_000.vpk", ChunkA, ChunkB, ChunkC);
        var job = TestManifests.Job(temp.Path, 1, archive);
        var path = Path.Combine(temp.Path, "pak01_000.vpk");
        WriteFile(path, [.. ChunkA, .. new byte[60], .. ChunkC, .. Enumerable.Repeat((byte)0xEE, 50)]);
        var source = SourceOf(ChunkA, ChunkB, ChunkC);
        using var downloader = Downloader(source);

        var success = await downloader.DownloadPartialFiles([new PartialFile(job, archive, [archive.Chunks[0], archive.Chunks[1]])]);

        await Assert.That(success).IsTrue();
        await Assert.That(await File.ReadAllBytesAsync(path)).IsEquivalentTo([.. ChunkA, .. ChunkB, .. ChunkC]);
        await Assert.That(source.Calls.Select(static c => c.Chunk)).IsEquivalentTo([archive.Chunks[1]]);
    }

    [Test]
    public async Task ShorterPartialFileIsExtendedKeepingItsChunks()
    {
        using var temp = new TempDirectory();
        var archive = TestManifests.File("pak01_000.vpk", ChunkA, ChunkB, ChunkC);
        var job = TestManifests.Job(temp.Path, 1, archive);
        var path = Path.Combine(temp.Path, "pak01_000.vpk");
        WriteFile(path, [.. ChunkA, .. ChunkB[..30]]);
        var source = SourceOf(ChunkA, ChunkB, ChunkC);
        using var downloader = Downloader(source);

        var success = await downloader.DownloadPartialFiles([new PartialFile(job, archive, [.. archive.Chunks])]);

        await Assert.That(success).IsTrue();
        await Assert.That(await File.ReadAllBytesAsync(path)).IsEquivalentTo([.. ChunkA, .. ChunkB, .. ChunkC]);
        await Assert.That(source.Calls.Select(static c => c.Chunk)).IsEquivalentTo([archive.Chunks[1], archive.Chunks[2]]);
    }
}
