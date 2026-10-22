using SteamKit2;

namespace SteamFileDownloader.Tests;

public class DepotsCommandTests
{
    private const uint AppID = 4242;

    private const string Depots = """
        "depots"
        {
            "100"
            {
                "name" "Content"
                "maxsize" "500"
                "config" { "oslist" "windows" }
                "manifests"
                {
                    "public" { "gid" "1000" }
                    "beta" { "gid" "1001" }
                }
            }
            "101"
            {
                "manifests"
                {
                    "public" { "gid" "1010" }
                }
            }
            "102"
            {
                "dlcappid" "4243"
            }
            "103"
            {
                "depotfromapp" "228980"
                "manifests"
                {
                    "public" { "gid" "1030" }
                }
            }
            "104"
            {
                "depotfromapp" "228980"
                "sharedinstall" "1"
            }
            "baselanguages" "english"
            "branches"
            {
                "public" { "buildid" "10" "timeupdated" "1700000000" }
                "beta" { "buildid" "11" }
            }
        }
        """;

    private static KeyValue Parse(string text) => KeyValue.LoadFromString(text)!;

    // The result rows by depot id, as their columns: target, max size, notes
    private static (bool Result, Dictionary<string, string[]> Rows) List(string depots, string branch)
    {
        var result = DepotsCommand.ListAppDepots(AppID, Parse(depots), branch);

        var rows = TestContext.Current!.GetStandardOutput()
            .Split(Environment.NewLine)
            .Select(static line => line.Split('\t'))
            .Where(static columns => columns.Length == 3)
            .ToDictionary(static columns => columns[0].Split('/', ':')[1]);

        return (result, rows);
    }

    [Test]
    [Arguments("public", "100", "4242/100:1000")]
    [Arguments("public", "101", "4242/101:1010")]
    [Arguments("beta", "100", "4242/100:1001")]
    [Arguments("beta", "101", "4242/101:1010")]
    [Arguments("public", "102", "4242/102")]
    [Arguments("public", "103", "4242/103:1030")]
    [Arguments("public", "104", "228980/104")]
    public async Task EveryDepotGetsARowWithATargetOfTheBranch(string branch, string depot, string target)
    {
        var (result, rows) = List(Depots, branch);

        await Assert.That(result).IsTrue();
        await Assert.That(rows.Keys).IsEquivalentTo(["100", "101", "102", "103", "104"]);
        await Assert.That(rows[depot][0]).IsEqualTo(target);
    }

    [Test]
    public async Task EveryBranchGetsARowWithItsBuildBeforeTheDepots()
    {
        DepotsCommand.ListAppDepots(AppID, Parse(Depots), "beta");

        var lines = TestContext.Current!.GetStandardOutput().Split(Environment.NewLine);

        await Assert.That(lines[0]).IsEqualTo("4242\tpublic\t10\t1700000000");
        await Assert.That(lines[1]).IsEqualTo("4242\tbeta\t11\t-");
        await Assert.That(lines[2]).StartsWith("4242/");
        await Assert.That(TestContext.Current!.GetErrorOutput()).DoesNotContain("branches");
    }

    [Test]
    public async Task TargetsParseAsTargets()
    {
        var (_, rows) = List(Depots, "public");
        var arguments = DepotArgumentParser.ParseArguments("ls", ["730", "1"])!;

        foreach (var columns in rows.Values)
        {
            await Assert.That(DepotArgumentParser.ParseTarget(columns[0], arguments)).IsNotNull();
        }
    }

    [Test]
    public async Task ManifestFromPublicIsNotedOnlyWhenTheBranchHasNone()
    {
        var (_, rows) = List(Depots, "beta");

        await Assert.That(rows["101"][2]).Contains("public");
        await Assert.That(rows["100"][2]).DoesNotContain("public");
        await Assert.That(TestContext.Current!.GetErrorOutput()).DoesNotContain("using the one on");
    }

    [Test]
    public async Task MissingBranchListsNothing()
    {
        var (result, rows) = List(Depots, "nope");

        await Assert.That(result).IsFalse();
        await Assert.That(rows).IsEmpty();
    }

    [Test]
    public async Task AppWithoutDepotsFails()
    {
        var (result, rows) = List("""
            "depots"
            {
                "branches" { "public" { "buildid" "10" } }
            }
            """, "public");

        await Assert.That(result).IsFalse();
        await Assert.That(rows).IsEmpty();
    }

    [Test]
    public async Task ArgumentThatIsNotAnAppFailsBeforeLoggingIn()
    {
        var arguments = DepotArgumentParser.ParseArguments("depots", ["730", "abc", "--anonymous"])!;

        // Logging in would throw on the cancelled token
        var exitCode = await DepotsCommand.ListDepotsAsync(arguments, new CancellationToken(canceled: true));

        await Assert.That(exitCode).IsEqualTo(ExitCodes.UsageError);
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains("\"abc\"");
    }
}
