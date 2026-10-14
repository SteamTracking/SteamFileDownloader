namespace SteamFileDownloader.Tests;

public class DepotArgumentsTests
{
    private static DepotArguments Arguments(uint? app, string branch = "public") =>
        new("get", new DepotOptions(app, Branch: branch), [], []);

    [Test]
    public async Task AppIdThenTargetsThenPatterns()
    {
        var result = DepotArgumentParser.ParseArguments("get", ["730", "2347771", "2347773:latest", "--", "server.dll", "a/b.dll"]);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Command).IsEqualTo("get");
        await Assert.That(result.Options.App).IsEqualTo(730u);
        await Assert.That(string.Join(",", result.Targets)).IsEqualTo("2347771,2347773:latest");
        await Assert.That(string.Join(",", result.Patterns)).IsEqualTo("server.dll,a/b.dll");
    }

    [Test]
    public async Task TargetWithAppNeedsNoAppId()
    {
        var result = DepotArgumentParser.ParseArguments("get", ["730/2347771", "--", "server.dll"]);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Options.App).IsNull();
        await Assert.That(string.Join(",", result.Targets)).IsEqualTo("730/2347771");
    }

    [Test]
    public async Task AppWithoutTargetsPointsAtTheDepotsCommand()
    {
        await Assert.That(DepotArgumentParser.ParseArguments("ls", ["2347771"])).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains("ls 2347771 needs targets after the app id").And.Contains("put its app first");
    }

    [Test]
    public async Task AppIdNoTargetUsesIsRejected()
    {
        await Assert.That(DepotArgumentParser.ParseArguments("get", ["2347771", "730/2347771", "--", "a"])).IsNull();
        await Assert.That(DepotArgumentParser.ParseArguments("ls", ["2347771", "730/2347771", "570/373303@beta"])).IsNull();
    }

    [Test]
    public async Task AppIdAloneIsAccepted()
    {
        var result = DepotArgumentParser.ParseArguments("depots", ["730"]);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Options.App).IsEqualTo(730u);
        await Assert.That(result.Targets.Count).IsEqualTo(0);
    }

    [Test]
    public async Task MixedTargetsKeepAppId()
    {
        var result = DepotArgumentParser.ParseArguments("ls", ["730", "2347771", "570/373303"]);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Options.App).IsEqualTo(730u);
    }

    [Test]
    public async Task DepotsNeedsAppIds()
    {
        await Assert.That(DepotArgumentParser.ParseArguments("depots", [])).IsNull();
        await Assert.That(DepotArgumentParser.ParseArguments("depots", ["--json"])).IsNull();
        await Assert.That(DepotArgumentParser.ParseArguments("depots", ["730", "570"])).IsNotNull();
    }

    [Test]
    public async Task DepotsHasNoJsonOption()
    {
        await Assert.That(DepotArgumentParser.ParseArguments("depots", ["730", "--json"])).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains("Unknown option \"--json\"");
    }

    [Test]
    public async Task OptionAfterDoubleDashIsRejected()
    {
        await Assert.That(DepotArgumentParser.ParseArguments("get", ["730", "1", "--", "a", "--output"])).IsNull();
    }

    [Test]
    public async Task OptionsAreMappedAndDefaulted()
    {
        var defaults = DepotArgumentParser.ParseArguments("get", ["730", "1", "--", "a"]);

        await Assert.That(defaults).IsNotNull();
        await Assert.That(defaults!.Options).IsEqualTo(new DepotOptions(730));

        var all = DepotArgumentParser.ParseArguments("get", ["730", "1", "--output", "out", "--branch", "beta", "--username", "me", "--all-files"]);

        await Assert.That(all).IsNotNull();
        await Assert.That(all!.Options).IsEqualTo(new DepotOptions(730, "out", "beta", false, "me", true));

        var anonymous = DepotArgumentParser.ParseArguments("get", ["730", "1", "--anonymous", "--all-files"]);

        await Assert.That(anonymous!.Options.Anonymous).IsTrue();
    }

    [Test]
    [Arguments("get", "--json")]
    [Arguments("ls", "--all-files")]
    [Arguments("ls", "--json")]
    public async Task OptionsOfOtherCommandsAreRejected(string command, string option)
    {
        await Assert.That(DepotArgumentParser.ParseArguments(command, ["730", "1", option])).IsNull();
    }

    [Test]
    [Arguments("get 730 1 -- a", true)]
    [Arguments("get 730 1 --all-files", true)]
    [Arguments("get 730 1", false)]
    [Arguments("get 730 1 --anonymous", false)]
    [Arguments("get 730 1 --all-files -- a", false)]
    [Arguments("get 730 -- a", false)]
    [Arguments("get 730 --all-files", false)]
    [Arguments("get --all-files", false)]
    [Arguments("ls 730 1", true)]
    [Arguments("ls 730 1 -- a", true)]
    [Arguments("ls 730", false)]
    [Arguments("ls 730 -- a", false)]
    [Arguments("ls --anonymous", false)]
    [Arguments("depots 730", true)]
    public async Task GetNeedsPatternsOrAllFilesAndGetAndLsNeedTargets(string commandLine, bool accepted)
    {
        var args = commandLine.Split(' ');

        await Assert.That(DepotArgumentParser.ParseArguments(args[0], args[1..]) != null).IsEqualTo(accepted);
    }

    [Test]
    [Arguments("2347771", 730u, 2347771u, null, "public")]
    [Arguments("2347771:8344780363095656278", 730u, 2347771u, 8344780363095656278UL, "public")]
    [Arguments("2347771:latest", 730u, 2347771u, null, "public")]
    [Arguments("2347771:LATEST", 730u, 2347771u, null, "public")]
    [Arguments("730/2347771", 730u, 2347771u, null, "public")]
    [Arguments("570/373303/8344780363095656278", 570u, 373303u, 8344780363095656278UL, "public")]
    [Arguments("570/373303:123", 570u, 373303u, 123UL, "public")]
    [Arguments("570/373303/latest", 570u, 373303u, null, "public")]
    [Arguments("2347771@beta", 730u, 2347771u, null, "beta")]
    [Arguments("2347771:123@beta", 730u, 2347771u, 123UL, "beta")]
    [Arguments("570/373303@beta", 570u, 373303u, null, "beta")]
    [Arguments("570/373303/123@beta", 570u, 373303u, 123UL, "beta")]
    public async Task ParsesDepotTargets(string arg, uint app, uint depot, ulong? manifest, string branch)
    {
        var target = DepotArgumentParser.ParseTarget(arg, Arguments(730));

        await Assert.That(target).IsNotNull();
        await Assert.That(target!.Source).IsEqualTo(arg);
        await Assert.That(target.AppID).IsEqualTo(app);
        await Assert.That(target.DepotID).IsEqualTo(depot);
        await Assert.That(target.ManifestID).IsEqualTo(manifest);
        await Assert.That(target.Branch).IsEqualTo(branch);
    }

    [Test]
    [Arguments("all", 730u, "public")]
    [Arguments("ALL", 730u, "public")]
    [Arguments("All", 730u, "public")]
    [Arguments("all:latest", 730u, "public")]
    [Arguments("all@beta", 730u, "beta")]
    [Arguments("570/all", 570u, "public")]
    [Arguments("570/all@beta", 570u, "beta")]
    public async Task ParsesAllTargets(string arg, uint app, string branch)
    {
        var target = DepotArgumentParser.ParseTarget(arg, Arguments(730));

        await Assert.That(target).IsNotNull();
        await Assert.That(target!.AppID).IsEqualTo(app);
        await Assert.That(target.DepotID).IsNull();
        await Assert.That(target.ManifestID).IsNull();
        await Assert.That(target.Branch).IsEqualTo(branch);
    }

    [Test]
    [Arguments("all:123")]
    [Arguments("570/all:123")]
    [Arguments("570/all/123")]
    [Arguments("garbage")]
    [Arguments("")]
    [Arguments("/2347771")]
    [Arguments("2347771:")]
    [Arguments("2347771@")]
    [Arguments("2347771@a/b")]
    [Arguments("2347771@a@b")]
    [Arguments("2347771:abc")]
    [Arguments("-5")]
    [Arguments("1/2/3/4")]
    [Arguments("99999999999")]
    [Arguments("1/99999999999")]
    [Arguments("2347771/8344780363095656278")]
    [Arguments("2347771:99999999999999999999")]
    [Arguments("server.dll")]
    [Arguments("0")]
    [Arguments("570/0")]
    [Arguments("2347771:0")]
    [Arguments("570/373303/0")]
    [Arguments("570/373303:0@beta")]
    public async Task RejectsInvalidTargets(string arg)
    {
        await Assert.That(DepotArgumentParser.ParseTarget(arg, Arguments(730))).IsNull();
    }

    [Test]
    public async Task TargetWithoutAppNeedsAppId()
    {
        await Assert.That(DepotArgumentParser.ParseTarget("2347771", Arguments(null))).IsNull();
        await Assert.That(DepotArgumentParser.ParseTarget("all", Arguments(null))).IsNull();
        await Assert.That(DepotArgumentParser.ParseTarget("2347771:123", Arguments(null))).IsNull();
    }

    [Test]
    public async Task ParsedTargetWithAppNeedsNoAppId()
    {
        var target = DepotArgumentParser.ParseTarget("730/2347771", Arguments(null));

        await Assert.That(target).IsNotNull();
        await Assert.That(target!.AppID).IsEqualTo(730u);
    }

    [Test]
    public async Task TargetAppOverridesGivenApp()
    {
        var target = DepotArgumentParser.ParseTarget("570/373303", Arguments(730));

        await Assert.That(target).IsNotNull();
        await Assert.That(target!.AppID).IsEqualTo(570u);
    }

    [Test]
    public async Task BranchDefaultsToOptionsBranchAndTargetBranchWins()
    {
        var inherited = DepotArgumentParser.ParseTarget("2347771", Arguments(730, "dev"));
        var explicitBranch = DepotArgumentParser.ParseTarget("2347771@beta", Arguments(730, "dev"));

        await Assert.That(inherited!.Branch).IsEqualTo("dev");
        await Assert.That(explicitBranch!.Branch).IsEqualTo("beta");
    }

    [Test]
    public async Task TargetsAreRecordEqual()
    {
        var target = DepotArgumentParser.ParseTarget("2347771:5", Arguments(730));

        await Assert.That(target).IsEqualTo(new Target("2347771:5", 730, 2347771, 5, "public"));
    }

    [Test]
    public async Task MissingTargetsAreReportedBeforeMissingPatterns()
    {
        await Assert.That(DepotArgumentParser.ParseArguments("get", ["730"])).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains("get 730 needs targets after the app id");
    }

    [Test]
    public async Task NoAppAndNoTargetsGivesAnExample()
    {
        await Assert.That(DepotArgumentParser.ParseArguments("ls", [])).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains("ls needs an app id and targets, like: ").And.Contains("ls 730 2347771.");
    }

    [Test]
    public async Task PatternsWithoutDoubleDashArePointedOut()
    {
        await Assert.That(DepotArgumentParser.ParseArguments("get", ["730", "2347771", "server.dll"])).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains("\"server.dll\" is not a target; patterns go after --");
    }

    [Test]
    public async Task AppIdNoTargetUsesNamesTheTarget()
    {
        await Assert.That(DepotArgumentParser.ParseArguments("ls", ["2347771", "730/2347773/55"])).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains("\"730/2347773/55\" reads as app 730 and depot 2347773, so the app id 2347771 is unused");
    }

    [Test]
    public async Task DepotsNamesTheNonNumericAppId()
    {
        await Assert.That(DepotArgumentParser.ParseArguments("depots", ["cs2"])).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains("\"cs2\" is not an app id");
    }

    [Test]
    public async Task DepotsRejectsPatterns()
    {
        await Assert.That(DepotArgumentParser.ParseArguments("depots", ["730", "--", "server.dll"])).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains("depots takes no patterns after --");
    }
}
