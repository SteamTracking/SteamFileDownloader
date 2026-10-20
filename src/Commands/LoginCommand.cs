using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using QRCoder;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Internal;

namespace SteamFileDownloader;

/// <summary>
/// A parsed login command line: login with an optional username, login list, or login remove with usernames.
/// </summary>
internal sealed record LoginArguments(string Command, LoginOptions Login, List<string> Usernames);

internal static class LoginCommand
{
    /// <summary>
    /// Parses "[list | remove] [options] [usernames]" after "login". Returns null after printing an error.
    /// </summary>
    public static LoginArguments? ParseArguments(string[] args)
    {
        var command = ArgumentParser.GetCommandName("login", args);
        var parsed = ArgumentParser.Parse(command, command == "login" ? args : args[1..]);

        if (parsed == null)
        {
            return null;
        }

        if (command == "login remove")
        {
            if (parsed.Positionals.Count == 0 || parsed.Patterns.Count > 0)
            {
                Log.Error($"login remove takes usernames, like: {Help.ExeName} login remove <name>. \"{Help.ExeName} login list\" lists them. {Help.UsageHint(command)}");
                return null;
            }

            return new LoginArguments(command, new LoginOptions(), parsed.Positionals);
        }

        var subcommands = ArgumentParser.GetSubcommands("login");

        // A mistyped subcommand, or a lone username given without --username that looks like one
        var startsWithPositional = command == "login" && args.Length > 0 && parsed.Positionals.Count > 0 && args[0] == parsed.Positionals[0];
        var subcommand = startsWithPositional ? ArgumentParser.Suggest(args[0], subcommands) : null;

        if (subcommand != null)
        {
            var asUsername = parsed.Positionals.Count == 1 ? $" To log in as {args[0]}: {Help.ExeName} login --username {args[0]}." : "";
            Log.Error($"Unknown login subcommand \"{args[0]}\". Did you mean {subcommand}? Expected {string.Join(", ", subcommands)}.{asUsername} {Help.UsageHint(command)}");
            return null;
        }

        if (command == "login" && parsed.Positionals.Count == 1 && parsed.Patterns.Count == 0)
        {
            Log.Error($"login takes the name as --username, like: {Help.ExeName} login --username {parsed.Positionals[0]}. {Help.UsageHint(command)}");
            return null;
        }

        if (!ArgumentParser.NoPositionals(parsed, command))
        {
            return null;
        }

        return new LoginArguments(command, LoginOptions.From(parsed.Options), []);
    }

    /// <summary>
    /// Logs in with a QR code when no username is given, otherwise with the given password or one typed in, and saves
    /// the refresh token. A replaced token is revoked. A username whose saved login still works is only made the default.
    /// </summary>
    public static async Task<int> RunAsync(LoginOptions login, CancellationToken cancellationToken)
    {
        var (username, password, _) = login;

        // Agents run commands with captured output, where nobody would see the QR code and it would wait forever
        if (username == null && (Console.IsOutputRedirected || Console.IsInputRedirected))
        {
            Log.Error($"login shows a QR code for a person to scan in the Steam mobile app, so it needs an interactive terminal. Ask the user to run \"{Help.ExeName} login\".");
            return ExitCodes.Failed;
        }

        using var session = new SteamSession(cancellationToken);

        // Tokens Steam already rejects need no revoking
        var deadToken = false;

        var saved = username == null ? null : AccountStore.Find(username);

        if (saved?.RefreshToken != null)
        {
            var savedToken = saved.RefreshToken;

            if (AccountStore.GetExpiry(savedToken) <= DateTimeOffset.UtcNow)
            {
                deadToken = true;
            }
            else
            {
                var credentials = new LogOnCredentials(saved.Username, RefreshToken: savedToken);
                var result = await session.TryLogOnAsync(credentials);
                var exitCode = ExitCodes.FromLogOnResult(result);

                if (exitCode == ExitCodes.Ok)
                {
                    if (!TrySave(new SavedAccount(saved.Username, savedToken)))
                    {
                        return ExitCodes.Failed;
                    }

                    Console.WriteLine($"{saved.Username} is already logged in; it is now the default.");
                    await session.RenewSavedLoginAsync();

                    return ExitCodes.Ok;
                }

                if (exitCode != ExitCodes.NoAccess)
                {
                    return session.ExplainLogOnResult(result, credentials);
                }

                Log.Info($"The saved login for {saved.Username} no longer works ({result}), so logging in again.");
                deadToken = true;
            }
        }

        if (username != null && password == null)
        {
            password = ReadPassword();

            if (string.IsNullOrEmpty(password))
            {
                Log.Error($"No password entered. A person has to type it, so ask the user to run \"{Help.ExeName} login\".");
                return ExitCodes.Failed;
            }
        }

        if (!await session.ConnectAsync())
        {
            Log.Error("Failed to connect to Steam. Check the network, or retry in a few minutes if Steam is down.");
            return ExitCodes.Failed;
        }

        SavedAccount account;

        try
        {
            if (username == null)
            {
                var result = await session.AuthenticateWithQrAsync(ShowQrCode);
                account = new SavedAccount(result.AccountName, result.RefreshToken);
            }
            else
            {
                var refreshToken = await session.AuthenticateAsync(username, password!, isPersistentSession: true);
                account = new SavedAccount(username, refreshToken);
            }
        }
        catch (AuthenticationException e)
        {
            Log.Error($"Authentication failed: {e.Message}");
            return ExitCodes.FromLogOnResult(e.Result);
        }
        catch (Exception e) when (SteamSession.IsError(e, cancellationToken))
        {
            // Like a lost connection, or Steam Guard asking for a code without a terminal to type it in
            Log.Error($"Login failed: {e.Message}");
            return ExitCodes.Failed;
        }

        var replaced = deadToken ? null : AccountStore.Find(account.Username)?.RefreshToken;

        if (!TrySave(account))
        {
            return ExitCodes.Failed;
        }

        Console.WriteLine($"Saved login for {account.Username} to {AccountStore.FilePath}; it is now the default.");

        // Like the Steam client, a replaced token is revoked with the logout action
        if (replaced != null)
        {
            using var api = WebAPI.GetAsyncInterface("IAuthenticationService");

            var failure = await RevokeAsync(api, replaced, EAuthTokenRevokeAction.k_EAuthTokenRevokeLogout, cancellationToken);

            if (failure != null)
            {
                Log.Info($"Revoking the replaced token of {account.Username} failed ({failure}); a token Steam already rejected can't be revoked.");
            }
        }

        return ExitCodes.Ok;
    }

    // Returns false after printing an error
    private static bool TryUpdateStore(Action update)
    {
        try
        {
            update();
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Failed to update the saved logins in {AccountStore.FilePath}: {e.Message}");
            return false;
        }
    }

    private static bool TrySave(SavedAccount account) => TryUpdateStore(() => AccountStore.Save(account));

    /// <summary>
    /// Prints a row per saved login: username, default or -, and the UTC date its token expires, or - when unknown.
    /// </summary>
    public static int List()
    {
        // stdout carries the rows
        Log.UseStdoutForResults();

        var logins = AccountStore.GetLogins();

        foreach (var login in logins)
        {
            var expiry = login.RefreshToken == null ? null : AccountStore.GetExpiry(login.RefreshToken);

            var defaultColumn = login.IsDefault ? "default" : "-";
            var expiryColumn = expiry != null ? AccountStore.FormatDate(expiry.Value) : "-";

            Log.Result($"{login.Username}\t{defaultColumn}\t{expiryColumn}");

            if (login.RefreshToken == null)
            {
                Log.Warn($"{AccountStore.CantDecrypt(login.Username)} Log in again with that account.");
            }
            else if (expiry <= DateTimeOffset.UtcNow)
            {
                Log.Warn($"The saved login for {login.Username} has expired. Log in again with that account.");
            }
        }

        if (logins.Count == 0)
        {
            Log.Info($"No saved logins. Run \"{Help.ExeName} login\" to save one; it needs a person to scan a QR code.");
        }

        return ExitCodes.Ok;
    }

    /// <summary>
    /// Revokes each login's token with Steam, so copies of it stop working too, and forgets it. Revoking takes the
    /// token itself, so no Steam login is needed. A failed revoke is a warning, since the token still expires.
    /// </summary>
    public static async Task<int> RemoveAsync(IReadOnlyList<string> usernames, CancellationToken cancellationToken)
    {
        var logins = AccountStore.GetLogins();
        var wanted = usernames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = logins.FindAll(l => wanted.Contains(l.Username));

        if (removed.Count < wanted.Count)
        {
            var missing = wanted.Where(u => !removed.Exists(l => l.Username.Equals(u, StringComparison.OrdinalIgnoreCase)));
            var saved = logins.Count > 0 ? $" (saved: {string.Join(", ", logins.Select(static l => l.Username))})" : "";
            Log.Error($"No saved login for {string.Join(", ", missing)}{saved}, so nothing was removed.");
            return ExitCodes.Failed;
        }

        using (var api = WebAPI.GetAsyncInterface("IAuthenticationService"))
        {
            await Task.WhenAll(removed.Select(l => RevokeRemovedAsync(api, l, cancellationToken)));
        }

        string? defaultLogin = null;

        if (!TryUpdateStore(() => defaultLogin = AccountStore.Remove(removed.Select(static l => l.Username))))
        {
            return ExitCodes.Failed;
        }

        foreach (var login in removed)
        {
            Console.WriteLine($"Removed the saved login for {login.Username}.");
        }

        if (defaultLogin != null)
        {
            var wasDefault = logins.Exists(l => l.IsDefault && l.Username.Equals(defaultLogin, StringComparison.OrdinalIgnoreCase));

            if (!wasDefault)
            {
                Console.WriteLine($"{defaultLogin} is now the default login.");
            }
        }

        return ExitCodes.Ok;
    }

    private static async Task RevokeRemovedAsync(WebAPI.AsyncInterface api, SavedLogin login, CancellationToken cancellationToken)
    {
        if (login.RefreshToken == null)
        {
            Log.Warn($"{AccountStore.CantDecrypt(login.Username)} It isn't revoked with Steam, only removed here.");
            return;
        }

        var failure = await RevokeAsync(api, login.RefreshToken, EAuthTokenRevokeAction.k_EAuthTokenRevokePermanent, cancellationToken);

        if (failure == null)
        {
            return;
        }

        var expiry = AccountStore.GetExpiry(login.RefreshToken);
        var until = expiry != null ? $" until {AccountStore.FormatDate(expiry.Value)}" : "";

        Log.Warn($"Revoking the token of {login.Username} with Steam failed ({failure}). Steam answers Fail for a token that is already revoked; otherwise a copy of it keeps working{until}. It's removed here anyway.");
    }

    /// <summary>
    /// Revokes a refresh token, which takes only the token itself, so no Steam login is needed. Returns why it failed,
    /// or null when Steam revoked it.
    /// </summary>
    private static async Task<string?> RevokeAsync(
        WebAPI.AsyncInterface api,
        string refreshToken,
        EAuthTokenRevokeAction action,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await api.CallProtobufAsync<CAuthentication_Token_Revoke_Response, CAuthentication_Token_Revoke_Request>(
                HttpMethod.Post,
                "RevokeToken",
                new CAuthentication_Token_Revoke_Request
                {
                    token = refreshToken,
                    revoke_action = action,
                }).WaitAsync(cancellationToken);

            return response.Result == EResult.OK ? null : response.Result.ToString();
        }
        catch (Exception e) when (SteamSession.IsError(e, cancellationToken))
        {
            return SteamSession.DescribeError(e);
        }
    }

    private static void ShowQrCode(string challengeUrl)
    {
        using var generator = new QRCodeGenerator();
        using var qrCodeData = generator.CreateQrCode(challengeUrl, QRCodeGenerator.ECCLevel.L);
        using var qrCode = new AsciiQRCode(qrCodeData);

        Console.WriteLine();
        Console.WriteLine("Scan this QR code in the Steam mobile app (Steam Guard tab, scan icon) to log in:");
        Console.WriteLine(qrCode.GetGraphicSmall(drawQuietZones: true));
        Console.WriteLine("Waiting for confirmation... (the code is redrawn when Steam refreshes it)");
    }

    private static string ReadPassword()
    {
        Console.Error.Write("Password: ");

        if (Console.IsInputRedirected)
        {
            var line = Console.ReadLine();
            Console.Error.WriteLine();

            return line ?? string.Empty;
        }

        var password = new StringBuilder();

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
            {
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (password.Length > 0)
                {
                    password.Length--;
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                password.Append(key.KeyChar);
            }
        }

        Console.Error.WriteLine();

        return password.ToString();
    }
}
