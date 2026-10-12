using SteamKit2;

namespace SteamFileDownloader;

internal static class ExitCodes
{
    public const int Ok = 0;
    public const int Failed = 1;
    public const int NoMatch = 2;
    public const int NoAccess = 3;
    public const int RateLimited = 4;

    // Steam gave no depot key or manifest request code for some targets, and nothing else failed
    public const int NoDepotAccess = 5;

    public static int FromLogOnResult(EResult result) => result switch
    {
        EResult.OK => Ok,
        EResult.RateLimitExceeded or EResult.AccountLoginDeniedThrottle => RateLimited,
        EResult.InvalidPassword
            or EResult.InvalidSignature
            or EResult.AccessDenied
            or EResult.Expired
            or EResult.Revoked
            or EResult.AccountDisabled
            or EResult.AccountLogonDenied
            or EResult.AccountLoginDeniedNeedTwoFactor
            or EResult.InvalidLoginAuthCode
            or EResult.TwoFactorCodeMismatch => NoAccess,
        _ => Failed,
    };

    // The exit code of a finished get or ls run and the error that explains it, or null for success
    public static (int ExitCode, string? Message) GetExitCode(bool failed, bool failedOtherwise, int sessionFailureExitCode, bool unmatched)
    {
        if (failed && sessionFailureExitCode != Failed)
        {
            return (sessionFailureExitCode, $"Exit code {sessionFailureExitCode}: logging in again after a lost connection failed (see above), so stdout is partial.");
        }

        if (failed)
        {
            var (exitCode, reason) = failedOtherwise
                ? (Failed, "some targets or files failed")
                : (NoDepotAccess, "this account has no access to some targets");

            return (exitCode, $"Exit code {exitCode}: {reason}, so stdout is partial{(unmatched ? ", and a pattern matched nothing" : "")}.");
        }

        if (unmatched)
        {
            return (NoMatch, "Exit code 2: a pattern matched nothing.");
        }

        return (Ok, null);
    }
}
