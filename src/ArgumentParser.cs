using System;
using System.Collections.Generic;
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
    public static ParsedArguments? Parse(
        string command,
        string[] args,
        IReadOnlyCollection<string> valueOptions,
        IReadOnlyCollection<string> flags)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var positionals = new List<string>();
        var (optionArgs, patterns) = SplitPatterns(args);
        var usage = Help.UsageHint(command);

        for (var i = 0; i < optionArgs.Length; i++)
        {
            var arg = optionArgs[i];

            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                positionals.Add(arg);
                continue;
            }

            var name = arg[2..];

            if (flags.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                options[name] = "";
            }
            else if (!valueOptions.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                var known = valueOptions.Select(static o => $"--{o} <value>").Concat(flags.Select(static f => $"--{f}"));
                Log.Error($"Unknown option \"{arg}\". Options are {string.Join(", ", known)}. {usage}");
                return null;
            }
            else if (i + 1 >= optionArgs.Length)
            {
                Log.Error($"{arg} needs a value. {usage}");
                return null;
            }
            else
            {
                options[name] = optionArgs[++i];
            }
        }

        return new ParsedArguments(options, positionals, patterns);
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
