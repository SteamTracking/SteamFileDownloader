using System;
using System.Collections.Generic;
using System.Threading;
using SteamKit2.CDN;

namespace SteamFileDownloader;

/// <summary>
/// The CDN servers that requests are spread over, how many requests each is busy with, and which of them are sitting
/// out after failing.
/// </summary>
internal sealed class CdnServerPool(TimeProvider timeProvider)
{
    private const int ActiveServers = 8;
    private const int FailuresToBench = 3;
    private static readonly TimeSpan BenchTime = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A server picked for one request, which counts as busy with it until disposed.
    /// </summary>
    internal readonly struct Lease(CdnServerPool pool, Server server) : IDisposable
    {
        public Server Server => server;

        public void Dispose() => pool.Release(server);
    }

    private readonly Lock ServerLock = new();
    private List<Server> Servers = [];
    private (int Failures, long BenchedUntil)[] Health = [];
    private int[] Busy = [];
    private uint Next;

    public int Count => Servers.Count;

    /// <summary>
    /// Replaces the servers, in the order of preference, all of them working and idle.
    /// </summary>
    public void SetServers(List<Server> servers)
    {
        lock (ServerLock)
        {
            Servers = servers;
            Health = new (int, long)[servers.Count];
            Busy = new int[servers.Count];
        }
    }

    /// <summary>
    /// Picks the least busy of the best few servers that work, so a slow server, which stays busy longer, gets fewer
    /// requests and doesn't hold back the whole download.
    /// </summary>
    public Lease Rent(Server? avoid)
    {
        lock (ServerLock)
        {
            var now = timeProvider.GetTimestamp();
            Span<int> candidates = stackalloc int[ActiveServers];
            var count = 0;

            // The directory lists the best servers for the cell first
            for (var i = 0; i < Servers.Count && count < candidates.Length; i++)
            {
                if (Health[i].BenchedUntil <= now && Servers[i] != avoid)
                {
                    candidates[count++] = i;
                }
            }

            // Every server failed lately, so they all take part, still skipping the one to avoid
            if (count == 0)
            {
                candidates = new int[Servers.Count];

                for (var i = 0; i < Servers.Count; i++)
                {
                    if (Servers[i] != avoid || Servers.Count == 1)
                    {
                        candidates[count++] = i;
                    }
                }
            }

            var index = PickLeastBusy(candidates[..count]);
            Busy[index]++;

            return new Lease(this, Servers[index]);
        }
    }

    // Starting at another candidate each time makes servers that are equally busy take turns
    private int PickLeastBusy(ReadOnlySpan<int> candidates)
    {
        var start = (int)(Next++ % (uint)candidates.Length);
        var best = candidates[start];

        for (var offset = 1; offset < candidates.Length; offset++)
        {
            var index = candidates[(start + offset) % candidates.Length];

            if (Busy[index] < Busy[best])
            {
                best = index;
            }
        }

        return best;
    }

    private void Release(Server server)
    {
        lock (ServerLock)
        {
            var index = Servers.IndexOf(server);

            // The servers may have been replaced meanwhile, along with their counts
            if (index >= 0 && Busy[index] > 0)
            {
                Busy[index]--;
            }
        }
    }

    /// <summary>
    /// A failed request, refusals included. A server that fails several with no success in between sits out for a while,
    /// like a cache that refuses everything, but one that only fails some, like a cache that is slow to fetch old chunks
    /// or one asked for a deleted chunk, keeps its turn.
    /// </summary>
    public void MarkBad(Server server)
    {
        lock (ServerLock)
        {
            ref var health = ref Health[Servers.IndexOf(server)];
            var now = timeProvider.GetTimestamp();

            // Requests that were under way when it was benched don't count towards the next time
            if (health.BenchedUntil > now || ++health.Failures < FailuresToBench)
            {
                return;
            }

            health = (0, now + (long)(BenchTime.TotalSeconds * timeProvider.TimestampFrequency));
        }
    }

    public void MarkGood(Server server)
    {
        lock (ServerLock)
        {
            Health[Servers.IndexOf(server)].Failures = 0;
        }
    }
}
