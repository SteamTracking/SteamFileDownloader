using System.Net;
using SteamKit2.CDN;

namespace SteamFileDownloader.Tests;

public class CdnServerPoolTests
{
    private sealed class FakeTimeProvider : TimeProvider
    {
        private long Ticks;

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => Ticks;

        public void Advance(TimeSpan time) => Ticks += (long)(time.TotalSeconds * TimestampFrequency);
    }

    private static List<Server> MakeServers(int count) =>
        [.. Enumerable.Range(0, count).Select(static i => (Server)new DnsEndPoint($"cache{i}.example.com", 80))];

    private static (CdnServerPool Pool, FakeTimeProvider Clock, List<Server> Servers) Create(int count)
    {
        var clock = new FakeTimeProvider();
        var pool = new CdnServerPool(clock);
        var servers = MakeServers(count);
        pool.SetServers(servers);

        return (pool, clock, servers);
    }

    private static void Fail(CdnServerPool pool, Server server, int times)
    {
        for (var i = 0; i < times; i++)
        {
            pool.MarkBad(server);
        }
    }

    // Leases that are never released, so each server taken stays busy
    private static List<Server> Take(CdnServerPool pool, int count, Server? avoid = null) =>
        [.. Enumerable.Range(0, count).Select(_ => pool.Rent(avoid).Server)];

    // Leases released right away, so every server stays idle
    private static List<Server> TakeAndRelease(CdnServerPool pool, int count)
    {
        var taken = new List<Server>();

        for (var i = 0; i < count; i++)
        {
            using var lease = pool.Rent(null);
            taken.Add(lease.Server);
        }

        return taken;
    }

    [Test]
    public async Task LeastBusyServerIsPicked()
    {
        var (pool, _, _) = Create(3);

        var first = pool.Rent(null);
        pool.Rent(null);
        pool.Rent(null);
        first.Dispose();

        await Assert.That(pool.Rent(null).Server).IsSameReferenceAs(first.Server);
    }

    [Test]
    public async Task SlowServerGetsNoNewRequestsUntilItCatchesUp()
    {
        var (pool, _, _) = Create(2);

        var slow = pool.Rent(null);
        var taken = TakeAndRelease(pool, 4);

        await Assert.That(taken.Contains(slow.Server)).IsFalse();

        slow.Dispose();

        await Assert.That(TakeAndRelease(pool, 4).Contains(slow.Server)).IsTrue();
    }

    [Test]
    public async Task EquallyBusyServersTakeTurns()
    {
        var (pool, _, _) = Create(3);

        var taken = TakeAndRelease(pool, 6);

        await Assert.That(taken.Take(3).Distinct().Count()).IsEqualTo(3);
        await Assert.That(taken.Skip(3).Distinct().Count()).IsEqualTo(3);
    }

    [Test]
    public async Task LeaseOfAReplacedServerIsIgnored()
    {
        var (pool, _, _) = Create(2);

        var lease = pool.Rent(null);
        pool.SetServers(MakeServers(2));
        lease.Dispose();

        await Assert.That(TakeAndRelease(pool, 2).Distinct().Count()).IsEqualTo(2);
    }

    [Test]
    public async Task LeastBusyIsPickedWhenAllAreBenched()
    {
        var (pool, _, servers) = Create(3);

        foreach (var server in servers)
        {
            Fail(pool, server, 3);
        }

        var first = pool.Rent(null);
        var second = pool.Rent(null);

        var third = pool.Rent(null).Server;

        await Assert.That(third != first.Server && third != second.Server).IsTrue();
    }

    [Test]
    public async Task SpreadsOverTheFirstEightServersOnly()
    {
        var (pool, _, servers) = Create(12);

        var taken = Take(pool, 16);

        await Assert.That(taken.Distinct().Count()).IsEqualTo(8);
        await Assert.That(taken.Except(servers.Take(8)).Any()).IsFalse();
        await Assert.That(taken.Take(8).Distinct().Count()).IsEqualTo(8);
    }

    [Test]
    public async Task SpreadsOverAllServersWhenThereAreFewer()
    {
        var (pool, _, servers) = Create(3);

        var taken = Take(pool, 6);

        await Assert.That(taken.Take(3).Distinct().Count()).IsEqualTo(3);
        await Assert.That(taken.Except(servers).Any()).IsFalse();
    }

    [Test]
    public async Task AvoidedServerIsSkipped()
    {
        var (pool, _, servers) = Create(4);

        var taken = Take(pool, 20, servers[1]);

        await Assert.That(taken.Contains(servers[1])).IsFalse();
        await Assert.That(taken.Distinct().Count()).IsEqualTo(3);
    }

    [Test]
    public async Task AvoidedServerLetsTheNinthServerIn()
    {
        var (pool, _, servers) = Create(10);

        var taken = Take(pool, 20, servers[0]);

        await Assert.That(taken.Contains(servers[0])).IsFalse();
        await Assert.That(taken.Distinct().Count()).IsEqualTo(8);
        await Assert.That(taken.Contains(servers[8])).IsTrue();
    }

    [Test]
    public async Task ServerIsBenchedAfterThreeFailures()
    {
        var (pool, _, servers) = Create(4);

        Fail(pool, servers[2], 3);

        var taken = Take(pool, 12);

        await Assert.That(taken.Contains(servers[2])).IsFalse();
        await Assert.That(taken.Distinct().Count()).IsEqualTo(3);
    }

    [Test]
    public async Task ServerIsNotBenchedAfterTwoFailures()
    {
        var (pool, _, servers) = Create(4);

        Fail(pool, servers[2], 2);

        await Assert.That(Take(pool, 8).Contains(servers[2])).IsTrue();
    }

    [Test]
    public async Task SuccessResetsTheFailureCount()
    {
        var (pool, _, servers) = Create(4);

        Fail(pool, servers[2], 2);
        pool.MarkGood(servers[2]);
        Fail(pool, servers[2], 2);

        await Assert.That(Take(pool, 8).Contains(servers[2])).IsTrue();
    }

    [Test]
    public async Task FailuresOfOtherServersDoNotCountTowardsABench()
    {
        var (pool, _, servers) = Create(4);

        Fail(pool, servers[0], 2);
        Fail(pool, servers[1], 2);

        await Assert.That(Take(pool, 8).Distinct().Count()).IsEqualTo(4);
    }

    [Test]
    public async Task BenchedServerReturnsAfterThirtySeconds()
    {
        var (pool, clock, servers) = Create(4);

        Fail(pool, servers[2], 3);

        clock.Advance(TimeSpan.FromSeconds(29));
        await Assert.That(Take(pool, 12).Contains(servers[2])).IsFalse();

        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(Take(pool, 12).Contains(servers[2])).IsTrue();
    }

    [Test]
    public async Task SuccessOfABenchedServerKeepsItBenched()
    {
        var (pool, clock, servers) = Create(4);

        Fail(pool, servers[2], 3);
        pool.MarkGood(servers[2]);

        clock.Advance(TimeSpan.FromSeconds(29));
        await Assert.That(Take(pool, 12).Contains(servers[2])).IsFalse();

        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(Take(pool, 12).Contains(servers[2])).IsTrue();
    }

    [Test]
    public async Task FailuresWhileBenchedDoNotCountTowardsTheNextBench()
    {
        var (pool, clock, servers) = Create(4);

        Fail(pool, servers[2], 3);
        Fail(pool, servers[2], 5);
        clock.Advance(TimeSpan.FromSeconds(30));

        Fail(pool, servers[2], 2);
        await Assert.That(Take(pool, 8).Contains(servers[2])).IsTrue();

        pool.MarkBad(servers[2]);
        await Assert.That(Take(pool, 8).Contains(servers[2])).IsFalse();
    }

    [Test]
    public async Task BenchedAgainAfterThreeMoreFailuresOnceItReturned()
    {
        var (pool, clock, servers) = Create(4);

        Fail(pool, servers[2], 3);
        clock.Advance(TimeSpan.FromSeconds(30));
        Fail(pool, servers[2], 3);

        await Assert.That(Take(pool, 8).Contains(servers[2])).IsFalse();
    }

    [Test]
    public async Task EveryServerTakesTurnsWhenAllAreBenched()
    {
        var (pool, _, servers) = Create(4);

        foreach (var server in servers)
        {
            Fail(pool, server, 3);
        }

        var taken = Take(pool, 8);

        await Assert.That(taken.Distinct().Count()).IsEqualTo(4);
    }

    [Test]
    public async Task AvoidedServerIsStillSkippedWhenAllAreBenched()
    {
        var (pool, _, servers) = Create(4);

        foreach (var server in servers)
        {
            Fail(pool, server, 3);
        }

        var taken = Take(pool, 20, servers[1]);

        await Assert.That(taken.Contains(servers[1])).IsFalse();
        await Assert.That(taken.Distinct().Count()).IsEqualTo(3);
    }

    [Test]
    public async Task SingleServerIsReturnedEvenWhenItIsTheOneToAvoid()
    {
        var (pool, _, servers) = Create(1);

        await Assert.That(pool.Rent(servers[0]).Server).IsSameReferenceAs(servers[0]);

        Fail(pool, servers[0], 3);

        await Assert.That(pool.Rent(servers[0]).Server).IsSameReferenceAs(servers[0]);
    }

    [Test]
    public async Task AvoidedServerIsSkippedForABenchedOne()
    {
        var (pool, _, servers) = Create(2);

        Fail(pool, servers[1], 3);

        await Assert.That(Take(pool, 4, servers[0]).All(s => s == servers[1])).IsTrue();
    }

    [Test]
    public async Task SetServersResetsHealth()
    {
        var (pool, _, servers) = Create(4);

        Fail(pool, servers[2], 3);
        pool.SetServers(servers);

        await Assert.That(Take(pool, 8).Contains(servers[2])).IsTrue();
        await Assert.That(pool.Count).IsEqualTo(4);
    }
}
