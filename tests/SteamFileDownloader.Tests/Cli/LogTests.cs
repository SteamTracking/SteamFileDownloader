namespace SteamFileDownloader.Tests;

public class LogTests
{
    [Test]
    [Arguments(0, "file", null, "0 files")]
    [Arguments(1, "file", null, "1 file")]
    [Arguments(2, "file", null, "2 files")]
    [Arguments(1, "VPK entry", "VPK entries", "1 VPK entry")]
    [Arguments(3, "VPK entry", "VPK entries", "3 VPK entries")]
    public async Task PluralUsesTheSingularOnlyForOne(int count, string noun, string? plural, string expected)
    {
        await Assert.That(Log.Plural(count, noun, plural)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(1, 1, "1 manifest")]
    [Arguments(3, 3, "3 manifests")]
    [Arguments(0, 1, "0 of 1 manifests")]
    [Arguments(2, 3, "2 of 3 manifests")]
    public async Task PluralOfSaysTheTotalOnlyWhenItIsNotAll(int count, int total, string expected)
    {
        await Assert.That(Log.PluralOf(count, total, "manifest")).IsEqualTo(expected);
    }
}
