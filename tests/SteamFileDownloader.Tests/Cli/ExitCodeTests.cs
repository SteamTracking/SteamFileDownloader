namespace SteamFileDownloader.Tests;

public class ExitCodeTests
{
    [Test]
    public async Task CodesAreTheDocumentedOnes()
    {
        int[] codes = [ExitCodes.Ok, ExitCodes.Failed, ExitCodes.UsageError, ExitCodes.NoAccess, ExitCodes.RateLimited, ExitCodes.NoDepotAccess, ExitCodes.NoMatch];

        await Assert.That(codes).IsEquivalentTo([0, 1, 2, 3, 4, 5, 6], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("get", ExitCodes.UsageError)]
    [Arguments("workshop ls", ExitCodes.UsageError)]
    [Arguments("login", ExitCodes.UsageError)]
    [Arguments("track", ExitCodes.Failed)]
    public async Task UsageErrorsOfTrackExitWithOne(string command, int expected)
    {
        await Assert.That(ExitCodes.ForUsageError(command)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(false, false, ExitCodes.Failed, false, ExitCodes.Ok)]
    [Arguments(false, false, ExitCodes.Failed, true, ExitCodes.NoMatch)]
    [Arguments(true, true, ExitCodes.Failed, false, ExitCodes.Failed)]
    [Arguments(true, true, ExitCodes.Failed, true, ExitCodes.Failed)]
    [Arguments(true, false, ExitCodes.Failed, false, ExitCodes.NoDepotAccess)]
    [Arguments(true, false, ExitCodes.Failed, true, ExitCodes.NoDepotAccess)]
    [Arguments(true, true, ExitCodes.NoAccess, true, ExitCodes.NoAccess)]
    [Arguments(true, false, ExitCodes.RateLimited, false, ExitCodes.RateLimited)]
    [Arguments(false, false, ExitCodes.NoAccess, false, ExitCodes.Ok)]
    [Arguments(false, false, ExitCodes.RateLimited, true, ExitCodes.NoMatch)]
    public async Task ExitCode(bool failed, bool failedOtherwise, int sessionFailureExitCode, bool unmatched, int expected)
    {
        var (code, message) = ExitCodes.GetExitCode(new RunOutcome(failed, failedOtherwise, sessionFailureExitCode, unmatched));

        await Assert.That(code).IsEqualTo(expected);
        // Unmatched patterns are reported where they're found
        await Assert.That(message == null).IsEqualTo(expected is ExitCodes.Ok or ExitCodes.NoMatch);
    }

    [Test]
    public async Task FailedTargetsAreNamedInTheOneClosingError()
    {
        var (code, message) = ExitCodes.GetExitCode(new RunOutcome(true, false, ExitCodes.Failed, false, ["all (12 depots)", "2347771:123"]));

        await Assert.That(code).IsEqualTo(ExitCodes.NoDepotAccess);
        await Assert.That(message).IsEqualTo("Exit code 5: this account has no access to all (12 depots), 2347771:123, so stdout is partial and lacks their rows, though the files may exist.");

        (code, message) = ExitCodes.GetExitCode(new RunOutcome(true, true, ExitCodes.Failed, true, ["2347771 (branch beta)"]));

        await Assert.That(code).IsEqualTo(ExitCodes.Failed);
        await Assert.That(message).IsEqualTo("Exit code 1: 2347771 (branch beta) failed, so stdout is partial and lacks their rows, though the files may exist, and a pattern matched nothing.");
    }

    [Test]
    [Arguments(true, "Exit code 1: some targets or files failed, so stdout is partial.")]
    [Arguments(false, "Exit code 5: this account has no access to some targets, so stdout is partial.")]
    public async Task FailuresWithoutTargetsKeepTheGeneralReason(bool failedOtherwise, string expected)
    {
        var (_, message) = ExitCodes.GetExitCode(new RunOutcome(true, failedOtherwise, ExitCodes.Failed, false, []));

        await Assert.That(message).IsEqualTo(expected);
    }
}
