using System.Collections.Generic;
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

    /// <summary>
    /// The exit code of a finished run, after logging the error that explains it, if any.
    /// </summary>
    public static int Report(RunOutcome outcome)
    {
        var (exitCode, message) = GetExitCode(outcome);

        if (message != null)
        {
            Log.Error(message);
        }

        return exitCode;
    }

    // The exit code of a finished run and the error that explains it, or null when there is nothing to add
    public static (int ExitCode, string? Message) GetExitCode(RunOutcome outcome)
    {
        var (failed, failedOtherwise, sessionFailureExitCode, unmatched, failedTargets) = outcome;

        if (failed && sessionFailureExitCode != Failed)
        {
            return (sessionFailureExitCode, $"Exit code {sessionFailureExitCode}: logging in again after a lost connection failed (see above), so stdout is partial.");
        }

        if (failed)
        {
            var exitCode = failedOtherwise ? Failed : NoDepotAccess;
            var targets = failedTargets is { Count: > 0 } ? Log.JoinFirst(failedTargets) : null;
            var subject = targets ?? (failedOtherwise ? "some targets or files" : "some targets");
            var reason = failedOtherwise ? $"{subject} failed" : $"this account has no access to {subject}";

            // A failed target has no rows, which says nothing about whether its files exist
            var partial = targets != null ? "stdout is partial and lacks their rows, though the files may exist" : "stdout is partial";

            return (exitCode, $"Exit code {exitCode}: {reason}, so {partial}{(unmatched ? ", and a pattern matched nothing" : "")}.");
        }

        // The unmatched patterns were already reported
        if (unmatched)
        {
            return (NoMatch, null);
        }

        return (Ok, null);
    }
}

/// <summary>
/// How a run ended: whether something failed, and whether for something other than lack of access, the session's
/// <see cref="SteamSession.FailureExitCode"/>, whether a pattern matched nothing, and the failed targets, which the
/// closing error names as the targets without rows.
/// </summary>
internal sealed record RunOutcome(bool Failed, bool FailedOtherwise, int SessionFailureExitCode, bool Unmatched = false, IReadOnlyCollection<string>? FailedTargets = null);
