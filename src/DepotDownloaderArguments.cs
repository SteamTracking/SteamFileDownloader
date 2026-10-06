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
    /// <summary>
    /// Returns the command (get or ls) and its arguments, or null after printing an error.
    /// </summary>
    public static (string Command, DepotArguments Arguments)? Parse(string[] args)
    {
        uint? app = null;
        var depots = new List<string>();
        var manifests = new List<string>();
        var patterns = new List<string>();
        var options = new DepotOptions(0);
        var manifestOnly = false;

        for (var i = 0; i < args.Length; i++)
        {
            var name = args[i].ToLowerInvariant();

            // Flags without a value are followed by the next flag
            var value = i + 1 < args.Length && !args[i + 1].StartsWith('-') ? args[i + 1] : null;

            switch (name)
            {
                case "-manifest-only":
                    manifestOnly = true;
                    continue;
                case "-app" or "-depot" or "-manifest" or "-filelist" or "-dir" or "-branch" or "-beta" when value == null:
                    return Fail($"{args[i]} needs a value.");
                case "-app":
                    if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var appID))
                    {
                        return Fail($"-app needs a numeric app id, got \"{value}\".");
                    }

                    app = appID;
                    break;
                case "-depot" or "-manifest" when !ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _):
                    return Fail($"{args[i]} needs a numeric id, got \"{value}\".");
                case "-depot":
                    depots.Add(value);
                    break;
                case "-manifest":
                    manifests.Add(value);
                    break;
                case "-filelist":
                    string[] lines;

                    try
                    {
                        lines = File.ReadAllLines(value);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        return Fail($"Could not read file list \"{value}\": {e.Message}");
                    }

                    // DepotDownloader's file lists are exact paths and "regex:" lines. Paths with a folder match exactly here too,
                    // but a bare name is a file in the root there, rather than in any folder
                    patterns.AddRange(lines
                        .Select(static l => l.Trim())
                        .Where(static l => l.Length > 0)
                        .Select(static l => l.StartsWith("regex:", StringComparison.Ordinal) || FileFilter.NormalizePath(l).Contains('/', StringComparison.Ordinal)
                            ? l
                            : $"regex:^{Regex.Escape(FileFilter.NormalizePath(l))}$"));
                    break;
                case "-dir":
                    options = options with { Output = value };
                    break;
                case "-branch" or "-beta":
                    options = options with { Branch = value };
                    break;
                case "-branchpassword" or "-betapassword":
                    return Fail("Password protected branches are not supported.");
                case "-username" or "-user" when value != null:
                    options = options with { Username = value };
                    break;
                case "-os" or "-osarch" or "-language" or "-all-platforms" or "-all-archs" or "-all-languages" or "-lowviolence":
                    // Without -depot, DepotDownloader picks depots by these, but here every depot of the app is used
                    Log.Warn($"Ignoring {args[i]}: depots aren't filtered by platform or language, so pick them with -depot.");

                    if (value == null)
                    {
                        continue;
                    }

                    break;
                default:
                    // Login and download tuning options are handled by this tool on its own
                    Log.Info($"Ignoring {args[i]}, not needed.");

                    if (value == null)
                    {
                        continue;
                    }

                    break;
            }

            i++;
        }

        if (app == null)
        {
            return Fail("DepotDownloader commands need -app <id>.");
        }

        if (manifests.Count > depots.Count)
        {
            return Fail("Each -manifest needs a -depot before it.");
        }

        // -depot and -manifest pair up in order; without -depot, DepotDownloader takes every depot of the app
        var targets = depots.Count == 0
            ? ["all"]
            : depots.Select((depot, index) => index < manifests.Count ? $"{depot}:{manifests[index]}" : depot).ToList();

        var command = manifestOnly ? "ls" : "get";

        // Without a file list DepotDownloader downloads every file
        var allFiles = !manifestOnly && patterns.Count == 0;
        var arguments = new DepotArguments(options with { App = app.Value, AllFiles = allFiles }, targets, patterns);

        Log.Info($"Equivalent command, which takes many targets in one run: {Help.ExeName} {command} {app}{(options.Output != null ? $" --output \"{options.Output}\"" : "")}{(options.Branch != "public" ? $" --branch {options.Branch}" : "")}{(allFiles ? " --all-files" : "")} {string.Join(' ', targets)}{(patterns.Count > 0 ? " -- " + string.Join(' ', patterns.Select(static p => $"'{p}'")) : "")}");

        return (command, arguments);
    }

    private static (string, DepotArguments)? Fail(string message)
    {
        Log.Error($"{message} {Help.UsageHint("dd")}");
        return null;
    }
}
