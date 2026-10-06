using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SteamFileDownloader;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (Help.TryPrint(args))
        {
            return ExitCodes.Ok;
        }

        if (args[0] == "--version")
        {
            var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
            Console.WriteLine(version.Split('+')[0]);
            return ExitCodes.Ok;
        }

        using var cts = new CancellationTokenSource();

        // The first Ctrl+C stops cleanly, a second one kills the process
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = !cts.IsCancellationRequested;
            cts.Cancel();
        };

        // Paths and the progress bar can have characters that the default Windows console code page lacks
        Console.OutputEncoding = Encoding.UTF8;

        try
        {
            return args[0] switch
            {
                "get" or "ls" or "depots" or "dd" => await RunDepotCommandAsync(args[0], args[1..], cts.Token),
                "login" => await RunLoginAsync(args[1..], cts.Token),
                _ => await RunTrackAsync(args, cts.Token),
            };
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Log.Error("Cancelled.");
            return ExitCodes.Failed;
        }
        catch (Exception e)
        {
            // Still exit code 1 rather than a crash code, with the details for a bug report
            Log.Error($"Unexpected error, this is a bug in {Help.ExeName}: {e}");
            return ExitCodes.Failed;
        }
        finally
        {
            Log.Flush();
        }
    }

    private static async Task<int> RunDepotCommandAsync(string command, string[] args, CancellationToken cancellationToken)
    {
        // stdout carries the results
        Log.UseStdoutForResults();

        var (resolvedCommand, arguments) = command == "dd"
            ? DepotDownloaderArguments.Parse(args) ?? default
            : (command, DepotCommands.ParseArguments(command, args));

        if (arguments == null)
        {
            return ExitCodes.Failed;
        }

        return resolvedCommand switch
        {
            "get" => await DepotCommands.GetAsync(arguments, cancellationToken),
            "ls" => await DepotCommands.ListAsync(arguments, cancellationToken),
            _ => await DepotCommands.ListDepotsAsync(arguments, cancellationToken),
        };
    }

    private static async Task<int> RunLoginAsync(string[] args, CancellationToken cancellationToken)
    {
        var parsed = ArgumentParser.Parse("login", args, ["username"], []);

        if (parsed == null || !NoPositionals(parsed, "login"))
        {
            return ExitCodes.Failed;
        }

        return await LoginCommand.RunAsync(parsed.Options.GetValueOrDefault("username"), cancellationToken);
    }

    // The GameTracking command, which has no command name
    private static async Task<int> RunTrackAsync(string[] args, CancellationToken cancellationToken)
    {
        var parsed = ArgumentParser.Parse("", args, ["appid", "output", "username", "password", "branch"], ["save-manifest", "save-all-manifests"]);

        if (parsed == null || !NoPositionals(parsed, ""))
        {
            return ExitCodes.Failed;
        }

        var options = parsed.Options;

        if (!options.TryGetValue("appid", out var appText) || !uint.TryParse(appText, NumberStyles.None, CultureInfo.InvariantCulture, out var appID) || !options.TryGetValue("output", out var output))
        {
            Log.Error($"GameTracking mode needs --appid <id> and --output <dir>. {Help.UsageHint()}");
            return ExitCodes.Failed;
        }

        if (options.ContainsKey("password") && !options.ContainsKey("username"))
        {
            Log.Error($"--password needs --username. Without both, the last saved login is used. {Help.UsageHint()}");
            return ExitCodes.Failed;
        }

        return await TrackCommand.RunAsync(
            appID,
            output,
            options.GetValueOrDefault("username"),
            options.GetValueOrDefault("password"),
            options.GetValueOrDefault("branch") ?? "public",
            options.ContainsKey("save-manifest"),
            options.ContainsKey("save-all-manifests"),
            cancellationToken);
    }

    private static bool NoPositionals(ParsedArguments parsed, string command)
    {
        if (parsed.Positionals.Count == 0 && parsed.Patterns.Count == 0)
        {
            return true;
        }

        var argument = parsed.Positionals.Count > 0 ? parsed.Positionals[0] : "--";

        // Without a command, the argument was likely a misspelled command, or one after its options
        var hint = command.Length > 0 ? ""
            : Help.IsCommand(argument) ? $" The command goes first, before its options: {Help.ExeName} {argument} ..."
            : " Commands are get, ls, depots, dd and login.";

        Log.Error($"Unexpected argument \"{argument}\".{hint} {Help.UsageHint(command)}");
        return false;
    }
}
