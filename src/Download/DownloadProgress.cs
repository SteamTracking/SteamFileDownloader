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

    // Bytes already on disk count as done, without being downloaded
    public static (double Fraction, string Text) FormatProgress(long downloadedBytes, long skippedBytes, long totalBytes, TimeSpan elapsed, string count)
    {
        var doneBytes = downloadedBytes + skippedBytes;
        var total = Math.Max(totalBytes, 1);
        var speed = Speed(downloadedBytes, elapsed);
        var left = speed > 0 ? $"{Separator}{FormatDuration((total - doneBytes) / speed)} left" : "";

        return ((double)doneBytes / total, $"{FormatSize(doneBytes)} / {FormatSize(total)}{Separator}{FormatSize((long)speed)}/s{Separator}{count}{left}");
    }

    public static string FormatTransfer(long bytes, TimeSpan elapsed) => $"{FormatSize(bytes)} in {FormatDuration(elapsed.TotalSeconds)} at {FormatSize((long)Speed(bytes, elapsed))}/s";

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
