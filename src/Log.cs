using System;
using System.IO;
using System.Text;

namespace SteamFileDownloader;

internal static class Log
{
    private static readonly bool IsCI = Environment.GetEnvironmentVariable("CI") != null;

    private static TextWriter Output = Console.Out;
    private static bool Quiet;

    /// <summary>
    /// For commands whose stdout carries results: progress moves to stderr, and per-file progress,
    /// which the results already list, is hidden. Results are buffered, since there can be millions of rows.
    /// </summary>
    public static void UseStdoutForResults()
    {
        Output = Console.Error;
        Quiet = true;

#pragma warning disable CA2000 // Lives as long as the process, which flushes it before exiting
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false), 64 * 1024));
#pragma warning restore CA2000
    }

    /// <summary>
    /// A result line on stdout, for commands that use it for results.
    /// </summary>
    public static void Result(string line) => Console.Out.WriteLine(line);

    /// <summary>
    /// Writes out buffered results, before exiting.
    /// </summary>
    public static void Flush() => Console.Out.Flush();

    public static void Info(string message) => Write(Output, message);

    public static void Detail(string message)
    {
        if (!Quiet)
        {
            Write(Output, message);
        }
    }

    /// <summary>
    /// A problem that stops the command or fails the run.
    /// </summary>
    public static void Error(string message) => WriteProblem("error", "ERROR", message);

    public static void Warn(string message) => WriteProblem("warning", "WARN", message);

    private static void WriteProblem(string ciCommand, string label, string message)
    {
        Write(Console.Error, IsCI ? $"::{ciCommand}::{message}" : $"[{label}] {message}");
    }

    private static void Write(TextWriter writer, string message)
    {
        // Rows written so far come first, so stdout and stderr stay in order when they go to one place
        if (Quiet)
        {
            Console.Out.Flush();
        }

        writer.WriteLine(message);
    }
}
