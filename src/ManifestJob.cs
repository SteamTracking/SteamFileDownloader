using SteamKit2.CDN;

namespace SteamFileDownloader;

internal class ManifestJob
{
    public uint DepotID;
    public ulong ManifestID;
    public required string Branch;
    public Server? Server;
    public byte[]? DepotKey;

    // Whether the depot is in files.json. Files are only downloaded from those, the others are only fetched to save their manifest
    public bool IsTracked;
}
