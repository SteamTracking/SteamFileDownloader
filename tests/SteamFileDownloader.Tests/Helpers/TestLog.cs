namespace SteamFileDownloader.Tests;

internal static class TestLog
{
    public static string[] GetProblemLines() =>
        TestContext.Current!.GetErrorOutput().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
}
