using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SteamFileDownloader;

internal sealed record ParsedArguments(
    Dictionary<string, string> Options,
    List<string> Positionals,
    List<string> Patterns);

/// <summary>
/// Parses "--name value" options and "--name" flags anywhere before "--", positional arguments,
/// and the patterns after "--". Option names ignore case.
/// </summary>
internal static class ArgumentParser
{
    // Options of each command: ones followed by a value, and flags
    internal static readonly Dictionary<string, (string[] Values, string[] Flags)> CommandOptions = new()
    {
        ["get"] = (["output", "branch", "username"], ["anonymous", "all-files"]),
        ["ls"] = (["output", "branch", "username"], ["anonymous"]),
        ["depots"] = (["branch", "username"], ["anonymous", "json"]),
        ["login"] = (["username"], []),
        ["track"] = (["appid", "output", "username", "password", "branch", "manifests"], ["save-manifest", "save-all-manifests"]),
        ["workshop info"] = (["limit", "username"], ["history", "json", "anonymous"]),
        ["workshop search"] = (["limit", "username"], ["anonymous"]),
        ["workshop ls"] = (["output", "username"], ["anonymous"]),
        ["workshop get"] = (["output", "username"], ["anonymous", "all-files"]),
    };

    public static ParsedArguments? Parse(string command, string[] args)
    {
        var (valueOptions, flags) = CommandOptions[command];
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var positionals = new List<string>();
        var (optionArgs, patterns) = SplitPatterns(args);
        var usage = Help.UsageHint(command);
        var known = valueOptions.Select(static o => $"--{o} <value>").Concat(flags.Select(static f => $"--{f}")).ToList();
        var optionList = known.Count > 0 ? $"Options are {string.Join(", ", known)}." : $"{command} takes no options.";

        for (var i = 0; i < optionArgs.Length; i++)
        {
            var arg = optionArgs[i];

            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                // Options are all double-dash, so a single dash is a mistyped option rather than a target
                if (arg.Length > 1 && arg[0] == '-')
                {
                    Log.Error($"Unknown option \"{arg}\"; options start with --. {optionList} {usage}");
                    return null;
                }

                positionals.Add(arg);
                continue;
            }

            var name = arg[2..];
            var equals = name.IndexOf('=', StringComparison.Ordinal);

            if (equals > 0 && valueOptions.Contains(name[..equals], StringComparer.OrdinalIgnoreCase))
            {
                Log.Error($"\"{arg}\": give the value after a space, like --{name[..equals]} {name[(equals + 1)..]}. {usage}");
                return null;
            }

            if (flags.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                options[name] = "";
            }
            else if (!valueOptions.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                Log.Error($"Unknown option \"{arg}\". {optionList} {usage}");
                return null;
            }
            else if (i + 1 >= optionArgs.Length || optionArgs[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                Log.Error($"{arg} needs a value. {usage}");
                return null;
            }
            else if (!options.TryAdd(name, optionArgs[++i]))
            {
                // Keeping only one of them would silently drop the other
                Log.Error($"{arg} is given more than once. {usage}");
                return null;
            }
        }

        if (options.ContainsKey("anonymous") && options.ContainsKey("username"))
        {
            Log.Error($"--anonymous and --username can't be combined. {usage}");
            return null;
        }

        if (options.TryGetValue("output", out var output) && !CheckOutputPath(output, "--output"))
        {
            return null;
        }

        return new ParsedArguments(options, positionals, patterns);
    }

    /// <summary>
    /// Checks what follows "--": only patterns, and for a get command patterns or --all-files but not both, explained
    /// by noPatternsError when neither is given. Returns false after printing an error.
    /// </summary>
    public static bool CheckPatterns(ParsedArguments parsed, bool get, string noPatternsError)
    {
        var misplacedOption = parsed.Patterns.FirstOrDefault(static p => p.StartsWith("--", StringComparison.Ordinal));

        if (misplacedOption != null)
        {
            Log.Error($"\"{misplacedOption}\" is after --, which is only for patterns. Put options before --.");
            return false;
        }

        var allFiles = parsed.Options.ContainsKey("all-files");

        // Whole depots can be tens of gigabytes, so downloading every file has to be asked for
        if (get && parsed.Patterns.Count == 0 && !allFiles)
        {
            Log.Error(noPatternsError);
            return false;
        }

        if (get && parsed.Patterns.Count > 0 && allFiles)
        {
            Log.Error("--all-files downloads every file, so it can't be combined with patterns.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Rejects patterns, for commands that take none. Returns false after printing an error.
    /// </summary>
    public static bool NoPatterns(ParsedArguments parsed, string command)
    {
        if (parsed.Patterns.Count == 0)
        {
            return true;
        }

        Log.Error($"{command} takes no patterns after --. {Help.UsageHint(command)}");
        return false;
    }

    /// <summary>
    /// Rejects positional arguments and patterns, for commands that only take options. Returns false after printing an error.
    /// </summary>
    public static bool NoPositionals(ParsedArguments parsed, string command)
    {
        if (parsed.Positionals.Count == 0 && parsed.Patterns.Count == 0)
        {
            return true;
        }

        var argument = parsed.Positionals.Count > 0 ? parsed.Positionals[0] : "--";

        Log.Error($"Unexpected argument \"{argument}\". {Help.UsageHint(command)}");
        return false;
    }

    /// <summary>
    /// Rejects paths like "D:foo" or "\foo", which are relative to a drive's current folder or root. Shells like bash turn
    /// an unquoted D:\foo\bar into D:foobar, which would silently write somewhere else. Returns false after printing an error.
    /// </summary>
    public static bool CheckOutputPath(string path, string option)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            Log.Error($"{option} needs a folder, got an empty one.");
            return false;
        }

        if (!Path.IsPathRooted(path) || Path.IsPathFullyQualified(path))
        {
            return true;
        }

        Log.Error($"{option} \"{path}\" is relative to the current folder or root of a drive, likely a path whose backslashes the shell removed. Use forward slashes or quotes, like D:/SteamDepots.");
        return false;
    }

    /// <summary>
    /// Splits arguments at the first "--" into options and targets before it, and patterns after it.
    /// </summary>
    public static (string[] Options, List<string> Patterns) SplitPatterns(string[] args)
    {
        var patternsStart = Array.IndexOf(args, "--");

        return patternsStart < 0 ? (args, []) : (args[..patternsStart], [.. args[(patternsStart + 1)..]]);
    }
}
