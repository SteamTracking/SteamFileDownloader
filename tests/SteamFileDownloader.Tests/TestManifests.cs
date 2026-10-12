using System.Security.Cryptography;
using SteamKit2;

namespace SteamFileDownloader.Tests;

internal static class TestManifests
{
    public static readonly DateTime CreationTime = new(2024, 5, 17, 3, 4, 5, DateTimeKind.Utc);

    // Chunk ids are the SHA-1 of the data, which is what the downloader checks
    public static DepotManifest.ChunkData Chunk(byte[] data, ulong offset) =>
        new(SHA1.HashData(data), 0, offset, (uint)data.Length, (uint)data.Length);

    // A file made of the chunks one after another, with the hash of the whole content
    public static DepotManifest.FileData File(string name, params byte[][] chunks)
    {
        var content = chunks.SelectMany(static c => c).ToArray();
        var file = new DepotManifest.FileData(name, [], default, (ulong)content.Length, SHA1.HashData(content), "", false, chunks.Length);
        var offset = 0ul;

        foreach (var chunk in chunks)
        {
            file.Chunks.Add(Chunk(chunk, offset));
            offset += (ulong)chunk.Length;
        }

        return file;
    }

    public static DepotManifest.FileData Directory(string name) => new(name, [], EDepotFileFlag.Directory, 0, new byte[20], "", false, 0);

    public static ManifestJob Job(string outputFolder, uint depotID, params DepotManifest.FileData[] files) => new()
    {
        AppID = 1,
        DepotID = depotID,
        ManifestID = depotID * 10,
        Branch = "public",
        OutputFolder = outputFolder,
        Manifest = new DepotManifest { DepotID = depotID, CreationTime = CreationTime, Files = [.. files] },
    };
}
