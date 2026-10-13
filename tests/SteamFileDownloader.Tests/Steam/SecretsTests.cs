using TUnit.Core.Enums;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace SteamFileDownloader.Tests;

public partial class SecretsTests
{
    internal sealed class Sample
    {
        public string? Name { get; set; }

        public Dictionary<string, string> Tokens { get; set; } = [];
    }

    [JsonSerializable(typeof(Sample))]
    internal sealed partial class SampleJsonContext : JsonSerializerContext
    {
    }

    private static JsonTypeInfo<Sample> SampleInfo => SampleJsonContext.Default.Sample;

    [Test]
    [Arguments("refresh-token")]
    [Arguments("")]
    [Arguments("unicode é中文 and \"quotes\"")]
    [RunOn(OS.Windows)]
    public async Task ProtectedSecretRoundTrips(string secret)
    {
        var stored = Secrets.Protect(secret);

        await Assert.That(stored).IsNotEqualTo(secret);
        await Assert.That(Secrets.Unprotect(stored)).IsEqualTo(secret);
    }

    [Test]
    [RunOn(OS.Windows)]
    public async Task UnprotectReturnsPlainValuesAsTheyAre()
    {
        await Assert.That(Secrets.Unprotect("plain-token")).IsEqualTo("plain-token");
    }

    [Test]
    [RunOn(OS.Windows)]
    public async Task UnprotectThrowsOnCorruptedValue()
    {
        var stored = Secrets.Protect("secret");

        await Assert.That(() => Secrets.Unprotect(stored[..^8] + "AAAAAAAA")).Throws<CryptographicException>();
    }

    [Test]
    public async Task JsonRoundTrips()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "nested", "data.json");

        Secrets.WriteJson(path, new Sample { Name = "xpaw", Tokens = { ["a"] = "1" } }, SampleInfo);
        var read = Secrets.ReadJson(path, SampleInfo);

        await Assert.That(read.Name).IsEqualTo("xpaw");
        await Assert.That(read.Tokens["a"]).IsEqualTo("1");
    }

    [Test]
    public async Task WriteJsonReplacesFileAndLeavesNoTempFiles()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "data.json");

        Secrets.WriteJson(path, new Sample { Name = "first" }, SampleInfo);
        Secrets.WriteJson(path, new Sample { Name = "second" }, SampleInfo);

        await Assert.That(Secrets.ReadJson(path, SampleInfo).Name).IsEqualTo("second");
        await Assert.That(Directory.GetFiles(temp.Path).Length).IsEqualTo(1);
    }

    [Test]
    public async Task ReadJsonOfMissingFileReturnsNewValue()
    {
        using var temp = new TempDirectory();

        var read = Secrets.ReadJson(Path.Combine(temp.Path, "missing.json"), SampleInfo);

        await Assert.That(read.Name).IsNull();
        await Assert.That(read.Tokens.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ReadJsonOfBrokenFileReturnsNewValueAndMovesTheFileAside()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("data.json", "{ \"Name\": ");

        var read = Secrets.ReadJson(path, SampleInfo);

        await Assert.That(read.Name).IsNull();
        await Assert.That(File.Exists(path)).IsFalse();
        await Assert.That(File.ReadAllText(path + ".broken")).IsEqualTo("{ \"Name\": ");
    }

    [Test]
    public async Task ReadJsonOfNullDocumentReturnsNewValue()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("data.json", "null");

        await Assert.That(Secrets.ReadJson(path, SampleInfo).Tokens.Count).IsEqualTo(0);
    }
}
