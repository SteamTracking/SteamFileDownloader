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
    /// Parses "&lt;setting&gt; [value | --reset]" after "config". Returns null after printing an error.
    /// </summary>
    public static ConfigArguments? ParseArguments(string[] args)
    {
        var command = ArgumentParser.GetCommandName("config", args);

        if (command == "config")
        {
            ArgumentParser.ReportUnknownSubcommand("config", "setting", args, ArgumentParser.GetSubcommands("config"));
            return null;
        }

        var parsed = ArgumentParser.Parse(command, args[1..]);

        if (parsed == null)
        {
            return null;
        }

        if (parsed.Positionals.Count > 1 || parsed.Patterns.Count > 0)
        {
            var unexpected = parsed.Positionals.Count > 1 ? parsed.Positionals[1] : "--";

            Log.Error($"Unexpected argument \"{unexpected}\"; {command} takes one folder, quoted when it has spaces. {Help.UsageHint(command)}");
            return null;
        }

        var value = parsed.Positionals.FirstOrDefault();
        var reset = parsed.Options.ContainsKey("reset");

        if (reset && value != null)
        {
            Log.Error($"--reset clears the saved folder, so it can't be combined with one. {Help.UsageHint(command)}");
            return null;
        }

        if (value != null && !ArgumentParser.CheckOutputPath(value, command))
        {
            return null;
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
