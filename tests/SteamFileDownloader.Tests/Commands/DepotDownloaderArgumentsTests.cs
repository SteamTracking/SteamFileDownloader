using TUnit.Core.Enums;

namespace SteamFileDownloader.Tests;

public class DepotDownloaderArgumentsTests
{
    [Test]
    public async Task MinimalCommandDownloadsEveryDepotAndFile()
    {
        var result = DepotDownloaderArguments.Parse(["-app", "730"]);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Command).IsEqualTo("get");
        await Assert.That(result.Options.App).IsEqualTo(730u);
        await Assert.That(result.Targets.Count).IsEqualTo(0);
        await Assert.That(result.Patterns.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DepotsAndManifestsPairUpInOrder()
    {
        var result = DepotDownloaderArguments.Parse(["-app", "730", "-depot", "1", "-manifest", "11", "-depot", "2", "-depot", "3", "-manifest", "33"]);

        await Assert.That(result).IsNotNull();
        await Assert.That(string.Join(",", result!.Targets)).IsEqualTo("1:11,2:33,3");
    }

    [Test]
    public async Task OptionNamesIgnoreCase()
    {
        var result = DepotDownloaderArguments.Parse(["-APP", "730", "-Depot", "1"]);

        await Assert.That(result).IsNotNull();
        await Assert.That(string.Join(",", result!.Targets)).IsEqualTo("1");
    }

    [Test]
    public async Task MoreManifestsThanDepotsIsAnError()
    {
        await Assert.That(DepotDownloaderArguments.Parse(["-app", "730", "-manifest", "5"])).IsNull();
        await Assert.That(DepotDownloaderArguments.Parse(["-app", "730", "-depot", "1", "-manifest", "5", "-manifest", "6"])).IsNull();
    }

    [Test]
    [Arguments(false, "workshop get")]
    [Arguments(true, "workshop ls")]
    public async Task PubfileRunsAsWorkshopCommandWithoutAnApp(bool manifestOnly, string command)
    {
        var result = DepotDownloaderArguments.Parse(["-pubfile", "3070715607", .. manifestOnly ? new[] { "-manifest-only" } : []]);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Command).IsEqualTo(command);
        await Assert.That(result.Targets).IsEquivalentTo(["3070715607"]);
    }

    [Test]
    public async Task UgcIsAManifestOfTheDepotWithTheAppsId()
    {
        var result = DepotDownloaderArguments.Parse(["-app", "730", "-ugc", "8344780363095656278"]);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Command).IsEqualTo("get");
        await Assert.That(result.Targets).IsEquivalentTo(["730:8344780363095656278"]);
    }

    [Test]
    public async Task UgcHintsAtPubfileForMissingManifests()
    {
        var result = DepotDownloaderArguments.Parse(["-app", "730", "-ugc", "8344780363095656278"]);

        await Assert.That(result!.Options.ManifestNotFoundHint).IsEqualTo("If 8344780363095656278 is a workshop item id, use -pubfile instead of -ugc.");
        await Assert.That(result.Options.ItemNotFoundHint).IsNull();
    }

    [Test]
    public async Task PubfileHintsAtUgcForMissingItems()
    {
        var result = DepotDownloaderArguments.Parse(["-pubfile", "8344780363095656278"]);

        await Assert.That(result!.Options.ItemNotFoundHint).IsEqualTo("If 8344780363095656278 is a UGC id, use -ugc instead of -pubfile.");
        await Assert.That(result.Options.ManifestNotFoundHint).IsNull();
    }

    [Test]
    public async Task DepotsGetNoIdHints()
    {
        var result = DepotDownloaderArguments.Parse(["-app", "730", "-depot", "731"]);

        await Assert.That(result!.Options.ItemNotFoundHint).IsNull();
        await Assert.That(result.Options.ManifestNotFoundHint).IsNull();
    }

    [Test]
    [Arguments("-pubfile 3070715607 -depot 730")]
    [Arguments("-app 730 -ugc 5 -depot 730 -manifest 5")]
    [Arguments("-pubfile 3070715607 -app 730 -ugc 5")]
    [Arguments("-ugc 5")]
    [Arguments("-pubfile item")]
    [Arguments("-pubfile 0")]
    public async Task WorkshopOptionsThatConflictAreErrors(string commandLine)
    {
        await Assert.That(DepotDownloaderArguments.Parse(commandLine.Split(' '))).IsNull();
    }

    [Test]
    public async Task MissingAppIsAnError()
    {
        await Assert.That(DepotDownloaderArguments.Parse(["-depot", "1"])).IsNull();
        await Assert.That(DepotDownloaderArguments.Parse([])).IsNull();
    }

    [Test]
    [Arguments("-app")]
    [Arguments("-depot")]
    [Arguments("-manifest")]
    [Arguments("-filelist")]
    [Arguments("-dir")]
    [Arguments("-branch")]
    [Arguments("-beta")]
    [Arguments("-branchpassword")]
    [Arguments("-betapassword")]
    public async Task OptionWithoutValueIsAnError(string option)
    {
        await Assert.That(DepotDownloaderArguments.Parse(["-app", "730", option])).IsNull();
        await Assert.That(DepotDownloaderArguments.Parse(["-app", "730", option, "-manifest-only"])).IsNull();
    }

    [Test]
    [Arguments("-app", "abc")]
    [Arguments("-app", "-5")]
    [Arguments("-depot", "x")]
    [Arguments("-manifest", "x")]
    [Arguments("-app", "99999999999")]
    [Arguments("-depot", "99999999999")]
    public async Task NonNumericIdsAreAnError(string option, string value)
    {
        var args = option == "-app" ? new[] { option, value } : ["-app", "730", option, value];

        await Assert.That(DepotDownloaderArguments.Parse(args)).IsNull();
    }

    [Test]
    public async Task ManifestOnlyRunsLs()
    {
        var result = DepotDownloaderArguments.Parse(["-app", "730", "-depot", "1", "-manifest-only"]);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Command).IsEqualTo("ls");
    }

    [Test]
    public async Task BranchAndBetaSetTheBranch()
    {
        await Assert.That(DepotDownloaderArguments.Parse(["-app", "730", "-branch", "dev"])!.Options.Branch).IsEqualTo("dev");
        await Assert.That(DepotDownloaderArguments.Parse(["-app", "730", "-beta", "dev"])!.Options.Branch).IsEqualTo("dev");
        await Assert.That(DepotDownloaderArguments.Parse(["-app", "730"])!.Options.Branch).IsEqualTo("public");
    }

    [Test]
    [Arguments("-branchpassword")]
    [Arguments("-betapassword")]
    public async Task BranchPasswordsAreKept(string option)
    {
        var result = DepotDownloaderArguments.Parse(["-app", "730", "-branch", "dev", option, "secret"]);

        await Assert.That(result!.Options.BranchPassword).IsEqualTo("secret");
        await Assert.That(DepotDownloaderArguments.Parse(["-app", "730"])!.Options.BranchPassword).IsNull();
    }

    [Test]
    public async Task UsernameIsKept()
    {
        await Assert.That(DepotDownloaderArguments.Parse(["-app", "730", "-username", "me"])!.Options.Login.Username).IsEqualTo("me");
        await Assert.That(DepotDownloaderArguments.Parse(["-app", "730", "-user", "me"])!.Options.Login.Username).IsEqualTo("me");
    }

    [Test]
    public async Task PasswordIsKeptWithAUsername()
    {
        await Assert.That(DepotDownloaderArguments.Parse(["-app", "730", "-username", "me", "-password", "x"])!.Options.Login).IsEqualTo(new LoginOptions("me", "x"));
        await Assert.That(DepotDownloaderArguments.Parse(["-app", "730", "-password", "x"])).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains("-password needs -username.");
    }

    [Test]
    [Arguments("-remember-password")]
    [Arguments("-loginid")]
    [Arguments("-max-downloads")]
    [Arguments("-os")]
    [Arguments("-language")]
    [Arguments("-all-platforms")]
    public async Task OtherDepotDownloaderOptionsAreUnsupported(string option)
    {
        await Assert.That(DepotDownloaderArguments.Parse(["-app", "730", option])).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains($"Unsupported option {option}.");
    }

    [Test]
    public async Task UnsupportedOptionWithAValueIsAnError()
    {
        await Assert.That(DepotDownloaderArguments.Parse(["-app", "730", "-max-downloads", "8"])).IsNull();
    }

    [Test]
    public async Task MistypedOptionSuggestsASupportedOne()
    {
        await Assert.That(DepotDownloaderArguments.Parse(["-app", "730", "-mainfest", "5"])).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains("Unsupported option -mainfest. Did you mean -manifest?");
    }

    [Test]
    public async Task UnsupportedOptionWithoutACloseNameHasNoSuggestion()
    {
        await Assert.That(DepotDownloaderArguments.Parse(["-app", "730", "-zzzzzzzz"])).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains("Unsupported option -zzzzzzzz.").And.DoesNotContain("Did you mean");
    }

    [Test]
    public async Task FileListLinesBecomeEscapedRegexPatterns()
    {
        using var temp = new TempDirectory();
        var list = temp.Write("files.txt", "game/bin/a+b.dll\n  game\\bin\\c.dll  \n\n/lead/slash.txt\r\nregex:^game/.*\\.so$\n");

        var result = DepotDownloaderArguments.Parse(["-app", "730", "-filelist", list]);

        await Assert.That(result).IsNotNull();

        var filter = FileFilter.FromPatterns(result!.Patterns);

        await Assert.That(result.Patterns.Count).IsEqualTo(4);
        await Assert.That(filter.GetMatchingPatterns("game/bin/a+b.dll").Count).IsEqualTo(1);
        await Assert.That(filter.GetMatchingPatterns("game/bin/aab.dll").Count).IsEqualTo(0);
        await Assert.That(filter.GetMatchingPatterns("game/bin/a+bXdll").Count).IsEqualTo(0);
        await Assert.That(filter.GetMatchingPatterns("x/game/bin/a+b.dll").Count).IsEqualTo(0);
        await Assert.That(filter.GetMatchingPatterns("game/bin/c.dll").Count).IsEqualTo(1);
        await Assert.That(filter.GetMatchingPatterns("game/lib/x.so").Count).IsEqualTo(1);
        await Assert.That(filter.GetMatchingPatterns("lead/slash.txt").Count).IsEqualTo(1);
    }

    [Test]
    [Arguments("")]
    [Arguments("  ")]
    public async Task EmptyDirIsAnError(string dir)
    {
        await Assert.That(DepotDownloaderArguments.Parse(["-app", "730", "-dir", dir])).IsNull();
    }

    [Test]
    public async Task FileListEntriesAreExactPathsWithoutGlobs()
    {
        using var temp = new TempDirectory();
        var list = temp.Write("files.txt", "game/*.dll\nserver.dll\n");

        var filter = FileFilter.FromPatterns(DepotDownloaderArguments.Parse(["-app", "730", "-filelist", list])!.Patterns);

        await Assert.That(filter.GetMatchingPatterns("game/*.dll").Count).IsEqualTo(1);
        await Assert.That(filter.GetMatchingPatterns("game/a.dll").Count).IsEqualTo(0);
        await Assert.That(filter.GetMatchingPatterns("server.dll").Count).IsEqualTo(1);
        await Assert.That(filter.GetMatchingPatterns("bin/server.dll").Count).IsEqualTo(0);
    }

    [Test]
    public async Task FileListsAccumulate()
    {
        using var temp = new TempDirectory();
        var first = temp.Write("a.txt", "a.txt");
        var second = temp.Write("b.txt", "regex:b");

        var result = DepotDownloaderArguments.Parse(["-app", "730", "-filelist", first, "-filelist", second]);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Patterns.Count).IsEqualTo(2);
    }

    [Test]
    [Arguments("")]
    [Arguments("\n\n")]
    [Arguments("   \r\n\t\r\n")]
    public async Task EmptyFileListIsAnError(string content)
    {
        using var temp = new TempDirectory();
        var list = temp.Write("files.txt", content);

        await Assert.That(DepotDownloaderArguments.Parse(["-app", "730", "-filelist", list])).IsNull();
    }

    [Test]
    public async Task MissingFileListIsAnError()
    {
        using var temp = new TempDirectory();

        await Assert.That(DepotDownloaderArguments.Parse(["-app", "730", "-filelist", Path.Combine(temp.Path, "missing.txt")])).IsNull();
    }

    [Test]
    [Arguments("C:foo", false)]
    [Arguments("\\foo", false)]
    [Arguments("D:/SteamDepots", true)]
    [Arguments("D:\\SteamDepots", true)]
    [Arguments("out/dir", true)]
    [RunOn(OS.Windows)]
    public async Task DirIsCheckedForDriveRelativePaths(string dir, bool accepted)
    {
        var result = DepotDownloaderArguments.Parse(["-app", "730", "-dir", dir]);

        await Assert.That(result != null).IsEqualTo(accepted);

        if (accepted)
        {
            await Assert.That(result!.Options.Output).IsEqualTo(dir);
        }
    }

    [Test]
    public async Task DoubleDashArgumentIsRejected()
    {
        await Assert.That(DepotDownloaderArguments.Parse(["--app", "730"])).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains("dd takes DepotDownloader's single-dash options, like -app 730.");
    }

    [Test]
    public async Task BareValueIsRejected()
    {
        await Assert.That(DepotDownloaderArguments.Parse(["730"])).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains("Unexpected argument \"730\"; dd takes options like -app 730.");
    }

    [Test]
    [Arguments("-username")]
    [Arguments("-user")]
    public async Task UsernameNeedsAValue(string option)
    {
        await Assert.That(DepotDownloaderArguments.Parse(["-app", "730", option])).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains($"{option} needs a value.");
    }

    [Test]
    public async Task ZeroDepotIsRejected()
    {
        await Assert.That(DepotDownloaderArguments.Parse(["-app", "730", "-depot", "0"])).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains("-depot needs a nonzero depot id, got \"0\".");
    }

    [Test]
    public async Task NonNumericDepotIsRejected()
    {
        await Assert.That(DepotDownloaderArguments.Parse(["-app", "730", "-depot", "abc"])).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains("-depot needs a numeric id, got \"abc\".");
    }

    [Test]
    public async Task ZeroManifestIsRejected()
    {
        await Assert.That(DepotDownloaderArguments.Parse(["-app", "730", "-depot", "1", "-manifest", "0"])).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains("-manifest needs a nonzero manifest id, got \"0\".");
    }

    [Test]
    [Arguments("-ugc", "manifest")]
    [Arguments("-pubfile", "item")]
    public async Task ZeroWorkshopIdIsRejected(string option, string kind)
    {
        await Assert.That(DepotDownloaderArguments.Parse(["-app", "730", option, "0"])).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains($"{option} needs a nonzero {kind} id, got \"0\".");
    }

    [Test]
    public async Task MissingAppShowsAnExample()
    {
        await Assert.That(DepotDownloaderArguments.Parse(["-depot", "1"])).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains("dd needs -app <app>, like: ").And.Contains("dd -app 730 -depot 2347771.");
    }
}
