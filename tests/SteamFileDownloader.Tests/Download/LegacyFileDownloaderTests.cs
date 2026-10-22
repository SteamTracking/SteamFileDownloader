using System.Net;

namespace SteamFileDownloader.Tests;

public class LegacyFileDownloaderTests
{
    private sealed class StubHandler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Requests;
        public List<HttpMethod> Methods = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Methods.Add(request.Method);

            return Task.FromResult(respond(++Requests));
        }
    }

    private static HttpResponseMessage Ok(byte[] content) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };

    private static LegacyItem ItemIn(string folder, string name = "map.zip", long size = 4, uint timeUpdated = 1000) => new()
    {
        ItemID = 7,
        FileName = name,
        Url = new Uri("https://cdn.steamusercontent.com/ugc/1/"),
        Size = size,
        TimeUpdated = timeUpdated,
        OutputFolder = folder,
    };

    private static LegacyFileDownloader Downloader(StubHandler handler) => new(new HttpClient(handler), CancellationToken.None) { Backoff = static _ => 0 };

    [Test]
    [Arguments("map.zip", "map.zip")]
    [Arguments("workshop/content/8930/7/map.zip", "map.zip")]
    [Arguments("C:\\temp\\map.zip", "map.zip")]
    [Arguments("  map.zip ", "map.zip")]
    [Arguments("a b.vpk", "a b.vpk")]
    public async Task FileNameIsThePartAfterTheLastSlash(string path, string expected)
    {
        await Assert.That(LegacyItem.GetFileName(path)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("dir/")]
    [Arguments("dir\\")]
    [Arguments("..")]
    [Arguments("a/..")]
    [Arguments(".")]
    [Arguments("a\0b")]
    public async Task PathWithoutAUsableNameHasNone(string? path)
    {
        await Assert.That(LegacyItem.GetFileName(path)).IsNull();
    }

    [Test]
    [Arguments("https://cdn.example/map.zip", "workshop/map.zip", true)]
    [Arguments("http://cdn.example/map.zip", "map.zip", true)]
    [Arguments("ftp://cdn.example/map.zip", "map.zip", false)]
    [Arguments("", "map.zip", false)]
    [Arguments("not a url", "map.zip", false)]
    [Arguments("https://cdn.example/map.zip", "", false)]
    public async Task ItemNeedsAWebAddressAndAFileName(string fileUrl, string fileName, bool created)
    {
        var details = new SteamKit2.Internal.PublishedFileDetails { publishedfileid = 5, file_url = fileUrl, filename = fileName, file_size = 4, time_updated = 7 };
        var item = LegacyItem.TryCreate(details, "folder");

        await Assert.That(item != null).IsEqualTo(created);

        if (item != null)
        {
            await Assert.That(item.ItemID).IsEqualTo(5ul);
            await Assert.That(item.FileName).IsEqualTo("map.zip");
            await Assert.That(item.Size).IsEqualTo(4L);
            await Assert.That(item.TimeUpdated).IsEqualTo(7u);
            await Assert.That(item.OutputFolder).IsEqualTo("folder");
            await Assert.That(item.Label).IsEqualTo("5:legacy");
        }
    }

    [Test]
    public async Task FileIsUpToDateWithTheSizeAndAnUpdateNotNewerThanItsDate()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "map.zip");
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        File.SetLastWriteTimeUtc(path, DateTimeOffset.FromUnixTimeSeconds(1000).UtcDateTime);

        await Assert.That(LegacyFileDownloader.IsUpToDate(path, ItemIn(temp.Path, timeUpdated: 1000))).IsTrue();
        await Assert.That(LegacyFileDownloader.IsUpToDate(path, ItemIn(temp.Path, timeUpdated: 999))).IsTrue();
        await Assert.That(LegacyFileDownloader.IsUpToDate(path, ItemIn(temp.Path, timeUpdated: 1001))).IsFalse();
        await Assert.That(LegacyFileDownloader.IsUpToDate(path, ItemIn(temp.Path, size: 5))).IsFalse();
        await Assert.That(LegacyFileDownloader.IsUpToDate(path, ItemIn(temp.Path, size: 0))).IsFalse();
        await Assert.That(LegacyFileDownloader.IsUpToDate(Path.Combine(temp.Path, "none.zip"), ItemIn(temp.Path))).IsFalse();
    }

    [Test]
    public async Task FileIsDownloadedWithGetAndDatedToTheUpdate()
    {
        using var temp = new TempDirectory();
        var handler = new StubHandler(_ => Ok([1, 2, 3, 4]));

        var (success, files) = await Downloader(handler).DownloadAllAsync([ItemIn(temp.Path)]);

        var path = Path.Combine(temp.Path, "map.zip");
        await Assert.That(success).IsTrue();
        await Assert.That(files.Single().Path).IsEqualTo(path);
        await Assert.That(File.ReadAllBytes(path)).IsEquivalentTo(new byte[] { 1, 2, 3, 4 });
        await Assert.That(File.GetLastWriteTimeUtc(path)).IsEqualTo(DateTimeOffset.FromUnixTimeSeconds(1000).UtcDateTime);
        await Assert.That(File.Exists(path + ".partial")).IsFalse();
        await Assert.That(handler.Methods).IsEquivalentTo([HttpMethod.Get]);
    }

    [Test]
    public async Task FileThatIsUpToDateIsNotDownloadedAgain()
    {
        using var temp = new TempDirectory();
        var handler = new StubHandler(_ => Ok([1, 2, 3, 4]));
        var downloader = Downloader(handler);

        await downloader.DownloadAllAsync([ItemIn(temp.Path)]);
        var (success, files) = await downloader.DownloadAllAsync([ItemIn(temp.Path)]);

        await Assert.That(success).IsTrue();
        await Assert.That(files.Count).IsEqualTo(1);
        await Assert.That(handler.Requests).IsEqualTo(1);
    }

    [Test]
    public async Task FileOfTheWrongSizeFailsWithoutLeavingAFile()
    {
        using var temp = new TempDirectory();
        var handler = new StubHandler(_ => Ok([1, 2, 3]));

        var (success, files) = await Downloader(handler).DownloadAllAsync([ItemIn(temp.Path)]);

        await Assert.That(success).IsFalse();
        await Assert.That(files).IsEmpty();
        await Assert.That(handler.Requests).IsEqualTo(1);
        await Assert.That(Directory.GetFiles(temp.Path)).IsEmpty();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains("got 3 bytes, but Steam says the file has 4");
    }

    [Test]
    public async Task FileOfUnknownSizeIsKeptAsItIs()
    {
        using var temp = new TempDirectory();
        var handler = new StubHandler(_ => Ok([1, 2, 3]));

        var (success, _) = await Downloader(handler).DownloadAllAsync([ItemIn(temp.Path, size: 0)]);

        await Assert.That(success).IsTrue();
        await Assert.That(new FileInfo(Path.Combine(temp.Path, "map.zip")).Length).IsEqualTo(3L);
    }

    [Test]
    public async Task ServerErrorsAreRetried()
    {
        using var temp = new TempDirectory();
        var handler = new StubHandler(n => n < 3 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Ok([1, 2, 3, 4]));

        var (success, _) = await Downloader(handler).DownloadAllAsync([ItemIn(temp.Path)]);

        await Assert.That(success).IsTrue();
        await Assert.That(handler.Requests).IsEqualTo(3);
    }

    [Test]
    public async Task ServerErrorsThatKeepComingFailAfterTheLastAttempt()
    {
        using var temp = new TempDirectory();
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));

        var (success, _) = await Downloader(handler).DownloadAllAsync([ItemIn(temp.Path)]);

        await Assert.That(success).IsFalse();
        await Assert.That(handler.Requests).IsEqualTo(5);
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains("HTTP 502");
    }

    [Test]
    public async Task NotFoundIsNotRetried()
    {
        using var temp = new TempDirectory();
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var (success, _) = await Downloader(handler).DownloadAllAsync([ItemIn(temp.Path)]);

        await Assert.That(success).IsFalse();
        await Assert.That(handler.Requests).IsEqualTo(1);
    }
}
