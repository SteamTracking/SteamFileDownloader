using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using SteamKit2;

namespace SteamFileDownloader;

/// <summary>
/// A depot that the filters left out, and the config key or rule that did it. The value is the depot's own value of the
/// config key, like "czero" for the key mod, and null for a rule.
/// </summary>
internal sealed record SkippedDepot(uint ID, string Reason, string? Value = null);

/// <summary>
/// The depots to install, in the order of the app's info, the ones left out, and the language the choice used.
/// </summary>
internal sealed record DepotSelection(List<(uint ID, KeyValue Depot)> Depots, List<SkippedDepot> Skipped, string Language)
{
    /// <summary>
    /// The depots that were added to the selection by name, with the reason the filters had skipped them.
    /// </summary>
    public List<SkippedDepot> Included { get; init; } = [];
}

/// <summary>
/// Chooses the depots of an app to install the way Steam does: a depot's config values must match the user's, which
/// describe the platform and the language.
/// </summary>
internal static class DepotFilter
{
    private const string DefaultLanguage = "english";

    /// <summary>
    /// The reason of a depot that --exclude left out.
    /// </summary>
    public const string ExcludedReason = "excluded";

    // The keys that say which platform a depot is for
    private static readonly string[] PlatformKeys = ["oslist", "osarch", "oscpu", "steamdeck"];

    private static readonly char[] Separators = [',', ' ', '\t', '\r', '\n'];

    /// <summary>
    /// The name of the operating system this runs on, as depots name it.
    /// </summary>
    public static string GetMachineOs()
    {
        if (OperatingSystem.IsWindows())
        {
            return "windows";
        }

        return OperatingSystem.IsMacOS() ? "macos" : "linux";
    }

    /// <summary>
    /// The CPU of a machine as depots name it. Steam is an arm64 program on Linux and macOS, but runs as x64 under
    /// emulation on Windows.
    /// </summary>
    public static string GetMachineCpu(Architecture processArchitecture, string machineOs)
    {
        if (processArchitecture == Architecture.Arm64 && machineOs is "linux" or "macos")
        {
            return "arm64";
        }

        return "x86";
    }

    /// <summary>
    /// The user's config of this machine, with the options applied, except for the language. Steam sets
    /// highqualityaudio only for soundtracks.
    /// </summary>
    public static Dictionary<string, string> CreateUserConfig(InstallOptions options, bool isMusic)
    {
        var machineOs = GetMachineOs();
        var cpu = GetMachineCpu(RuntimeInformation.ProcessArchitecture, machineOs);

        return CreateUserConfig(options, machineOs, Environment.Is64BitOperatingSystem, cpu, isMusic);
    }

    /// <summary>
    /// The user's config for a machine with the given operating system, bitness and CPU.
    /// </summary>
    public static Dictionary<string, string> CreateUserConfig(InstallOptions options, string machineOs, bool is64BitOs, string machineCpu, bool isMusic = false)
    {
        var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["oslist"] = options.Os ?? machineOs,
            ["osarch"] = options.OsArch ?? (is64BitOs ? "64" : "32"),
            ["oscpu"] = options.OsCpu ?? machineCpu,
            ["steamdeck"] = options.SteamDeck ? "1" : "0",
            ["realm"] = "steamglobal",
        };

        if (isMusic)
        {
            config["highqualityaudio"] = "1";
        }

        if (options.LowViolence)
        {
            config["lowviolence"] = "1";
        }

        return config;
    }

    /// <summary>
    /// The first of the depot's config keys whose value doesn't match the user's, or null when they all match. A key
    /// without a value is no constraint, and a key that the user has no value for never matches. Keys can be left out.
    /// </summary>
    public static string? FindMismatch(KeyValue? depotConfig, IReadOnlyDictionary<string, string> userConfig, Func<string, bool>? ignoreKey = null)
    {
        if (depotConfig == null)
        {
            return null;
        }

        foreach (var key in depotConfig.Children)
        {
            if (key.Name == null || string.IsNullOrWhiteSpace(key.Value))
            {
                continue;
            }

            if (ignoreKey != null && ignoreKey(key.Name))
            {
                continue;
            }

            userConfig.TryGetValue(key.Name, out var userValue);

            if (!ValueMatches(key.Value, userValue))
            {
                return key.Name.ToLowerInvariant();
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a config value of a depot matches the user's. Both are lists split on commas and spaces, compared without
    /// regard to case. It matches when any of the depot's entries matches: one of the user's, or for !x any of the
    /// user's that is not x, or for &lt;N and &gt;N a user's number below or above N.
    /// </summary>
    public static bool ValueMatches(string depotValue, string? userValue)
    {
        var depotTokens = Split(depotValue);

        if (depotTokens.Count == 0)
        {
            return true;
        }

        var userTokens = Split(userValue);

        if (userTokens.Count == 0)
        {
            return false;
        }

        foreach (var depotToken in depotTokens)
        {
            if (TokenMatches(depotToken, userTokens))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TokenMatches(string depotToken, List<string> userTokens)
    {
        if (depotToken[0] == '!')
        {
            var excluded = depotToken[1..];

            return userTokens.Exists(u => u != excluded);
        }

        if (depotToken[0] is '<' or '>')
        {
            var limit = ParseNumber(depotToken[1..]);

            if (depotToken[0] == '<')
            {
                return userTokens.Exists(u => ParseNumber(u) < limit);
            }

            return userTokens.Exists(u => ParseNumber(u) > limit);
        }

        return userTokens.Contains(depotToken);
    }

    // Like atoi: the leading digits, or 0
    private static int ParseNumber(string text)
    {
        var length = 0;

        if (length < text.Length && text[length] is '-' or '+')
        {
            length++;
        }

        while (length < text.Length && char.IsAsciiDigit(text[length]))
        {
            length++;
        }

        return int.TryParse(text.AsSpan(0, length), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) ? number : 0;
    }

    private static List<string> Split(string? value)
    {
        if (value == null)
        {
            return [];
        }

        return [.. value.ToLowerInvariant().Split(Separators, StringSplitOptions.RemoveEmptyEntries)];
    }

    /// <summary>
    /// The language to install: the requested one when the app has it, else its fallback, else English, else the first
    /// of the candidates.
    /// </summary>
    public static string ResolveLanguage(string requested, IReadOnlyList<string> candidates)
    {
        var language = requested.ToLowerInvariant();

        if (candidates.Contains(language))
        {
            return language;
        }

        var fallback = GetFallbackLanguage(language);

        if (fallback != null && candidates.Contains(fallback))
        {
            return fallback;
        }

        if (candidates.Contains(DefaultLanguage))
        {
            return DefaultLanguage;
        }

        return candidates.Count > 0 ? candidates[0] : DefaultLanguage;
    }

    // The language that Steam uses instead when an app lacks the first
    private static string? GetFallbackLanguage(string language) => language switch
    {
        "spanish" => "latam",
        "latam" => "spanish",
        "sc_schinese" => "schinese",
        _ => null,
    };

    /// <summary>
    /// The languages an app can be installed in: its base languages, and the languages of its depots that pass the
    /// platform check. DLC depots don't add languages.
    /// </summary>
    public static List<string> GetLanguageCandidates(KeyValue depots, IReadOnlyDictionary<string, string> userConfig, bool allPlatforms)
    {
        var baseLanguages = Split(depots["baselanguages"].Value);
        var candidates = baseLanguages.Count > 0 ? baseLanguages : [DefaultLanguage];

        foreach (var (_, depot) in AppInfo.GetDepots(depots))
        {
            var language = depot["config"]["language"].Value;

            if (string.IsNullOrWhiteSpace(language) || depot["dlcappid"].AsUnsignedInteger() != 0 || IsNoContentDlc(depot))
            {
                continue;
            }

            if (!allPlatforms && FindMismatch(depot["config"], userConfig, static key => !IsPlatformKey(key)) != null)
            {
                continue;
            }

            foreach (var token in Split(language))
            {
                if (!candidates.Contains(token))
                {
                    candidates.Add(token);
                }
            }
        }

        return candidates;
    }

    /// <summary>
    /// Picks the depots of an app that Steam would install with the options, in the order of the app's info, and lists the
    /// ones it skips. The language is the one the options ask for, or the one the app has instead.
    /// </summary>
    public static DepotSelection Select(KeyValue depots, InstallOptions options, IReadOnlyDictionary<string, string> userConfig)
    {
        var requested = options.Language ?? DefaultLanguage;
        var candidates = GetLanguageCandidates(depots, userConfig, options.AllPlatforms);
        var language = ResolveLanguage(requested, candidates);
        var config = new Dictionary<string, string>(userConfig, StringComparer.OrdinalIgnoreCase)
        {
            ["language"] = language,
        };
        var selected = new List<(uint ID, KeyValue Depot)>();
        var skipped = new List<SkippedDepot>();

        foreach (var (depotID, depot) in AppInfo.GetDepots(depots))
        {
            if (IsNoContentDlc(depot))
            {
                skipped.Add(new SkippedDepot(depotID, "no content DLC"));
                continue;
            }

            var mismatch = FindMismatch(depot["config"], config, key => IsIgnored(key, options));

            if (mismatch != null)
            {
                skipped.Add(new SkippedDepot(depotID, mismatch, depot["config"][mismatch].Value));
                continue;
            }

            selected.Add((depotID, depot));
        }

        return new DepotSelection(selected, skipped, language);
    }

    /// <summary>
    /// Picks the depots of an app with the options and this machine's config, which has the audio of soundtracks.
    /// </summary>
    public static DepotSelection SelectForApp(KeyValue appInfo, InstallOptions options)
    {
        var userConfig = CreateUserConfig(options, AppInfo.IsMusic(appInfo));

        return Select(appInfo["depots"], options, userConfig);
    }

    /// <summary>
    /// The ids that name no depot of the app, neither as a depot id nor as the DLC app id of a depot.
    /// </summary>
    public static List<uint> FindUnknownIds(KeyValue depots, IEnumerable<uint> ids)
    {
        var unknown = new List<uint>();

        foreach (var id in ids)
        {
            var found = AppInfo.GetDepots(depots).Any(d => NamesDepot(id, d.ID, d.Depot));

            if (!found)
            {
                unknown.Add(id);
            }
        }

        return unknown;
    }

    /// <summary>
    /// Adds the depots that the include ids name to a selection, and removes the ones that the exclude ids name. An id
    /// is a depot id, or a DLC app id for every depot of that DLC. Exclude wins over include, and the depots keep
    /// the order of the app's info.
    /// </summary>
    public static DepotSelection ApplyIncludeExclude(KeyValue depots, DepotSelection selection, IReadOnlyCollection<uint> include, IReadOnlyCollection<uint> exclude)
    {
        var selectedIDs = selection.Depots.Select(static d => d.ID).ToHashSet();
        var result = new List<(uint ID, KeyValue Depot)>();
        var skipped = new List<SkippedDepot>(selection.Skipped);
        var included = new List<SkippedDepot>();

        foreach (var (depotID, depot) in AppInfo.GetDepots(depots))
        {
            var isSelected = selectedIDs.Contains(depotID);

            if (exclude.Any(id => NamesDepot(id, depotID, depot)))
            {
                if (isSelected)
                {
                    skipped.Add(new SkippedDepot(depotID, ExcludedReason));
                }

                continue;
            }

            if (!isSelected && include.Any(id => NamesDepot(id, depotID, depot)))
            {
                var index = skipped.FindIndex(s => s.ID == depotID);

                if (index >= 0)
                {
                    included.Add(skipped[index]);
                    skipped.RemoveAt(index);
                }

                isSelected = true;
            }

            if (isSelected)
            {
                result.Add((depotID, depot));
            }
        }

        return selection with { Depots = result, Skipped = skipped, Included = included };
    }

    private static bool NamesDepot(uint id, uint depotID, KeyValue depot)
    {
        if (id == depotID)
        {
            return true;
        }

        var dlcAppID = depot["dlcappid"].AsUnsignedInteger();

        return dlcAppID != 0 && id == dlcAppID;
    }

    private static bool IsIgnored(string key, InstallOptions options)
    {
        if (options.AllPlatforms && IsPlatformKey(key))
        {
            return true;
        }

        return options.AllLanguages && key.Equals("language", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPlatformKey(string key) => PlatformKeys.Contains(key, StringComparer.OrdinalIgnoreCase);

    private static bool IsNoContentDlc(KeyValue depot)
    {
        var value = depot["nocontentdlc"];

        return value.AsBoolean() || string.Equals(value.Value, "true", StringComparison.OrdinalIgnoreCase);
    }
}
