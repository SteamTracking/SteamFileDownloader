using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using SteamKit2;

namespace SteamFileDownloader;

internal sealed class ManifestJob
{
    public uint AppID;
    public uint DepotID;
    public ulong ManifestID;
    public required string Branch;
    public required string OutputFolder;
    // The workshop item whose files the manifest has, which names the job as <item>:<manifest> instead of <depot>:<manifest>
    public ulong? ItemID;
    public byte[]? DepotKey;
    public DepotManifest? Manifest;

    // Where the manifest is saved once downloaded, and read from instead of downloading it again
    public string? ManifestFile;

    // The files.json filter of the track command; depots without one are only fetched for their manifest
    public FileFilter? Filter;

    // Lacking access is expected and only informational, like for one of every depot of an app
    public bool Optional;

    // Any failure is only a warning, since the run doesn't need this manifest, like track's depots that aren't in files.json
    public bool FailureIsFine;

    // Steam denied the depot key or manifest request code, as opposed to an error
    public bool NoAccess;

    // Other folders that may already hold identical files, which are copied instead of downloaded.
    // Newest manifests first, since only the first few with a same-size file are hashed
    public string[] ReuseFolders = [];

    /// <summary>
    /// The manifest's files by normalized path, built once, to find the archives next to VPK directory files.
    /// </summary>
    public Dictionary<string, DepotManifest.FileData> FilesByPath => field ??= IndexFiles();

    // Files and folders written from the manifest are dated to this
    public DateTime CreationTime => Manifest!.CreationTime;

    public override string ToString() => $"{ItemID ?? DepotID}:{ManifestID}";

    /// <summary>
    /// A job whose files go to &lt;parentFolder&gt;/&lt;manifest&gt;, where folders of other manifests may already have identical files.
    /// </summary>
    public static ManifestJob Create(uint appID, uint depotID, ulong manifestID, string branch, string parentFolder)
    {
        var outputFolder = Path.Combine(parentFolder, manifestID.ToString(CultureInfo.InvariantCulture));
        string[] reuseFolders = [];

        // Newest manifests first by their folder's date
        if (Directory.Exists(parentFolder))
        {
            reuseFolders = [.. new DirectoryInfo(parentFolder).GetDirectories()
                .Where(d => !string.Equals(d.FullName, outputFolder, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(static d => d.LastWriteTimeUtc)
                .Select(static d => d.FullName)];
        }

        var job = CreateIn(appID, depotID, manifestID, branch, outputFolder);
        job.ReuseFolders = reuseFolders;

        return job;
    }

    /// <summary>
    /// A job whose files go to the output folder itself, without other folders to copy identical files from.
    /// </summary>
    public static ManifestJob CreateIn(uint appID, uint depotID, ulong manifestID, string branch, string outputFolder) => new()
    {
        AppID = appID,
        DepotID = depotID,
        ManifestID = manifestID,
        Branch = branch,
        OutputFolder = outputFolder,
    };

    /// <summary>
    /// The manifest's files with their normalized paths, without its directories.
    /// </summary>
    public IEnumerable<(DepotManifest.FileData File, string Path)> GetFiles()
    {
        foreach (var file in Manifest!.Files!)
        {
            if (!IsDirectory(file))
            {
                yield return (file, FileFilter.NormalizePath(file.FileName));
            }
        }
    }

    private static bool IsDirectory(DepotManifest.FileData file) => (file.Flags & EDepotFileFlag.Directory) != 0;

    private Dictionary<string, DepotManifest.FileData> IndexFiles()
    {
        var files = new Dictionary<string, DepotManifest.FileData>(StringComparer.OrdinalIgnoreCase);

        foreach (var (file, path) in GetFiles())
        {
            files[path] = file;
        }

        return files;
    }
}
