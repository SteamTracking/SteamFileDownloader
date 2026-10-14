namespace SteamFileDownloader.Tests;

public class ConfigCommandTests
{
    private static string? Describe(string commandLine)
    {
        var arguments = ConfigCommand.ParseArguments(commandLine.Length == 0 ? [] : commandLine.Split(' '));

        return arguments == null ? null : $"{arguments.Value}|{arguments.Reset}";
    }

    [Test]
    [Arguments("output", "|False")]
    [Arguments("output D:/SteamDepots", "D:/SteamDepots|False")]
    [Arguments("output out/dir", "out/dir|False")]
    [Arguments("output --reset", "|True")]
    public async Task CommandLinesAreParsed(string commandLine, string expected)
    {
        await Assert.That(Describe(commandLine)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("", "config needs a setting, expected output.")]
    [Arguments("ouptut x", "Unknown config setting \"ouptut\". Did you mean output? Expected output.")]
    [Arguments("bogus", "Unknown config setting \"bogus\". Expected output.")]
    [Arguments("output a b", "Unexpected argument \"b\"; config output takes one folder")]
    [Arguments("output a -- b", "Unexpected argument \"--\"; config output takes one folder")]
    [Arguments("output a --reset", "--reset clears the saved folder, so it can't be combined with one.")]
    [Arguments("output --rest", "Unknown option \"--rest\". Did you mean --reset?")]
    [Arguments("output --reset=1", "--reset is a flag and takes no value")]
    public async Task InvalidCommandLinesAreRejected(string commandLine, string error)
    {
        await Assert.That(Describe(commandLine)).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains(error);
    }

    [Test]
    [RunOn(TUnit.Core.Enums.OS.Windows)]
    public async Task FolderGetsTheChecksOfOutput()
    {
        await Assert.That(Describe("output C:foo")).IsNull();
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains("config output \"C:foo\" is relative to the current folder or root of a drive");
    }

    [Test]
    public async Task SavedFolderIsAbsoluteAndPrintedUntilReset()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "settings.json");

        await Assert.That(ConfigCommand.Run(new ConfigArguments("out/dir", Reset: false), path)).IsEqualTo(ExitCodes.Ok);
        await Assert.That(Settings.Read(path).Output).IsEqualTo(Path.GetFullPath("out/dir"));

        await Assert.That(ConfigCommand.Run(new ConfigArguments(null, Reset: false), path)).IsEqualTo(ExitCodes.Ok);
        await Assert.That(TestContext.Current!.GetStandardOutput()).Contains(Path.GetFullPath("out/dir"));

        await Assert.That(ConfigCommand.Run(new ConfigArguments(null, Reset: true), path)).IsEqualTo(ExitCodes.Ok);
        await Assert.That(Settings.Read(path).Output).IsNull();
        await Assert.That(File.ReadAllText(path)).DoesNotContain("Output");
    }

    [Test]
    public async Task PrintingWithoutASavedFolderSaysSo()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "settings.json");

        await Assert.That(ConfigCommand.Run(new ConfigArguments(null, Reset: false), path)).IsEqualTo(ExitCodes.Ok);
        await Assert.That(TestContext.Current!.GetStandardOutput() + TestContext.Current!.GetErrorOutput()).Contains("No output folder is saved");
        await Assert.That(File.Exists(path)).IsFalse();
    }

    [Test]
    [RunOn(TUnit.Core.Enums.OS.Windows)]
    public async Task UnreadableSettingsFailWithAnErrorNamingTheFile()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("settings.json", "{}");

        // Like another program holding it open
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.That(Settings.GetOutputRoot(null, path)).IsNull();
            await Assert.That(Settings.GetOutputRoot("given", path)).IsEqualTo(Path.GetFullPath("given"));
            await Assert.That(ConfigCommand.Run(new ConfigArguments(null, Reset: false), path)).IsEqualTo(ExitCodes.Failed);
        }

        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains($"Failed to read the settings in {path}: ");
    }

    [Test]
    public async Task OutputOptionOverridesTheSavedFolderWhichOverridesTheDefault()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        var saved = Path.Combine(temp.Path, "saved");

        await Assert.That(Settings.GetOutputRoot(null, path)).IsEqualTo(Path.GetFullPath("depots"));

        Settings.Write(new Settings.StoredSettings { Output = saved }, path);

        await Assert.That(Settings.GetOutputRoot(null, path)).IsEqualTo(saved);
        await Assert.That(Settings.GetOutputRoot("given", path)).IsEqualTo(Path.GetFullPath("given"));
    }
}
