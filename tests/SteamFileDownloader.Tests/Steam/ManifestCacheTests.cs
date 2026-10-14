using System.Security.Cryptography;
using System.Text;

namespace SteamFileDownloader.Tests;

public class ManifestCacheTests
{
    private static ManifestJob SavedJob(TempDirectory temp)
    {
        var job = TestManifests.Job(temp.Path, 7, TestManifests.File("game/bin/a.dll", new byte[] { 1, 2, 3 }, new byte[] { 4 }), TestManifests.Directory("game/bin"));
        job.Manifest!.ManifestGID = job.ManifestID;
        job.DepotKey = new byte[32];
        job.ManifestFile = ManifestCache.GetPath(Path.Combine(temp.Path, "manifests"), job.DepotID, job.ManifestID);

        return job;
    }

    [Test]
    public async Task PathUsesTheDepotcacheNaming()
    {
        await Assert.That(Path.GetFileName(ManifestCache.GetPath("manifests", 2347779, 3537825762417016625))).IsEqualTo("2347779_3537825762417016625.manifest");
    }

    [Test]
    public async Task SavedManifestLoadsBack()
    {
        using var temp = new TempDirectory();
        var job = SavedJob(temp);

        ManifestCache.Save(job);
        var loaded = ManifestCache.Load(job);

        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded!.DepotID).IsEqualTo(job.DepotID);
        await Assert.That(loaded.ManifestGID).IsEqualTo(job.ManifestID);
        await Assert.That(loaded.CreationTime).IsEqualTo(TestManifests.CreationTime);
        await Assert.That(loaded.FilenamesEncrypted).IsFalse();

        var original = job.Manifest!.Files!.Select(static f => (FileFilter.NormalizePath(f.FileName), f.TotalSize, Convert.ToHexString(f.FileHash), f.Chunks.Count));
        var roundTripped = loaded.Files!.Select(static f => (FileFilter.NormalizePath(f.FileName), f.TotalSize, Convert.ToHexString(f.FileHash), f.Chunks.Count));

        await Assert.That(roundTripped.Order()).IsEquivalentTo(original.Order());
        await Assert.That(Directory.GetFiles(Path.GetDirectoryName(job.ManifestFile)!)).HasSingleItem();
    }

    // Like the CDN serves names without the depot key: the IV encrypted with ECB, then the name and a null with CBC, in base64
    private static string EncryptName(string name, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Key = key;

        var iv = RandomNumberGenerator.GetBytes(16);
        var encrypted = aes.EncryptEcb(iv, PaddingMode.None).Concat(aes.EncryptCbc(Encoding.UTF8.GetBytes(name + "\0"), iv, PaddingMode.PKCS7));

        return Convert.ToBase64String([.. encrypted]);
    }

    [Test]
    public async Task EncryptedManifestIsSavedEncryptedAndLoadsBackDecrypted()
    {
        using var temp = new TempDirectory();
        var key = RandomNumberGenerator.GetBytes(32);
        string[] names = [@"game\bin\win64\server.dll", @"game\csgo\steam.inf", @"game\bin\win64"];
        var job = SavedJob(temp);
        job.DepotKey = key;
        job.Manifest = new SteamKit2.DepotManifest
        {
            DepotID = job.DepotID,
            ManifestGID = job.ManifestID,
            CreationTime = TestManifests.CreationTime,
            FilenamesEncrypted = true,
            Files =
            [
                new(EncryptName(names[0], key), [], default, 3, SHA1.HashData(new byte[] { 1, 2, 3 }), "", true, 1) { Chunks = { TestManifests.Chunk([1, 2, 3], 0) } },
                new(EncryptName(names[1], key), [], default, 1, SHA1.HashData(new byte[] { 4 }), "", true, 1) { Chunks = { TestManifests.Chunk([4], 0) } },
                new(EncryptName(names[2], key), [], SteamKit2.EDepotFileFlag.Directory, 0, new byte[20], "", true, 0),
            ],
        };

        ManifestCache.Save(job);

        var saved = SteamKit2.DepotManifest.LoadFromFile(job.ManifestFile!);
        await Assert.That(saved!.FilenamesEncrypted).IsTrue();

        var loaded = ManifestCache.Load(job);

        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded!.FilenamesEncrypted).IsFalse();
        await Assert.That(loaded.Files!.Select(static f => FileFilter.NormalizePath(f.FileName)).Order(StringComparer.Ordinal))
            .IsEquivalentTo(names.Select(FileFilter.NormalizePath).Order(StringComparer.Ordinal), TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(loaded.Files!.Single(static f => f.FileName.EndsWith("steam.inf", StringComparison.Ordinal)).TotalSize).IsEqualTo(1ul);
    }

    [Test]
    public async Task MissingFileLoadsNothing()
    {
        using var temp = new TempDirectory();

        await Assert.That(ManifestCache.Load(SavedJob(temp))).IsNull();
    }

    [Test]
    public async Task OtherManifestLoadsNothing()
    {
        using var temp = new TempDirectory();
        var job = SavedJob(temp);
        ManifestCache.Save(job);

        // A file of another manifest under this manifest's name
        job.ManifestID++;

        await Assert.That(ManifestCache.Load(job)).IsNull();
    }

    [Test]
    [Arguments(0)]
    [Arguments(10)]
    [Arguments(-10)]
    public async Task BrokenFileLoadsNothing(int length)
    {
        using var temp = new TempDirectory();
        var job = SavedJob(temp);
        ManifestCache.Save(job);

        // Truncated to a length, or from the end when negative
        var bytes = File.ReadAllBytes(job.ManifestFile!);
        File.WriteAllBytes(job.ManifestFile!, bytes[..(length >= 0 ? length : bytes.Length + length)]);

        await Assert.That(ManifestCache.Load(job)).IsNull();
    }
}
