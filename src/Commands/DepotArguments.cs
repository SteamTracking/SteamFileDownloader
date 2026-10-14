using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace SteamFileDownloader;

/// <summary>
/// Options shared by the get, ls and depots commands, and the workshop commands.
/// </summary>
internal sealed record DepotOptions(
    uint? App,
    string? Output = null,
    string Branch = "public",
    bool Anonymous = false,
    string? Username = null,
    bool AllFiles = false)
{
    public static DepotOptions From(ParsedArguments parsed, uint? app) => new(
        app,
        parsed.Options.GetValueOrDefault("output"),
        parsed.Options.GetValueOrDefault("branch") ?? "public",
        parsed.Options.ContainsKey("anonymous"),
        parsed.Options.GetValueOrDefault("username"),
        parsed.Options.ContainsKey("all-files"));
}

/// <summary>
/// A parsed get, ls or depots command line.
/// </summary>
internal sealed record DepotArguments(
    string Command,
    DepotOptions Options,
    List<string> Targets,
    List<string> Patterns);

// DepotID null means every depot of the app, ManifestID null means the latest on the branch
internal sealed record Target(
    string Source,
    uint AppID,
    uint? DepotID,
    ulong? ManifestID,
    string Branch);

/// <summary>
/// Parses the command lines and targets of the get, ls and depots commands.
/// </summary>
internal static partial class DepotArgumentParser
{
    // The manifest can follow a slash too, since app/depot/manifest is an easy mistake to make. Without an app, a slash
    // means app/depot, so depot/manifest is never read as such
    [GeneratedRegex(@"^(?:(?<app>\d+)/)?(?<depot>\d+|all)(?:[:/](?<manifest>\d+|latest))?(?:@(?<branch>[^@/:]+))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TargetRegex();

    /// <summary>
    /// Parses "[app] [options] [targets] [-- patterns]" for get, ls and depots. Options can go anywhere before "--".
    /// Returns null after printing an error.
    /// </summary>
    public static DepotArguments? ParseArguments(string command, string[] args)
    {
        var parsed = ArgumentParser.Parse(command, args);

        if (parsed == null)
        {
            return null;
        }

        var targets = parsed.Positionals;
        uint? app = null;

        // The app id is optional when every target names its own app
        if (targets.Count > 0 && uint.TryParse(targets[0], NumberStyles.None, CultureInfo.InvariantCulture, out var firstApp))
        {
            app = firstApp;
            targets = targets[1..];
        }
        else if (command == "depots")
        {
            if (targets.Count > 0)
            {
                Log.Error($"\"{targets[0]}\" is not an app id, the number in the app's store page URL, like: {Help.ExeName} depots 730 570.");
            }
            else
            {
                Log.Error($"depots needs app ids, like: {Help.ExeName} depots 730 570.");
            }

            return null;
        }

        if (command == "depots" && !ArgumentParser.NoPatterns(parsed, command))
        {
            return null;
        }

        // A number first that no target uses was likely meant as a depot, which would otherwise be skipped silently
        var appMayBeDepot = app != null && command != "depots";

        if (appMayBeDepot && EveryTargetNamesApp(targets))
        {
            var slashTarget = TargetRegex().Match(targets[0]);
            var targetApp = slashTarget.Groups["app"].Value;
            var targetDepot = slashTarget.Groups["depot"].Value;
            string manifestHint;

            // Manifest ids don't fit in 32 bits like depot ids do, so a larger one after the slash is a manifest
            if (uint.TryParse(targetDepot, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                manifestHint = "A manifest follows a colon, like <depot>:<manifest>.";
            }
            else
            {
                manifestHint = $"A manifest follows a colon, like: {Help.ExeName} {command} {app} {targetApp}:{targetDepot}.";
            }

            Log.Error($"\"{targets[0]}\" reads as app {targetApp} and depot {targetDepot}, so the app id {app} is unused. {manifestHint} If {app} is a depot, give it as <app>/{app}.");
            return null;
        }

        // ls with patterns searches every depot of the app when no target narrows it
        var searchesEveryDepot = command == "ls" && app != null && targets.Count == 0 && parsed.Patterns.Count > 0;

        if (searchesEveryDepot)
        {
            targets = ["all"];
        }

        if (command is "get" or "ls" && targets.Count == 0)
        {
            Log.Error(DescribeMissingTargets(command, app, parsed.Patterns));
            return null;
        }

        // Without patterns, a target that isn't one is most likely a file name that belongs after --
        if (command is "get" or "ls" && parsed.Patterns.Count == 0)
        {
            var kept = new List<string>();
            var moved = new List<string>();

            foreach (var target in targets)
            {
                if (TargetRegex().IsMatch(target))
                {
                    kept.Add(target);
                }
                else
                {
                    moved.Add(target);
                }
            }

            if (moved.Count > 0)
            {
                Log.Error($"\"{moved[0]}\" is not a target; patterns go after --, like: {SuggestCommand(command, app, kept, moved)}.");
                return null;
            }
        }

        if (!ArgumentParser.CheckPatterns(parsed, get: command == "get",
            $"get needs patterns after --, like: {SuggestCommand(command, app, targets, [])}. Use ls to see a manifest's files, or --all-files to download every file."))
        {
            return null;
        }

        return new DepotArguments(command, DepotOptions.From(parsed, app), targets, parsed.Patterns);
    }

    /// <summary>
    /// The error for get or ls without targets, with commands built from the app and patterns given.
    /// </summary>
    internal static string DescribeMissingTargets(string command, uint? app, List<string> patterns)
    {
        // ls with an app and patterns searches every depot, so only get gets here with both
        if (app == null || patterns.Count > 0)
        {
            var findDepot = SuggestCommand("ls", app, [], patterns);

            if (command == "ls")
            {
                return $"ls needs an app id, like: {findDepot} to find which depot of the app has a file, or {Help.ExeName} ls <app> <target>... to list the files of targets.";
            }

            var getAll = SuggestCommand("get", app, [], patterns);

            if (app == null)
            {
                return $"get needs an app id and targets, like: {getAll}, or {findDepot} to find the depot first.";
            }

            return $"get {app} needs targets, like: {getAll}, or {findDepot} to find the depot first.";
        }

        // A lone number may be a depot
        var withApp = SuggestCommand(command, null, [app.Value.ToString(CultureInfo.InvariantCulture)], patterns);
        string allDepots;

        if (command == "ls")
        {
            allDepots = $"{Help.ExeName} ls {app} all, or {Help.ExeName} ls {app} -- <pattern> to search them";
        }
        else
        {
            allDepots = SuggestCommand(command, app, [], patterns);
        }

        return $"{command} {app} needs targets after the app id, like: {allDepots}. If {app} is a depot, put its app first: {withApp}.";
    }

    /// <summary>
    /// A get or ls command line for the app, targets and patterns, with &lt;app&gt; when the app is unknown and a
    /// placeholder for missing patterns of get. Without targets, get gets all and ls searches every depot.
    /// </summary>
    internal static string SuggestCommand(string command, uint? app, List<string> targets, IReadOnlyCollection<string> patterns)
    {
        var parts = new List<string> { Help.ExeName, command };

        // The app id can be left out when every target names its own
        if (app != null || !EveryTargetNamesApp(targets))
        {
            parts.Add(app?.ToString(CultureInfo.InvariantCulture) ?? "<app>");
        }

        if (targets.Count == 0 && command == "get")
        {
            parts.Add("all");
        }
        else
        {
            parts.AddRange(targets);
        }

        if (patterns.Count > 0 || command == "get" || targets.Count == 0)
        {
            parts.Add("--");

            if (patterns.Count > 0)
            {
                parts.AddRange(patterns.Select(Help.QuoteArgument));
            }
            else
            {
                parts.Add("<pattern>");
            }
        }

        return string.Join(' ', parts);
    }

    // Returns null after printing an error
    internal static Target? ParseTarget(string arg, DepotArguments arguments)
    {
        var match = TargetRegex().Match(arg);

        if (!match.Success)
        {
            return Invalid();
        }

        uint appID;

        if (match.Groups["app"].Success)
        {
            if (!uint.TryParse(match.Groups["app"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out appID))
            {
                return Invalid();
            }
        }
        else if (arguments.Options.App != null)
        {
            appID = arguments.Options.App.Value;
        }
        else
        {
            Log.Error($"Target \"{arg}\" has no app. Put the app id before the targets, like \"{SuggestCommand(arguments.Command, null, arguments.Targets, arguments.Patterns)}\", or in the target, like <app>/{arg}.");
            return null;
        }

        uint? depotID = null;
        ulong? manifestID = null;
        var depot = match.Groups["depot"].Value;
        var manifest = match.Groups["manifest"].Value;

        if (!depot.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            if (!uint.TryParse(depot, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedDepot) || parsedDepot == 0)
            {
                return Invalid();
            }

            depotID = parsedDepot;
        }

        if (manifest.Length > 0 && !manifest.Equals("latest", StringComparison.OrdinalIgnoreCase))
        {
            // A manifest id only makes sense for one depot
            if (depotID == null)
            {
                return Invalid();
            }

            if (!ulong.TryParse(manifest, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedManifest) || parsedManifest == 0)
            {
                return Invalid();
            }

            manifestID = parsedManifest;
        }

        var branch = match.Groups["branch"].Success ? match.Groups["branch"].Value : arguments.Options.Branch;

        return new Target(arg, appID, depotID, manifestID, branch);

        Target? Invalid()
        {
            Log.Error($"Invalid target \"{arg}\": {DescribeInvalidTarget(arg)}.");
            return null;
        }
    }

    /// <summary>
    /// What is wrong with a target that doesn't parse, only the part of the syntax that it gets wrong.
    /// </summary>
    internal static string DescribeInvalidTarget(string arg)
    {
        const string Syntax = "expected [<app>/]<depot>[:<manifest>|:latest][@<branch>] or all, like 2347771:8344780363095656278 or 570/373303@beta";
        var match = TargetRegex().Match(arg);

        if (match.Success)
        {
            var app = match.Groups["app"];
            var depot = match.Groups["depot"].Value;
            var manifest = match.Groups["manifest"].Value;

            if (depot.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                return "all is the latest manifest of every depot, so it takes no manifest";
            }

            // Like depot/manifest, which reads as app/depot with a depot id too large to be one
            if (app.Success && manifest.Length == 0 && !uint.TryParse(depot, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                return $"a manifest follows a colon, like {app.Value}:{depot}";
            }

            var depotIsValid = uint.TryParse(depot, NumberStyles.None, CultureInfo.InvariantCulture, out var depotID) && depotID != 0;
            var appIsValid = !app.Success || uint.TryParse(app.ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out _);

            if (!depotIsValid || !appIsValid)
            {
                return "app and depot ids are nonzero 32-bit numbers";
            }

            return "the manifest must be a nonzero 64-bit number or latest";
        }

        if (arg.Contains('.', StringComparison.Ordinal) || arg.AsSpan().IndexOfAny('*', '?') >= 0)
        {
            return "patterns go after --";
        }

        var at = arg.IndexOf('@', StringComparison.Ordinal);

        if (at >= 0 && (at == arg.Length - 1 || arg.AsSpan(at + 1).IndexOfAny("@/:".AsSpan()) >= 0))
        {
            return "a branch follows a single @, like 2347771@beta";
        }

        var main = at >= 0 ? arg[..at] : arg;

        if (main.Contains(':', StringComparison.Ordinal))
        {
            return "the manifest after the colon must be a number or latest";
        }

        if (main.Contains('/', StringComparison.Ordinal))
        {
            return "a slash only follows an app, like 570/373303 or 570/373303/<manifest>";
        }

        return Syntax;
    }

    // Whether there are targets and each names its own app, like 570/373303
    private static bool EveryTargetNamesApp(List<string> targets) => targets.Count > 0 && targets.All(static t => TargetRegex().Match(t).Groups["app"].Success);
}
