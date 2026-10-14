using SteamKit2;

namespace SteamFileDownloader.Tests;

public class AppInfoTests
{
    private const string Depots = """
        "depots"
        {
            "228990"
            {
                "manifests"
                {
                    "public"
                    {
                        "gid" "111"
                        "size" "10"
                    }
                    "beta"
                    {
                        "gid" "222"
                    }
                }
            }
            "228991"
            {
                "manifests"
                {
                    "public"
                    {
                        "gid" "333"
                    }
                }
            }
            "baselanguages" "english"
            "branches"
            {
                "public"
                {
                    "buildid" "1"
                }
                "beta"
                {
                    "buildid" "2"
                }
                "locked"
                {
                    "buildid" "3"
                    "pwdrequired" "1"
                }
            }
        }
        """;

    private static KeyValue Parse(string text) => KeyValue.LoadFromString(text)!;

    private static KeyValue DepotOf(uint id) => AppInfo.GetDepots(Parse(Depots)).First(d => d.ID == id).Depot;

    [Test]
    public async Task GetDepotsReturnsOnlyNumericKeys()
    {
        var depots = AppInfo.GetDepots(Parse(Depots)).ToList();

        await Assert.That(depots.Select(static d => d.ID)).IsEquivalentTo([228990u, 228991u]);
        await Assert.That(depots.All(static d => d.Depot.Name == d.ID.ToString())).IsTrue();
    }

    [Test]
    public async Task GetDepotsOfEmptySectionIsEmpty()
    {
        await Assert.That(AppInfo.GetDepots(Parse("\"depots\" { }")).Any()).IsFalse();
    }

    [Test]
    [Arguments("public")]
    [Arguments("PUBLIC")]
    [Arguments("beta")]
    public async Task CheckBranchAcceptsExistingBranches(string branch)
    {
        await Assert.That(AppInfo.CheckBranch(Parse(Depots), 228980, branch)).IsTrue();
    }

    [Test]
    public async Task CheckBranchRejectsMissingBranch()
    {
        await Assert.That(AppInfo.CheckBranch(Parse(Depots), 228980, "nope")).IsFalse();
    }

    [Test]
    public async Task CheckBranchAcceptsPublicWithoutAnyBranches()
    {
        await Assert.That(AppInfo.CheckBranch(Parse("\"depots\" { }"), 1, "public")).IsTrue();
    }

    [Test]
    public async Task CheckBranchRejectsPasswordProtectedBranchWithoutManifests()
    {
        await Assert.That(AppInfo.CheckBranch(Parse(Depots), 228980, "locked")).IsFalse();
    }

    [Test]
    public async Task CheckBranchAcceptsPasswordProtectedBranchWhoseManifestsAreVisible()
    {
        var depots = Parse("""
            "depots"
            {
                "1"
                {
                    "manifests"
                    {
                        "secret" "999"
                    }
                }
                "branches"
                {
                    "secret"
                    {
                        "buildid" "5"
                        "pwdrequired" "1"
                    }
                }
            }
            """);

        await Assert.That(AppInfo.CheckBranch(depots, 1, "secret")).IsTrue();
    }

    [Test]
    public async Task ManifestIdOfBranchIsReadFromGid()
    {
        await Assert.That(AppInfo.GetManifestIdForBranch(DepotOf(228990), "beta")).IsEqualTo((222ul, false));
    }

    [Test]
    public async Task ManifestIdCanBeTheValueItself()
    {
        var depot = Parse("""
            "1"
            {
                "manifests"
                {
                    "public" "444"
                }
            }
            """);
        await Assert.That(AppInfo.GetManifestIdForBranch(depot, "public")).IsEqualTo((444ul, false));
    }

    [Test]
    public async Task MissingBranchManifestFallsBackToPublic()
    {
        await Assert.That(AppInfo.GetManifestIdForBranch(DepotOf(228991), "beta")).IsEqualTo((333ul, true));
    }

    [Test]
    public async Task FallbacksToPublicAreLoggedOncePerAppAndBranch()
    {
        var fallbacks = new PublicFallbacks();

        await Assert.That(fallbacks.Resolve(730, 228990, DepotOf(228990), "beta")).IsEqualTo((222ul, "beta"));
        await Assert.That(fallbacks.Resolve(730, 228991, DepotOf(228991), "beta")).IsEqualTo((333ul, "public"));
        await Assert.That(fallbacks.Resolve(730, 228991, DepotOf(228991), "beta")).IsEqualTo((333ul, "public"));
        await Assert.That(fallbacks.Resolve(570, 228991, DepotOf(228991), "other")).IsEqualTo((333ul, "public"));

        fallbacks.Log();

        // Messages go to stdout or stderr, depending on whether another test made stdout carry results
        var output = TestContext.Current!.GetStandardOutput() + TestContext.Current.GetErrorOutput();

        await Assert.That(output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)).IsEquivalentTo([
            "Depot 228991 of app 730 has no manifest on \"beta\", using the one on \"public\".",
            "Depot 228991 of app 570 has no manifest on \"other\", using the one on \"public\".",
        ], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task MissingManifestReturnsZeroAndKeepsTheBranch()
    {
        var depot = Parse("""
            "1"
            {
                "manifests" { }
            }
            """);
        await Assert.That(AppInfo.GetManifestIdForBranch(depot, "beta")).IsEqualTo((0ul, false));
    }

    [Test]
    public async Task DepotWithoutManifestsSectionReturnsZero()
    {
        await Assert.That(AppInfo.GetManifestIdForBranch(Parse("\"1\" { \"config\" { } }"), "public")).IsEqualTo((0ul, false));
    }

    [Test]
    public async Task UnparsableManifestIdFallsBackToPublic()
    {
        var depot = Parse("""
            "1"
            {
                "manifests"
                {
                    "public" "555"
                    "beta" { "gid" "abc" }
                }
            }
            """);
        await Assert.That(AppInfo.GetManifestIdForBranch(depot, "beta")).IsEqualTo((555ul, true));
    }
}
