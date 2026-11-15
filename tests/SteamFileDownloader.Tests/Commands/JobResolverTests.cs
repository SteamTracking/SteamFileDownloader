using SteamKit2;

namespace SteamFileDownloader.Tests;

public class JobResolverTests
{
    private const uint AppID = 1000;

    private const string AppInfoText = """
        "appinfo"
        {
            "depots"
            {
                "10"
                {
                    "manifests"
                    {
                        "public" { "gid" "100" }
                        "beta" { "gid" "101" }
                    }
                }
                "11"
                {
                    "manifests"
                    {
                        "public" { "gid" "110" }
                    }
                }
                "12"
                {
                    "config" { "oslist" "linux" }
                }
                "13"
                {
                    "sharedinstall" "1"
                    "depotfromapp" "2000"
                    "manifests"
                    {
                        "public" { "gid" "130" }
                    }
                }
                "branches"
                {
                    "public" { "buildid" "1" }
                    "beta" { "buildid" "2" }
                }
            }
        }
        """;

    private const string SourceAppInfoText = """
        "appinfo"
        {
            "depots"
            {
                "13"
                {
                    "dlcappid" "2001"
                    "manifests"
                    {
                        "public" { "gid" "230" }
                    }
                }
            }
        }
        """;

    private static KeyValue Parse(string text) => KeyValue.LoadFromString(text)!;

    private static Dictionary<uint, KeyValue> AppInfos() => new() { [AppID] = Parse(AppInfoText) };

    private static Target MakeTarget(uint? depotID, ulong? manifestID, string branch) =>
        new($"app {AppID}", AppID, depotID, manifestID, branch);

    private static (List<ResolvedTarget> Targets, List<string> Failures) Resolve(Target target, Dictionary<uint, KeyValue>? appInfos = null)
    {
        var failures = new FailureLog();
        var targets = JobResolver.ResolveTargets([target], appInfos ?? AppInfos(), [], failures);

        return (targets, failures.FailedTargets);
    }

    [Test]
    public async Task WholeAppIsOptionalAndFallsBackToPublic()
    {
        var resolution = Resolve(MakeTarget(null, null, "beta"));
        var target = resolution.Targets.Single();

        await Assert.That(resolution.Failures).IsEmpty();
        await Assert.That(target.WholeApp).IsTrue();
        await Assert.That(target.Depots.Select(static d => (d.DepotID, d.ManifestID, d.Branch)))
            .IsEquivalentTo([(10u, 101ul, "beta"), (11u, 110ul, "public")]);
        await Assert.That(target.Depots.All(static d => d.Optional)).IsTrue();
    }

    [Test]
    public async Task WholeAppLeavesOutSharedInstallDepots()
    {
        var resolution = Resolve(MakeTarget(null, null, "public"));

        await Assert.That(resolution.Targets.Single().Depots.Select(static d => d.DepotID)).IsEquivalentTo([10u, 11u]);
    }

    [Test]
    public async Task WholeAppWithoutAnyManifestFails()
    {
        var appInfos = new Dictionary<uint, KeyValue> { [AppID] = Parse("\"appinfo\" { \"depots\" { \"12\" { } } }") };
        var resolution = Resolve(MakeTarget(null, null, "public"), appInfos);

        await Assert.That(resolution.Targets).IsEmpty();
        await Assert.That(resolution.Failures).IsEquivalentTo(["app 1000 (no depots)"]);
    }

    [Test]
    public async Task UnknownBranchFails()
    {
        var resolution = Resolve(MakeTarget(null, null, "missing"));

        await Assert.That(resolution.Targets).IsEmpty();
        await Assert.That(resolution.Failures).IsEquivalentTo(["app 1000 (branch missing)"]);
    }

    [Test]
    public async Task MissingAppInfoFails()
    {
        var resolution = Resolve(MakeTarget(null, null, "public"), []);

        await Assert.That(resolution.Failures).IsEquivalentTo(["app 1000 (no app info for 1000)"]);
    }

    [Test]
    public async Task DepotTargetUsesTheLatestManifestOfItsBranch()
    {
        var resolution = Resolve(MakeTarget(10, null, "beta"));
        var depot = resolution.Targets.Single().Depots.Single();

        await Assert.That(resolution.Targets.Single().WholeApp).IsFalse();
        await Assert.That(depot.ManifestID).IsEqualTo(101ul);
        await Assert.That(depot.Branch).IsEqualTo("beta");
        await Assert.That(depot.Optional).IsFalse();
    }

    [Test]
    public async Task DepotTargetWithoutManifestFails()
    {
        var resolution = Resolve(MakeTarget(12, null, "public"));

        await Assert.That(resolution.Targets).IsEmpty();
        await Assert.That(resolution.Failures).IsEquivalentTo(["app 1000 (no manifest)"]);
    }

    [Test]
    public async Task DepotTargetNotInTheAppFails()
    {
        var resolution = Resolve(MakeTarget(99, null, "public"));

        await Assert.That(resolution.Failures).IsEquivalentTo(["app 1000 (not in app 1000)"]);
    }

    [Test]
    public async Task ExplicitManifestNeedsNoAppInfo()
    {
        var resolution = Resolve(MakeTarget(99, 555, "public"), []);
        var depot = resolution.Targets.Single().Depots.Single();

        await Assert.That(resolution.Failures).IsEmpty();
        await Assert.That((depot.AppID, depot.DepotID, depot.ManifestID, depot.Branch, depot.Optional)).IsEqualTo((AppID, 99u, 555ul, "public", false));
    }

    [Test]
    public async Task ExplicitManifestKeysADlcDepotWithItsDlc()
    {
        var appInfo = Parse("\"appinfo\" { \"depots\" { \"10\" { \"dlcappid\" \"1001\" } } }");
        var resolution = Resolve(MakeTarget(10, 555, "beta"), new() { [AppID] = appInfo });
        var depot = resolution.Targets.Single().Depots.Single();

        await Assert.That((depot.ManifestID, depot.KeyAppID)).IsEqualTo((555ul, 1001u));
    }

    [Test]
    public async Task LatestManifestKeysADlcDepotWithItsDlc()
    {
        var appInfo = Parse("\"appinfo\" { \"depots\" { \"10\" { \"dlcappid\" \"1001\" \"manifests\" { \"public\" { \"gid\" \"100\" } } } } }");
        var resolution = Resolve(MakeTarget(10, null, "public"), new() { [AppID] = appInfo });
        var depot = resolution.Targets.Single().Depots.Single();

        await Assert.That(resolution.Failures).IsEmpty();
        await Assert.That((depot.ManifestID, depot.KeyAppID)).IsEqualTo((100ul, 1001u));
    }

    [Test]
    public async Task DepotsWithoutManifestAreSkippedWithReasons()
    {
        var depots = AppInfo.GetDepots(Parse(AppInfoText)["depots"]);
        var skipped = new List<SkippedDepot>();
        var resolved = JobResolver.ResolveDepots(AppID, "public", depots, new PublicFallbacks().Resolve, skipped, optional: false);

        await Assert.That(resolved.Select(static d => d.DepotID)).IsEquivalentTo([10u, 11u]);
        await Assert.That(skipped.Select(static s => (s.ID, s.Reason, s.Value)))
            .IsEquivalentTo([(12u, JobResolver.NoManifestReason, (string?)null), (13u, JobResolver.SharedReason, "2000")]);
    }

    [Test]
    public async Task SharedInstallsTakeTheDepotOfTheirSourceApp()
    {
        var depots = AppInfo.GetDepots(Parse(AppInfoText)["depots"]);
        var sourceInfos = new Dictionary<uint, KeyValue> { [2000] = Parse(SourceAppInfoText) };
        var skipped = new List<SkippedDepot>();
        var resolved = JobResolver.ResolveDepots(AppID, "public", depots, new PublicFallbacks().Resolve, skipped, optional: true, sharedSourceInfos: sourceInfos);
        var shared = resolved.Single(static d => d.DepotID == 13);

        await Assert.That((shared.AppID, shared.KeyAppID, shared.ManifestID, shared.Optional)).IsEqualTo((2000u, 2001u, 230ul, true));
        await Assert.That(skipped.Select(static s => s.ID)).IsEquivalentTo([12u]);
    }

    [Test]
    public async Task SharedInstallWithoutSourceInfoIsSkipped()
    {
        var depots = AppInfo.GetDepots(Parse(AppInfoText)["depots"]);
        var skipped = new List<SkippedDepot>();
        var resolved = JobResolver.ResolveDepots(AppID, "public", depots, new PublicFallbacks().Resolve, skipped, optional: false, sharedSourceInfos: []);

        await Assert.That(resolved.Select(static d => d.DepotID)).IsEquivalentTo([10u, 11u]);
        await Assert.That(skipped.Select(static s => (s.ID, s.Reason))).Contains((13u, "app 2000 unavailable"));
    }

    [Test]
    public async Task ThePickerReplacesTheAppInfosManifests()
    {
        var depots = AppInfo.GetDepots(Parse(AppInfoText)["depots"]);
        var manifestIds = new Dictionary<uint, ulong> { [10] = 7 };
        var resolved = JobResolver.ResolveDepots(AppID, "beta", depots, PickFromIds, [], optional: false);

        await Assert.That(resolved.Select(static d => (d.DepotID, d.ManifestID, d.Branch))).IsEquivalentTo([(10u, 7ul, "beta")]);

        (ulong ManifestID, string Branch) PickFromIds(uint appID, uint depotID, KeyValue depot, string branch) =>
            (manifestIds.GetValueOrDefault(depotID), branch);
    }
}
