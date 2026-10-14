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
/// Parses "--name value" or "--name=value" options and "--name" flags anywhere before "--", positional arguments,
/// and the patterns after "--". Option names ignore case.
/// </summary>
internal static class ArgumentParser
{
    // Options of each command: ones followed by a value, and flags
    internal static readonly Dictionary<string, (string[] Values, string[] Flags)> CommandOptions = new()
    {
        ["get"] = (["output", "branch", "username"], ["anonymous", "all-files"]),
        ["ls"] = (["output", "branch", "username"], ["anonymous"]),
        ["depots"] = (["branch", "username"], ["anonymous"]),
        ["login"] = (["username"], []),
        ["login list"] = ([], []),
        ["login remove"] = ([], []),
        ["track"] = (["appid", "output", "username", "password", "branch", "manifests"], ["anonymous", "save-manifest", "save-all-manifests"]),
        ["workshop info"] = (["limit", "username"], ["history", "anonymous"]),
        ["workshop search"] = (["limit", "username"], ["anonymous"]),
        ["workshop ls"] = (["output", "username"], ["anonymous"]),
        ["workshop get"] = (["output", "username"], ["anonymous", "all-files"]),
        ["config output"] = ([], ["reset"]),
    };

    /// <summary>
    /// The command with its subcommand, like "workshop info", when the arguments after it start with one.
    /// </summary>
    public static string GetCommandName(string command, string[] args) =>
        args.Length > 0 && CommandOptions.ContainsKey($"{command} {args[0]}") ? $"{command} {args[0]}" : command;

    /// <summary>
    /// The subcommands of a command, like info and search of workshop.
    /// </summary>
    public static string[] GetSubcommands(string command) => [.. CommandOptions.Keys
        .Where(k => k.StartsWith($"{command} ", StringComparison.Ordinal))
        .Select(k => k[(command.Length + 1)..])];

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

            // --name=value is the same as --name value
            var name = arg[2..];
            var equals = name.IndexOf('=', StringComparison.Ordinal);
            var inlineValue = equals > 0 ? name[(equals + 1)..] : null;

            if (inlineValue != null)
            {
                name = name[..equals];
            }

            if (flags.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                if (inlineValue != null)
                {
                    Log.Error($"--{name} is a flag and takes no value, got \"{arg}\". {usage}");
                    return null;
                }

                options[name] = "";
                continue;
            }

            if (!valueOptions.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                Log.Error($"Unknown option \"{arg}\".{DidYouMean(name, valueOptions.Concat(flags), "--")} {optionList} {usage}");
                return null;
            }

            string value;

            if (inlineValue is { Length: > 0 })
            {
                value = inlineValue;
            }
            else if (inlineValue != null || i + 1 >= optionArgs.Length || optionArgs[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                Log.Error($"--{name} needs a value. {usage}");
                return null;
            }
            else
            {
                value = optionArgs[++i];
            }

            // Keeping only one of them would silently drop the other
            if (!options.TryAdd(name, value))
            {
                Log.Error($"--{name} is given more than once. {usage}");
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
    /// The candidate that a mistyped name most likely meant, ignoring case: the only one it is a prefix of, or else the
    /// only one closest to it within a small edit distance. Null when there is none, or it is ambiguous.
    /// </summary>
    public static string? Suggest(string input, IEnumerable<string> candidates)
    {
        var list = candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (input.Length == 0)
        {
            return null;
        }

        var prefixed = list.Where(c => c.StartsWith(input, StringComparison.OrdinalIgnoreCase)).ToList();

        if (prefixed.Count > 0)
        {
            return prefixed.Count == 1 ? prefixed[0] : null;
        }

        // Short names are close to each other, so one edit is the most they can be off by
        var maxDistance = Math.Min(2, Math.Max(1, input.Length / 2));
        var lowerInput = input.ToLowerInvariant();
        var close = list
            .Select(c => (Candidate: c, Distance: EditDistance(lowerInput, c.ToLowerInvariant())))
            .Where(c => c.Distance <= maxDistance)
            .ToList();

        if (close.Count == 0)
        {
            return null;
        }

        var best = close.Min(static c => c.Distance);
        var closest = close.Where(c => c.Distance == best).ToList();

        return closest.Count == 1 ? closest[0].Candidate : null;
    }

    /// <summary>
    /// " Did you mean X?" with the suggestion for a mistyped name, or an empty string when there is none.
    /// </summary>
    public static string DidYouMean(string input, IEnumerable<string> candidates, string prefix = "") =>
        Suggest(input, candidates) is { } suggestion ? $" Did you mean {prefix}{suggestion}?" : "";

    /// <summary>
    /// The number of insertions, deletions, substitutions and swaps of adjacent characters that turn a into b.
    /// </summary>
    internal static int EditDistance(string a, string b)
    {
        // The distance between the first i characters of a and the first j of b is at [i * width + j]
        var width = b.Length + 1;
        var distances = new int[(a.Length + 1) * width];

        for (var i = 0; i <= a.Length; i++)
        {
            distances[i * width] = i;
        }

        for (var j = 0; j <= b.Length; j++)
        {
            distances[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                var distance = Math.Min(Math.Min(distances[((i - 1) * width) + j] + 1, distances[(i * width) + j - 1] + 1), distances[((i - 1) * width) + j - 1] + cost);

                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    distance = Math.Min(distance, distances[((i - 2) * width) + j - 2] + 1);
                }

                distances[(i * width) + j] = distance;
            }
        }

        return distances[^1];
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
