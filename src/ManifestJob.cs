using System;
using System.Collections.Generic;
using SteamKit2;

namespace SteamFileDownloader;

internal sealed class ManifestJob
{
    public uint AppID;
    public uint DepotID;
    public ulong ManifestID;
    public required string Branch;
    public required string OutputFolder;
    public byte[]? DepotKey;
    public DepotManifest? Manifest;

    // The files.json filter of the track command; depots without one are only fetched for their manifest
    public FileFilter? Filter;

    // Lacking access is expected and only informational, like for a depot from an "all" target
    public bool Optional;

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

    public override string ToString() => $"{DepotID}:{ManifestID}";

    private Dictionary<string, DepotManifest.FileData> IndexFiles()
    {
        var files = new Dictionary<string, DepotManifest.FileData>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Manifest!.Files!)
        {
            files[FileFilter.NormalizePath(file.FileName)] = file;
        }

        return files;
    }
}
