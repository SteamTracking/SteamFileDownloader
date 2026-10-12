using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SteamFileDownloader;

internal sealed record FilePattern(string Source, Regex Regex);

// Selects entries inside the VPKs whose _dir.vpk depot path matches Dir
internal sealed record VpkPattern(string Source, Regex Dir, Regex Entry);

/// <summary>
/// Decides which files of a depot to download.
/// </summary>
internal sealed partial class FileFilter
{
    private const string RegexPrefix = "regex:";
    private const string DirVpkSuffix = "_dir.vpk:";

    public List<FilePattern> Patterns { get; } = [];

    // "<dir.vpk>:<entry>" entries, only the needed chunks of archives are downloaded
    public List<VpkPattern> VpkPatterns { get; } = [];

    public List<FilePattern> GetMatchingPatterns(string path) => [.. Patterns.Where(p => p.Regex.IsMatch(path))];

    // The VPK patterns whose directory file this is
    public IReadOnlyList<VpkPattern> GetVpkPatterns(string path) => VpkPatterns.Count == 0 ? [] : [.. VpkPatterns.Where(p => p.Dir.IsMatch(path))];

    [JsonSourceGenerationOptions(AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip)]
    [JsonSerializable(typeof(Dictionary<uint, List<string>>))]
    private sealed partial class FilesJsonContext : JsonSerializerContext
    {
    }

    /// <summary>
    /// Loads files.json, which maps depot ids to case-sensitive paths, globs and "regex:" patterns, and VPK entry patterns.
    /// </summary>
    public static Dictionary<uint, FileFilter> LoadFilesJson(string path)
    {
        var files = JsonSerializer.Deserialize(File.ReadAllText(path), FilesJsonContext.Default.DictionaryUInt32ListString)
            ?? throw new InvalidDataException($"{path} is empty.");

        var filters = new Dictionary<uint, FileFilter>();

        foreach (var (depotID, fileMatches) in files)
        {
            var filter = new FileFilter();

            foreach (var fileMatch in fileMatches)
            {
                if (filter.TryAddVpkPattern(fileMatch))
                {
                    continue;
                }

                // Would otherwise be a file name that matches nothing
                if (fileMatch.StartsWith("vpk:", StringComparison.Ordinal))
                {
                    var extensions = string.Join('|', fileMatch["vpk:".Length..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

                    throw new InvalidDataException($"\"{fileMatch}\" in depot {depotID} is no longer supported. Use \"pak01_dir.vpk:regex:\\\\.({extensions})$\" instead, which downloads only the chunks the entries are in.");
                }

                // Paths here are whole paths from the depot root, so globs match the whole path too
                var pattern = fileMatch.StartsWith(RegexPrefix, StringComparison.Ordinal) ? fileMatch[RegexPrefix.Length..] : GlobToRegex(fileMatch);

                filter.Patterns.Add(new FilePattern(fileMatch, new Regex($"^({pattern})$", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture)));
            }

            filters.Add(depotID, filter);
        }

        return filters;
    }

    /// <summary>
    /// Parses command line patterns, see the get command for the syntax.
    /// </summary>
    public static FileFilter FromPatterns(IEnumerable<string> patterns)
    {
        var filter = new FileFilter();

        foreach (var pattern in patterns)
        {
            if (pattern.StartsWith('/'))
            {
                Log.Warn($"Pattern \"{pattern}\" starts with /, but depot paths are relative to the depot, so it matches as \"{NormalizePath(pattern)}\".");
            }
            else if (WindowsPathRegex().IsMatch(NormalizePath(pattern)))
            {
                Log.Warn($"Pattern \"{pattern}\" is a Windows path, which no depot path matches. Git Bash turns arguments that start with / into Windows paths, so leave out the leading slash.");
            }

            if (!filter.TryAddVpkPattern(pattern))
            {
                filter.Patterns.Add(new FilePattern(pattern, ParsePattern(pattern)));
            }
        }

        return filter;
    }

    // "<path>_dir.vpk:<entry pattern>", the same on the command line and in files.json. Returns false for other patterns
    private bool TryAddVpkPattern(string pattern)
    {
        var vpkSplit = pattern.IndexOf(DirVpkSuffix, StringComparison.OrdinalIgnoreCase);

        if (vpkSplit < 0)
        {
            return false;
        }

        // The directory file follows the same rules, so a bare "pak01_dir.vpk" matches it in any folder
        var dirPattern = pattern[..(vpkSplit + DirVpkSuffix.Length - 1)];
        var entryPattern = pattern[(vpkSplit + DirVpkSuffix.Length)..];

        VpkPatterns.Add(new VpkPattern(pattern, ParsePattern(dirPattern), ParsePattern(entryPattern.Length == 0 ? "regex:" : entryPattern)));
        return true;
    }

    [GeneratedRegex(@"^[A-Za-z]:/")]
    private static partial Regex WindowsPathRegex();

    // Exact paths or globs when there is a slash, otherwise a file name in any folder, or an unanchored regex like DepotDownloader's
    private static Regex ParsePattern(string pattern)
    {
        string regex;

        if (pattern.StartsWith(RegexPrefix, StringComparison.Ordinal))
        {
            regex = pattern[RegexPrefix.Length..];
        }
        else
        {
            pattern = NormalizePath(pattern);
            regex = $"{(pattern.Contains('/', StringComparison.Ordinal) ? "^" : "(^|/)")}{GlobToRegex(pattern)}$";
        }

        return new Regex(regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    [GeneratedRegex(@"(\*\*/)+")]
    private static partial Regex RepeatedGlobstarRegex();

    // "**/" is any number of folders, "**" anything, "*" anything within a folder, and "?" one character. Text without them
    // stays literal. Repeated "**/" mean the same as one, and each would make the regex backtrack a lot more
    private static string GlobToRegex(string glob) => Regex.Escape(RepeatedGlobstarRegex().Replace(glob, "**/"))
        .Replace(@"\*\*/", "(.*/)?", StringComparison.Ordinal)
        .Replace(@"\*\*", ".*", StringComparison.Ordinal)
        .Replace(@"\*", "[^/]*", StringComparison.Ordinal)
        .Replace(@"\?", "[^/]", StringComparison.Ordinal);

    public static string NormalizePath(string path) => path.Replace('\\', '/').TrimStart('/');
}
