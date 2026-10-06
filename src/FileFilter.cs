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

    // files.json "vpk:" entries, archives holding these extensions are downloaded whole
    public string[] PakExtensions { get; private set; } = [];

    // Command line "<dir.vpk>:<entry>" entries, only the needed chunks of archives are downloaded
    public List<VpkPattern> VpkPatterns { get; } = [];

    public bool IsMatch(string path) => Patterns.Any(p => p.Regex.IsMatch(path));

    [JsonSourceGenerationOptions(AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip)]
    [JsonSerializable(typeof(Dictionary<uint, List<string>>))]
    private sealed partial class FilesJsonContext : JsonSerializerContext
    {
    }

    /// <summary>
    /// Loads files.json, which maps depot ids to exact file names, "regex:" patterns and "vpk:" extension lists.
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
                if (fileMatch.StartsWith("vpk:", StringComparison.Ordinal))
                {
                    filter.PakExtensions = [.. filter.PakExtensions, .. fileMatch["vpk:".Length..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];
                    continue;
                }

                var pattern = fileMatch.StartsWith(RegexPrefix, StringComparison.Ordinal) ? fileMatch[RegexPrefix.Length..] : Regex.Escape(fileMatch);

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
            var vpkSplit = pattern.IndexOf(DirVpkSuffix, StringComparison.OrdinalIgnoreCase);

            if (vpkSplit >= 0)
            {
                // The directory file follows the same rules, so a bare "pak01_dir.vpk" matches it in any folder
                var dirPattern = pattern[..(vpkSplit + DirVpkSuffix.Length - 1)];
                var entryPattern = pattern[(vpkSplit + DirVpkSuffix.Length)..];

                filter.VpkPatterns.Add(new VpkPattern(pattern, ParsePattern(dirPattern), ParsePattern(entryPattern.Length == 0 ? "regex:" : entryPattern)));
                continue;
            }

            filter.Patterns.Add(new FilePattern(pattern, ParsePattern(pattern)));
        }

        return filter;
    }

    // Exact paths when there is a slash, otherwise a file name in any folder, or an unanchored regex like DepotDownloader's
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
            regex = pattern.Contains('/', StringComparison.Ordinal) ? $"^{Regex.Escape(pattern)}$" : $"(^|/){Regex.Escape(pattern)}$";
        }

        return new Regex(regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static string NormalizePath(string path) => path.Replace('\\', '/').TrimStart('/');
}
