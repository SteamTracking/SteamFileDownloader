using SteamKit2.CDN;

namespace SteamFileDownloader;

internal class ManifestJob
{
    public uint DepotID;
    public ulong ManifestID;
    public required string Branch;
    public Server? Server;
    public byte[]? DepotKey;
}
