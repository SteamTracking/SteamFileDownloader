using System;
using System.Collections.Generic;
using System.Globalization;

namespace SteamFileDownloader;

/// <summary>
/// The options of the download command: where to install, and for which platform and language.
/// </summary>
internal sealed record InstallOptions(
    string? Dir = null,
    string? Os = null,
    string? OsArch = null,
    string? Language = null,
    bool LowViolence = false,
    bool AllPlatforms = false,
    bool AllLanguages = false,
    bool SteamDeck = false,
    string? OsCpu = null,
    IReadOnlyList<uint>? Include = null,
    IReadOnlyList<uint>? Exclude = null)
{
    private static readonly string[] OsNames = ["windows", "linux", "macos"];

    private static readonly string[] OsCpuNames = ["x86", "arm64"];

    /// <summary>
    /// Whether depots are named to add or remove after the filters.
    /// </summary>
    public bool HasIncludeOrExclude => Include is { Count: > 0 } || Exclude is { Count: > 0 };

    /// <summary>
    /// Whether the files are for another operating system than this one, which can't use the symbolic links of the depots.
    /// </summary>
    public bool IsForOtherOs => Os != null && !Os.Equals(DepotFilter.GetMachineOs(), StringComparison.Ordinal);

    public static InstallOptions From(Dictionary<string, string> options) => new(
        options.GetValueOrDefault("dir"),
        options.GetValueOrDefault("os")?.ToLowerInvariant(),
        options.GetValueOrDefault("osarch"),
        options.GetValueOrDefault("language")?.ToLowerInvariant(),
        options.ContainsKey("lowviolence"),
        options.ContainsKey("all-platforms"),
        options.ContainsKey("all-languages"),
        options.ContainsKey("steamdeck"),
        options.GetValueOrDefault("oscpu")?.ToLowerInvariant(),
        ParseOptionIds(options.GetValueOrDefault("include")),
        ParseOptionIds(options.GetValueOrDefault("exclude")));

    private static List<uint>? ParseOptionIds(string? list)
    {
        if (string.IsNullOrEmpty(list))
        {
            return null;
        }

        return ParseIds(list, out _);
    }

    /// <summary>
    /// The ids of a comma separated list. The first text that is not a nonzero id is left out and returned as invalid,
    /// which is null when all of them are ids.
    /// </summary>
    public static List<uint> ParseIds(string list, out string? invalid)
    {
        var ids = new List<uint>();

        invalid = null;

        foreach (var text in list.Split(','))
        {
            if (uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id != 0)
            {
                ids.Add(id);
            }
            else
            {
                invalid ??= text;
            }
        }

        return ids;
    }

    /// <summary>
    /// The error for options with a wrong value or that can't be combined, with the option names after the prefix, or
    /// null when they are fine.
    /// </summary>
    public string? Validate(string prefix)
    {
        if (Os != null && !OsNames.Contains(Os))
        {
            return $"{prefix}os needs windows, linux or macos, got \"{Os}\".";
        }

        if (OsArch != null && OsArch is not ("32" or "64"))
        {
            return $"{prefix}osarch needs 32 or 64, got \"{OsArch}\".";
        }

        if (OsCpu != null && !OsCpuNames.Contains(OsCpu))
        {
            return $"{prefix}oscpu needs x86 or arm64, got \"{OsCpu}\".";
        }

        if (AllPlatforms && (Os != null || OsArch != null))
        {
            return $"{prefix}all-platforms can't be combined with {prefix}os or {prefix}osarch.";
        }

        if (AllPlatforms && OsCpu != null)
        {
            return $"{prefix}all-platforms can't be combined with {prefix}oscpu.";
        }

        if (AllPlatforms && SteamDeck)
        {
            return $"{prefix}all-platforms can't be combined with {prefix}steamdeck.";
        }

        if (AllLanguages && Language != null)
        {
            return $"{prefix}all-languages can't be combined with {prefix}language.";
        }

        return null;
    }
}
