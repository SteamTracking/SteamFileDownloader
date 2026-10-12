using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace SteamFileDownloader;

// ManifestID null means the item's current manifest
internal sealed record WorkshopTarget(string Source, ulong ItemID, ulong? ManifestID);

/// <summary>
/// A parsed workshop command line. Limit is int.MaxValue for all.
/// </summary>
internal sealed record WorkshopArguments(
    string Command,
    DepotOptions Options,
    List<WorkshopTarget> Targets,
    List<string> Patterns,
    bool History = false,
    int Limit = WorkshopArgumentParser.DefaultLimit,
    uint SearchApp = 0,
    string SearchText = "");

/// <summary>
/// Parses the command lines of the workshop subcommands.
/// </summary>
internal static partial class WorkshopArgumentParser
{
    public const int DefaultLimit = 20;

    public static readonly string[] Subcommands = [.. ArgumentParser.CommandOptions.Keys
        .Where(static k => k.StartsWith("workshop ", StringComparison.Ordinal))
        .Select(static k => k["workshop ".Length..])];

    // Item pages, their tabs like changelog, and the Steam client's link to them. Other query parameters can come before or after the id
    [GeneratedRegex(@"^(?:(?:https?://)?(?:www\.)?steamcommunity\.com/(?:sharedfiles|workshop)/filedetails/(?:[a-z]+/(?<id>\d+)/?|\?(?:[^#]*&)?id=(?<id>\d+))|steam://url/CommunityFilePage/(?<id>\d+)/?)(?:[?&#].*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"^(?<id>\d+)(?::(?<manifest>\d+|latest))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TargetRegex();

    /// <summary>
    /// Parses "&lt;subcommand&gt; [options] ..." after "workshop". Returns null after printing an error.
    /// </summary>
    public static WorkshopArguments? Parse(string[] args)
    {
        if (args.Length == 0 || !Subcommands.Contains(args[0]))
        {
            Log.Error($"{(args.Length == 0 ? "workshop needs a subcommand" : $"Unknown workshop subcommand \"{args[0]}\"")}, expected {string.Join(", ", Subcommands)}. {Help.UsageHint("workshop")}");
            return null;
        }

        var command = $"workshop {args[0]}";
        var parsed = ArgumentParser.Parse(command, args[1..]);

        if (parsed == null)
        {
            return null;
        }

        var usage = Help.UsageHint(command);
        var takesPatterns = args[0] is "ls" or "get";

        if (!takesPatterns && parsed.Patterns.Count > 0)
        {
            Log.Error($"{command} takes no patterns after --. {usage}");
            return null;
        }

        if (!ArgumentParser.CheckPatterns(parsed, get: args[0] == "get",
            $"workshop get needs patterns after --, like: {Help.ExeName} workshop get 3070715607 -- \"*.vpk\". Use workshop ls to see an item's files, or --all-files to download every file."))
        {
            return null;
        }

        var history = parsed.Options.ContainsKey("history");
        var limit = DefaultLimit;

        if (parsed.Options.TryGetValue("limit", out var limitText))
        {
            if (args[0] == "info" && !history)
            {
                Log.Error($"--limit is for --history. {usage}");
                return null;
            }

            if (ParseLimit(limitText) is not { } parsedLimit)
            {
                Log.Error($"--limit needs a positive number or all, got \"{limitText}\".");
                return null;
            }

            limit = parsedLimit;
        }

        var options = DepotOptions.From(parsed, app: null);

        if (args[0] == "search")
        {
            if (parsed.Positionals.Count < 2 || !uint.TryParse(parsed.Positionals[0], NumberStyles.None, CultureInfo.InvariantCulture, out var app) || app == 0)
            {
                Log.Error($"workshop search needs an app id and text, like: {Help.ExeName} workshop search 730 de_dust2. {usage}");
                return null;
            }

            return new WorkshopArguments(command, options, [], [], Limit: limit, SearchApp: app, SearchText: string.Join(' ', parsed.Positionals.Skip(1)));
        }

        if (parsed.Positionals.Count == 0)
        {
            Log.Error($"No items, expected <item>..., like: {Help.ExeName} {command} 3070715607. {usage}");
            return null;
        }

        var targets = new List<WorkshopTarget>();

        foreach (var arg in parsed.Positionals)
        {
            if (ParseTarget(arg) is not { } target)
            {
                return null;
            }

            if (args[0] == "info" && target.ManifestID != null)
            {
                Log.Error($"workshop info takes items without a manifest, got \"{arg}\". Its versions are listed with --history.");
                return null;
            }

            targets.Add(target);
        }

        return new WorkshopArguments(command, options, targets, parsed.Patterns, history, limit);
    }

    // Returns null for anything but a positive number or all
    internal static int? ParseLimit(string text)
    {
        if (text.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return int.MaxValue;
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var limit) && limit > 0 ? limit : null;
    }

    // Returns null after printing an error
    internal static WorkshopTarget? ParseTarget(string arg)
    {
        var url = UrlRegex().Match(arg);

        if (url.Success)
        {
            return ulong.TryParse(url.Groups["id"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var urlID) && urlID != 0
                ? new WorkshopTarget(arg, urlID, null)
                : Invalid();
        }

        if (arg.Contains("://", StringComparison.Ordinal) || arg.Contains("steamcommunity.com", StringComparison.OrdinalIgnoreCase))
        {
            Log.Error($"Unrecognized workshop URL \"{arg}\", expected an item's page like https://steamcommunity.com/sharedfiles/filedetails/?id=3070715607, or give the item id.");
            return null;
        }

        var match = TargetRegex().Match(arg);

        if (!match.Success || !ulong.TryParse(match.Groups["id"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var itemID) || itemID == 0)
        {
            return Invalid();
        }

        var manifest = match.Groups["manifest"].Value;

        if (manifest.Length == 0 || manifest.Equals("latest", StringComparison.OrdinalIgnoreCase))
        {
            return new WorkshopTarget(arg, itemID, null);
        }

        return ulong.TryParse(manifest, NumberStyles.None, CultureInfo.InvariantCulture, out var manifestID) && manifestID != 0
            ? new WorkshopTarget(arg, itemID, manifestID)
            : Invalid();

        WorkshopTarget? Invalid()
        {
            Log.Error($"Invalid item \"{arg}\", expected <item>[:<manifest>|:latest] or an item's URL, like 3070715607 or 3070715607:<manifest>. Patterns go after --.");
            return null;
        }
    }
}
