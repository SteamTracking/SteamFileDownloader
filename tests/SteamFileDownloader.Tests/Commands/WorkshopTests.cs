using SteamKit2;
using SteamKit2.Internal;
using static SteamFileDownloader.Tests.FakeWorkshopSource;

namespace SteamFileDownloader.Tests;

public class WorkshopTests
{
    // The result rows on stdout by their first column
    private static Dictionary<string, string[]> Rows() => TestContext.Current!.GetStandardOutput()
        .Split(Environment.NewLine)
        .Select(static line => line.Split('\t'))
        .Where(static columns => columns.Length > 2)
        .ToDictionary(static columns => columns[0]);

    private static List<string> RowKeys() => [.. Rows().Keys];

    private static WorkshopArguments Info(string commandLine) => WorkshopArgumentParser.Parse(["info", .. commandLine.Split(' ')])!;

    [Test]
    [Arguments("3070715607", 3070715607ul, null)]
    [Arguments("3070715607:latest", 3070715607ul, null)]
    [Arguments("3070715607:8344780363095656278", 3070715607ul, 8344780363095656278ul)]
    [Arguments("https://steamcommunity.com/sharedfiles/filedetails/?id=3070715607", 3070715607ul, null)]
    [Arguments("https://steamcommunity.com/sharedfiles/filedetails/?id=3070715607&searchtext=dust", 3070715607ul, null)]
    [Arguments("steamcommunity.com/workshop/filedetails/?l=english&id=3070715607", 3070715607ul, null)]
    [Arguments("https://steamcommunity.com/sharedfiles/filedetails/changelog/3070715607", 3070715607ul, null)]
    [Arguments("steam://url/CommunityFilePage/3070715607", 3070715607ul, null)]
    public async Task ItemsAreIdsOrPageUrls(string arg, ulong itemID, ulong? manifestID)
    {
        var target = WorkshopArgumentParser.ParseTarget(arg);

        await Assert.That(target).IsNotNull();
        await Assert.That(target!.ItemID).IsEqualTo(itemID);
        await Assert.That(target.ManifestID).IsEqualTo(manifestID);
    }

    [Test]
    [Arguments("0")]
    [Arguments("3070715607:0")]
    [Arguments("730/3070715607")]
    [Arguments("dust2")]
    [Arguments("https://steamcommunity.com/sharedfiles/filedetails/?id=3070715607:8344780363095656278")]
    [Arguments("https://steamcommunity.com/app/730/workshop/")]
    public async Task OtherItemsAreRejected(string arg)
    {
        await Assert.That(WorkshopArgumentParser.ParseTarget(arg)).IsNull();
    }

    [Test]
    [Arguments("info 3070715607:8344780363095656278")]
    [Arguments("info 3070715607 --limit 5")]
    [Arguments("info 3070715607 --history --limit 0")]
    [Arguments("info 3070715607 --history --limit some")]
    [Arguments("info 3070715607 -- a.vpk")]
    [Arguments("info")]
    [Arguments("bogus 3070715607")]
    public async Task InvalidCommandLinesAreRejected(string commandLine)
    {
        await Assert.That(WorkshopArgumentParser.Parse(commandLine.Split(' '))).IsNull();
    }

    [Test]
    [Arguments("--history", WorkshopArgumentParser.DefaultLimit)]
    [Arguments("--history --limit 3", 3)]
    [Arguments("--history --limit ALL", int.MaxValue)]
    public async Task HistoryLimitDefaultsAndAll(string options, int limit)
    {
        var arguments = Info($"3070715607 {options}");

        await Assert.That(arguments.History).IsTrue();
        await Assert.That(arguments.Limit).IsEqualTo(limit);
    }

    [Test]
    public async Task CollectionsExpandLevelByLevelFetchingEachItemOnce()
    {
        var source = new FakeWorkshopSource().Add(
            Collection(1, (2, 1), (10, 2)),
            Collection(2, (11, 1), (1, 2), (10, 3)),
            Item(10),
            Item(11));

        var details = await Workshop.GetDetailsAsync(source, [1, 10, 1]);

        await Assert.That(details.Keys.Order()).IsEquivalentTo([1ul, 2ul, 10ul, 11ul]);
        await Assert.That(source.DetailsRequests.Select(static r => string.Join(",", r))).IsEquivalentTo(["1,10", "2", "11"]);
    }

    [Test]
    public async Task CollectionItemsFollowSortOrderWithNestedCollectionsInPlace()
    {
        var source = new FakeWorkshopSource().Add(
            Collection(1, (12, 5), (2, 0), (10, 3)),
            Collection(2, (11, 2), (1, 1), (10, 7)),
            Item(10),
            Item(11),
            Unavailable(12, EResult.AccessDenied));
        var details = await Workshop.GetDetailsAsync(source, [1]);

        var items = Workshop.GetCollectionItems(details[1], details);

        await Assert.That(items).IsEquivalentTo([11ul, 10ul, 12ul], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task ItemsWithoutFilesToDownloadSayWhy()
    {
        await Assert.That(Workshop.GetProblem(5, Item(5))).IsNull();
        await Assert.That(Workshop.GetProblem(5, Item(5, manifest: 0))).IsNull();
        await Assert.That(Workshop.GetProblem(5, Item(5, manifest: 9, flags: 0))!.Value.NoAccess).IsFalse();
        await Assert.That(Workshop.GetProblem(5, Unavailable(5, EResult.AccessDenied))!.Value.NoAccess).IsTrue();
        await Assert.That(Workshop.GetProblem(5, null)!.Value.NoAccess).IsFalse();
    }

    [Test]
    [Arguments(EResult.FileNotFound, "removed")]
    [Arguments(EResult.AccessDenied, "private")]
    [Arguments(EResult.Busy, "without details")]
    public async Task ProblemsHaveAShortReasonForSkippedItems(EResult result, string reason)
    {
        await Assert.That(Workshop.GetProblem(5, Unavailable(5, result))!.Value.SkipReason).IsEqualTo(reason);
        await Assert.That(Workshop.GetProblem(5, Item(5, manifest: 9, flags: 0))!.Value.SkipReason).IsEqualTo("legacy");
    }

    [Test]
    public async Task MissingIdThatLooksLikeAnAppPointsToSearch()
    {
        await Assert.That(Workshop.GetProblem(730, null)!.Value.Message).Contains("workshop search 730");
        await Assert.That(Workshop.GetProblem(3070715607, null)!.Value.Message).DoesNotContain("workshop search");
    }

    [Test]
    public async Task TextOnlyChangesCollapseIntoTheVersionThatUploadedTheManifest()
    {
        var versions = Workshop.CollapseHistory([
            Change(30, 300, "description"),
            Change(20, 250, "typo"),
            Change(30, 200, "new map"),
            Change(20, 100, "first"),
            Change(10, 50, "old"),
        ]);

        await Assert.That(versions.Select(static v => v.ManifestID)).IsEquivalentTo([30ul, 20ul, 10ul], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(versions[0]).IsEqualTo(new WorkshopVersion(30, 200, "new map"));
        await Assert.That(versions[1]).IsEqualTo(new WorkshopVersion(20, 100, "first"));
    }

    [Test]
    public async Task HistoryPagesUntilTheLastListedVersionIsComplete()
    {
        var source = new FakeWorkshopSource();
        var changes = Enumerable.Range(0, (int)Workshop.HistoryPageSize + 1).Select(static i => Change(7, (uint)(5000 - i))).ToList();
        changes.Add(Change(6, 10));
        changes.Add(Change(5, 5));
        source.Histories[1] = changes;

        var (total, raw) = await Workshop.GetHistoryAsync(source, 1, 1);

        await Assert.That(Workshop.CollapseHistory(raw)).IsEquivalentTo([new WorkshopVersion(7, 5000 - Workshop.HistoryPageSize, "")]);
        await Assert.That(total).IsEqualTo((uint)changes.Count);
        await Assert.That(raw.Count).IsEqualTo((int)Workshop.HistoryPageSize + 1);
        await Assert.That(source.HistoryRequests.Count).IsEqualTo(2);
    }

    [Test]
    public async Task HistoryWithoutALimitReadsEveryPage()
    {
        var source = new FakeWorkshopSource();
        source.Histories[1] = [.. Enumerable.Range(0, 2500).Select(static i => Change((ulong)(2500 - i), (uint)(2500 - i)))];

        var (_, changes) = await Workshop.GetHistoryAsync(source, 1, int.MaxValue);

        await Assert.That(Workshop.CollapseHistory(changes).Count).IsEqualTo(2500);
        await Assert.That(source.HistoryRequests.Select(static r => r.StartIndex)).IsEquivalentTo([0u, 1000u, 2000u]);
    }

    [Test]
    public async Task InfoRowsListCollectionsWithTheirItems()
    {
        var source = new FakeWorkshopSource()
            .Add(
                Collection(1, (10, 1), (11, 2), (12, 3)),
                Item(10, manifest: 1000, title: "Map\twith\r\na tab"),
                Item(11, manifest: 0),
                Unavailable(12, EResult.AccessDenied),
                Item(20, manifest: 2000, app: 550, flags: 0))
            .AddApp(730, 730);
        source.Histories[11] = [Change(1100, 50)];
        var outcome = new WorkshopCommands.Outcome();

        await WorkshopCommands.WriteInfoAsync(source, Info("1 20"), outcome);

        var rows = Rows();
        await Assert.That(RowKeys()).IsEquivalentTo(["1", "10", "11", "20"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(rows["1"][3]).IsEqualTo("collection");
        await Assert.That(rows["10"][2..4]).IsEquivalentTo(["730", "1000"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(rows["10"][^1]).IsEqualTo("Map with a tab");
        await Assert.That(rows["11"][3]).IsEqualTo("1100");
        await Assert.That(rows["20"][2..4]).IsEquivalentTo(["-", "legacy"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(outcome.Failed).IsFalse();
    }

    [Test]
    public async Task PrivateItemFailsForLackOfAccess()
    {
        var source = new FakeWorkshopSource().Add(Item(10, manifest: 1000), Unavailable(12, EResult.AccessDenied)).AddApp(730, 730);
        var outcome = new WorkshopCommands.Outcome();

        await WorkshopCommands.WriteInfoAsync(source, Info("10 12"), outcome);

        await Assert.That(RowKeys()).IsEquivalentTo(["10"]);
        await Assert.That(outcome.Failed).IsTrue();
        await Assert.That(outcome.FailedOtherwise).IsFalse();
    }

    [Test]
    public async Task HistoryRowsAreItemManifestPairs()
    {
        var source = new FakeWorkshopSource().Add(Item(10, manifest: 3), Collection(1, (10, 1)));
        source.Histories[10] = [Change(3, 300, "new\nmap"), Change(2, 200), Change(1, 100)];
        var outcome = new WorkshopCommands.Outcome();

        await WorkshopCommands.WriteInfoAsync(source, Info("10 1 --history --limit 2"), outcome);

        var rows = Rows();
        await Assert.That(RowKeys()).IsEquivalentTo(["10:3", "10:2"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(rows["10:3"][2]).IsEqualTo("new map");
        await Assert.That(outcome.FailedOtherwise).IsTrue();
    }

    [Test]
    public async Task InfoHasNoJsonOption()
    {
        await Assert.That(WorkshopArgumentParser.Parse(["info", "3070715607", "--json"])).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains("Unknown option \"--json\"");
    }

    [Test]
    public async Task SearchTakesAnAppThenTheWords()
    {
        var arguments = WorkshopArgumentParser.Parse(["search", "730", "de", "dust", "--limit", "all"]);

        await Assert.That(arguments).IsNotNull();
        await Assert.That(arguments!.SearchApp).IsEqualTo(730u);
        await Assert.That(arguments.SearchText).IsEqualTo("de dust");
        await Assert.That(arguments.Limit).IsEqualTo(int.MaxValue);
        await Assert.That(WorkshopArgumentParser.Parse(["search", "de_dust2"])).IsNull();
        await Assert.That(WorkshopArgumentParser.Parse(["search", "730"])).IsNull();
    }

    private static FakeWorkshopSource SearchSource(int results) => new FakeWorkshopSource { SearchResults = [.. Enumerable.Range(1, results).Select(static i => Item((ulong)i, manifest: (ulong)(i * 10)))] }.AddApp(730, 730);

    [Test]
    public async Task SearchPagesPastTheCapUpToTheLimit()
    {
        var source = SearchSource(250);

        await WorkshopCommands.WriteSearchAsync(source, WorkshopArgumentParser.Parse(["search", "730", "map", "--limit", "230"])!);

        await Assert.That(RowKeys().Count).IsEqualTo(230);
        await Assert.That(RowKeys()[0]).IsEqualTo("1");
        await Assert.That(source.QueryRequests.Select(static r => r.numperpage)).IsEquivalentTo([100u, 100u, 30u], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(source.QueryRequests.All(static r => r.search_text == "map" && r.appid == 730 && r.return_details)).IsTrue();
    }

    [Test]
    public async Task SearchWithoutALimitStopsWhenResultsRunOut()
    {
        var source = SearchSource(150);

        await WorkshopCommands.WriteSearchAsync(source, WorkshopArgumentParser.Parse(["search", "730", "map", "--limit", "all"])!);

        await Assert.That(RowKeys().Count).IsEqualTo(150);
    }

    [Test]
    public async Task SearchRowsShowManifestsOnlyWhenTheDetailsHaveThem()
    {
        var withManifest = Item(1, manifest: 10);
        withManifest.subscriptions = 1234;
        var source = new FakeWorkshopSource { SearchResults = [withManifest, Item(2, manifest: 0), Item(3, manifest: 30, flags: 0)] }.AddApp(730, 730);

        await WorkshopCommands.WriteSearchAsync(source, WorkshopArgumentParser.Parse(["search", "730", "map"])!);

        var rows = Rows();
        await Assert.That(rows["1"][2..4]).IsEquivalentTo(["730", "10"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(rows["1"][6]).IsEqualTo("1234");
        await Assert.That(rows["2"][3]).IsEqualTo("-");
        await Assert.That(rows["3"][3]).IsEqualTo("legacy");
    }

    [Test]
    [Arguments("a\tb\r\n  c\u0007", "a b c")]
    [Arguments("  padded  ", "padded")]
    [Arguments(null, "")]
    public async Task TextInRowsHasNoControlCharacters(string? text, string expected)
    {
        await Assert.That(Workshop.CleanText(text)).IsEqualTo(expected);
    }

    [Test]
    public async Task SearchWithoutTextNamesTheApp()
    {
        await Assert.That(WorkshopArgumentParser.Parse(["search", "730"])).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains("workshop search 730 needs text after the app id");
    }

    [Test]
    public async Task SearchWithTextFirstSaysTheAppGoesFirst()
    {
        await Assert.That(WorkshopArgumentParser.Parse(["search", "de", "dust2"])).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains("The app id goes first, like: ").And.Contains("workshop search 730 de dust2.");
    }

    [Test]
    public async Task SearchWithTheAppAfterTheTextMovesItFirst()
    {
        await Assert.That(WorkshopArgumentParser.Parse(["search", "de_dust2", "570"])).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains("workshop search 570 de_dust2.");
    }

    [Test]
    public async Task MissingItemsPointAtSearch()
    {
        await Assert.That(WorkshopArgumentParser.Parse(["info"])).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains("workshop info needs item ids or URLs");
    }

    [Test]
    [Arguments("3070715607")]
    [Arguments("https://steamcommunity.com/sharedfiles/filedetails/?id=3070715607")]
    public async Task ItemInPlaceOfTheSubcommandIsExplained(string item)
    {
        await Assert.That(WorkshopArgumentParser.Parse([item])).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains("workshop needs a subcommand before the item");
    }

    [Test]
    public async Task InvalidItemOfInfoPointsAtSearch()
    {
        await Assert.That(WorkshopArgumentParser.Parse(["info", "abc"])).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains("Invalid item \"abc\", expected an item id or URL");
    }

    [Test]
    public async Task InvalidItemOfLsKeepsTheTargetSyntax()
    {
        await Assert.That(WorkshopArgumentParser.Parse(["ls", "abc"])).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains("expected <item>[:<manifest>|:latest]");
    }
}
