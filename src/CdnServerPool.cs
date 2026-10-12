using System;
using System.Collections.Generic;
using System.Threading;
using SteamKit2.CDN;

namespace SteamFileDownloader;

/// <summary>
/// The CDN servers that requests are spread over, and which of them are sitting out after failing.
/// </summary>
internal sealed class CdnServerPool(TimeProvider timeProvider)
{
    private const int ActiveServers = 8;
    private const int FailuresToBench = 3;
    private static readonly TimeSpan BenchTime = TimeSpan.FromSeconds(30);

    private readonly Lock ServerLock = new();
    private List<Server> Servers = [];
    private (int Failures, long BenchedUntil)[] Health = [];
    private uint Next;

    public int Count => Servers.Count;

    /// <summary>
    /// Replaces the servers, in the order of preference, all of them working.
    /// </summary>
    public void SetServers(List<Server> servers)
    {
        lock (ServerLock)
        {
            Servers = servers;
            Health = new (int, long)[servers.Count];
        }
    }

    /// <summary>
    /// Takes turns between the best few servers that work, so one slow or overloaded server doesn't hold back the whole download.
    /// </summary>
    public Server Get(Server? avoid)
    {
        lock (ServerLock)
        {
            var now = timeProvider.GetTimestamp();
            Span<int> usable = stackalloc int[ActiveServers];
            var count = 0;

            // The directory lists the best servers for the cell first
            for (var i = 0; i < Servers.Count && count < usable.Length; i++)
            {
                if (Health[i].BenchedUntil <= now && Servers[i] != avoid)
                {
                    usable[count++] = i;
                }
            }

            if (count > 0)
            {
                return Servers[usable[(int)(Next++ % (uint)count)]];
            }

            // Every server failed lately, so they all take turns, still skipping the one to avoid
            var server = Servers[(int)(Next++ % (uint)Servers.Count)];

            return server == avoid && Servers.Count > 1 ? Servers[(int)(Next++ % (uint)Servers.Count)] : server;
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
