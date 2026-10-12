using System.Text.RegularExpressions;

namespace SteamFileDownloader.Tests;

public partial class HelpTests
{
    private const int MaxLineLength = 120;
    private const int HeadLines = 30;

    private static string Normalize(string text) => Help.ExeName.Length == 0 ? text : text.Replace(Help.ExeName, "SteamFileDownloader", StringComparison.Ordinal);

    private static string GetHelp(string command) => command switch
    {
        "get" => Help.Get,
        "ls" => Help.Ls,
        "depots" => Help.Depots,
        "login" => Help.Login,
        "track" => Help.Track,
        "workshop info" => Help.WorkshopInfo,
        "workshop search" => Help.WorkshopSearch,
        "workshop ls" => Help.WorkshopLs,
        "workshop get" => Help.WorkshopGet,
        _ => throw new ArgumentOutOfRangeException(nameof(command)),
    };

    public static IEnumerable<string> Commands() => ArgumentParser.CommandOptions.Keys;

    [Test]
    [MethodDataSource(nameof(Commands))]
    public async Task HelpMentionsEveryOptionOfTheCommand(string command)
    {
        var help = GetHelp(command);
        var (values, flags) = ArgumentParser.CommandOptions[command];
        var missing = values.Concat(flags).Where(o => !help.Contains($"--{o}", StringComparison.Ordinal)).ToList();

        await Assert.That(string.Join(",", missing)).IsEqualTo("");
    }

    [Test]
    [MethodDataSource(nameof(Commands))]
    public async Task HelpMentionsOnlyOptionsOfTheCommand(string command)
    {
        var (values, flags) = ArgumentParser.CommandOptions[command];
        var known = values.Concat(flags).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var help = GetHelp(command);
        // Up to the exit codes section at the end, which commands share and which mentions track's --password
        var unknown = OptionRegex().Matches(help[..help.LastIndexOf("\nExit codes:", StringComparison.Ordinal)])
            .Select(static m => m.Groups[1].Value)
            .Where(o => !known.Contains(o) && o is not ("help" or "version"))
            .Distinct()
            .ToList();

        await Assert.That(string.Join(",", unknown)).IsEqualTo("");
    }

    // What a truncated read like head -30 sees
    private static string Head(string text) => string.Join("\n", Normalize(text).Split('\n').Take(HeadLines));

    [Test]
    [MethodDataSource(nameof(Commands))]
    public async Task HelpHeadHasTheExamplesAndEveryOption(string command)
    {
        var help = GetHelp(command);
        var head = Head(help);
        var (values, flags) = ArgumentParser.CommandOptions[command];
        var expected = values.Concat(flags).Select(static o => $"--{o}");

        // login's usage lines are its examples
        if (help.Contains("\nExamples:", StringComparison.Ordinal))
        {
            expected = expected.Prepend("\nExamples:");
        }

        var missing = expected.Where(s => !head.Contains(s, StringComparison.Ordinal)).ToList();

        await Assert.That(string.Join(",", missing)).IsEqualTo("");
    }

    [Test]
    public async Task OverviewHeadHasTheTypicalRunTargetsAndPatterns()
    {
        var head = Head(Help.Overview);

        await Assert.That(head).Contains("Typical run").And.Contains("Targets").And.Contains("<dir.vpk>:<pattern>");
    }

    private static string FirstLine(string text) => text.Split('\n')[0].TrimEnd('\r');

    [Test]
    public async Task NoArgumentsPrintTheOverview()
    {
        await Assert.That(Help.TryPrint([])).IsTrue();
        await Assert.That(TestContext.Current!.GetStandardOutput()).Contains(FirstLine(Help.Overview));
    }

    [Test]
    [Arguments("login")]
    [Arguments("--version")]
    [Arguments("get 730 1 -- --help")]
    [Arguments("get 730 1 -- server.dll")]
    [Arguments("workshop info 3070715607")]
    public async Task OtherCommandLinesAreNotHelp(string commandLine)
    {
        await Assert.That(Help.TryPrint(commandLine.Split(' '))).IsFalse();
        await Assert.That(TestContext.Current!.GetStandardOutput()).IsEmpty();
    }

    [Test]
    [Arguments("get", "get")]
    [Arguments("dd", "dd")]
    [Arguments("get --help", "get")]
    [Arguments("get 1 -h", "get")]
    [Arguments("ls 730 /? 1", "ls")]
    [Arguments("help get", "get")]
    [Arguments("--help track", "track")]
    [Arguments("help", "overview")]
    [Arguments("bogus --help", "overview")]
    [Arguments("workshop", "workshop")]
    [Arguments("workshop bogus --help", "workshop")]
    [Arguments("workshop info", "workshop info")]
    [Arguments("workshop info 3070715607 --help", "workshop info")]
    [Arguments("help workshop info", "workshop info")]
    [Arguments("workshop get 3070715607 -h -- a.vpk", "workshop get")]
    public async Task HelpRequestPrintsTheHelpOfItsCommand(string commandLine, string command)
    {
        var expected = command switch
        {
            "overview" => Help.Overview,
            "dd" => Help.DepotDownloader,
            "workshop" => Help.Workshop,
            _ => GetHelp(command),
        };

        await Assert.That(Help.TryPrint(commandLine.Split(' '))).IsTrue();

        var output = TestContext.Current!.GetStandardOutput();
        await Assert.That(output).Contains(FirstLine(expected));

        if (command != "overview")
        {
            await Assert.That(output).DoesNotContain(FirstLine(Help.Overview));
        }
    }

    [Test]
    public async Task EveryHelpTextLineFitsTheWidth()
    {
        var texts = new Dictionary<string, string>
        {
            ["Overview"] = Help.Overview,
            ["get"] = Help.Get,
            ["ls"] = Help.Ls,
            ["depots"] = Help.Depots,
            ["dd"] = Help.DepotDownloader,
            ["login"] = Help.Login,
            ["track"] = Help.Track,
            ["workshop"] = Help.Workshop,
            ["workshop info"] = Help.WorkshopInfo,
            ["workshop search"] = Help.WorkshopSearch,
            ["workshop ls"] = Help.WorkshopLs,
            ["workshop get"] = Help.WorkshopGet,
        };

        var tooLong = new List<string>();

        foreach (var (name, text) in texts)
        {
            foreach (var line in Normalize(text).Split('\n').Select(static l => l.TrimEnd('\r')))
            {
                if (line.Length > MaxLineLength)
                {
                    tooLong.Add($"{name}: {line.Length} {line}");
                }
            }
        }

        await Assert.That(string.Join("\n", tooLong)).IsEqualTo("");
    }

    [GeneratedRegex(@"(?<![\w-])--([a-z][a-z-]*)")]
    private static partial Regex OptionRegex();
}
