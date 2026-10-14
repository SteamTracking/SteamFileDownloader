using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SteamFileDownloader;

internal static class Program
{
    public static string Version { get; } = (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "").Split('+')[0];

    private static readonly string[] Commands = ["get", "ls", "depots", "workshop", "dd", "login", "config", "track"];

    private static async Task<int> Main(string[] args)
    {
        if (Help.TryPrint(args) is { } helpExitCode)
        {
            return helpExitCode;
        }

        if (args[0] == "--version")
        {
            Console.WriteLine(Version);
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
                "config" => RunConfig(args[1..]),
                "track" => await RunTrackAsync(args[1..], cts.Token),
                "workshop" => await RunWorkshopAsync(args[1..], cts.Token),
                _ => UnknownCommand(args[0]),
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

        var arguments = command == "dd" ? DepotDownloaderArguments.Parse(args) : DepotArgumentParser.ParseArguments(command, args);

        if (arguments == null)
        {
            return ExitCodes.ForUsageError(command);
        }

        return arguments.Command switch
        {
            "get" or "ls" => await GetCommand.RunAsync(arguments, cancellationToken),
            "workshop get" or "workshop ls" => await WorkshopCommands.RunAsync(
                new WorkshopArguments(arguments.Command, arguments.Options, [.. arguments.Targets.Select(static t => new WorkshopTarget(t, ulong.Parse(t, CultureInfo.InvariantCulture), null))], arguments.Patterns),
                cancellationToken),
            _ => await DepotsCommand.ListDepotsAsync(arguments, cancellationToken),
        };
    }

    private static async Task<int> RunWorkshopAsync(string[] args, CancellationToken cancellationToken)
    {
        // stdout carries the results
        Log.UseStdoutForResults();

        var arguments = WorkshopArgumentParser.Parse(args);

        if (arguments == null)
        {
            return ExitCodes.ForUsageError("workshop");
        }

        return arguments.Command switch
        {
            "workshop search" => await WorkshopCommands.SearchAsync(arguments, cancellationToken),
            "workshop ls" or "workshop get" => await WorkshopCommands.RunAsync(arguments, cancellationToken),
            _ => await WorkshopCommands.InfoAsync(arguments, cancellationToken),
        };
    }

    private static async Task<int> RunLoginAsync(string[] args, CancellationToken cancellationToken)
    {
        var arguments = LoginCommand.ParseArguments(args);

        if (arguments == null)
        {
            return ExitCodes.ForUsageError("login");
        }

        return arguments.Command switch
        {
            "login list" => LoginCommand.List(),
            "login remove" => await LoginCommand.RemoveAsync(arguments.Usernames, cancellationToken),
            _ => await LoginCommand.RunAsync(arguments.Username, cancellationToken),
        };
    }

    private static int RunConfig(string[] args)
    {
        // stdout carries the saved value
        Log.UseStdoutForResults();

        var arguments = ConfigCommand.ParseArguments(args);

        return arguments == null ? ExitCodes.ForUsageError("config") : ConfigCommand.Run(arguments);
    }

    // The GameTracking command, which downloads the files in files.json
    private static async Task<int> RunTrackAsync(string[] args, CancellationToken cancellationToken)
    {
        var arguments = TrackCommand.ParseArguments(args);

        return arguments == null ? ExitCodes.ForUsageError("track") : await TrackCommand.RunAsync(arguments, cancellationToken);
    }

    internal static int UnknownCommand(string argument)
    {
        string hint;

        // GameTracking mode used to be the command line without a command name
        if (argument.StartsWith("--", StringComparison.Ordinal))
        {
            var option = ArgumentParser.Suggest(argument[2..], ["help", "version"]);

            hint = option != null
                ? $" Did you mean --{option}?"
                : $" The command goes first, before its options. Downloading the files in files.json is now: {Help.ExeName} track <app> --output <dir> ...";
        }
        else
        {
            hint = $"{ArgumentParser.DidYouMean(argument, [.. Commands, "help"])} Commands are {string.Join(", ", Commands[..^1])} and {Commands[^1]}.";
        }

        Log.Error($"Unknown command \"{argument}\".{hint} {Help.UsageHint()}");
        return ExitCodes.UsageError;
    }
}
