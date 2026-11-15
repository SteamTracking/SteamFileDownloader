using System;
using System.Diagnostics.CodeAnalysis;

namespace SteamFileDownloader;

/// <summary>
/// Thrown by the command line parsers for a command line that is wrong: the problem, followed by the hint or usage line that helps fix it.
/// </summary>
[SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Only the parsers create it, always with a message")]
internal sealed class UsageException(string message) : Exception(message);
