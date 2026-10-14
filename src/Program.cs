using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SteamFileDownloader;

internal static class Program
{
    private static readonly (string Version, string? Commit) VersionInfo = ParseVersion(
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "");

    public static string Version => VersionInfo.Version;

    public static string? Commit => VersionInfo.Commit;

    private static readonly string[] Commands = ["get", "ls", "depots", "workshop", "dd", "login", "config", "track"];

    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] is "-V" or "--version" or "version")
        {
            Console.WriteLine($"{nameof(SteamFileDownloader)} {FormatVersion(Version, Commit)}");
            return ExitCodes.Ok;
        }

        var helpExitCode = Help.TryPrint(args);

        if (helpExitCode != null)
        {
            return helpExitCode.Value;
        }

        using var cts = new CancellationTokenSource();

        // The first Ctrl+C, or SIGTERM from kill, docker stop or systemd, stops cleanly, which deletes partial files and
        // writes out buffered results; a second one kills the process. Returns whether the process keeps running
        bool RequestStop()
        {
            var isFirst = !cts.IsCancellationRequested;
            cts.Cancel();
            return isFirst;
        }

        Console.CancelKeyPress += (_, e) => e.Cancel = RequestStop();

        using var terminateRegistration = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => context.Cancel = RequestStop());

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
            Log.Error($"Unexpected error, this is a bug in {Help.ExeName} {FormatVersion(Version, Commit)}, please report it at {Help.IssuesUrl}: {e}");
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

        if (arguments.Command is "workshop get" or "workshop ls")
        {
            var items = arguments.Targets
                .Select(static t => new WorkshopTarget(t, ulong.Parse(t, CultureInfo.InvariantCulture), null))
                .ToList();
            var workshopArguments = new WorkshopArguments(arguments.Command, arguments.Options, items, arguments.Patterns);

            return await WorkshopCommands.RunAsync(workshopArguments, cancellationToken);
        }

        return arguments.Command switch
        {
            "get" or "ls" => await GetCommand.RunAsync(arguments, cancellationToken),
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

    /// <summary>
    /// The version and the short hash of the build's commit, which the SDK appends to the informational version after a +.
    /// </summary>
    internal static (string Version, string? Commit) ParseVersion(string informationalVersion)
    {
        var parts = informationalVersion.Split('+', 2);

        if (parts.Length == 1 || parts[1].Length == 0)
        {
            return (parts[0], null);
        }

        var commit = parts[1];

        return (parts[0], commit[..Math.Min(7, commit.Length)]);
    }

    internal static string FormatVersion(string version, string? commit) => commit != null ? $"{version} ({commit})" : version;

    internal static int UnknownCommand(string argument)
    {
        string hint;

        // GameTracking mode used to be the command line without a command name
        if (argument.StartsWith("--", StringComparison.Ordinal))
        {
            hint = ArgumentParser.DidYouMean(argument[2..], ["help", "version"], "--");

            if (hint.Length == 0)
            {
                hint = $" The command goes first, before its options. Downloading the files in files.json is now: {Help.ExeName} track <app> --output <dir> ...";
            }
        }
        else
        {
            var didYouMean = ArgumentParser.DidYouMean(argument, [.. Commands, "version", "help"]);

            hint = $"{didYouMean} Commands are {string.Join(", ", Commands[..^1])} and {Commands[^1]}.";
        }

        Log.Error($"Unknown command \"{argument}\".{hint} {Help.UsageHint()}");
        return ExitCodes.UsageError;
    }
}
