using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SteamFileDownloader;

/// <summary>
/// Translates DepotDownloader's command line into get or ls, so existing DepotDownloader commands keep working.
/// </summary>
internal static class DepotDownloaderArguments
{
    // Every option dd supports, without its dash, to suggest from for a mistyped one
    private static readonly string[] SupportedOptions =
    [
        "app",
        "depot",
        "manifest",
        "manifest-only",
        "pubfile",
        "ugc",
        "filelist",
        "dir",
        "branch",
        "beta",
        "username",
        "user",
        "password",
    ];

    /// <summary>
    /// Returns the arguments of the get or ls command it runs as, or null after printing an error.
    /// </summary>
    public static DepotArguments? Parse(string[] args)
    {
        var depots = new List<string>();
        var manifests = new List<string>();
        var patterns = new List<string>();
        var options = new DepotOptions(null);
        var manifestOnly = false;
        string? pubfile = null;
        string? ugc = null;

        for (var i = 0; i < args.Length; i++)
        {
            var name = args[i].ToLowerInvariant();

            // Flags without a value are followed by the next flag
            var value = i + 1 < args.Length && !args[i + 1].StartsWith('-') ? args[i + 1] : null;

            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                return Fail("dd takes DepotDownloader's single-dash options, like -app 730.");
            }

            if (!args[i].StartsWith('-'))
            {
                return Fail($"Unexpected argument \"{args[i]}\"; dd takes options like -app 730.");
            }

            switch (name)
            {
                case "-manifest-only":
                    manifestOnly = true;
                    continue;
                case "-app" or "-depot" or "-manifest" or "-pubfile" or "-ugc" or "-filelist"
                    or "-dir" or "-branch" or "-beta" or "-username" or "-user" or "-password" when value == null:
                    return Fail($"{args[i]} needs a value.");
                case "-app":
                    if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var appID))
                    {
                        return Fail($"-app needs a numeric app id, got \"{value}\".");
                    }

                    options = options with { App = appID };
                    break;
                case "-depot" or "-manifest" or "-pubfile" or "-ugc" when InvalidId(name, value) is { } invalid:
                    return Fail(invalid);
                case "-pubfile":
                    pubfile = value;
                    break;
                case "-ugc":
                    ugc = value;
                    break;
                case "-depot":
                    depots.Add(value);
                    break;
                case "-manifest":
                    manifests.Add(value);
                    break;
                case "-filelist":
                    var lines = ReadLines(value, "-filelist");

                    if (lines == null)
                    {
                        return null;
                    }

                    var filePatterns = lines
                        .Select(static l => l.Trim())
                        .Where(static l => l.Length > 0)
                        .Select(ToFileListPattern)
                        .ToList();

                    // A list without entries downloads nothing in DepotDownloader, rather than every file
                    if (filePatterns.Count == 0)
                    {
                        return Fail($"The file list \"{value}\" has no entries.");
                    }

                    patterns.AddRange(filePatterns);
                    break;
                case "-dir":
                    if (!ArgumentParser.CheckOutputPath(value, "-dir"))
                    {
                        return null;
                    }

                    options = options with { Output = value };
                    break;
                case "-branch" or "-beta":
                    options = options with { Branch = value };
                    break;
                case "-branchpassword" or "-betapassword":
                    return Fail("Password protected branches are not supported.");
                case "-username" or "-user":
                    options = options with { Login = options.Login with { Username = value } };
                    break;
                case "-password":
                    options = options with { Login = options.Login with { Password = value } };
                    break;
                default:
                    var suggestion = ArgumentParser.DidYouMean(name[1..], SupportedOptions, "-");

                    return Fail($"Unsupported option {args[i]}.{suggestion}");
            }

            i++;
        }

        var loginError = options.Login.Validate("-");

        if (loginError != null)
        {
            return Fail(loginError);
        }

        var hasItem = pubfile != null || ugc != null;
        var hasDepots = depots.Count > 0 || manifests.Count > 0;

        if (hasItem && hasDepots)
        {
            var itemOption = pubfile != null ? "-pubfile" : "-ugc";

            return Fail($"{itemOption} downloads a workshop item, so it can't be combined with -depot or -manifest.");
        }

        if (pubfile != null && ugc != null)
        {
            return Fail("-pubfile and -ugc each name a workshop item, give one of them.");
        }

        // The item's files go to the workshop folder, like with workshop get, and its app doesn't matter
        if (pubfile != null)
        {
            return new DepotArguments(manifestOnly ? "workshop ls" : "workshop get", options, [pubfile], patterns);
        }

        if (options.App == null)
        {
            return Fail($"dd needs -app <app>, like: {Help.ExeName} dd -app 730 -depot 2347771.");
        }

        if (manifests.Count > depots.Count)
        {
            return Fail("Each -manifest needs a -depot before it.");
        }

        // A UGC id is a manifest of the depot with the app's id. Without -depot, DepotDownloader takes every depot of
        // the app, as no targets do, and -depot and -manifest pair up in order
        var targets = new List<string>();

        if (ugc != null)
        {
            targets.Add($"{options.App}:{ugc}");
        }
        else
        {
            for (var index = 0; index < depots.Count; index++)
            {
                targets.Add(index < manifests.Count ? $"{depots[index]}:{manifests[index]}" : depots[index]);
            }
        }

        return new DepotArguments(manifestOnly ? "ls" : "get", options, targets, patterns);
    }

    // Returns null after printing an error
    private static string[]? ReadLines(string path, string option)
    {
        try
        {
            return File.ReadAllLines(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Log.Error($"Failed to read {option} \"{path}\": {e.Message}");
            return null;
        }
    }

    private static DepotArguments? Fail(string message)
    {
        Log.Error($"{message} {Help.UsageHint("dd")}");
        return null;
    }

    // Why the value of an id option is invalid, or null for a nonzero number that fits the id
    private static string? InvalidId(string option, string value)
    {
        ulong? id = null;

        if (option == "-depot")
        {
            if (uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var depotID))
            {
                id = depotID;
            }
        }
        else if (ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var otherID))
        {
            id = otherID;
        }

        var idKind = option == "-pubfile" ? "item" : "manifest";

        return id switch
        {
            null => $"{option} needs a numeric id, got \"{value}\".",
            0 when option == "-depot" => $"-depot needs a nonzero depot id, got \"{value}\". \"{Help.ExeName} depots <app>\" lists an app's depots.",
            0 => $"{option} needs a nonzero {idKind} id, got \"{value}\".",
            _ => null,
        };
    }

    // DepotDownloader's file lists are "regex:" lines and exact paths from the depot root, even for a bare name, and
    // without globs
    private static string ToFileListPattern(string line)
    {
        if (line.StartsWith("regex:", StringComparison.Ordinal))
        {
            return line;
        }

        return $"regex:^{Regex.Escape(FileFilter.NormalizePath(line))}$";
    }
}
