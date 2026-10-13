namespace SteamFileDownloader.Tests;

public class ExitCodeTests
{
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
        var (code, message) = ExitCodes.GetExitCode(failed, failedOtherwise, sessionFailureExitCode, unmatched);

        await Assert.That(code).IsEqualTo(expected);
        await Assert.That(message == null).IsEqualTo(expected == ExitCodes.Ok);
    }
}
