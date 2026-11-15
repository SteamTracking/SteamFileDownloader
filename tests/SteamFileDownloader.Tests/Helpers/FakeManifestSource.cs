using System.Net;
using SteamKit2;
using SteamKit2.CDN;

namespace SteamFileDownloader.Tests;

// Answers depot key and request code requests from what is set, and serves a manifest unless Download throws
internal sealed class FakeManifestSource : IManifestSource
{
    private readonly List<uint> KeyRequestList = [];
    private readonly List<ulong> CodeList = [];
    private readonly List<(ulong Code, Server Server)> DownloadList = [];

    public DepotKeyAnswer KeyAnswer { get; set; } = new(EResult.OK, new byte[32]);

    // The code for each request, by the number of requests before it, which starts at 1
    public Func<int, ulong> Code { get; set; } = static count => (ulong)count;

    // Serves a manifest of the job, or throws, for the number of downloads before it
    public Func<int, ManifestJob, DepotManifest> Download { get; set; } = static (_, job) => ServeManifest(job);

    public bool HasCdnServers { get; set; } = true;

    public List<uint> KeyRequests
    {
        get
        {
            lock (KeyRequestList)
            {
                return [.. KeyRequestList];
            }
        }
    }

    public List<ulong> CodeRequests
    {
        get
        {
            lock (CodeList)
            {
                return [.. CodeList];
            }
        }
    }

    public List<(ulong Code, Server Server)> Downloads
    {
        get
        {
            lock (DownloadList)
            {
                return [.. DownloadList];
            }
        }
    }

    public CdnServerPool CdnServers { get; } = FakeCdn.CreatePool();

    public static DepotManifest ServeManifest(ManifestJob job) => new()
    {
        DepotID = job.DepotID,
        ManifestGID = job.ManifestID,
        CreationTime = TestManifests.CreationTime,
        Files = [TestManifests.File("a.bin", new byte[] { 1, 2, 3 })],
    };

    public static Exception Refusal() => new SteamKitWebRequestException("refused", new HttpResponseMessage(HttpStatusCode.NotFound));

    public Task<bool> EnsureCdnServersAsync() => Task.FromResult(HasCdnServers);

    public Task<DepotKeyAnswer> RequestDepotKeyAsync(ManifestJob job)
    {
        lock (KeyRequestList)
        {
            KeyRequestList.Add(job.DepotID);
        }

        return Task.FromResult(KeyAnswer);
    }

    public Task<ulong> GetManifestRequestCodeAsync(ManifestJob job)
    {
        lock (CodeList)
        {
            var code = Code(CodeList.Count + 1);
            CodeList.Add(code);

            return Task.FromResult(code);
        }
    }

    public Task<DepotManifest> DownloadManifestAsync(ManifestJob job, ulong requestCode, Server server)
    {
        int count;

        lock (DownloadList)
        {
            count = DownloadList.Count;
            DownloadList.Add((requestCode, server));
        }

        return Task.FromResult(Download(count, job));
    }
}
