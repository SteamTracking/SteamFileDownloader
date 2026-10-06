using SteamKit2;

namespace SteamFileDownloader;

internal static class ExitCodes
{
    public const int Ok = 0;
    public const int Failed = 1;
    public const int NoMatch = 2;
    public const int NoAccess = 3;
    public const int RateLimited = 4;

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
}
