using System.Text.Json;
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

    // The result rows of the app by depot id, as their columns: app, depot, manifest, max size, notes
    private static (bool Result, Dictionary<string, string[]> Rows) List(string depots, string branch)
    {
        var result = DepotsCommand.ListAppDepots(AppID, Parse(depots), branch);

        var rows = TestContext.Current!.GetStandardOutput()
            .Split(Environment.NewLine)
            .Select(static line => line.Split('\t'))
            .Where(static columns => columns.Length == 5 && columns[0] == AppID.ToString())
            .ToDictionary(static columns => columns[1]);

        return (result, rows);
    }

    [Test]
    [Arguments("public", "100", "1000")]
    [Arguments("public", "101", "1010")]
    [Arguments("beta", "100", "1001")]
    [Arguments("beta", "101", "1010")]
    [Arguments("public", "102", "-")]
    [Arguments("public", "103", "-")]
    public async Task EveryDepotGetsARowWithTheManifestOfTheBranch(string branch, string depot, string manifest)
    {
        var (result, rows) = List(Depots, branch);

        await Assert.That(result).IsTrue();
        await Assert.That(rows.Keys).IsEquivalentTo(["100", "101", "102", "103"]);
        await Assert.That(rows[depot][2]).IsEqualTo(manifest);
    }

    [Test]
    public async Task ManifestFromPublicIsNotedOnlyWhenTheBranchHasNone()
    {
        var (_, rows) = List(Depots, "beta");

        await Assert.That(rows["101"][4]).Contains("public");
        await Assert.That(rows["100"][4]).DoesNotContain("public");
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

        await Assert.That(exitCode).IsEqualTo(ExitCodes.Failed);
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains("\"abc\"");
    }

    [Test]
    public async Task JsonHasAnObjectPerAppWithTheWholeDepotsSection()
    {
        var json = DepotsCommand.ToJson([(AppID, Parse(Depots)), (AppID + 1, Parse("\"depots\" { }"))]);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var app = root.GetProperty("4242");

        await Assert.That(root.EnumerateObject().Select(static p => p.Name)).IsEquivalentTo(["4242", "4243"]);
        await Assert.That(app.GetProperty("100").GetProperty("manifests").GetProperty("beta").GetProperty("gid").GetString()).IsEqualTo("1001");
        await Assert.That(app.GetProperty("branches").GetProperty("public").GetProperty("timeupdated").GetString()).IsEqualTo("1700000000");
        await Assert.That(app.GetProperty("baselanguages").GetString()).IsEqualTo("english");
        await Assert.That(root.GetProperty("4243").ValueKind).IsEqualTo(JsonValueKind.Object);
        await Assert.That(root.GetProperty("4243").EnumerateObject().Any()).IsFalse();
    }

    [Test]
    public async Task JsonKeepsQuotesBackslashesAndUnicodeInValues()
    {
        const string Name = "Ünïcode \"quoted\" back\\slash <tag>";
        var depots = new KeyValue("depots");
        var depot = new KeyValue("100");
        depot.Children.Add(new KeyValue("name", Name));
        depots.Children.Add(depot);

        using var document = JsonDocument.Parse(DepotsCommand.ToJson([(AppID, depots)]));

        await Assert.That(document.RootElement.GetProperty("4242").GetProperty("100").GetProperty("name").GetString()).IsEqualTo(Name);
    }
}
