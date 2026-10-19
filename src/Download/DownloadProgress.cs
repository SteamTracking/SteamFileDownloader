using System;

namespace SteamFileDownloader;

/// <summary>
/// The sizes, speeds and times in download progress lines and summaries.
/// </summary>
internal static class DownloadProgress
{
    // Between the parts of a progress line
    public const string Separator = " · ";

    public static double Speed(long bytes, TimeSpan elapsed) => bytes / Math.Max(elapsed.TotalSeconds, 0.001);

    // Bytes already on disk count as done, without being downloaded. Sizes and the time left go by the bytes written
    // to disk, which are what the files add up to. The rates go last, since a terminal narrower than the line cuts its end
    public static (double Fraction, string Text) FormatProgress(long networkBytes, long diskBytes, long skippedBytes, long totalBytes, TimeSpan elapsed, string count)
    {
        var doneBytes = diskBytes + skippedBytes;
        var total = Math.Max(totalBytes, 1);
        var speed = Speed(diskBytes, elapsed);
        var left = speed > 0 ? $"{Separator}{FormatDuration((total - doneBytes) / speed)} left" : "";

        return ((double)doneBytes / total, $"{FormatSize(doneBytes)} / {FormatSize(total)}{Separator}{count}{left}{Separator}{FormatRates(networkBytes, diskBytes, elapsed)}");
    }

    /// <summary>
    /// The rate of chunks as they come over the network, compressed, and as they are written to disk, like Steam shows them.
    /// </summary>
    public static string FormatRates(long networkBytes, long diskBytes, TimeSpan elapsed)
    {
        var network = FormatSize((long)Speed(networkBytes, elapsed));
        var disk = FormatSize((long)Speed(diskBytes, elapsed));

        return $"network {network}/s{Separator}disk {disk}/s";
    }

    public static string FormatTransfer(long networkBytes, long diskBytes, TimeSpan elapsed)
    {
        var network = FormatSize((long)Speed(networkBytes, elapsed));
        var disk = FormatSize((long)Speed(diskBytes, elapsed));

        return $"{FormatSize(diskBytes)} in {FormatDuration(elapsed.TotalSeconds)}, network {FormatSize(networkBytes)} at {network}/s, disk {disk}/s";
    }

    public static string FormatDuration(double seconds) => TimeSpan.FromSeconds(Math.Min(seconds, TimeSpan.MaxValue.TotalSeconds / 2)) switch
    {
        { TotalHours: >= 1 } t => $"{(int)t.TotalHours}h {t.Minutes}m",
        { TotalMinutes: >= 1 } t => $"{t.Minutes}m {t.Seconds}s",
        var t => $"{t.Seconds}s",
    };

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:F1} GiB",
        >= 1024 * 1024 => $"{bytes / 1024.0 / 1024:F1} MiB",
        _ => $"{bytes / 1024.0:F1} KiB",
    };
}
