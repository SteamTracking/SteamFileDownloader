using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace SteamFileDownloader;

/// <summary>
/// Paths and writes of the files in the output folders.
/// </summary>
internal static partial class OutputFiles
{
    private const uint FsctlSetSparse = 0x900C4;

    /// <summary>
    /// A file that can't be written, like on a full disk, locked by another program, or with a path that escapes the output folder.
    /// </summary>
    public static bool IsFileError(Exception e) => e is IOException or UnauthorizedAccessException or InvalidDataException;

    /// <summary>
    /// Resolves a depot file name inside the job's output folder, refusing paths that escape it.
    /// </summary>
    public static string GetFinalPath(ManifestJob job, string fileName)
    {
        var root = Path.GetFullPath(job.OutputFolder);
        var normalized = FileFilter.NormalizePath(fileName);

        // NTFS would write "a:b" to the stream b of the file a
        if (OperatingSystem.IsWindows() && normalized.Contains(':', StringComparison.Ordinal))
        {
            throw new InvalidDataException($"File name has a colon, which Windows doesn't allow: {fileName}");
        }

        var path = Path.GetFullPath(Path.Combine(root, normalized));

        // A drive root already ends with a separator
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;

        if (!path.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"File path escapes the output folder: {fileName}");
        }

        return path;
    }

    /// <summary>
    /// Writes a whole file, dated to the given time.
    /// </summary>
    public static async Task WriteFileAsync(string path, byte[] data, DateTime lastWriteTime, CancellationToken cancellationToken = default)
    {
        using var handle = File.OpenHandle(path, FileMode.Create, FileAccess.Write, FileShare.None, FileOptions.Asynchronous, data.Length);

        await RandomAccess.WriteAsync(handle, data, 0, cancellationToken);
        File.SetLastWriteTimeUtc(handle, lastWriteTime);
    }

    /// <summary>
    /// Dates the folders of the files, up to their manifest's folder, to when the manifest was created, like the files, so builds sort by date.
    /// Only once everything is written, since writing a file in a folder changes the folder's date.
    /// </summary>
    public static void SetFolderTimes(IEnumerable<(ManifestJob Job, string Path)> files)
    {
        var folders = new Dictionary<string, ManifestJob>(StringComparer.OrdinalIgnoreCase);

        foreach (var (job, path) in files)
        {
            var folder = Path.GetDirectoryName(path);

            // Stops at a folder that is already added, since its parents are too
            while (folder != null && folder.Length >= job.OutputFolder.Length && folders.TryAdd(folder, job))
            {
                folder = Path.GetDirectoryName(folder);
            }
        }

        foreach (var (folder, job) in folders)
        {
            try
            {
                var info = new DirectoryInfo(folder);

                if (info.LastWriteTimeUtc != job.CreationTime)
                {
                    info.LastWriteTimeUtc = job.CreationTime;
                }
            }
            catch (Exception e) when (IsFileError(e))
            {
                Log.Warn($"[{job}] Failed to set the date of {folder}: {e.Message}");
            }
        }
    }

    /// <summary>
    /// Marks a file as sparse, which NTFS needs to leave unwritten parts unallocated. Takes a handle opened without FileOptions.Asynchronous.
    /// </summary>
    public static void SetSparse(SafeFileHandle handle) => DeviceIoControl(handle, FsctlSetSparse, 0, 0, 0, 0, out _, 0);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, nint inBuffer, uint inBufferSize, nint outBuffer, uint outBufferSize, out uint bytesReturned, nint overlapped);
}
