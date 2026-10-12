using System.Diagnostics;

namespace SteamFileDownloader.Tests;

public class FileFilterTests
{
    private static bool Matches(string pattern, string path) =>
        FileFilter.FromPatterns([pattern]).GetMatchingPatterns(path).Count > 0;

    private static bool JsonMatches(string pattern, string path)
    {
        using var temp = new TempDirectory();
        var file = temp.Write("files.json", $"{{ \"1\": [{System.Text.Json.JsonSerializer.Serialize(pattern)}] }}");

        return FileFilter.LoadFilesJson(file)[1].GetMatchingPatterns(path).Count > 0;
    }

    [Test]
    [Arguments("game/bin/server.dll", "game/bin/server.dll", true)]
    [Arguments("game/bin/server.dll", "x/game/bin/server.dll", false)]
    [Arguments("game/bin/server.dll", "game/bin/server.dll.bak", false)]
    [Arguments("game/bin/server.dll", "GAME/BIN/Server.DLL", true)]
    [Arguments("game/bin/a.b", "game/bin/aXb", false)]
    [Arguments("server.dll", "server.dll", true)]
    [Arguments("server.dll", "game/bin/server.dll", true)]
    [Arguments("server.dll", "myserver.dll", false)]
    [Arguments("server.dll", "game/bin/server.dll.bak", false)]
    [Arguments("server.dll", "game/server_dll", false)]
    [Arguments("*.dll", "a.dll", true)]
    [Arguments("*.dll", "game/bin/a.dll", true)]
    [Arguments("*.dll", "game/bin/a.so", false)]
    [Arguments("game/*.dll", "game/a.dll", true)]
    [Arguments("game/*.dll", "game/sub/a.dll", false)]
    [Arguments("game/*/a.dll", "game/x/a.dll", true)]
    [Arguments("game/*/a.dll", "game/a.dll", false)]
    [Arguments("game/*/a.dll", "game/x/y/a.dll", false)]
    [Arguments("game/a?.dll", "game/ab.dll", true)]
    [Arguments("game/a?.dll", "game/a.dll", false)]
    [Arguments("game/a?.dll", "game/abc.dll", false)]
    [Arguments("game/a?b", "game/a/b", false)]
    [Arguments("game/**/a.dll", "game/a.dll", true)]
    [Arguments("game/**/a.dll", "game/x/a.dll", true)]
    [Arguments("game/**/a.dll", "game/x/y/z/a.dll", true)]
    [Arguments("game/**/a.dll", "game/xa.dll", false)]
    [Arguments("game/**/a.dll", "other/x/a.dll", false)]
    [Arguments("**/a.dll", "a.dll", true)]
    [Arguments("**/a.dll", "x/y/a.dll", true)]
    [Arguments("game/**", "game/x/y/z.dll", true)]
    [Arguments("game/**.dll", "game/x/y/z.dll", true)]
    [Arguments("game\\bin\\server.dll", "game/bin/server.dll", true)]
    [Arguments("game\\bin\\*.dll", "game/bin/a.dll", true)]
    [Arguments("/game/bin/server.dll", "game/bin/server.dll", true)]
    [Arguments("regex:lib(tier0|engine2)\\.so$", "game/bin/linuxsteamrt64/libtier0.so", true)]
    [Arguments("regex:lib(tier0|engine2)\\.so$", "game/bin/libengine2.so.1", false)]
    [Arguments("regex:^scripts/", "scripts/a.txt", true)]
    [Arguments("regex:^scripts/", "x/scripts/a.txt", false)]
    [Arguments("regex:SERVER", "game/server.dll", true)]
    public async Task CommandLinePatternMatches(string pattern, string path, bool expected)
    {
        await Assert.That(Matches(pattern, path)).IsEqualTo(expected);
    }

    [Test]
    public async Task CommandLinePatternsAreListedByTheirSource()
    {
        var filter = FileFilter.FromPatterns(["a.dll", "b/*.so", "regex:c"]);

        await Assert.That(string.Join("|", filter.GetMatchingPatterns("b/a.dll").Select(static p => p.Source))).IsEqualTo("a.dll");
        await Assert.That(string.Join("|", filter.GetMatchingPatterns("b/c.so").Select(static p => p.Source))).IsEqualTo("b/*.so|regex:c");
    }

    [Test]
    public async Task VpkPatternsAreSeparatedFromFilePatterns()
    {
        var filter = FileFilter.FromPatterns(["pak01_dir.vpk:scripts/scenes.vdata_c", "server.dll"]);

        await Assert.That(filter.Patterns.Count).IsEqualTo(1);
        await Assert.That(filter.VpkPatterns.Count).IsEqualTo(1);
        await Assert.That(filter.GetMatchingPatterns("pak01_dir.vpk").Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("pak01_dir.vpk:scripts/a.vdata_c", "game/core/pak01_dir.vpk", true)]
    [Arguments("pak01_dir.vpk:scripts/a.vdata_c", "pak01_dir.vpk", true)]
    [Arguments("pak01_dir.vpk:scripts/a.vdata_c", "game/core/pak02_dir.vpk", false)]
    [Arguments("pak01_dir.vpk:scripts/a.vdata_c", "game/core/xpak01_dir.vpk", false)]
    [Arguments("game/core/pak01_dir.vpk:scripts/a.vdata_c", "game/core/pak01_dir.vpk", true)]
    [Arguments("game/core/pak01_dir.vpk:scripts/a.vdata_c", "other/game/core/pak01_dir.vpk", false)]
    [Arguments("game\\core\\pak01_dir.vpk:x", "game/core/pak01_dir.vpk", true)]
    [Arguments("PAK01_DIR.VPK:Scripts/A.vdata_c", "game/pak01_dir.vpk", true)]
    public async Task VpkPatternMatchesDirectory(string pattern, string dirPath, bool expected)
    {
        await Assert.That(FileFilter.FromPatterns([pattern]).GetVpkPatterns(dirPath).Count).IsEqualTo(expected ? 1 : 0);
    }

    [Test]
    [Arguments("pak01_dir.vpk:scripts/a.vdata_c", "scripts/a.vdata_c", true)]
    [Arguments("pak01_dir.vpk:scripts/a.vdata_c", "x/scripts/a.vdata_c", false)]
    [Arguments("pak01_dir.vpk:scripts/*.vdata_c", "scripts/a.vdata_c", true)]
    [Arguments("pak01_dir.vpk:scripts/*.vdata_c", "scripts/sub/a.vdata_c", false)]
    [Arguments("pak01_dir.vpk:regex:^scripts/", "scripts/a.vdata_c", true)]
    [Arguments("pak01_dir.vpk:regex:^scripts/", "x/scripts/a.vdata_c", false)]
    [Arguments("pak01_dir.vpk:regex:\\.vdata_c$", "x/y/a.vdata_c", true)]
    [Arguments("pak01_dir.vpk:", "anything/at/all", true)]
    [Arguments("PAK01_DIR.VPK:Scripts/A.vdata_c", "scripts/a.vdata_c", true)]
    public async Task VpkPatternMatchesEntry(string pattern, string entry, bool expected)
    {
        await Assert.That(FileFilter.FromPatterns([pattern]).VpkPatterns.Single().Entry.IsMatch(entry)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("regex:(")]
    [Arguments("pak01_dir.vpk:regex:(")]
    [Arguments("regex:(_dir.vpk:x")]
    public async Task InvalidRegexThrowsArgumentException(string pattern)
    {
        using var temp = new TempDirectory();
        var file = temp.Write("files.json", $"{{ \"1\": [{System.Text.Json.JsonSerializer.Serialize(pattern)}] }}");

        await Assert.That(() => FileFilter.FromPatterns([pattern])).Throws<ArgumentException>();
        await Assert.That(() => FileFilter.LoadFilesJson(file)).Throws<ArgumentException>();
    }

    [Test]
    [Arguments("""{ "1": null }""")]
    [Arguments("""{ "1": [null] }""")]
    [Arguments("""{ "1": ["a.dll", null] }""")]
    public async Task FilesJsonNullPatternsAreRejected(string json)
    {
        using var temp = new TempDirectory();
        var file = temp.Write("files.json", json);

        await Assert.That(() => FileFilter.LoadFilesJson(file)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task GetVpkPatternsIsEmptyWithoutVpkPatterns()
    {
        await Assert.That(FileFilter.FromPatterns(["a.dll"]).GetVpkPatterns("pak01_dir.vpk").Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("game/bin/server.dll", "game/bin/server.dll", true)]
    [Arguments("game/bin/server.dll", "x/game/bin/server.dll", false)]
    [Arguments("game/bin/server.dll", "game/bin/SERVER.dll", false)]
    [Arguments("server.dll", "game/bin/server.dll", false)]
    [Arguments("game/bin/a+b.dll", "game/bin/a+b.dll", true)]
    [Arguments("game/bin/a.dll", "game/bin/aXdll", false)]
    [Arguments("game/*.dll", "game/a.dll", true)]
    [Arguments("game/*.dll", "game/sub/a.dll", false)]
    [Arguments("game/a?.dll", "game/ab.dll", true)]
    [Arguments("game/a?.dll", "game/a/.dll", false)]
    [Arguments("game/**/a.dll", "game/a.dll", true)]
    [Arguments("game/**/a.dll", "game/x/y/a.dll", true)]
    [Arguments("game/**/a.dll", "game/x/y/b.dll", false)]
    [Arguments("*.dll", "game/a.dll", false)]
    [Arguments("**/*.dll", "game/a.dll", true)]
    [Arguments("**/*.dll", "a.dll", true)]
    [Arguments("regex:game/.*\\.dll", "game/x/a.dll", true)]
    [Arguments("regex:game/.*\\.dll", "xgame/x/a.dll", false)]
    [Arguments("regex:game/.*\\.dll", "game/x/a.dll.bak", false)]
    [Arguments("regex:a|b", "a", true)]
    [Arguments("regex:a|b", "xb", false)]
    [Arguments("regex:game/A", "game/a", false)]
    [Arguments("game\\bin\\server.dll", "game/bin/server.dll", true)]
    [Arguments("game\\bin\\server.dll", "game/bin/SERVER.dll", false)]
    [Arguments("game\\**\\*.dll", "game/x/a.dll", true)]
    [Arguments("/game/bin/server.dll", "game/bin/server.dll", true)]
    public async Task FilesJsonPatternMatchesWholePathCaseSensitively(string pattern, string path, bool expected)
    {
        await Assert.That(JsonMatches(pattern, path)).IsEqualTo(expected);
    }

    [Test]
    public async Task FilesJsonLoadsDepotsVpkPatternsCommentsAndTrailingCommas()
    {
        using var temp = new TempDirectory();
        var file = temp.Write("files.json", """
            {
              // comment
              "2347771": ["game/bin/server.dll", "pak01_dir.vpk:regex:^scripts/",],
              "2347773": [],
            }
            """);

        var filters = FileFilter.LoadFilesJson(file);

        await Assert.That(string.Join(",", filters.Keys.Order())).IsEqualTo("2347771,2347773");
        await Assert.That(filters[2347771].Patterns.Count).IsEqualTo(1);
        await Assert.That(filters[2347771].VpkPatterns.Count).IsEqualTo(1);
        await Assert.That(filters[2347773].Patterns.Count).IsEqualTo(0);
        await Assert.That(filters[2347773].VpkPatterns.Count).IsEqualTo(0);
    }

    [Test]
    public async Task FilesJsonRejectsRemovedVpkSyntax()
    {
        using var temp = new TempDirectory();
        var file = temp.Write("files.json", """{ "1": ["vpk:vdata_c, vtex_c"] }""");

        Assert.Throws<InvalidDataException>(() => FileFilter.LoadFilesJson(file));

        await Task.CompletedTask;
    }

    [Test]
    public async Task FilesJsonNullDocumentIsRejected()
    {
        using var temp = new TempDirectory();
        var file = temp.Write("files.json", "null");

        Assert.Throws<InvalidDataException>(() => FileFilter.LoadFilesJson(file));

        await Task.CompletedTask;
    }

    [Test]
    public async Task NormalizePathUsesForwardSlashesWithoutLeadingSlash()
    {
        await Assert.That(FileFilter.NormalizePath("\\game\\bin/x.dll")).IsEqualTo("game/bin/x.dll");
        await Assert.That(FileFilter.NormalizePath("//a")).IsEqualTo("a");
        await Assert.That(FileFilter.NormalizePath("a/b")).IsEqualTo("a/b");
    }

    [Test]
    public async Task RepeatedGlobstarsCompileLikeOne()
    {
        var one = FileFilter.FromPatterns(["a/**/b"]).Patterns[0].Regex.ToString();

        await Assert.That(FileFilter.FromPatterns(["a/**/**/**/b"]).Patterns[0].Regex.ToString()).IsEqualTo(one);
        await Assert.That(FileFilter.FromPatterns(["a/**/**/b"]).Patterns[0].Regex.ToString()).IsEqualTo(one);
    }

    [Test]
    public async Task RepeatedGlobstarsDoNotBacktrackOnLongPaths()
    {
        var filter = FileFilter.FromPatterns(["a/**/**/**/**/**/**/**/b"]);
        var path = "a/" + string.Join('/', Enumerable.Repeat("xx", 2000)) + "/c";

        var stopwatch = Stopwatch.StartNew();
        var matched = filter.GetMatchingPatterns(path).Count > 0;
        stopwatch.Stop();

        await Assert.That(matched).IsFalse();
        await Assert.That(stopwatch.Elapsed).IsLessThan(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task RepeatedGlobstarsInFilesJsonMatchLikeOne()
    {
        await Assert.That(JsonMatches("a/**/**/**/b", "a/b")).IsTrue();
        await Assert.That(JsonMatches("a/**/**/**/b", "a/x/y/b")).IsTrue();
        await Assert.That(JsonMatches("a/**/**/**/b", "a/x/y/c")).IsFalse();
    }
}
