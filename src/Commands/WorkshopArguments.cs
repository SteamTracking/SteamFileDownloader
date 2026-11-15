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

    public static readonly string[] Subcommands = CommandTable.GetSubcommands("workshop");

    // Item pages, their tabs like changelog, and the Steam client's link to them. Other query parameters can come before or after the id
    [GeneratedRegex(@"^(?:(?:https?://)?(?:www\.)?steamcommunity\.com/(?:sharedfiles|workshop)/filedetails/(?:[a-z]+/(?<id>\d+)/?|\?(?:[^#]*&)?id=(?<id>\d+))|steam://url/CommunityFilePage/(?<id>\d+)/?)(?:[?&#].*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"^(?<id>\d+)(?::(?<manifest>\d+|latest))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TargetRegex();

    /// <summary>
    /// Parses "&lt;subcommand&gt; [options] ..." after "workshop".
    /// </summary>
    public static WorkshopArguments Parse(string[] args)
    {
        // An item given where the subcommand goes
        if (args.Length > 0)
        {
            var isItem = ulong.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out _) || UrlRegex().IsMatch(args[0]);

            if (isItem)
            {
                throw new UsageException($"workshop needs a subcommand before the item, like: {Help.ExeName} workshop info {args[0]} or {Help.ExeName} workshop ls {args[0]}.");
            }
        }

        var command = CommandTable.GetCommandName("workshop", args);

        if (command == "workshop")
        {
            throw ArgumentParser.UnknownSubcommand("workshop", "subcommand", args, Subcommands);
        }

        var parsed = ArgumentParser.Parse(command, args[1..]);
        var usage = Help.UsageHint(command);

        if (args[0] is not ("ls" or "get"))
        {
            ArgumentParser.NoPatterns(parsed, command);
        }

        ArgumentParser.CheckPatterns(parsed);

        var history = parsed.Options.ContainsKey("history");
        var limit = DefaultLimit;

        if (parsed.Options.TryGetValue("limit", out var limitText))
        {
            if (args[0] == "info" && !history)
            {
                throw new UsageException($"--limit is for --history. {usage}");
            }

            var parsedLimit = ParseLimit(limitText);

            if (parsedLimit == null)
            {
                throw new UsageException($"--limit needs a positive number or all, got \"{limitText}\".");
            }

            limit = parsedLimit.Value;
        }

        var options = DepotOptions.From(parsed, app: null);

        if (args[0] == "search")
        {
            if (parsed.Positionals.Count == 0)
            {
                throw new UsageException($"workshop search needs an app id and text, like: {Help.ExeName} workshop search 730 de_dust2. {usage}");
            }

            if (!uint.TryParse(parsed.Positionals[0], NumberStyles.None, CultureInfo.InvariantCulture, out var app) || app == 0)
            {
                throw new UsageException($"The app id goes first, like: {Help.ExeName} workshop search {SearchExample(parsed.Positionals)}. {usage}");
            }

            if (parsed.Positionals.Count == 1)
            {
                throw new UsageException($"workshop search {app} needs text after the app id, like: {Help.ExeName} workshop search {app} de_dust2. {usage}");
            }

            var text = string.Join(' ', parsed.Positionals.Skip(1));

            return new WorkshopArguments(command, options, [], [], Limit: limit, SearchApp: app, SearchText: text);
        }

        if (parsed.Positionals.Count == 0)
        {
            throw new UsageException($"{command} needs item ids or URLs, like: {Help.ExeName} {command} 3070715607. \"{Help.ExeName} workshop search <app> <text>\" finds items. {usage}");
        }

        var targets = new List<WorkshopTarget>();

        foreach (var arg in parsed.Positionals)
        {
            targets.Add(ParseTarget(arg, itemOnly: args[0] == "info"));
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

    // The search arguments with an app id typed after the text moved first, or 730 when there is none
    private static string SearchExample(List<string> positionals)
    {
        var app = positionals.Find(static p => uint.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id != 0);

        return $"{app ?? "730"} {string.Join(' ', positionals.Where(p => p != app))}";
    }

    // Throws UsageException for an invalid target. With itemOnly, items with a manifest are rejected
    internal static WorkshopTarget ParseTarget(string arg, bool itemOnly = false)
    {
        var url = UrlRegex().Match(arg);

        if (url.Success)
        {
            if (!ulong.TryParse(url.Groups["id"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var urlID) || urlID == 0)
            {
                throw Invalid();
            }

            return new WorkshopTarget(arg, urlID, null);
        }

        if (arg.Contains("://", StringComparison.Ordinal) || arg.Contains("steamcommunity.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new UsageException($"Unrecognized workshop URL \"{arg}\", expected an item's page like https://steamcommunity.com/sharedfiles/filedetails/?id=3070715607, or give the item id.");
        }

        var match = TargetRegex().Match(arg);

        if (!match.Success)
        {
            throw Invalid();
        }

        if (!ulong.TryParse(match.Groups["id"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var itemID) || itemID == 0)
        {
            throw Invalid();
        }

        var manifest = match.Groups["manifest"].Value;

        if (manifest.Length == 0 || manifest.Equals("latest", StringComparison.OrdinalIgnoreCase))
        {
            return new WorkshopTarget(arg, itemID, null);
        }

        if (!ulong.TryParse(manifest, NumberStyles.None, CultureInfo.InvariantCulture, out var manifestID) || manifestID == 0)
        {
            throw Invalid();
        }

        if (itemOnly)
        {
            throw new UsageException($"workshop info takes items without a manifest, got \"{arg}\". Its versions are listed with --history.");
        }

        return new WorkshopTarget(arg, itemID, manifestID);

        UsageException Invalid()
        {
            if (itemOnly)
            {
                return new UsageException($"Invalid item \"{arg}\", expected an item id or URL, like 3070715607. \"{Help.ExeName} workshop search <app> <text>\" finds items by text.");
            }

            return new UsageException($"Invalid item \"{arg}\", expected <item>[:<manifest>|:latest] or an item's URL, like 3070715607 or 3070715607:<manifest>. Patterns go after --.");
        }
    }
}
