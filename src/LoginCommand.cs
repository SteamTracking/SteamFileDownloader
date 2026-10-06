using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using QRCoder;
using SteamKit2.Authentication;

namespace SteamFileDownloader;

internal static class LoginCommand
{
    /// <summary>
    /// Logs in with a QR code when no username is given, otherwise with a password, and saves the refresh token.
    /// </summary>
    public static async Task<int> RunAsync(string? username, CancellationToken cancellationToken)
    {
        string? password = null;

        // Agents run commands with captured output, where nobody would see the QR code and it would wait forever
        if (username == null && (Console.IsOutputRedirected || Console.IsInputRedirected))
        {
            Log.Error($"login shows a QR code for a person to scan in the Steam mobile app, so it needs an interactive terminal. Ask the user to run \"{Help.ExeName} login\".");
            return ExitCodes.Failed;
        }

        if (username != null)
        {
            password = ReadPassword();

            if (string.IsNullOrEmpty(password))
            {
                Log.Error($"No password entered. A person has to type it, so ask the user to run \"{Help.ExeName} login\".");
                return ExitCodes.Failed;
            }
        }

        using var session = new SteamSession(cancellationToken);

        if (!await session.ConnectAsync())
        {
            Log.Error("Could not connect to Steam. Check the network, or retry in a few minutes if Steam is down.");
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

        try
        {
            AccountStore.Save(account);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Failed to save the login to {AccountStore.FilePath}: {e.Message}");
            return ExitCodes.Failed;
        }

        Console.WriteLine($"Saved login for {account.Username} to {AccountStore.FilePath}; it is now the default.");

        return ExitCodes.Ok;
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
