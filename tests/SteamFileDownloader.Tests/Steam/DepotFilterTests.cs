using System.Runtime.InteropServices;
using SteamKit2;

namespace SteamFileDownloader.Tests;

public class DepotFilterTests
{
    private const string Depots = """
        "depots"
        {
            "baselanguages" "english,french"
            "1" { "config" { "oslist" "windows" } }
            "2" { "config" { "oslist" "linux" } }
            "3" { "config" { "oslist" "windows" "osarch" "32" } }
            "4" { "config" { "language" "french" } }
            "5" { "config" { "language" "german" } }
            "6" { "NoContentDLC" "1" }
            "7" { "config" { "lowviolence" "1" } }
            "8" { "dlcappid" "123" }
            "9" { "config" { "oslist" "macos" "language" "english" } }
            "10" { "name" "no config" }
            "11" { "config" { "oslist" "" "language" "" } }
        }
        """;

    private static KeyValue Parse(string text) => KeyValue.LoadFromString(text)!;

    private static Dictionary<string, string> WindowsConfig(InstallOptions? options = null) =>
        DepotFilter.CreateUserConfig(options ?? new InstallOptions(), "windows", true, "x86");

    private static string Select(string depots, InstallOptions? options = null)
    {
        var installOptions = options ?? new InstallOptions();
        var selection = DepotFilter.Select(Parse(depots), installOptions, WindowsConfig(installOptions));

        return string.Join(",", selection.Depots.Select(static d => d.ID));
    }

    [Test]
    [Arguments("windows", "windows", true)]
    [Arguments("Windows", "windows", true)]
    [Arguments("windows", "WINDOWS", true)]
    [Arguments("windows,linux", "linux", true)]
    [Arguments("windows, linux", "linux", true)]
    [Arguments("windows linux", "linux", true)]
    [Arguments("windows,linux", "macos", false)]
    [Arguments("windows", "windows,linux", true)]
    [Arguments("windows", "linux macos", false)]
    [Arguments("windows,linux", "macos,linux", true)]
    [Arguments("!windows", "linux", true)]
    [Arguments("!windows", "windows", false)]
    [Arguments("!windows", "windows,linux", true)]
    [Arguments("!WINDOWS", "windows", false)]
    [Arguments(">5", "7", true)]
    [Arguments(">5", "5", false)]
    [Arguments("<5", "3", true)]
    [Arguments("<5", "7", false)]
    [Arguments("<5", "x", true)]
    [Arguments(">5", "7abc", true)]
    [Arguments("<-1", "-3", true)]
    [Arguments("windows", "", false)]
    [Arguments("windows", " ", false)]
    [Arguments("windows", null, false)]
    [Arguments("", "windows", true)]
    [Arguments(" , ", "windows", true)]
    [Arguments("", null, true)]
    public async Task ValueMatchesComparesTokenLists(string depotValue, string? userValue, bool expected)
    {
        await Assert.That(DepotFilter.ValueMatches(depotValue, userValue)).IsEqualTo(expected);
    }

    [Test]
    public async Task EveryKeyOfTheDepotMustMatch()
    {
        var config = Parse("\"config\" { \"oslist\" \"windows\" \"osarch\" \"32\" }");

        await Assert.That(DepotFilter.FindMismatch(config, WindowsConfig())).IsEqualTo("osarch");
        await Assert.That(DepotFilter.FindMismatch(config, WindowsConfig(new InstallOptions(OsArch: "32")))).IsNull();
        await Assert.That(DepotFilter.FindMismatch(config, WindowsConfig(new InstallOptions(Os: "linux", OsArch: "32")))).IsEqualTo("oslist");
    }

    [Test]
    public async Task KeysWithoutAValueAndMissingConfigAreNoConstraint()
    {
        await Assert.That(DepotFilter.FindMismatch(null, WindowsConfig())).IsNull();
        await Assert.That(DepotFilter.FindMismatch(Parse("\"config\" { \"oslist\" \"\" \"language\" \" \" }"), WindowsConfig())).IsNull();
        await Assert.That(DepotFilter.FindMismatch(Parse("\"config\" { }"), WindowsConfig())).IsNull();
    }

    [Test]
    public async Task KeyTheUserHasNoValueForNeverMatches()
    {
        var config = Parse("\"config\" { \"mod\" \"cstrike\" }");

        await Assert.That(DepotFilter.FindMismatch(config, WindowsConfig())).IsEqualTo("mod");
        await Assert.That(DepotFilter.FindMismatch(config, new Dictionary<string, string> { ["mod"] = "" })).IsEqualTo("mod");
        await Assert.That(DepotFilter.FindMismatch(config, new Dictionary<string, string> { ["mod"] = "cstrike" })).IsNull();
    }

    [Test]
    public async Task KeyNamesIgnoreCase()
    {
        var config = Parse("\"config\" { \"OSList\" \"Windows\" }");

        await Assert.That(DepotFilter.FindMismatch(config, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["OSLIST"] = "windows" })).IsNull();
        await Assert.That(DepotFilter.FindMismatch(config, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["oslist"] = "linux" })).IsEqualTo("oslist");
    }

    [Test]
    public async Task IgnoredKeysAreNotChecked()
    {
        var config = Parse("\"config\" { \"oslist\" \"linux\" \"language\" \"french\" }");

        await Assert.That(DepotFilter.FindMismatch(config, WindowsConfig())).IsEqualTo("oslist");
        await Assert.That(DepotFilter.FindMismatch(config, WindowsConfig(), static key => key == "oslist")).IsEqualTo("language");
        await Assert.That(DepotFilter.FindMismatch(config, WindowsConfig(), static _ => true)).IsNull();
    }

    [Test]
    public async Task UserConfigDefaultsToThisMachineAndSteamsValues()
    {
        var config = DepotFilter.CreateUserConfig(new InstallOptions(), "linux", true, "x86");

        await Assert.That(config["oslist"]).IsEqualTo("linux");
        await Assert.That(config["osarch"]).IsEqualTo("64");
        await Assert.That(config["oscpu"]).IsEqualTo("x86");
        await Assert.That(config["steamdeck"]).IsEqualTo("0");
        await Assert.That(config["realm"]).IsEqualTo("steamglobal");
        await Assert.That(config.ContainsKey("lowviolence")).IsFalse();
        await Assert.That(config.ContainsKey("language")).IsFalse();
    }

    [Test]
    public async Task UserConfigUsesThe32BitOsAndTheOptions()
    {
        await Assert.That(DepotFilter.CreateUserConfig(new InstallOptions(), "windows", false, "x86")["osarch"]).IsEqualTo("32");

        var config = DepotFilter.CreateUserConfig(new InstallOptions(Os: "macos", OsArch: "64", LowViolence: true), "windows", false, "arm64");

        await Assert.That(config["oslist"]).IsEqualTo("macos");
        await Assert.That(config["osarch"]).IsEqualTo("64");
        await Assert.That(config["oscpu"]).IsEqualTo("arm64");
        await Assert.That(config["lowviolence"]).IsEqualTo("1");
    }

    [Test]
    [Arguments("french", "english,french", "french")]
    [Arguments("French", "english,french", "french")]
    [Arguments("spanish", "english,spanish,latam", "spanish")]
    [Arguments("spanish", "english,latam", "latam")]
    [Arguments("latam", "english,spanish", "spanish")]
    [Arguments("sc_schinese", "english,schinese", "schinese")]
    [Arguments("schinese", "english,sc_schinese", "english")]
    [Arguments("german", "english,french", "english")]
    [Arguments("german", "french,italian", "french")]
    [Arguments("spanish", "french,italian", "french")]
    public async Task ResolveLanguageFollowsTheChain(string requested, string candidates, string expected)
    {
        await Assert.That(DepotFilter.ResolveLanguage(requested, [.. candidates.Split(',')])).IsEqualTo(expected);
    }

    [Test]
    public async Task ResolveLanguageWithoutCandidatesIsEnglish()
    {
        await Assert.That(DepotFilter.ResolveLanguage("french", [])).IsEqualTo("english");
    }

    [Test]
    public async Task LanguageCandidatesAreTheBaseLanguagesAndThoseOfDepotsForThePlatform()
    {
        var depots = Parse("""
            "depots"
            {
                "baselanguages" "english, french"
                "1" { "config" { "language" "german" } }
                "2" { "config" { "language" "italian" "oslist" "linux" } }
                "3" { "config" { "language" "polish" } "dlcappid" "5" }
                "4" { "config" { "language" "russian" } "NoContentDLC" "1" }
                "5" { "config" { "language" "french" } }
            }
            """);

        var candidates = DepotFilter.GetLanguageCandidates(depots, WindowsConfig(), allPlatforms: false);
        var everyPlatform = DepotFilter.GetLanguageCandidates(depots, WindowsConfig(), allPlatforms: true);

        await Assert.That(candidates).IsEquivalentTo(["english", "french", "german"]);
        await Assert.That(everyPlatform).IsEquivalentTo(["english", "french", "german", "italian"]);
    }

    [Test]
    public async Task LanguageCandidatesDefaultToEnglishWithoutBaseLanguages()
    {
        var depots = Parse("\"depots\" { \"1\" { \"config\" { \"language\" \"german\" } } }");

        await Assert.That(DepotFilter.GetLanguageCandidates(depots, WindowsConfig(), allPlatforms: false)).IsEquivalentTo(["english", "german"]);
    }

    [Test]
    public async Task DefaultSelectionIsForThisMachineInEnglish()
    {
        var selection = DepotFilter.Select(Parse(Depots), new InstallOptions(), WindowsConfig());

        await Assert.That(selection.Language).IsEqualTo("english");
        await Assert.That(string.Join(",", selection.Depots.Select(static d => d.ID))).IsEqualTo("1,8,10,11");
        await Assert.That(string.Join(",", selection.Skipped.Select(static s => $"{s.ID}:{s.Reason}")))
            .IsEqualTo("2:oslist,3:osarch,4:language,5:language,6:no content DLC,7:lowviolence,9:oslist");
    }

    [Test]
    public async Task NoContentDlcIsSkippedWhateverTheValueSays()
    {
        await Assert.That(Select("\"depots\" { \"1\" { \"nocontentdlc\" \"1\" } \"2\" { \"NoContentDLC\" \"true\" } \"3\" { \"nocontentdlc\" \"0\" } }")).IsEqualTo("3");
        await Assert.That(Select("\"depots\" { \"1\" { \"nocontentdlc\" \"1\" } \"2\" { \"nocontentdlc\" \"1\" } }", new InstallOptions(AllPlatforms: true, AllLanguages: true))).IsEqualTo("");
    }

    [Test]
    public async Task SelectionKeepsTheOrderOfTheAppInfo()
    {
        await Assert.That(Select("\"depots\" { \"30\" { } \"10\" { } \"20\" { } \"baselanguages\" \"english\" }")).IsEqualTo("30,10,20");
    }

    [Test]
    public async Task AllPlatformsIgnoresOsArchAndCpu()
    {
        var depots = """
            "depots"
            {
                "1" { "config" { "oslist" "windows" } }
                "2" { "config" { "oslist" "linux" } }
                "3" { "config" { "osarch" "32" } }
                "4" { "config" { "oscpu" "arm64" } }
                "5" { "config" { "oslist" "linux" "language" "french" } }
            }
            """;

        await Assert.That(Select(depots)).IsEqualTo("1");
        await Assert.That(Select(depots, new InstallOptions(AllPlatforms: true))).IsEqualTo("1,2,3,4");
    }

    [Test]
    public async Task SteamDeckDepotsNeedTheSteamDeckOption()
    {
        var depots = "\"depots\" { \"1\" { } \"2\" { \"config\" { \"steamdeck\" \"1\" } } }";

        await Assert.That(WindowsConfig()["steamdeck"]).IsEqualTo("0");
        await Assert.That(WindowsConfig(new InstallOptions(SteamDeck: true))["steamdeck"]).IsEqualTo("1");
        await Assert.That(Select(depots)).IsEqualTo("1");
        await Assert.That(Select(depots, new InstallOptions(SteamDeck: true))).IsEqualTo("1,2");
    }

    [Test]
    public async Task AllPlatformsIgnoresTheSteamDeck()
    {
        var depots = "\"depots\" { \"1\" { } \"2\" { \"config\" { \"steamdeck\" \"1\" } } }";

        await Assert.That(Select(depots, new InstallOptions(AllPlatforms: true))).IsEqualTo("1,2");
    }

    [Test]
    public async Task HighQualityAudioDepotsAreForSoundtracks()
    {
        var depots = Parse("\"depots\" { \"1\" { } \"2\" { \"config\" { \"highqualityaudio\" \"1\" } } }");
        var options = new InstallOptions();
        var music = DepotFilter.CreateUserConfig(options, "windows", true, "x86", isMusic: true);
        var other = DepotFilter.CreateUserConfig(options, "windows", true, "x86", isMusic: false);

        await Assert.That(music["highqualityaudio"]).IsEqualTo("1");
        await Assert.That(other.ContainsKey("highqualityaudio")).IsFalse();
        await Assert.That(string.Join(",", DepotFilter.Select(depots, options, music).Depots.Select(static d => d.ID))).IsEqualTo("1,2");

        var selection = DepotFilter.Select(depots, options, other);

        await Assert.That(string.Join(",", selection.Depots.Select(static d => d.ID))).IsEqualTo("1");
        await Assert.That(selection.Skipped[0]).IsEqualTo(new SkippedDepot(2, "highqualityaudio", "1"));
    }

    [Test]
    [Arguments("Music", true)]
    [Arguments("music", true)]
    [Arguments("Game", false)]
    public async Task OnlyMusicAppsAreSoundtracks(string type, bool expected)
    {
        var appInfo = Parse($"\"appinfo\" {{ \"common\" {{ \"type\" \"{type}\" }} }}");

        await Assert.That(AppInfo.IsMusic(appInfo)).IsEqualTo(expected);
        await Assert.That(AppInfo.IsMusic(Parse("\"appinfo\" { }"))).IsFalse();
    }

    [Test]
    public async Task AllLanguagesIgnoresTheLanguage()
    {
        await Assert.That(Select(Depots, new InstallOptions(AllLanguages: true))).IsEqualTo("1,4,5,8,10,11");
    }

    [Test]
    public async Task LowViolenceAddsItsDepots()
    {
        await Assert.That(Select(Depots, new InstallOptions(LowViolence: true))).IsEqualTo("1,7,8,10,11");
    }

    [Test]
    public async Task OsOptionPicksThatSystemsDepots()
    {
        await Assert.That(Select(Depots, new InstallOptions(Os: "linux"))).IsEqualTo("2,8,10,11");
        await Assert.That(Select(Depots, new InstallOptions(Os: "macos"))).IsEqualTo("8,9,10,11");
    }

    [Test]
    public async Task OsArchOptionPicksThatBitness()
    {
        await Assert.That(Select(Depots, new InstallOptions(OsArch: "32"))).IsEqualTo("1,3,8,10,11");
    }

    [Test]
    public async Task RequestedLanguageIsInstalledWhenTheAppHasIt()
    {
        var selection = DepotFilter.Select(Parse(Depots), new InstallOptions(Language: "french"), WindowsConfig());

        await Assert.That(selection.Language).IsEqualTo("french");
        await Assert.That(string.Join(",", selection.Depots.Select(static d => d.ID))).IsEqualTo("1,4,8,10,11");
    }

    [Test]
    public async Task MissingLanguageFallsBackToEnglish()
    {
        var selection = DepotFilter.Select(Parse(Depots), new InstallOptions(Language: "japanese"), WindowsConfig());

        await Assert.That(selection.Language).IsEqualTo("english");
        await Assert.That(string.Join(",", selection.Depots.Select(static d => d.ID))).IsEqualTo("1,8,10,11");
    }

    [Test]
    public async Task AppWithoutEnglishUsesItsFirstLanguage()
    {
        var depots = "\"depots\" { \"baselanguages\" \"german,french\" \"1\" { \"config\" { \"language\" \"german\" } } \"2\" { \"config\" { \"language\" \"french\" } } }";
        var selection = DepotFilter.Select(Parse(depots), new InstallOptions(), WindowsConfig());

        await Assert.That(selection.Language).IsEqualTo("german");
        await Assert.That(string.Join(",", selection.Depots.Select(static d => d.ID))).IsEqualTo("1");
    }

    [Test]
    public async Task DepotsOfOtherConfigsAreSkipped()
    {
        var depots = "\"depots\" { \"1\" { \"config\" { \"optionaldlc\" \"123\" } } \"2\" { \"config\" { \"steamdeck\" \"1\" } } \"3\" { \"config\" { \"realm\" \"steamchina\" } } \"4\" { \"config\" { \"mod\" \"x\" } } \"5\" { \"config\" { \"realm\" \"steamglobal\" \"steamdeck\" \"0\" } } }";
        var selection = DepotFilter.Select(Parse(depots), new InstallOptions(), WindowsConfig());

        await Assert.That(string.Join(",", selection.Depots.Select(static d => d.ID))).IsEqualTo("5");
        await Assert.That(string.Join(",", selection.Skipped.Select(static s => $"{s.ID}:{s.Reason}"))).IsEqualTo("1:optionaldlc,2:steamdeck,3:realm,4:mod");
    }

    [Test]
    public async Task SharedInstallDepotsAreSelectedByTheirConfig()
    {
        var depots = "\"depots\" { \"1\" { \"depotfromapp\" \"70\" \"sharedinstall\" \"2\" \"config\" { \"oslist\" \"windows\" } } \"2\" { \"depotfromapp\" \"70\" \"sharedinstall\" \"2\" \"config\" { \"oslist\" \"linux\" } } }";

        await Assert.That(Select(depots)).IsEqualTo("1");
    }

    [Test]
    public async Task OnlyForNewInstallsDepotsAreIncluded()
    {
        await Assert.That(Select("\"depots\" { \"1\" { \"OnlyForNewInstalls\" \"1\" } }")).IsEqualTo("1");
    }

    [Test]
    public async Task LocalContentServerBranchesAreRecognized()
    {
        var depots = Parse("\"depots\" { \"branches\" { \"public\" { \"buildid\" \"1\" } \"lan\" { \"buildid\" \"2\" \"lcsrequired\" \"1\" } \"beta\" { \"buildid\" \"3\" } } }");

        await Assert.That(AppInfo.RequiresLocalContentServer(depots, "lan")).IsTrue();
        await Assert.That(AppInfo.RequiresLocalContentServer(depots, "beta")).IsFalse();
        await Assert.That(AppInfo.RequiresLocalContentServer(depots, "public")).IsFalse();
        await Assert.That(AppInfo.RequiresLocalContentServer(depots, "missing")).IsFalse();
    }

    [Test]
    [Arguments(Architecture.Arm64, "linux", "arm64")]
    [Arguments(Architecture.Arm64, "macos", "arm64")]
    [Arguments(Architecture.Arm64, "windows", "x86")]
    [Arguments(Architecture.X64, "linux", "x86")]
    [Arguments(Architecture.X64, "macos", "x86")]
    [Arguments(Architecture.X64, "windows", "x86")]
    public async Task MachineCpuIsArm64OnlyForArm64LinuxAndMacOs(Architecture architecture, string machineOs, string expected)
    {
        await Assert.That(DepotFilter.GetMachineCpu(architecture, machineOs)).IsEqualTo(expected);
    }

    [Test]
    public async Task OsCpuOptionReplacesTheMachinesCpu()
    {
        var depots = "\"depots\" { \"1\" { } \"2\" { \"config\" { \"oscpu\" \"arm64\" } } }";
        var options = new InstallOptions(OsCpu: "arm64");
        var config = DepotFilter.CreateUserConfig(options, "linux", true, "x86");

        await Assert.That(config["oscpu"]).IsEqualTo("arm64");
        await Assert.That(DepotFilter.CreateUserConfig(new InstallOptions(), "linux", true, "arm64")["oscpu"]).IsEqualTo("arm64");
        await Assert.That(Select(depots)).IsEqualTo("1");
        await Assert.That(string.Join(",", DepotFilter.Select(Parse(depots), options, config).Depots.Select(static d => d.ID))).IsEqualTo("1,2");
    }

    private const string ModDepots = """
        "depots"
        {
            "baselanguages" "english"
            "1" { }
            "2" { "config" { "mod" "czero" } }
            "3" { "config" { "oslist" "macos" } }
            "4" { "dlcappid" "500" }
            "5" { "dlcappid" "500" "config" { "oslist" "linux" } }
            "6" { "nocontentdlc" "1" }
            "7" { "dlcappid" "600" }
            "8" { }
        }
        """;

    private static (string Depots, string Skipped, string Included) Apply(uint[] include, uint[] exclude)
    {
        var depots = Parse(ModDepots);
        var selection = DepotFilter.Select(depots, new InstallOptions(), WindowsConfig());
        var result = DepotFilter.ApplyIncludeExclude(depots, selection, include, exclude);

        return (
            string.Join(",", result.Depots.Select(static d => d.ID)),
            string.Join(",", result.Skipped.Select(static s => $"{s.ID}:{s.Reason}")),
            string.Join(",", result.Included.Select(static s => $"{s.ID}:{s.Reason}")));
    }

    [Test]
    public async Task WithoutIncludeOrExcludeTheSelectionIsTheSame()
    {
        var (depots, skipped, included) = Apply([], []);

        await Assert.That(depots).IsEqualTo("1,4,7,8");
        await Assert.That(skipped).IsEqualTo("2:mod,3:oslist,5:oslist,6:no content DLC");
        await Assert.That(included).IsEqualTo("");
    }

    [Test]
    public async Task IncludeAddsASkippedDepotInTheOrderOfTheAppInfo()
    {
        var (depots, skipped, included) = Apply([6, 2], []);

        await Assert.That(depots).IsEqualTo("1,2,4,6,7,8");
        await Assert.That(skipped).IsEqualTo("3:oslist,5:oslist");
        await Assert.That(included).IsEqualTo("2:mod,6:no content DLC");
    }

    [Test]
    public async Task IncludeOfASelectedDepotChangesNothing()
    {
        var (depots, _, included) = Apply([1], []);

        await Assert.That(depots).IsEqualTo("1,4,7,8");
        await Assert.That(included).IsEqualTo("");
    }

    [Test]
    public async Task ExcludeRemovesASelectedDepot()
    {
        var (depots, skipped, _) = Apply([], [4, 8]);

        await Assert.That(depots).IsEqualTo("1,7");
        await Assert.That(skipped).IsEqualTo("2:mod,3:oslist,5:oslist,6:no content DLC,4:excluded,8:excluded");
    }

    [Test]
    public async Task DlcAppIdNamesEveryDepotOfTheDlc()
    {
        var (included, _, _) = Apply([500], []);
        var (excluded, skipped, _) = Apply([], [500]);

        await Assert.That(included).IsEqualTo("1,4,5,7,8");
        await Assert.That(excluded).IsEqualTo("1,7,8");
        await Assert.That(skipped).Contains("4:excluded");
    }

    [Test]
    public async Task ExcludeWinsOverInclude()
    {
        var (depots, skipped, included) = Apply([2, 500], [2, 5]);

        await Assert.That(depots).IsEqualTo("1,4,7,8");
        await Assert.That(skipped).Contains("2:mod");
        await Assert.That(included).IsEqualTo("");
    }

    [Test]
    public async Task UnknownIdsAreFound()
    {
        var depots = Parse(ModDepots);

        await Assert.That(DepotFilter.FindUnknownIds(depots, [2, 500, 999, 4, 600, 12])).IsEquivalentTo([999u, 12u]);
    }

    [Test]
    public async Task OsOptionForAnotherSystemSkipsLinks()
    {
        var other = DepotFilter.GetMachineOs() == "windows" ? "linux" : "windows";

        await Assert.That(new InstallOptions().IsForOtherOs).IsFalse();
        await Assert.That(new InstallOptions(Os: DepotFilter.GetMachineOs()).IsForOtherOs).IsFalse();
        await Assert.That(new InstallOptions(Os: other).IsForOtherOs).IsTrue();
    }
}
