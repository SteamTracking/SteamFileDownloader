namespace SteamFileDownloader.Tests;

public class GetCommandTests
{
    [Test]
    public async Task UnmatchedInOneManifestNamesIt()
    {
        var message = GetCommand.DescribeUnmatched(["server.dll"], 1, "2347771:123", 0, "depot", null);

        await Assert.That(message).IsEqualTo("\"server.dll\" matched nothing in 2347771:123.");
    }

    [Test]
    public async Task UnmatchedSaysHowManyDepotsWereSkipped()
    {
        var message = GetCommand.DescribeUnmatched(["a", "b"], 9, null, 3, "depot", null);

        await Assert.That(message).IsEqualTo("\"a\", \"b\" matched nothing in any of the 9 manifests (3 depots skipped without access).");
    }

    [Test]
    public async Task UnmatchedHintQuotesPatternsForTheShell()
    {
        var message = GetCommand.DescribeUnmatched(["*.dll", "x"], 2, null, 1, "item", "To search every depot's latest manifest: SFD ls 730 --");

        await Assert.That(message).IsEqualTo("\"*.dll\", \"x\" matched nothing in any of the 2 manifests (1 item skipped without access). To search every depot's latest manifest: SFD ls 730 -- '*.dll' x");
    }

    [Test]
    [Arguments("server.dll", "server.dll")]
    [Arguments("game/bin/win64/*.dll", "'game/bin/win64/*.dll'")]
    [Arguments("regex:lib(tier0|engine2)\\.so$", "'regex:lib(tier0|engine2)\\.so$'")]
    [Arguments("a b", "'a b'")]
    [Arguments("pak01_dir.vpk:regex:^scripts/[a-z]+\\.txt$", "'pak01_dir.vpk:regex:^scripts/[a-z]+\\.txt$'")]
    [Arguments("{a,b}.dll", "'{a,b}.dll'")]
    [Arguments("~/x", "'~/x'")]
    [Arguments("#x", "'#x'")]
    [Arguments("a!b", "'a!b'")]
    [Arguments("`x`", "'`x`'")]
    [Arguments("say \"hi\"", "'say \"hi\"'")]
    [Arguments("it's", "\"it's\"")]
    public async Task ArgumentsAreQuotedForTheShell(string argument, string expected)
    {
        await Assert.That(Help.QuoteArgument(argument)).IsEqualTo(expected);
    }

    private static Target Target(uint appID, uint? depotID, ulong? manifestID = null, string branch = "public") => new("t", appID, depotID, manifestID, branch);

    private static string? SearchAllHint(params Target[] targets) => GetCommand.GetSearchAllHint(targets)?.Replace(Help.ExeName, "SFD", StringComparison.Ordinal);

    [Test]
    public async Task SearchAllHintIsForTargetsOfOneApp()
    {
        await Assert.That(SearchAllHint(Target(730, 1))).IsEqualTo("To search every depot: SFD ls 730 --");
        await Assert.That(SearchAllHint(Target(730, 1, 5), Target(730, 2))).IsEqualTo("To search every depot's latest manifest: SFD ls 730 --");
        await Assert.That(SearchAllHint(Target(730, 1), Target(570, 2))).IsNull();
        await Assert.That(SearchAllHint(Target(730, null))).IsNull();
    }

    [Test]
    public async Task SearchAllHintHasTheBranchTheTargetsShare()
    {
        await Assert.That(SearchAllHint(Target(730, 1, branch: "beta"), Target(730, 2, 5, "BETA"))).IsEqualTo("To search every depot's latest manifest: SFD ls 730 --branch beta --");
        await Assert.That(SearchAllHint(Target(730, 1, branch: "my beta"))).IsEqualTo("To search every depot: SFD ls 730 --branch 'my beta' --");
        await Assert.That(SearchAllHint(Target(730, 1, branch: "Public"))).IsEqualTo("To search every depot: SFD ls 730 --");
        await Assert.That(SearchAllHint(Target(730, 1, branch: "beta"), Target(730, 2))).IsEqualTo("To search every depot: SFD ls 730 --");
    }
}
