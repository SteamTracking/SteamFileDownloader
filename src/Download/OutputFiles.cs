using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace SteamFileDownloader;

internal enum LinkResult
{
    Created,
    AlreadyValid,
    OutsideFolder,
    NoPrivilege,
}

/// <summary>
/// Paths and writes of the files in the output folders.
/// </summary>
internal static partial class OutputFiles
{
    private const uint FsctlSetSparse = 0x900C4;
    private const int ErrorPrivilegeNotHeld = 1314;

    private const UnixFileMode UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    /// <summary>
    /// A file that can't be written, like on a full disk, locked by another program, or with a path that escapes the output folder.
    /// </summary>
    public static bool IsFileError(Exception e) => e is IOException or UnauthorizedAccessException or InvalidDataException;

    /// <summary>
    /// Resolves a depot file name inside the job's output folder, refusing paths that escape it.
    /// </summary>
    public static string GetFinalPath(ManifestJob job, string fileName) => GetFinalPath(job.OutputFolder, fileName);

    /// <summary>
    /// Resolves a file name inside an output folder, refusing paths that escape it.
    /// </summary>
    public static string GetFinalPath(string outputFolder, string fileName)
    {
        var root = Path.GetFullPath(outputFolder);
        var normalized = FileFilter.NormalizePath(fileName);

        // NTFS would write "a:b" to the stream b of the file a
        if (OperatingSystem.IsWindows() && normalized.Contains(':', StringComparison.Ordinal))
        {
            throw new InvalidDataException($"File name has a colon, which Windows doesn't allow: {fileName}");
        }

        var path = Path.GetFullPath(Path.Combine(root, normalized));

        if (!path.StartsWith(GetFolderPrefix(root), StringComparison.Ordinal))
        {
            throw new InvalidDataException($"File path escapes the output folder: {fileName}");
        }

        return path;
    }

    /// <summary>
    /// Opens a file of the output folders. Like Steam, a new file is created with every permission the umask allows, which
    /// makes it executable on Linux and macOS, since the executable flag of depot files is not reliable.
    /// The file is accessed through the stream's handle, with no buffering.
    /// </summary>
    public static FileStream Open(string path, FileMode mode, FileAccess access, FileOptions options = FileOptions.None, long preallocationSize = 0)
    {
        var streamOptions = new FileStreamOptions
        {
            Mode = mode,
            Access = access,
            Share = FileShare.None,
            Options = options,
            PreallocationSize = preallocationSize,
            BufferSize = 0,
        };

        if (!OperatingSystem.IsWindows())
        {
            streamOptions.UnixCreateMode = UnixCreateMode;
        }

        return new FileStream(path, streamOptions);
    }

    /// <summary>
    /// Writes a whole file, dated to the given time.
    /// </summary>
    public static async Task WriteFileAsync(string path, byte[] data, DateTime lastWriteTime, CancellationToken cancellationToken = default)
    {
        using var stream = Open(path, FileMode.Create, FileAccess.Write, FileOptions.Asynchronous, data.Length);
        var handle = stream.SafeFileHandle;

        await RandomAccess.WriteAsync(handle, data, 0, cancellationToken);
        File.SetLastWriteTimeUtc(handle, lastWriteTime);
    }

    /// <summary>
    /// Creates the folders of the manifest's directory entries, including empty ones, and adds their paths to the list.
    /// A file in the way is replaced by the folder. Returns false when any folder failed.
    /// </summary>
    public static bool CreateDirectories(ManifestJob job, List<string> created)
    {
        var success = true;

        foreach (var directory in job.GetDirectories())
        {
            try
            {
                var path = GetFinalPath(job, directory);

                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                Directory.CreateDirectory(path);
                created.Add(path);
            }
            catch (Exception e) when (IsFileError(e))
            {
                Log.Error($"[{job}] Failed to create folder {directory}: {e.Message}");
                success = false;
            }
        }

        return success;
    }

    /// <summary>
    /// Makes the path a symbolic link to the target, which is relative and has backslashes, replacing whatever is there.
    /// Links that would point outside the output folder are refused.
    /// </summary>
    public static LinkResult WriteLink(ManifestJob job, string path, string target, bool isDirectory)
    {
        var platformTarget = target.Replace('\\', Path.DirectorySeparatorChar);
        var resolved = Path.GetFullPath(platformTarget, Path.GetDirectoryName(path)!);

        if (!IsInFolder(Path.GetFullPath(job.OutputFolder), resolved))
        {
            return LinkResult.OutsideFolder;
        }

        var existing = new FileInfo(path);

        if (string.Equals(existing.LinkTarget, platformTarget, StringComparison.Ordinal))
        {
            return LinkResult.AlreadyValid;
        }

        DeleteExisting(existing);

        try
        {
            if (isDirectory)
            {
                Directory.CreateSymbolicLink(path, platformTarget);
            }
            else
            {
                File.CreateSymbolicLink(path, platformTarget);
            }
        }
        catch (Exception e) when (OperatingSystem.IsWindows() && (e is UnauthorizedAccessException || (e is IOException && (e.HResult & 0xFFFF) == ErrorPrivilegeNotHeld)))
        {
            return LinkResult.NoPrivilege;
        }

        return LinkResult.Created;
    }

    // A real folder goes with everything in it, while a link only goes itself
    private static void DeleteExisting(FileInfo existing)
    {
        if (existing.LinkTarget != null)
        {
            // Windows removes a link to a folder like a folder
            if (OperatingSystem.IsWindows() && existing.Attributes.HasFlag(FileAttributes.Directory))
            {
                Directory.Delete(existing.FullName);
            }
            else
            {
                File.Delete(existing.FullName);
            }
        }
        else if (Directory.Exists(existing.FullName))
        {
            Directory.Delete(existing.FullName, recursive: true);
        }
        else
        {
            File.Delete(existing.FullName);
        }
    }

    private static bool IsInFolder(string root, string path)
    {
        var folder = Path.TrimEndingDirectorySeparator(path);

        return string.Equals(folder, Path.TrimEndingDirectorySeparator(root), StringComparison.Ordinal) || path.StartsWith(GetFolderPrefix(root), StringComparison.Ordinal);
    }

    // A drive root already ends with a separator
    private static string GetFolderPrefix(string root) => Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;

    /// <summary>
    /// Dates the folders of the files, up to their manifest's folder, to when the manifest was created, like the files, so builds sort by date.
    /// Only once everything is written, since writing a file in a folder changes the folder's date.
    /// </summary>
    public static void SetFolderTimes(IEnumerable<(ManifestJob Job, string Path)> files, IEnumerable<(ManifestJob Job, string Path)>? directories = null)
    {
        var folders = new Dictionary<string, ManifestJob>(StringComparer.OrdinalIgnoreCase);

        foreach (var (job, path) in files)
        {
            AddWithParents(folders, job, Path.GetDirectoryName(path));
        }

        if (directories != null)
        {
            foreach (var (job, path) in directories)
            {
                AddWithParents(folders, job, path);
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

    private static void AddWithParents(Dictionary<string, ManifestJob> folders, ManifestJob job, string? folder)
    {
        while (folder != null && folder.Length >= job.OutputFolder.Length)
        {
            // Stops at a folder that is already added, since its parents are too
            if (!folders.TryAdd(folder, job))
            {
                break;
            }

            folder = Path.GetDirectoryName(folder);
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
