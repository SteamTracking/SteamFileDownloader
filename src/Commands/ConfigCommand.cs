using System;
using System.IO;
using System.Linq;

namespace SteamFileDownloader;

/// <summary>
/// A parsed config output command line: the folder to save if any, and whether to clear the saved one.
/// </summary>
internal sealed record ConfigArguments(string? Value, bool Reset);

/// <summary>
/// The config command, which saves settings for later runs.
/// </summary>
internal static class ConfigCommand
{
    /// <summary>
    /// Parses "&lt;setting&gt; [value | --reset]" after "config".
    /// </summary>
    public static ConfigArguments ParseArguments(string[] args)
    {
        var command = CommandTable.GetCommandName("config", args);

        if (command == "config")
        {
            throw ArgumentParser.UnknownSubcommand("config", "setting", args, CommandTable.GetSubcommands("config"));
        }

        var parsed = ArgumentParser.Parse(command, args[1..]);

        if (parsed.Positionals.Count > 1 || parsed.Patterns.Count > 0)
        {
            var unexpected = parsed.Positionals.Count > 1 ? parsed.Positionals[1] : "--";

            throw new UsageException($"Unexpected argument \"{unexpected}\"; {command} takes one folder, quoted when it has spaces. {Help.UsageHint(command)}");
        }

        var value = parsed.Positionals.FirstOrDefault();
        var reset = parsed.Options.ContainsKey("reset");

        if (reset && value != null)
        {
            throw new UsageException($"--reset clears the saved folder, so it can't be combined with one. {Help.UsageHint(command)}");
        }

        if (value != null)
        {
            ArgumentParser.CheckOutputPath(value, command);
        }

        return new ConfigArguments(value, reset);
    }

    /// <summary>
    /// Prints, saves or clears the saved output folder. The folder is saved as an absolute path, so it doesn't depend
    /// on the folder later runs start in.
    /// </summary>
    public static int Run(ConfigArguments arguments, string? path = null)
    {
        var settings = Settings.TryRead(path);

        if (settings == null)
        {
            return ExitCodes.Failed;
        }

        if (arguments.Value == null && !arguments.Reset)
        {
            if (settings.Output == null)
            {
                Log.Info($"No output folder is saved, so files go to depots in the current folder. Save one with: {Help.ExeName} config output <dir>");
            }
            else
            {
                Log.Result(settings.Output);
            }

            return ExitCodes.Ok;
        }

        var previous = settings.Output;
        settings.Output = arguments.Value != null ? Path.GetFullPath(arguments.Value) : null;

        try
        {
            Settings.Write(settings, path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Failed to save {path ?? Settings.FilePath}: {e.Message}");
            return ExitCodes.Failed;
        }

        if (settings.Output != null)
        {
            Log.Info($"Saved the output folder: {settings.Output}. --output still overrides it.");
        }
        else if (previous != null)
        {
            Log.Info($"Cleared the saved output folder {previous}, so files go to depots in the current folder.");
        }
        else
        {
            Log.Info("No output folder was saved, so files already go to depots in the current folder.");
        }

        return ExitCodes.Ok;
    }
}
