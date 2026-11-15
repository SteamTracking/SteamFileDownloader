using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace SteamFileDownloader;

internal sealed record ParsedArguments(
    Dictionary<string, string> Options,
    List<string> Positionals,
    List<string> Patterns,
    InstallOptions Install);

/// <summary>
/// Parses "--name value" or "--name=value" options and "--name" flags anywhere before "--", positional arguments,
/// and the patterns after "--". Option names ignore case.
/// </summary>
internal static class ArgumentParser
{
    // Options that can be given more than once
    private static readonly string[] RepeatableOptions = ["include", "exclude"];

    /// <summary>
    /// The exception for a command that only groups subcommands, run without one or with one it doesn't have.
    /// </summary>
    public static UsageException UnknownSubcommand(string command, string noun, string[] args, string[] subcommands)
    {
        var expected = string.Join(", ", subcommands);
        var usage = Help.UsageHint(command);

        if (args.Length == 0)
        {
            return new UsageException($"{command} needs a {noun}, expected {expected}. {usage}");
        }

        return new UsageException($"Unknown {command} {noun} \"{args[0]}\".{DidYouMean(args[0], subcommands)} Expected {expected}. {usage}");
    }

    public static ParsedArguments Parse(string command, string[] args)
    {
        var (valueOptions, flags) = CommandTable.GetOptions(command);
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
                    throw new UsageException($"Unknown option \"{arg}\"; options start with --. {optionList} {usage}");
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
                    throw new UsageException($"--{name} is a flag and takes no value, got \"{arg}\". {usage}");
                }

                options[name] = "";
                continue;
            }

            if (!valueOptions.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                throw new UsageException($"Unknown option \"{arg}\".{DidYouMean(name, valueOptions.Concat(flags), "--")} {optionList} {usage}");
            }

            var nextIsValue = i + 1 < optionArgs.Length && !optionArgs[i + 1].StartsWith("--", StringComparison.Ordinal);
            string value;

            if (!string.IsNullOrEmpty(inlineValue))
            {
                value = inlineValue;
            }
            else if (inlineValue != null || !nextIsValue)
            {
                throw new UsageException($"--{name} needs a value. {usage}");
            }
            else
            {
                value = optionArgs[++i];
            }

            if (RepeatableOptions.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                AddIds(options, name, value, usage);
                continue;
            }

            // Keeping only one of them would silently drop the other
            if (!options.TryAdd(name, value))
            {
                throw new UsageException($"--{name} is given more than once. {usage}");
            }
        }

        var loginError = LoginOptions.From(options).Validate("--");

        if (loginError != null)
        {
            throw new UsageException($"{loginError} {usage}");
        }

        if (options.TryGetValue("output", out var output))
        {
            CheckOutputPath(output, "--output");
        }

        if (options.TryGetValue("dir", out var dir))
        {
            CheckOutputPath(dir, "--dir");
        }

        var install = InstallOptions.From(options);
        var installError = install.Validate("--");

        if (installError != null)
        {
            throw new UsageException($"{installError} {usage}");
        }

        return new ParsedArguments(options, positionals, patterns, install);
    }

    // Adds depot or app ids to an option that can be given several times, which keeps them as a comma separated list
    private static void AddIds(Dictionary<string, string> options, string name, string value, string usage)
    {
        InstallOptions.ParseIds(value, out var invalid);

        if (invalid != null)
        {
            throw new UsageException($"--{name} needs a depot id or DLC app id, got \"{invalid}\". {usage}");
        }

        if (options.TryGetValue(name, out var existing))
        {
            value = $"{existing},{value}";
        }

        options[name] = value;
    }

    /// <summary>
    /// Checks that only patterns follow "--".
    /// </summary>
    public static void CheckPatterns(ParsedArguments parsed)
    {
        var misplacedOption = parsed.Patterns.FirstOrDefault(static p => p.StartsWith("--", StringComparison.Ordinal));

        if (misplacedOption != null)
        {
            throw new UsageException($"\"{misplacedOption}\" is after --, which is only for patterns. Put options before --.");
        }
    }

    /// <summary>
    /// Rejects patterns, for commands that take none.
    /// </summary>
    public static void NoPatterns(ParsedArguments parsed, string command)
    {
        if (parsed.Patterns.Count > 0)
        {
            throw new UsageException($"{command} takes no patterns after --. {Help.UsageHint(command)}");
        }
    }

    /// <summary>
    /// Rejects positional arguments and patterns, for commands that only take options.
    /// </summary>
    public static void NoPositionals(ParsedArguments parsed, string command)
    {
        if (parsed.Positionals.Count == 0 && parsed.Patterns.Count == 0)
        {
            return;
        }

        var argument = parsed.Positionals.Count > 0 ? parsed.Positionals[0] : "--";

        throw new UsageException($"Unexpected argument \"{argument}\". {Help.UsageHint(command)}");
    }

    /// <summary>
    /// Rejects paths like "D:foo" or "\foo", which are relative to a drive's current folder or root. Shells like bash turn
    /// an unquoted D:\foo\bar into D:foobar, which would silently write somewhere else.
    /// </summary>
    public static void CheckOutputPath(string path, string option)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new UsageException($"{option} needs a folder, got an empty one.");
        }

        if (Path.IsPathRooted(path) && !Path.IsPathFullyQualified(path))
        {
            throw new UsageException($"{option} \"{path}\" is relative to the current folder or root of a drive, likely a path whose backslashes the shell removed. Use forward slashes or quotes, like D:/SteamDepots.");
        }
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
        string? closest = null;
        var closestDistance = int.MaxValue;
        var closestCount = 0;

        foreach (var candidate in list)
        {
            var distance = EditDistance(lowerInput, candidate.ToLowerInvariant());

            if (distance > maxDistance)
            {
                continue;
            }

            if (distance < closestDistance)
            {
                closest = candidate;
                closestDistance = distance;
                closestCount = 1;
            }
            else if (distance == closestDistance)
            {
                closestCount++;
            }
        }

        // Several candidates at the closest distance are ambiguous
        return closestCount == 1 ? closest : null;
    }

    /// <summary>
    /// " Did you mean X?" with the suggestion for a mistyped name, or an empty string when there is none.
    /// </summary>
    public static string DidYouMean(string input, IEnumerable<string> candidates, string prefix = "")
    {
        var suggestion = Suggest(input, candidates);

        return suggestion != null ? $" Did you mean {prefix}{suggestion}?" : "";
    }

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
                var deletion = distances[((i - 1) * width) + j] + 1;
                var insertion = distances[(i * width) + j - 1] + 1;
                var substitution = distances[((i - 1) * width) + j - 1] + cost;
                var distance = Math.Min(Math.Min(deletion, insertion), substitution);

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

        if (patternsStart < 0)
        {
            return (args, []);
        }

        return (args[..patternsStart], [.. args[(patternsStart + 1)..]]);
    }
}
