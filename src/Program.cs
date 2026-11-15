using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SteamFileDownloader;

internal static partial class Program
{
    private static readonly (string Version, string? Commit) VersionInfo = ParseVersion(
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "");

    public static string Version => VersionInfo.Version;

    public static string? Commit => VersionInfo.Commit;

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
            // A double click in Explorer opens a console that closes as soon as the help is printed
            if (args.Length == 0 && OperatingSystem.IsWindows() && IsOnlyProcessOnConsole())
            {
                ShowRunFromTerminalMessage();
            }

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
            return await RunCommandAsync(args, cts.Token);
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

    private static async Task<int> RunCommandAsync(string[] args, CancellationToken cancellationToken)
    {
        var command = CommandTable.Find(args[0]);

        if (command?.Bind == null)
        {
            return UnknownCommand(args[0]);
        }

        if (command.ResultsOnStdout)
        {
            Log.UseStdoutForResults();
        }

        Invocation invocation;

        try
        {
            invocation = command.Bind(command.Name, args[1..]);
        }
        catch (UsageException e)
        {
            Log.Error(e.Message);
            return command.UsageErrorExitCode;
        }

        return await invocation(cancellationToken);
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
            var commands = CommandTable.CommandNames;
            var didYouMean = ArgumentParser.DidYouMean(argument, [.. commands, "version", "help"]);

            hint = $"{didYouMean} Commands are {string.Join(", ", commands[..^1])} and {commands[^1]}.";
        }

        Log.Error($"Unknown command \"{argument}\".{hint} {Help.UsageHint()}");
        return ExitCodes.UsageError;
    }

    // A shell that started the process shares its console, so the list has at least two processes
    private static bool IsOnlyProcessOnConsole()
    {
        var processIds = new uint[2];

        return GetConsoleProcessList(processIds, (uint)processIds.Length) == 1;
    }

    private static void ShowRunFromTerminalMessage()
    {
        const uint MessageBoxIconInformation = 0x40;

        var text = $"{nameof(SteamFileDownloader)} is a command-line program. Open a terminal and run it from there, like:\n\n{Help.ExeName} --help";

        _ = MessageBox(0, text, nameof(SteamFileDownloader), MessageBoxIconInformation);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetConsoleProcessList([Out] uint[] processList, uint processCount);

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int MessageBox(nint window, string text, string caption, uint type);
}
