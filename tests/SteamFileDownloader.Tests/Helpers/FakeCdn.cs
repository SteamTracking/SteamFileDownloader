using System.Net;
using SteamKit2.CDN;

namespace SteamFileDownloader.Tests;

internal static class FakeCdn
{
    public static CdnServerPool CreatePool()
    {
        var pool = new CdnServerPool(TimeProvider.System);
        pool.SetServers([.. Enumerable.Range(0, 3).Select(static i => (Server)new DnsEndPoint($"cache{i}.example.com", 80))]);

        return pool;
    }
}
