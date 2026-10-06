using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SteamFileDownloader;

internal static partial class Log
{
    private const int BarWidth = 30;

    // VT sequences: go to the line start and erase it, hide and show the cursor, turn line wrapping off and on
    private const string ClearLine = "\r\e[2K";
    private const string HideCursor = "\e[?25l";
    private const string ShowCursor = "\e[?25h";
    private const string NoWrap = "\e[?7l";
    private const string Wrap = "\e[?7h";

    private static readonly bool IsCI = Environment.GetEnvironmentVariable("CI") != null;
    private static readonly Lock WriteLock = new();

    // Messages go to stderr when stdout carries results
    private static bool ResultsOnStdout;

    // The progress currently on the terminal, redrawn below every message
    private static (double Fraction, string Text)? ProgressState;

    /// <summary>
    /// Whether a person is watching stderr, so a progress line can update in place. Agents and logs capture it instead.
    /// </summary>
    private static bool IsInteractive { get; } = !IsCI && !Console.IsErrorRedirected && EnableVirtualTerminal();

    /// <summary>
    /// For commands whose stdout carries results: progress moves to stderr, and per-file progress,
    /// which the results already list, is hidden. Results are buffered, since there can be millions of rows.
    /// </summary>
    public static void UseStdoutForResults()
    {
        ResultsOnStdout = true;

#pragma warning disable CA2000 // Lives as long as the process, which flushes it before exiting
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false), 64 * 1024));
#pragma warning restore CA2000
    }

    /// <summary>
    /// A result line on stdout, for commands that use it for results.
    /// </summary>
    public static void Result(string line)
    {
        lock (WriteLock)
        {
            Console.Out.WriteLine(line);
        }
    }

    /// <summary>
    /// Writes out buffered results, before exiting.
    /// </summary>
    public static void Flush() => Console.Out.Flush();

    public static void Info(string message) => Write(ResultsOnStdout ? Console.Error : Console.Out, message);

    public static void Detail(string message)
    {
        if (!ResultsOnStdout)
        {
            Write(Console.Out, message);
        }
    }

    /// <summary>
    /// A problem that stops the command or fails the run.
    /// </summary>
    public static void Error(string message) => WriteProblem("error", "ERROR", message);

    public static void Warn(string message) => WriteProblem("warning", "WARN", message);

    /// <summary>
    /// Shows a progress bar with a text after it, updating in place while the returned object is alive.
    /// Only on an interactive terminal, so agents and logs never see it.
    /// </summary>
    public static IAsyncDisposable? StartProgress(Func<(double Fraction, string Text)> render) => IsInteractive ? new Progress(render) : null;

    private sealed class Progress(Func<(double Fraction, string Text)> render) : IAsyncDisposable
    {
        private readonly Timer Timer = new(_ => SetProgress(render()), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(250));

        public async ValueTask DisposeAsync()
        {
            // Waits for a running callback, so the line can't be drawn again after it's cleared
            await Timer.DisposeAsync();

            lock (WriteLock)
            {
                if (ProgressState != null)
                {
                    Console.Error.Write(ClearLine + ShowCursor);
                    ProgressState = null;
                }
            }
        }
    }

    private static void SetProgress((double Fraction, string Text) state)
    {
        lock (WriteLock)
        {
            // An empty line separates it from the log above
            if (ProgressState == null)
            {
                Console.Error.Write(Environment.NewLine + HideCursor);
            }

            ProgressState = state;
            Console.Error.Write(ClearLine + ProgressFrame());
        }
    }

    // A whole frame is one write, so the terminal never shows a half drawn line
    private static string ProgressFrame()
    {
        var (fraction, text) = ProgressState!.Value;
        fraction = Math.Clamp(fraction, 0, 1);

        var filled = (int)Math.Round(fraction * BarWidth);
        var bar = new string('\u2501', filled);
        var rest = new string('\u2501', BarWidth - filled);

        // Without wrapping, a line wider than the terminal is cut off instead of spilling onto a second row
        return $"{NoWrap}\e[32m{bar}\e[90m{rest}\e[32m{(int)(fraction * 100),4}%\e[0m  {text}{Wrap}";
    }

    private static void WriteProblem(string ciCommand, string label, string message)
    {
        Write(Console.Error, IsCI ? $"::{ciCommand}::{message}" : $"[{label}] {message}");
    }

    private static void Write(TextWriter writer, string message)
    {
        lock (WriteLock)
        {
            // Rows written so far come first, so stdout and stderr stay in order when they go to one place
            if (ResultsOnStdout)
            {
                Console.Out.Flush();
            }

            if (ProgressState == null)
            {
                writer.WriteLine(message);
            }
            else if (writer == Console.Error)
            {
                // The message takes the progress line's place, and the line is drawn again below it, in one write
                Console.Error.Write(ClearLine + message + Environment.NewLine + ProgressFrame());
            }
            else
            {
                Console.Error.Write(ClearLine);
                writer.WriteLine(message);
                Console.Error.Write(ProgressFrame());
            }
        }
    }

    // Windows Terminal handles VT sequences on its own, the older console only once it's turned on
    private static bool EnableVirtualTerminal()
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        const uint EnableVirtualTerminalProcessing = 0x0004;
        const int StdErrorHandle = -12;

        var handle = GetStdHandle(StdErrorHandle);

        return GetConsoleMode(handle, out var mode) && SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetStdHandle(int stdHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleMode(nint consoleHandle, out uint mode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleMode(nint consoleHandle, uint mode);
}
