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
    [Arguments("\"depotfromapp\" \"70\" \"sharedinstall\" \"1\"", "70")]
    [Arguments("\"depotfromapp\" \"70\" \"sharedinstall\" \"2\"", "70")]
    [Arguments("\"depotfromapp\" \"90\" \"sharedinstall\" \"0\"", null)]
    [Arguments("\"depotfromapp\" \"90\"", null)]
    [Arguments("\"name\" \"depot\"", null)]
    public async Task GetSharedInstallAppNeedsANonZeroSharedInstall(string fields, string? expected)
    {
        var depot = Parse($"\"1004\" {{ {fields} }}");

        await Assert.That(AppInfo.GetSharedInstallApp(depot)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("\"dlcappid\" \"4780\"", 4780u)]
    [Arguments("\"dlcappid\" \"0\"", 4700u)]
    [Arguments("\"dlcappid\" \"x\"", 4700u)]
    [Arguments("\"name\" \"depot\"", 4700u)]
    public async Task GetKeyAppIDPrefersTheDlc(string fields, uint expected)
    {
        var depot = Parse($"\"4784\" {{ {fields} }}");

        await Assert.That(AppInfo.GetKeyAppID(depot, 4700)).IsEqualTo(expected);
    }

    [Test]
    public async Task GetKeyAppIDWithoutADepotIsTheApp()
    {
        await Assert.That(AppInfo.GetKeyAppID(null, 4700)).IsEqualTo(4700u);
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

    private const string PrivateDepots = """
        "privatedepots"
        {
            "228990"
            {
                "manifests"
                {
                    "locked"
                    {
                        "gid" "444"
                    }
                }
            }
            "228992"
            {
                "manifests"
                {
                    "locked"
                    {
                        "gid" "555"
                    }
                }
            }
            "branches"
            {
                "locked"
                {
                    "buildid" "30"
                    "pwdrequired" "1"
                }
                "hidden"
                {
                    "buildid" "40"
                    "pwdrequired" "1"
                }
            }
        }
        """;

    private static KeyValue ParseAppInfo()
    {
        var appInfo = new KeyValue("appinfo");
        appInfo.Children.Add(Parse(Depots));

        return appInfo;
    }

    [Test]
    public async Task MergePrivateDepotsAddsBranchesAndManifests()
    {
        var appInfo = ParseAppInfo();

        AppInfo.MergePrivateDepots(appInfo, Parse(PrivateDepots));

        var depots = appInfo["depots"];

        await Assert.That(depots["branches"]["locked"]["buildid"].Value).IsEqualTo("30");
        await Assert.That(depots["branches"]["hidden"]["buildid"].Value).IsEqualTo("40");
        await Assert.That(depots["branches"]["beta"]["buildid"].Value).IsEqualTo("2");
        await Assert.That(AppInfo.GetManifestIdForBranch(depots["228990"], "locked")).IsEqualTo((444ul, false));
        await Assert.That(AppInfo.GetManifestIdForBranch(depots["228990"], "beta")).IsEqualTo((222ul, false));
        await Assert.That(AppInfo.GetManifestIdForBranch(depots["228992"], "locked")).IsEqualTo((555ul, false));
        await Assert.That(AppInfo.GetDepots(depots).Select(static d => d.ID)).IsEquivalentTo([228990u, 228991u, 228992u]);
    }

    [Test]
    public async Task MergePrivateDepotsCreatesMissingDepotsSection()
    {
        var appInfo = new KeyValue("appinfo");

        AppInfo.MergePrivateDepots(appInfo, Parse(PrivateDepots));

        await Assert.That(AppInfo.GetDepots(appInfo["depots"]).Count()).IsEqualTo(2);
    }

    [Test]
    public async Task CheckBranchAcceptsUnlockedPasswordProtectedBranch()
    {
        var appInfo = ParseAppInfo();

        AppInfo.MergePrivateDepots(appInfo, Parse(PrivateDepots));

        await Assert.That(AppInfo.CheckBranch(appInfo["depots"], 228980, "locked", ["locked", "hidden"])).IsTrue();
    }

    [Test]
    public async Task CheckBranchRejectsPasswordProtectedBranchThePasswordDoesNotUnlock()
    {
        var depots = Parse(Depots);

        await Assert.That(AppInfo.CheckBranch(depots, 228980, "locked", ["other"])).IsFalse();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains("The branch password unlocks: other.");
    }

    [Test]
    public async Task CheckBranchAsksForThePasswordOfAPasswordProtectedBranch()
    {
        await Assert.That(AppInfo.CheckBranch(Parse(Depots), 228980, "locked")).IsFalse();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains("--branch-password");
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

    [Test]
    public async Task GetDlcAppIDsReadsListOfDlcWhenDepotsAreInDlc()
    {
        var appInfo = Parse("""
            "appinfo"
            {
                "depots" { "hasdepotsindlc" "1" }
                "extended" { "listofdlc" "4780, 4790,4780,x" }
            }
            """);

        await Assert.That(AppInfo.GetDlcAppIDs(appInfo)).IsEquivalentTo([4780u, 4790u]);
    }

    [Test]
    public async Task GetDlcAppIDsIsEmptyWithoutHasDepotsInDlc()
    {
        var appInfo = Parse("""
            "appinfo"
            {
                "depots" { }
                "extended" { "listofdlc" "4780" }
            }
            """);

        await Assert.That(AppInfo.GetDlcAppIDs(appInfo)).IsEmpty();
    }

    [Test]
    public async Task MergeDlcDepotsAddsOnlyNewDepotsWithDlcAppID()
    {
        var appInfo = Parse("""
            "appinfo"
            {
                "depots"
                {
                    "hasdepotsindlc" "1"
                    "4701" { "dlcappid" "4780" "name" "own" }
                }
            }
            """);
        var dlcInfo = Parse("""
            "appinfo"
            {
                "depots"
                {
                    "4701" { "dlcappid" "4780" "name" "other" }
                    "4781" { "dlcappid" "4780" }
                    "4782" { "name" "no dlc app" }
                    "branches" { "public" { "buildid" "9" } }
                }
            }
            """);

        AppInfo.MergeDlcDepots(appInfo, dlcInfo);

        var depots = AppInfo.GetDepots(appInfo["depots"]).ToList();

        await Assert.That(depots.Select(static d => d.ID)).IsEquivalentTo([4701u, 4781u]);
        await Assert.That(appInfo["depots"]["4701"]["name"].Value).IsEqualTo("own");
        await Assert.That(AppInfo.GetKeyAppID(appInfo["depots"]["4781"], 4700)).IsEqualTo(4780u);
    }
}
