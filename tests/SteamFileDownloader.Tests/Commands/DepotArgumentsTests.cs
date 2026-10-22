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
    [Arguments("ls", new string[0])]
    [Arguments("ls", new[] { "--", "settings.txt" })]
    [Arguments("get", new string[0])]
    [Arguments("get", new[] { "--", "settings.txt" })]
    public async Task NoTargetsTakeEveryDepot(string command, string[] rest)
    {
        var result = DepotArgumentParser.ParseArguments(command, ["730", .. rest]);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Options.App).IsEqualTo(730u);
        await Assert.That(result.Targets.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("ls", new string[0], "ls needs an app id, the number in the app's store page URL, like: SFD ls <app>.")]
    [Arguments("ls", new[] { "*.dll" }, "ls needs an app id, the number in the app's store page URL, like: SFD ls <app> -- '*.dll'.")]
    [Arguments("get", new[] { "server.dll" }, "get needs an app id, the number in the app's store page URL, like: SFD get <app> -- server.dll.")]
    public async Task MissingAppSuggestsCommandsWithThePatternsGiven(string command, string[] patterns, string expected)
    {
        var message = DepotArgumentParser.DescribeMissingApp(command, [.. patterns]);

        await Assert.That(message.Replace(Help.ExeName, "SFD", StringComparison.Ordinal)).IsEqualTo(expected);
    }

    [Test]
    public async Task GetWithoutPatternsTellsHowPatternsPickFiles()
    {
        await Assert.That(DepotArgumentParser.ParseArguments("get", ["730", "2347771"])).IsNotNull();

        var output = TestContext.Current!.GetStandardOutput() + TestContext.Current.GetErrorOutput();

        await Assert.That(output.Replace(Help.ExeName, "SFD", StringComparison.Ordinal))
            .Contains("Downloading every file, since no patterns follow --. Patterns pick files, like: SFD get 730 2347771 -- '*.dll', and SFD ls 730 2347771 lists them.");
    }

    [Test]
    public async Task GetWithPatternsHasNoTip()
    {
        await Assert.That(DepotArgumentParser.ParseArguments("get", ["730", "--", "server.dll"])).IsNotNull();

        var output = TestContext.Current!.GetStandardOutput() + TestContext.Current.GetErrorOutput();

        await Assert.That(output).DoesNotContain("Downloading every file");
    }

    [Test]
    [Arguments("get", new[] { "730", "all" }, "\"all\" is not a target: without targets, get takes every depot of the app, like: SFD get 730.")]
    [Arguments("ls", new[] { "730", "2347771", "ALL:latest" }, "\"ALL:latest\" is not a target: without targets, ls takes every depot of the app, like: SFD ls 730.")]
    [Arguments("ls", new[] { "570/all@beta", "--", "x.dll" }, "\"570/all@beta\" is not a target: without targets, ls takes every depot of the app, like: SFD ls 570 -- x.dll.")]
    public async Task AllIsPointedAtLeavingOutTheTargets(string command, string[] args, string expected)
    {
        await Assert.That(DepotArgumentParser.ParseArguments(command, args)).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput().Replace(Help.ExeName, "SFD", StringComparison.Ordinal)).Contains(expected);
    }

    [Test]
    public async Task MisplacedPatternIsMovedAfterTheTargetsOfTheSameApp()
    {
        await Assert.That(DepotArgumentParser.ParseArguments("get", ["570", "373303", "hammer.dll"])).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains($"{Help.ExeName} get 570 373303 -- hammer.dll.");
    }

    [Test]
    [Arguments("get", new[] { "570/373303", "x.dll" }, "get 570/373303 -- x.dll")]
    [Arguments("ls", new[] { "x.dll" }, "ls <app> -- x.dll")]
    [Arguments("get", new[] { "x.dll" }, "get <app> -- x.dll")]
    public async Task MisplacedPatternWithoutAppKeepsTheTargetsApps(string command, string[] args, string expected)
    {
        await Assert.That(DepotArgumentParser.ParseArguments(command, args)).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains($"{Help.ExeName} {expected}.");
    }

    [Test]
    [Arguments("2347771:abc", "the manifest after the colon must be a number or latest")]
    [Arguments("2347771/8344780363095656278", "a manifest follows a colon, like 2347771:8344780363095656278")]
    [Arguments("server.dll", "patterns go after --")]
    [Arguments("2347771@a/b", "a branch follows a single @, like 2347771@beta")]
    [Arguments("1/2/3/4", "a slash only follows an app, like 570/373303 or 570/373303/<manifest>")]
    [Arguments("0", "app and depot ids are nonzero 32-bit numbers")]
    [Arguments("2347771:0", "the manifest must be a nonzero 64-bit number or latest")]
    [Arguments("garbage", "expected [<app>/]<depot>[:<manifest>|:latest][@<branch>], like 2347771:8344780363095656278 or 570/373303@beta")]
    public async Task InvalidTargetGetsOnlyTheClauseThatApplies(string arg, string expected)
    {
        await Assert.That(DepotArgumentParser.DescribeInvalidTarget(arg)).IsEqualTo(expected);
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

        var all = DepotArgumentParser.ParseArguments("get", ["730", "1", "--output", "out", "--branch", "beta", "--username", "me"]);

        await Assert.That(all).IsNotNull();
        await Assert.That(all!.Options).IsEqualTo(new DepotOptions(730, "out", "beta") { Login = new LoginOptions("me") });

        var anonymous = DepotArgumentParser.ParseArguments("get", ["730", "1", "--anonymous"]);

        await Assert.That(anonymous!.Options.Login.Anonymous).IsTrue();

        var install = DepotArgumentParser.ParseArguments("download", ["730", "1", "--dir", "D:/Games/CS2"]);

        await Assert.That(install!.Options.Install.Dir).IsEqualTo("D:/Games/CS2");
        await Assert.That(install.Options.Output).IsNull();
    }

    [Test]
    public async Task InstallOptionsAreMappedAndDefaulted()
    {
        var defaults = DepotArgumentParser.ParseArguments("download", ["730"]);

        await Assert.That(defaults).IsNotNull();
        await Assert.That(defaults!.Command).IsEqualTo("download");
        await Assert.That(defaults.Options).IsEqualTo(new DepotOptions(730));

        var all = DepotArgumentParser.ParseArguments("download", ["730", "1", "2:5", "--os", "Linux", "--osarch", "32", "--language", "French", "--lowviolence", "--branch", "beta"]);

        await Assert.That(all).IsNotNull();
        await Assert.That(string.Join(",", all!.Targets)).IsEqualTo("1,2:5");
        await Assert.That(all.Options).IsEqualTo(new DepotOptions(730, Branch: "beta") { Install = new InstallOptions(Os: "linux", OsArch: "32", Language: "french", LowViolence: true) });

        var everything = DepotArgumentParser.ParseArguments("download", ["730", "--all-platforms", "--all-languages"]);

        await Assert.That(everything!.Options.Install).IsEqualTo(new InstallOptions(AllPlatforms: true, AllLanguages: true));

        var steamDeck = DepotArgumentParser.ParseArguments("download", ["730", "--steamdeck"]);

        await Assert.That(steamDeck!.Options.Install).IsEqualTo(new InstallOptions(SteamDeck: true));
    }

    [Test]
    public async Task AllPlatformsCannotBeCombinedWithSteamDeck()
    {
        await Assert.That(DepotArgumentParser.ParseArguments("download", ["730", "--all-platforms", "--steamdeck"])).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains("--all-platforms can't be combined with --steamdeck.");
    }

    [Test]
    [Arguments("--os", "beos", "--os needs windows, linux or macos, got \"beos\".")]
    [Arguments("--osarch", "16", "--osarch needs 32 or 64, got \"16\".")]
    public async Task InvalidPlatformValueIsRejected(string option, string value, string expected)
    {
        await Assert.That(DepotArgumentParser.ParseArguments("download", ["730", option, value])).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains(expected);
    }

    [Test]
    [Arguments("--all-platforms", "--os", "linux", "--all-platforms can't be combined with --os or --osarch.")]
    [Arguments("--all-platforms", "--osarch", "64", "--all-platforms can't be combined with --os or --osarch.")]
    [Arguments("--all-languages", "--language", "french", "--all-languages can't be combined with --language.")]
    public async Task ConflictingInstallOptionsAreRejected(string flag, string option, string value, string expected)
    {
        await Assert.That(DepotArgumentParser.ParseArguments("download", ["730", flag, option, value])).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains(expected);
    }

    [Test]
    public async Task OsCpuIsParsedAndLowercased()
    {
        var parsed = DepotArgumentParser.ParseArguments("download", ["730", "--oscpu", "ARM64"]);

        await Assert.That(parsed!.Options.Install).IsEqualTo(new InstallOptions(OsCpu: "arm64"));
    }

    [Test]
    public async Task InvalidOsCpuIsRejected()
    {
        await Assert.That(DepotArgumentParser.ParseArguments("download", ["730", "--oscpu", "mips"])).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains("--oscpu needs x86 or arm64, got \"mips\".");
    }

    [Test]
    public async Task AllPlatformsCannotBeCombinedWithOsCpu()
    {
        await Assert.That(DepotArgumentParser.ParseArguments("download", ["730", "--all-platforms", "--oscpu", "x86"])).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains("--all-platforms can't be combined with --oscpu.");
    }

    [Test]
    public async Task IncludeAndExcludeCanBeRepeated()
    {
        var parsed = DepotArgumentParser.ParseArguments("download", ["730", "--include", "91", "--exclude", "5", "--include=92,93", "--include", "94"]);
        var install = parsed!.Options.Install;

        await Assert.That(install.Include).IsEquivalentTo([91u, 92u, 93u, 94u]);
        await Assert.That(install.Exclude).IsEquivalentTo([5u]);
        await Assert.That(parsed.Targets).IsEmpty();
    }

    [Test]
    [Arguments("--include", "abc", "--include needs a depot id or DLC app id, got \"abc\".")]
    [Arguments("--exclude", "0", "--exclude needs a depot id or DLC app id, got \"0\".")]
    [Arguments("--include", "1,x", "--include needs a depot id or DLC app id, got \"x\".")]
    public async Task InvalidIncludeAndExcludeIdsAreRejected(string option, string value, string expected)
    {
        await Assert.That(DepotArgumentParser.ParseArguments("download", ["730", option, value])).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains(expected);
    }

    [Test]
    [Arguments("--include")]
    [Arguments("--exclude")]
    public async Task IncludeAndExcludeCannotBeCombinedWithDepotTargets(string option)
    {
        await Assert.That(DepotArgumentParser.ParseArguments("download", ["730", "1", option, "2"])).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains("can't be combined with depots as targets");
    }

    [Test]
    public async Task IncludeIsOnlyForDownload()
    {
        await Assert.That(DepotArgumentParser.ParseArguments("get", ["730", "1", "--include", "2"])).IsNull();
    }

    [Test]
    public async Task DownloadNeedsAnAppFirst()
    {
        await Assert.That(DepotArgumentParser.ParseArguments("download", [])).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput().Replace(Help.ExeName, "SFD", StringComparison.Ordinal)).Contains("download needs an app id, the number in the app's store page URL, like: SFD download <app>.");
        await Assert.That(DepotArgumentParser.ParseArguments("download", ["1"])).IsNotNull();
    }

    [Test]
    [Arguments("570/373303")]
    [Arguments("373303@beta")]
    public async Task DownloadTargetsAreDepotsOfItsApp(string target)
    {
        await Assert.That(DepotArgumentParser.ParseArguments("download", ["730", "1", target])).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains("names an app or branch.");
    }

    [Test]
    public async Task DownloadHasNoPatterns()
    {
        await Assert.That(DepotArgumentParser.ParseArguments("download", ["730", "--", "server.dll"])).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains("download takes no patterns after --.");
    }

    [Test]
    public async Task DownloadWithoutPatternsHasNoEveryFileTip()
    {
        await Assert.That(DepotArgumentParser.ParseArguments("download", ["730", "1"])).IsNotNull();

        var output = TestContext.Current!.GetStandardOutput() + TestContext.Current.GetErrorOutput();

        await Assert.That(output).DoesNotContain("Downloading every file");
    }

    [Test]
    public async Task GetHasNoInstallOptions()
    {
        await Assert.That(DepotArgumentParser.ParseArguments("get", ["730", "1", "--install-dir", "D:/Games/CS2"])).IsNull();
        await Assert.That(DepotArgumentParser.ParseArguments("get", ["730", "1", "--dir", "D:/Games/CS2"])).IsNull();
        await Assert.That(DepotArgumentParser.ParseArguments("get", ["730", "1", "--os", "linux"])).IsNull();
    }

    [Test]
    [Arguments("get", "--json")]
    [Arguments("get", "--all-files")]
    [Arguments("ls", "--all-files")]
    [Arguments("ls", "--json")]
    public async Task OptionsOfOtherCommandsAreRejected(string command, string option)
    {
        await Assert.That(DepotArgumentParser.ParseArguments(command, ["730", "1", option])).IsNull();
    }

    [Test]
    [Arguments("get 730 1 -- a", true)]
    [Arguments("get 730 1", true)]
    [Arguments("get 730 1 --anonymous", true)]
    [Arguments("get 730 -- a", true)]
    [Arguments("get 730", true)]
    [Arguments("get 730 all", false)]
    [Arguments("get --anonymous", false)]
    [Arguments("ls 730 1", true)]
    [Arguments("ls 730 1 -- a", true)]
    [Arguments("ls 730", true)]
    [Arguments("ls 730 -- a", true)]
    [Arguments("ls --anonymous", false)]
    [Arguments("depots 730", true)]
    public async Task GetAndLsNeedOnlyAnApp(string commandLine, bool accepted)
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
    [Arguments("all")]
    [Arguments("570/all@beta")]
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
    public async Task NoAppAndNoTargetsGivesAnExample()
    {
        await Assert.That(DepotArgumentParser.ParseArguments("ls", [])).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains("ls needs an app id, ").And.Contains($"{Help.ExeName} ls <app>.");
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
