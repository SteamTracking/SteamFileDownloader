using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace SteamFileDownloader;

/// <summary>
/// Options shared by the get, ls, download and depots commands, and the workshop commands.
/// </summary>
internal sealed record DepotOptions(
    uint? App,
    string? Output = null,
    string Branch = "public",
    string? BranchPassword = null)
{
    public LoginOptions Login { get; init; } = new();

    // Only the download command has these
    public InstallOptions Install { get; init; } = new();

    // Only dd has these, for ids that may be from the other of -ugc and -pubfile: added to the error of a workshop item
    // that does not exist, or of a manifest that Steam gave no access to
    public string? ItemNotFoundHint { get; init; }

    public string? ManifestNotFoundHint { get; init; }

    public static DepotOptions From(ParsedArguments parsed, uint? app) => new(
        app,
        parsed.Options.GetValueOrDefault("output"),
        parsed.Options.GetValueOrDefault("branch") ?? "public",
        parsed.Options.GetValueOrDefault("branch-password"))
    {
        Login = LoginOptions.From(parsed.Options),
        Install = parsed.Install,
    };
}

/// <summary>
/// A parsed get, ls, download or depots command line. Get and ls without targets take every depot of the app.
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
    [GeneratedRegex(@"^(?:(?<app>\d+)/)?(?<depot>\d+)(?:[:/](?<manifest>\d+|latest))?(?:@(?<branch>[^@/:]+))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TargetRegex();

    // "all" with an app or branch, which leaving out the targets does
    [GeneratedRegex(@"^(?:(?<app>\d+)/)?all(?:[:/@].*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AllTargetRegex();

    /// <summary>
    /// Parses "[app] [options] [targets] [-- patterns]" for get, ls, download and depots. Options can go anywhere before "--".
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

        if (command is "depots" or "download" && !ArgumentParser.NoPatterns(parsed, command))
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

        if (command == "download")
        {
            if (!CheckDownloadArguments(parsed, app, targets))
            {
                return null;
            }
        }
        else if (command is "get" or "ls" && app == null && targets.Count == 0)
        {
            // Get and ls can take the apps from their targets
            Log.Error(DescribeMissingApp(command, parsed.Patterns));
            return null;
        }

        foreach (var target in targets)
        {
            var allTarget = AllTargetRegex().Match(target);

            // Download without targets takes the depots that Steam installs, not every depot
            if (!allTarget.Success || command == "download")
            {
                continue;
            }

            var allApp = app;

            if (uint.TryParse(allTarget.Groups["app"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var targetApp))
            {
                allApp = targetApp;
            }

            Log.Error($"\"{target}\" is not a target: without targets, {command} takes every depot of the app, like: {SuggestCommand(command, allApp, [], parsed.Patterns)}.");
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

        if (!ArgumentParser.CheckPatterns(parsed))
        {
            return null;
        }

        // Whole depots can be tens of gigabytes, which a forgotten pattern shouldn't download unnoticed
        if (command == "get" && parsed.Patterns.Count == 0)
        {
            Log.Info(DescribeEveryFile(app, targets));
        }

        return new DepotArguments(command, DepotOptions.From(parsed, app), targets, parsed.Patterns);
    }

    // Download takes one app, which has to come first, and depots of it as targets, on the branch of --branch.
    // Returns false after printing an error
    private static bool CheckDownloadArguments(ParsedArguments parsed, uint? app, List<string> targets)
    {
        if (app == null)
        {
            Log.Error(DescribeMissingApp("download", parsed.Patterns));
            return false;
        }

        foreach (var target in targets)
        {
            var match = TargetRegex().Match(target);

            if (match.Success && (match.Groups["app"].Success || match.Groups["branch"].Success))
            {
                Log.Error($"\"{target}\" names an app or branch. download takes <depot>[:<manifest>] of its one app, and --branch for the branch. {Help.UsageHint("download")}");
                return false;
            }
        }

        if (targets.Count > 0 && parsed.Install.HasIncludeOrExclude)
        {
            Log.Error($"--include and --exclude change the depots that download picks, so they can't be combined with depots as targets. {Help.UsageHint("download")}");
            return false;
        }

        return true;
    }

    /// <summary>
    /// The error for get, ls or download without an app id or targets.
    /// </summary>
    internal static string DescribeMissingApp(string command, List<string> patterns) =>
        $"{command} needs an app id, the number in the app's store page URL, like: {SuggestCommand(command, null, [], patterns)}.";

    /// <summary>
    /// The tip for get without patterns, on how patterns pick files.
    /// </summary>
    internal static string DescribeEveryFile(uint? app, List<string> targets)
    {
        var getSome = SuggestCommand("get", app, targets, ["*.dll"]);
        var list = SuggestCommand("ls", app, targets, []);

        return $"Downloading every file, since no patterns follow --. Patterns pick files, like: {getSome}, and {list} lists them.";
    }

    /// <summary>
    /// A get or ls command line for the app, targets and patterns, with &lt;app&gt; when the app is unknown.
    /// </summary>
    internal static string SuggestCommand(string command, uint? app, List<string> targets, IReadOnlyCollection<string> patterns)
    {
        var parts = new List<string> { Help.ExeName, command };

        // The app id can be left out when every target names its own
        if (app != null || !EveryTargetNamesApp(targets))
        {
            parts.Add(app?.ToString(CultureInfo.InvariantCulture) ?? "<app>");
        }

        parts.AddRange(targets);

        if (patterns.Count > 0)
        {
            parts.Add("--");
            parts.AddRange(patterns.Select(Help.QuoteArgument));
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

        ulong? manifestID = null;
        var manifest = match.Groups["manifest"].Value;

        if (!uint.TryParse(match.Groups["depot"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var depotID) || depotID == 0)
        {
            return Invalid();
        }

        if (manifest.Length > 0 && !manifest.Equals("latest", StringComparison.OrdinalIgnoreCase))
        {
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
        const string Syntax = "expected [<app>/]<depot>[:<manifest>|:latest][@<branch>], like 2347771:8344780363095656278 or 570/373303@beta";
        var match = TargetRegex().Match(arg);

        if (match.Success)
        {
            var app = match.Groups["app"];
            var depot = match.Groups["depot"].Value;
            var manifest = match.Groups["manifest"].Value;

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
