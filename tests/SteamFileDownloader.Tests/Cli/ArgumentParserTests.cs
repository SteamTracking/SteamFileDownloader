using TUnit.Core.Enums;

namespace SteamFileDownloader.Tests;

public class ArgumentParserTests
{
    [Test]
    public async Task SplitsOptionsPositionalsAndPatternsAtDoubleDash()
    {
        var parsed = ArgumentParser.Parse("get", ["730", "2347771", "--output", "out/dir", "--", "server.dll", "--output"]);

        await Assert.That(parsed).IsNotNull();
        await Assert.That(string.Join(",", parsed!.Positionals)).IsEqualTo("730,2347771");
        await Assert.That(string.Join(",", parsed.Patterns)).IsEqualTo("server.dll,--output");
        await Assert.That(parsed.Options.Count).IsEqualTo(1);
        await Assert.That(parsed.Options["output"]).IsEqualTo("out/dir");
    }

    [Test]
    public async Task OnlyTheFirstDoubleDashSplits()
    {
        var (options, patterns) = ArgumentParser.SplitPatterns(["a", "--", "b", "--", "c"]);

        await Assert.That(string.Join(",", options)).IsEqualTo("a");
        await Assert.That(string.Join(",", patterns)).IsEqualTo("b,--,c");
    }

    [Test]
    public async Task OptionsCanComeBetweenPositionals()
    {
        var parsed = ArgumentParser.Parse("ls", ["--branch", "beta", "730", "--anonymous", "2347771"]);

        await Assert.That(parsed).IsNotNull();
        await Assert.That(string.Join(",", parsed!.Positionals)).IsEqualTo("730,2347771");
        await Assert.That(parsed.Options["branch"]).IsEqualTo("beta");
        await Assert.That(parsed.Options["anonymous"]).IsEqualTo("");
    }

    [Test]
    public async Task OptionNamesIgnoreCase()
    {
        var parsed = ArgumentParser.Parse("get", ["--OUTPUT", "out", "--Anonymous"]);

        await Assert.That(parsed).IsNotNull();
        await Assert.That(parsed!.Options.ContainsKey("output")).IsTrue();
        await Assert.That(parsed.Options.ContainsKey("anonymous")).IsTrue();
    }

    [Test]
    [Arguments("get", "--bogus")]
    [Arguments("ls", "--all-files")]
    [Arguments("depots", "--output")]
    [Arguments("login", "--anonymous")]
    [Arguments("track", "--json")]
    [Arguments("depots", "--json")]
    public async Task UnknownOptionForCommandIsRejected(string command, string option)
    {
        await Assert.That(ArgumentParser.Parse(command, [option, "x"])).IsNull();
    }

    [Test]
    public async Task UnknownOptionOfACommandWithoutOptionsSaysSo()
    {
        await Assert.That(ArgumentParser.Parse("login list", ["--username", "x"])).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains("login list takes no options.");
    }

    [Test]
    public async Task ValueOptionWithoutValueIsRejected()
    {
        await Assert.That(ArgumentParser.Parse("get", ["730", "--output"])).IsNull();
    }

    [Test]
    [Arguments("get", "--output", "--anonymous")]
    [Arguments("get", "--branch", "--output")]
    [Arguments("track", "--username", "--save-manifest")]
    public async Task ValueOptionFollowedByAnotherOptionIsRejected(string command, string option, string next)
    {
        await Assert.That(ArgumentParser.Parse(command, ["730", option, next, "1"])).IsNull();
    }

    [Test]
    public async Task ValueStartingWithOneDashIsAccepted()
    {
        await Assert.That(ArgumentParser.Parse("get", ["--branch", "-x"])?.Options["branch"]).IsEqualTo("-x");
    }

    [Test]
    [Arguments("")]
    [Arguments("  ")]
    public async Task EmptyOutputPathIsRejected(string path)
    {
        await Assert.That(ArgumentParser.CheckOutputPath(path, "--output")).IsFalse();
        await Assert.That(ArgumentParser.Parse("get", ["--output", path])).IsNull();
        await Assert.That(TrackCommand.ParseArguments(["--appid", "1", "--output", path])).IsNull();
    }

    [Test]
    public async Task RepeatedValueOptionIsRejected()
    {
        await Assert.That(ArgumentParser.Parse("get", ["--branch", "a", "--branch", "b"])).IsNull();
        await Assert.That(ArgumentParser.Parse("get", ["--branch", "a", "--BRANCH", "b"])).IsNull();
    }

    [Test]
    public async Task RepeatedFlagIsAccepted()
    {
        await Assert.That(ArgumentParser.Parse("get", ["--anonymous", "--anonymous"])).IsNotNull();
    }

    [Test]
    [Arguments("C:foo", false)]
    [Arguments("c:", false)]
    [Arguments("\\foo", false)]
    [Arguments("D:/SteamDepots", true)]
    [Arguments("D:\\SteamDepots", true)]
    [Arguments("out/dir", true)]
    [Arguments("out\\dir", true)]
    [Arguments(".", true)]
    [Arguments("../out", true)]
    [RunOn(OS.Windows)]
    public async Task OutputPathIsChecked(string path, bool accepted)
    {
        await Assert.That(ArgumentParser.CheckOutputPath(path, "--output")).IsEqualTo(accepted);
        await Assert.That(ArgumentParser.Parse("get", ["--output", path]) != null).IsEqualTo(accepted);
    }

    [Test]
    public async Task SingleDashOptionIsRejected()
    {
        await Assert.That(ArgumentParser.Parse("get", ["730", "-o", "out"])).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains("Unknown option \"-o\"; options start with --");
    }

    [Test]
    public async Task ValueCanFollowAnEqualsSign()
    {
        var parsed = ArgumentParser.Parse("get", ["730", "--OUTPUT=out/dir", "--branch=--beta", "--username=a=b", "1"]);

        await Assert.That(parsed).IsNotNull();
        await Assert.That(parsed!.Options["output"]).IsEqualTo("out/dir");
        await Assert.That(parsed.Options["branch"]).IsEqualTo("--beta");
        await Assert.That(parsed.Options["username"]).IsEqualTo("a=b");
        await Assert.That(string.Join(",", parsed.Positionals)).IsEqualTo("730,1");
    }

    [Test]
    public async Task EmptyValueAfterAnEqualsSignIsRejected()
    {
        await Assert.That(ArgumentParser.Parse("get", ["--branch=", "1"])).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains("--branch needs a value.");
    }

    [Test]
    public async Task ValueGivenBothWaysIsGivenMoreThanOnce()
    {
        await Assert.That(ArgumentParser.Parse("get", ["--branch=a", "--branch", "b"])).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains("--branch is given more than once.");
    }

    [Test]
    public async Task FlagWithAValueIsRejected()
    {
        await Assert.That(ArgumentParser.Parse("get", ["--anonymous=true"])).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains("--anonymous is a flag and takes no value, got \"--anonymous=true\".");
    }

    [Test]
    [Arguments("--ouptut", "--output")]
    [Arguments("--outpt=x", "--output")]
    [Arguments("--BRANH", "--branch")]
    [Arguments("--anon", "--anonymous")]
    public async Task MistypedOptionGetsAHint(string option, string expected)
    {
        await Assert.That(ArgumentParser.Parse("get", [option, "x"])).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains($"Unknown option \"{option}\". Did you mean {expected}? Options are");
    }

    [Test]
    public async Task UnrelatedOptionGetsNoHint()
    {
        await Assert.That(ArgumentParser.Parse("get", ["--json", "x"])).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains("Unknown option \"--json\". Options are");
    }

    [Test]
    [Arguments("gte", "get")]
    [Arguments("dpeots", "depots")]
    [Arguments("worksop", "workshop")]
    [Arguments("lx", "ls")]
    [Arguments("TRACK", "track")]
    [Arguments("w", "workshop")]
    [Arguments("lo", "login")]
    [Arguments("d", null)]
    [Arguments("xy", null)]
    [Arguments("get", "get")]
    [Arguments("dl", "dd")]
    [Arguments("de", "depots")]
    [Arguments("download", null)]
    public async Task SuggestsTheOneCloseCandidate(string input, string? expected)
    {
        string[] commands = ["get", "ls", "depots", "workshop", "dd", "login", "config", "track"];

        await Assert.That(ArgumentParser.Suggest(input, commands)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("", "", 0)]
    [Arguments("abc", "", 3)]
    [Arguments("abc", "abc", 0)]
    [Arguments("abc", "acb", 1)]
    [Arguments("kitten", "sitting", 3)]
    [Arguments("output", "ouptut", 1)]
    public async Task EditDistanceCountsSwapsAsOneEdit(string a, string b, int expected)
    {
        await Assert.That(ArgumentParser.EditDistance(a, b)).IsEqualTo(expected);
    }

    [Test]
    public async Task AnonymousCannotBeCombinedWithUsername()
    {
        await Assert.That(ArgumentParser.Parse("get", ["--anonymous", "--username", "me"])).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains("--anonymous and --username can't be combined.");
    }
}
