using TUnit.Core.Enums;
namespace SteamFileDownloader.Tests;

public class OutputFilesTests
{
    private static ManifestJob JobIn(string folder) => new() { Branch = "public", OutputFolder = folder };

    [Test]
    [Arguments("game/bin/server.dll")]
    [Arguments("game\\bin\\server.dll")]
    [Arguments("/game/bin/server.dll")]
    [Arguments("\\game\\bin\\server.dll")]
    [Arguments("a/../game/bin/server.dll")]
    public async Task PathInsideTheOutputFolderIsResolved(string fileName)
    {
        using var temp = new TempDirectory();

        var path = OutputFiles.GetFinalPath(JobIn(temp.Path), fileName);

        await Assert.That(path).IsEqualTo(Path.Combine(temp.Path, "game", "bin", "server.dll"));
    }

    [Test]
    public async Task DotDotInsideThePathStaysInside()
    {
        using var temp = new TempDirectory();

        var path = OutputFiles.GetFinalPath(JobIn(temp.Path), "a/../b");

        await Assert.That(path).IsEqualTo(Path.Combine(temp.Path, "b"));
    }

    [Test]
    [Arguments("../x")]
    [Arguments("..\\x")]
    [Arguments("a/../../x")]
    [Arguments("/../x")]
    [Arguments("..")]
    [Arguments(".")]
    [Arguments("")]
    public async Task PathEscapingTheOutputFolderThrows(string fileName)
    {
        using var temp = new TempDirectory();

        await Assert.That(() => OutputFiles.GetFinalPath(JobIn(temp.Path), fileName)).Throws<InvalidDataException>();
    }

    [Test]
    [Arguments("C:/x")]
    [Arguments("C:\\x")]
    [Arguments("D:/game/x")]
    [RunOn(OS.Windows)]
    public async Task DriveLetterPathThrows(string fileName)
    {
        using var temp = new TempDirectory();

        await Assert.That(() => OutputFiles.GetFinalPath(JobIn(temp.Path), fileName)).Throws<InvalidDataException>();
    }

    [Test]
    [Arguments("a:b")]
    [Arguments("game/a:b")]
    [Arguments("game/a.dll:stream")]
    [Arguments("game/nul")]
    [Arguments("game/NUL")]
    [RunOn(OS.Windows)]
    public async Task StreamAndDeviceNamesThrow(string fileName)
    {
        using var temp = new TempDirectory();

        await Assert.That(() => OutputFiles.GetFinalPath(JobIn(temp.Path), fileName)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task OutputFolderAtADriveRootResolvesInsideIt()
    {
        using var temp = new TempDirectory();
        var root = Path.GetPathRoot(temp.Path)!;

        var path = OutputFiles.GetFinalPath(JobIn(root), "a/b.txt");

        await Assert.That(path).IsEqualTo(Path.Combine(root, "a", "b.txt"));
    }

    [Test]
    [RunOn(OS.Windows)]
    public async Task UncPathIsTreatedAsRelative()
    {
        using var temp = new TempDirectory();

        var path = OutputFiles.GetFinalPath(JobIn(temp.Path), "\\\\server\\share\\x");

        await Assert.That(path).IsEqualTo(Path.Combine(temp.Path, "server", "share", "x"));
    }

    [Test]
    public async Task OutputFolderWithTrailingSeparatorWorks()
    {
        using var temp = new TempDirectory();

        var path = OutputFiles.GetFinalPath(JobIn(temp.Path + Path.DirectorySeparatorChar), "a/b.txt");

        await Assert.That(path).IsEqualTo(Path.Combine(temp.Path, "a", "b.txt"));
    }

    [Test]
    public async Task SiblingFolderWithTheSamePrefixIsOutside()
    {
        using var temp = new TempDirectory();
        var job = JobIn(Path.Combine(temp.Path, "out"));

        await Assert.That(() => OutputFiles.GetFinalPath(job, "../out2/x")).Throws<InvalidDataException>();
    }

    [Test]
    public async Task WriteFileWritesBytesAndSetsLastWriteTime()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "file.bin");
        var time = new DateTime(2020, 5, 17, 3, 4, 5, DateTimeKind.Utc);

        await OutputFiles.WriteFileAsync(path, [1, 2, 3, 4], time);

        await Assert.That(await File.ReadAllBytesAsync(path)).IsEquivalentTo(new byte[] { 1, 2, 3, 4 });
        await Assert.That(File.GetLastWriteTimeUtc(path)).IsEqualTo(time);
    }

    [Test]
    public async Task WriteFileReplacesExistingContent()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("file.bin", "a much longer previous content");

        await OutputFiles.WriteFileAsync(path, [9], DateTime.UtcNow);

        await Assert.That(await File.ReadAllBytesAsync(path)).IsEquivalentTo(new byte[] { 9 });
    }

    [Test]
    public async Task WriteFileWritesEmptyFile()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "empty");

        await OutputFiles.WriteFileAsync(path, [], DateTime.UtcNow);

        await Assert.That(new FileInfo(path).Length).IsEqualTo(0L);
    }

    [Test]
    public async Task WriteFileDoesNotCreateParentFolders()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "missing", "file.bin");

        await Assert.That(async () => await OutputFiles.WriteFileAsync(path, [1], DateTime.UtcNow)).Throws<DirectoryNotFoundException>();
    }

    [Test]
    public async Task IsFileErrorCoversIoAndPathErrorsOnly()
    {
        await Assert.That(OutputFiles.IsFileError(new IOException())).IsTrue();
        await Assert.That(OutputFiles.IsFileError(new UnauthorizedAccessException())).IsTrue();
        await Assert.That(OutputFiles.IsFileError(new InvalidDataException())).IsTrue();
        await Assert.That(OutputFiles.IsFileError(new InvalidOperationException())).IsFalse();
    }
}
