namespace SteamFileDownloader.Tests;

public class ProgramTests
{
    [Test]
    [Arguments("bogus", "Unknown command \"bogus\". Commands are get, ls, depots, workshop, dd, login and track.")]
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
}
