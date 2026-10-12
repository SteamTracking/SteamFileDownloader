using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace SteamFileDownloader;

/// <summary>
/// Options shared by the get, ls and depots commands.
/// </summary>
internal sealed record DepotOptions(
    uint? App,
    string? Output = null,
    string Branch = "public",
    bool Anonymous = false,
    string? Username = null,
    bool AllFiles = false,
    bool Json = false)
{
    public static DepotOptions From(ParsedArguments parsed, uint? app) => new(
        app,
        parsed.Options.GetValueOrDefault("output"),
        parsed.Options.GetValueOrDefault("branch") ?? "public",
        parsed.Options.ContainsKey("anonymous"),
        parsed.Options.GetValueOrDefault("username"),
        parsed.Options.ContainsKey("all-files"),
        parsed.Options.ContainsKey("json"));
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
            Log.Error($"depots needs app ids, for example: {Help.ExeName} depots 730 570. {Help.UsageHint(command)}");
            return null;
        }

        // A number first that no target uses was likely meant as a depot, which would otherwise be skipped silently
        if (app != null && command != "depots" && targets.Count > 0 && targets.All(static t => TargetRegex().Match(t) is { Success: true } m && m.Groups["app"].Success))
        {
            Log.Error($"{app} is taken as the app id, but no target uses it, since a slash in a target means <app>/<depot>. If {app} is a depot, give it as <app>/{app}, and a manifest follows a colon, like <depot>:<manifest>.");
            return null;
        }

        if (parsed.Options.ContainsKey("json") && parsed.Options.ContainsKey("branch"))
        {
            Log.Error("--json prints every branch, so it can't be combined with --branch.");
            return null;
        }

        if (!ArgumentParser.CheckPatterns(parsed, get: command == "get",
            $"get needs patterns after --, like: {Help.ExeName} get 730 2347771:latest -- server.dll. Use ls to see a manifest's files, or --all-files to download every file."))
        {
            return null;
        }

        if (command is "get" or "ls" && targets.Count == 0)
        {
            Log.Error($"No targets, expected [<app>] <target>..., like: {Help.ExeName} {command} 730 2347771:latest");
            return null;
        }

        return new DepotArguments(command, DepotOptions.From(parsed, app), targets, parsed.Patterns);
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
        else if (arguments.Options.App is { } app)
        {
            appID = app;
        }
        else
        {
            Log.Error($"Target \"{arg}\" has no app. Put the app id before the targets, like \"{Help.ExeName} {arguments.Command} 730 {arg}\", or in the target, like 730/{arg}.");
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
            if (depotID == null || !ulong.TryParse(manifest, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedManifest) || parsedManifest == 0)
            {
                return Invalid();
            }

            manifestID = parsedManifest;
        }

        var branch = match.Groups["branch"].Success ? match.Groups["branch"].Value : arguments.Options.Branch;

        return new Target(arg, appID, depotID, manifestID, branch);

        Target? Invalid()
        {
            Log.Error($"Invalid target \"{arg}\", expected [<app>/]<depot>[:<manifest>|:latest][@<branch>] or all, like 2347771:8344780363095656278 or 570/373303@beta. A manifest follows a colon, or a slash only after an app (570/373303/<manifest>). Patterns go after --.");
            return null;
        }
    }
}
