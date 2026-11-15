using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SteamFileDownloader;

/// <summary>
/// What a command does when run without arguments after its name.
/// </summary>
internal enum BareInvocation
{
    // The command needs no arguments, so it runs
    Run,

    // Prints the usage to stderr and exits with the command's usage error exit code
    PrintUsage,

    // Prints the whole help to stdout and exits with success
    PrintHelp,
}

/// <summary>
/// The options that <see cref="ArgumentParser"/> accepts for a command: ones followed by a value, and flags.
/// </summary>
internal sealed record CommandOptions(string[] Values, string[] Flags);

/// <summary>
/// A parsed command line, ready to run.
/// </summary>
internal delegate Task<int> Invocation(CancellationToken cancellationToken);

/// <summary>
/// A command or subcommand with what the parser, the help and the exit codes need to know about it.
/// </summary>
internal sealed record CommandSpec
{
    // The command, followed by the subcommand when it is one, like "workshop info"
    public required string Name { get; init; }

    public required Func<string> GetHelp { get; init; }

    // Null for a command that does not use ArgumentParser, like dd and commands that only group subcommands
    public CommandOptions? Options { get; init; }

    public BareInvocation Bare { get; init; } = BareInvocation.PrintUsage;

    // Whether stdout carries the results, which moves the log lines to stderr
    public bool ResultsOnStdout { get; init; } = true;

    public int UsageErrorExitCode { get; init; } = ExitCodes.UsageError;

    // Parses the arguments after the command's name; null for a subcommand, which its command parses
    public Func<string, string[], Invocation>? Bind { get; init; }
}

/// <summary>
/// Every command and subcommand of the command line, in the order that the help and the errors list them.
/// </summary>
internal static class CommandTable
{
    internal static readonly CommandSpec[] All =
    [
        new()
        {
            Name = "get",
            GetHelp = static () => Help.Get,
            Options = new(["output", "branch", "branch-password", "username", "password"], ["anonymous"]),
            Bind = BindDepot,
        },
        new()
        {
            Name = "download",
            GetHelp = static () => Help.Download,
            Options = new(
                ["output", "dir", "branch", "branch-password", "os", "osarch", "oscpu", "language", "include", "exclude", "username", "password"],
                ["lowviolence", "steamdeck", "all-platforms", "all-languages", "anonymous"]),
            Bind = BindDepot,
        },
        new()
        {
            Name = "ls",
            GetHelp = static () => Help.Ls,
            Options = new(["output", "branch", "branch-password", "username", "password"], ["anonymous"]),
            Bind = BindDepot,
        },
        new()
        {
            Name = "depots",
            GetHelp = static () => Help.Depots,
            Options = new(["branch", "branch-password", "username", "password"], ["anonymous"]),
            Bind = BindDepot,
        },
        new()
        {
            Name = "workshop",
            GetHelp = static () => Help.Workshop,
            Bare = BareInvocation.PrintHelp,
            Bind = static (_, args) =>
            {
                var arguments = WorkshopArgumentParser.Parse(args);

                return cancellationToken => RunWorkshopAsync(arguments, cancellationToken);
            },
        },
        new()
        {
            Name = "workshop info",
            GetHelp = static () => Help.WorkshopInfo,
            Options = new(["limit", "username", "password"], ["history", "anonymous"]),
        },
        new()
        {
            Name = "workshop search",
            GetHelp = static () => Help.WorkshopSearch,
            Options = new(["limit", "username", "password"], ["anonymous"]),
        },
        new()
        {
            Name = "workshop ls",
            GetHelp = static () => Help.WorkshopLs,
            Options = new(["output", "username", "password"], ["anonymous"]),
        },
        new()
        {
            Name = "workshop get",
            GetHelp = static () => Help.WorkshopGet,
            Options = new(["output", "username", "password"], ["anonymous"]),
        },
        new()
        {
            Name = "dd",
            GetHelp = static () => Help.DepotDownloader,
            Bind = static (_, args) =>
            {
                var arguments = DepotDownloaderArguments.Parse(args);

                return cancellationToken => RunDepotAsync(arguments, cancellationToken);
            },
        },
        new()
        {
            Name = "login",
            GetHelp = static () => Help.Login,
            Options = new(["username", "password"], []),
            Bare = BareInvocation.Run,
            ResultsOnStdout = false,
            Bind = static (_, args) =>
            {
                var arguments = LoginCommand.ParseArguments(args);

                return cancellationToken => RunLoginAsync(arguments, cancellationToken);
            },
        },
        new()
        {
            Name = "login list",
            GetHelp = static () => Help.LoginList,
            Options = new([], []),
            Bare = BareInvocation.Run,
        },
        new()
        {
            Name = "login remove",
            GetHelp = static () => Help.LoginRemove,
            Options = new([], []),
            Bare = BareInvocation.PrintHelp,
        },
        new()
        {
            Name = "config",
            GetHelp = static () => Help.Config,
            Bare = BareInvocation.PrintHelp,
            Bind = static (_, args) =>
            {
                var arguments = ConfigCommand.ParseArguments(args);

                return _ => Task.FromResult(ConfigCommand.Run(arguments));
            },
        },
        new()
        {
            Name = "config output",
            GetHelp = static () => Help.Config,
            Options = new([], ["reset"]),
            Bare = BareInvocation.Run,
        },
        new()
        {
            Name = "track",
            GetHelp = static () => Help.Track,
            Options = new(
                ["appid", "output", "username", "password", "branch", "branch-password", "manifests"],
                ["anonymous", "save-manifest", "save-all-manifests"]),
            ResultsOnStdout = false,
            UsageErrorExitCode = ExitCodes.Failed,
            Bind = static (_, args) =>
            {
                var arguments = TrackCommand.ParseArguments(args);

                return cancellationToken => TrackCommand.RunAsync(arguments, cancellationToken);
            },
        },
    ];

    /// <summary>
    /// The commands that start a command line, without their subcommands.
    /// </summary>
    public static readonly string[] CommandNames = [.. All.Where(static c => c.Bind != null).Select(static c => c.Name)];

    public static CommandSpec? Find(string name)
    {
        foreach (var command in All)
        {
            if (command.Name == name)
            {
                return command;
            }
        }

        return null;
    }

    /// <summary>
    /// The command with its subcommand, like "workshop info", when the arguments after it start with one.
    /// </summary>
    public static string GetCommandName(string command, string[] args)
    {
        if (args.Length == 0)
        {
            return command;
        }

        var subcommand = $"{command} {args[0]}";

        return Find(subcommand) != null ? subcommand : command;
    }

    /// <summary>
    /// The command line's command or subcommand, or null for a command that does not exist.
    /// </summary>
    public static CommandSpec? Resolve(string[] args)
    {
        if (args.Length == 0)
        {
            return null;
        }

        return Find(GetCommandName(args[0], args[1..]));
    }

    /// <summary>
    /// The subcommands of a command, like info and search of workshop.
    /// </summary>
    public static string[] GetSubcommands(string command) => [.. All
        .Where(c => c.Name.StartsWith($"{command} ", StringComparison.Ordinal))
        .Select(c => c.Name[(command.Length + 1)..])];

    /// <summary>
    /// The options of a command that ArgumentParser parses.
    /// </summary>
    public static CommandOptions GetOptions(string command)
    {
        var options = Find(command)?.Options;

        return options ?? throw new ArgumentException($"{command} has no options to parse.", nameof(command));
    }

    private static Invocation BindDepot(string command, string[] args)
    {
        var arguments = DepotArgumentParser.ParseArguments(command, args);

        return cancellationToken => RunDepotAsync(arguments, cancellationToken);
    }

    // dd translates its arguments into get, download or workshop commands, so every depot command ends up here
    private static async Task<int> RunDepotAsync(DepotArguments arguments, CancellationToken cancellationToken)
    {
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
            "download" => await DownloadCommand.RunAsync(arguments, cancellationToken),
            _ => await DepotsCommand.ListDepotsAsync(arguments, cancellationToken),
        };
    }

    private static async Task<int> RunWorkshopAsync(WorkshopArguments arguments, CancellationToken cancellationToken)
    {
        return arguments.Command switch
        {
            "workshop search" => await WorkshopCommands.SearchAsync(arguments, cancellationToken),
            "workshop ls" or "workshop get" => await WorkshopCommands.RunAsync(arguments, cancellationToken),
            _ => await WorkshopCommands.InfoAsync(arguments, cancellationToken),
        };
    }

    private static async Task<int> RunLoginAsync(LoginArguments arguments, CancellationToken cancellationToken)
    {
        return arguments.Command switch
        {
            "login list" => LoginCommand.List(),
            "login remove" => await LoginCommand.RemoveAsync(arguments.Usernames, cancellationToken),
            _ => await LoginCommand.RunAsync(arguments.Login, cancellationToken),
        };
    }
}
