using System.Net;
using System.Security.Cryptography;
using SteamKit2;
using SteamKit2.CDN;

namespace SteamFileDownloader.Tests;

// Serves chunks from their data, or what Serve returns for each attempt, over a few servers
internal sealed class FakeChunkSource : IChunkSource
{
    private readonly Dictionary<string, byte[]> Data = [];
    private readonly List<(DepotManifest.ChunkData Chunk, Server Server)> CallList = [];

    public FakeChunkSource() => CdnServers.SetServers([.. Enumerable.Range(0, 3).Select(static i => (Server)new DnsEndPoint($"cache{i}.example.com", 80))]);

    public Func<int, byte[], byte[]> Serve { get; set; } = static (_, data) => data;

    // Chunks whose every attempt fails, whatever Serve does
    public HashSet<DepotManifest.ChunkData> Broken { get; } = [];

    // When set, downloads wait for it after Started is set
    public TaskCompletionSource? Gate { get; set; }

    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<(DepotManifest.ChunkData Chunk, Server Server)> Calls
    {
        get
        {
            lock (CallList)
            {
                return [.. CallList];
            }
        }
    }

    public void Add(params byte[][] chunks)
    {
        foreach (var chunk in chunks)
        {
            Data[Convert.ToHexString(SHA1.HashData(chunk))] = chunk;
        }
    }

    public Task<bool> EnsureCdnServersAsync() => Task.FromResult(true);

    public CdnServerPool CdnServers { get; } = new(TimeProvider.System);

    public async Task<int> DownloadChunkAsync(ManifestJob job, DepotManifest.ChunkData chunk, Server server, byte[] destination)
    {
        int attempt;

        lock (CallList)
        {
            attempt = CallList.Count(c => c.Chunk == chunk);
            CallList.Add((chunk, server));
        }

        Started.TrySetResult();

        if (Gate != null)
        {
            await Gate.Task;
        }

        if (Broken.Contains(chunk))
        {
            throw new HttpRequestException("reset");
        }

        var data = Serve(attempt, Data[Convert.ToHexString(chunk.ChunkID!)]);
        data.CopyTo(destination, 0);

        return data.Length;
    }
}

// Waits between retries end right away
internal class InstantTimeProvider : TimeProvider
{
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        base.CreateTimer(callback, state, TimeSpan.Zero, period);
}

// Waits end right away, and every reading of the clock is a step later than the one before
internal sealed class SteppingTimeProvider(TimeSpan step) : InstantTimeProvider
{
    private long Timestamp;

    public override long TimestampFrequency => 1000;

    public override long GetTimestamp() => Interlocked.Add(ref Timestamp, (long)step.TotalMilliseconds);
}
