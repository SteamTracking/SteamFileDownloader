namespace SteamFileDownloader.Tests;

public class ProgramTests
{
    [Test]
    [Arguments("gte", "Unknown command \"gte\". Did you mean get? Commands are get, ls, depots, workshop, dd, login, config and track.")]
    [Arguments("verison", "Unknown command \"verison\". Did you mean version? Commands are")]
    [Arguments("bogus", "Unknown command \"bogus\". Commands are get, ls, depots, workshop, dd, login, config and track.")]
    [Arguments("--verison", "Unknown command \"--verison\". Did you mean --version?")]
    [Arguments("--appid", "Unknown command \"--appid\". The command goes first, before its options. Downloading the files in files.json is now: ")]
    public async Task UnknownCommandIsAUsageErrorWithAHint(string command, string expected)
    {
        await Assert.That(Program.UnknownCommand(command)).IsEqualTo(ExitCodes.UsageError);
        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains(expected);
    }

    [Test]
    public async Task OldGameTrackingCommandLineGetsTheTrackCommand()
    {
        Program.UnknownCommand("--appid");

        await Assert.That(TestContext.Current!.GetErrorOutput()).Contains($"{Help.ExeName} track <app> --output <dir> ...");
    }

    [Test]
    [Arguments("1.2.3+abcdef1234567890", "1.2.3", "abcdef1")]
    [Arguments("1.2.3+abc", "1.2.3", "abc")]
    [Arguments("1.2.3+", "1.2.3", null)]
    [Arguments("1.2.3", "1.2.3", null)]
    public async Task VersionKeepsTheShortCommitHash(string informationalVersion, string version, string? commit)
    {
        await Assert.That(Program.ParseVersion(informationalVersion)).IsEqualTo((version, commit));
    }

    [Test]
    public async Task VersionHasTheCommitWhenThereIsOne()
    {
        await Assert.That(Program.FormatVersion("1.2.3", "abcdef1")).IsEqualTo("1.2.3 (abcdef1)");
        await Assert.That(Program.FormatVersion("1.2.3", null)).IsEqualTo("1.2.3");
    }
}
