namespace SteamFileDownloader.Tests;

public class DownloadProgressTests
{
    private const long MiB = 1024 * 1024;

    [Test]
    public async Task ProgressShowsNetworkAndDiskRates()
    {
        var (fraction, text) = DownloadProgress.FormatProgress(50 * MiB, 100 * MiB, 0, 200 * MiB, TimeSpan.FromSeconds(10), "1 / 2 files");

        await Assert.That(fraction).IsEqualTo(0.5);
        await Assert.That(text).IsEqualTo("100.0 MiB / 200.0 MiB · 1 / 2 files · 10s left · network 5.0 MiB/s · disk 10.0 MiB/s");
    }

    [Test]
    public async Task TransferShowsTheSizeOnDiskAndBothRates()
    {
        var text = DownloadProgress.FormatTransfer(50 * MiB, 100 * MiB, TimeSpan.FromSeconds(10));

        await Assert.That(text).IsEqualTo("100.0 MiB in 10s, network 50.0 MiB at 5.0 MiB/s, disk 10.0 MiB/s");
    }
}
