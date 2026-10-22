using System;
using System.Collections.Generic;
using System.Globalization;

namespace SteamFileDownloader;

/// <summary>
/// Translates DepotDownloader's command line into download or ls, so existing DepotDownloader commands keep working.
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
        "branchpassword",
        "betapassword",
        "username",
        "user",
        "password",
        "os",
        "osarch",
        "language",
        "all-platforms",
        "all-languages",
        "lowviolence",
    ];

    /// <summary>
    /// Returns the arguments of the download or ls command it runs as, or null after printing an error.
    /// </summary>
    public static DepotArguments? Parse(string[] args)
    {
        var depots = new List<string>();
        var manifests = new List<string>();
        uint? app = null;
        var branch = "public";
        string? branchPassword = null;
        var login = new LoginOptions();
        var install = new InstallOptions();
        string? dir = null;
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
                case "-all-platforms":
                    install = install with { AllPlatforms = true };
                    continue;
                case "-all-languages":
                    install = install with { AllLanguages = true };
                    continue;
                case "-lowviolence":
                    install = install with { LowViolence = true };
                    continue;
                case "-filelist":
                    return Fail($"download has no file patterns, so -filelist is not supported. To get some files, use {Help.ExeName} get <app>/<depot> -- <pattern>...");
                case "-app" or "-depot" or "-manifest" or "-pubfile" or "-ugc" or "-dir" or "-branch" or "-beta" or "-branchpassword"
                    or "-betapassword" or "-username" or "-user" or "-password" or "-os" or "-osarch" or "-language" when value == null:
                    return Fail($"{args[i]} needs a value.");
                case "-app":
                    if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var appID))
                    {
                        return Fail($"-app needs a numeric app id, got \"{value}\".");
                    }

                    app = appID;
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
                case "-dir":
                    if (!ArgumentParser.CheckOutputPath(value, "-dir"))
                    {
                        return null;
                    }

                    dir = value;
                    break;
                case "-os":
                    install = install with { Os = value.ToLowerInvariant() };
                    break;
                case "-osarch":
                    install = install with { OsArch = value };
                    break;
                case "-language":
                    install = install with { Language = value.ToLowerInvariant() };
                    break;
                case "-branch" or "-beta":
                    branch = value;
                    break;
                case "-branchpassword" or "-betapassword":
                    branchPassword = value;
                    break;
                case "-username" or "-user":
                    login = login with { Username = value };
                    break;
                case "-password":
                    login = login with { Password = value };
                    break;
                default:
                    var suggestion = ArgumentParser.DidYouMean(name[1..], SupportedOptions, "-");

                    return Fail($"Unsupported option {args[i]}.{suggestion}");
            }

            i++;
        }

        var loginError = login.Validate("-");

        if (loginError != null)
        {
            return Fail(loginError);
        }

        var installError = install.Validate("-");

        if (installError != null)
        {
            return Fail(installError);
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
            var itemOptions = CreateOptions(dir) with { ItemNotFoundHint = $"If {pubfile} is a UGC id, use -ugc instead of -pubfile." };

            return new DepotArguments(manifestOnly ? "workshop ls" : "workshop get", itemOptions, [pubfile], []);
        }

        if (app == null)
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
        string? manifestNotFoundHint = null;

        if (ugc != null)
        {
            targets.Add($"{app}:{ugc}");
            manifestNotFoundHint = $"If {ugc} is a workshop item id, use -pubfile instead of -ugc.";
        }
        else
        {
            for (var index = 0; index < depots.Count; index++)
            {
                targets.Add(index < manifests.Count ? $"{depots[index]}:{manifests[index]}" : depots[index]);
            }
        }

        // Listing manifests puts the _dir.vpk files of entries in the output folder, while an install is one folder
        if (manifestOnly)
        {
            var lsOptions = CreateOptions(dir) with { ManifestNotFoundHint = manifestNotFoundHint };

            return new DepotArguments("ls", lsOptions, targets, []);
        }

        install = install with { Dir = dir };

        return new DepotArguments("download", CreateOptions(null) with { ManifestNotFoundHint = manifestNotFoundHint }, targets, []);

        DepotOptions CreateOptions(string? output) => new(app, output, branch, branchPassword)
        {
            Login = login,
            Install = install,
        };
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
}
