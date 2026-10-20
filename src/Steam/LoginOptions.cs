using System.Collections.Generic;

namespace SteamFileDownloader;

/// <summary>
/// How a command logs in: anonymously, with a username and password, or with a saved login, picked by the username or
/// the default one.
/// </summary>
internal sealed record LoginOptions(string? Username = null, string? Password = null, bool Anonymous = false)
{
    public static LoginOptions From(Dictionary<string, string> options) => new(
        options.GetValueOrDefault("username"),
        options.GetValueOrDefault("password"),
        options.ContainsKey("anonymous"));

    /// <summary>
    /// The error for options that can't be combined, with the option names after the prefix, or null when they can be.
    /// </summary>
    public string? Validate(string prefix)
    {
        if (Anonymous && Username != null)
        {
            return $"{prefix}anonymous and {prefix}username can't be combined.";
        }

        if (Password == null)
        {
            return null;
        }

        if (Anonymous)
        {
            return $"{prefix}password can't be combined with {prefix}anonymous.";
        }

        if (Username == null)
        {
            return $"{prefix}password needs {prefix}username. Without both, the last saved login is used.";
        }

        if (LogOnCredentials.IsAnonymousName(Username))
        {
            return $"{prefix}password can't be combined with {prefix}username anonymous.";
        }

        return null;
    }

    /// <summary>
    /// The credentials to log on with, anonymous ones when no username is given and no login is saved. Returns null
    /// with an error explaining how to log in.
    /// </summary>
    public LogOnCredentials? GetCredentials()
    {
        if (Anonymous)
        {
            return LogOnCredentials.Anonymous;
        }

        if (Username != null && Password != null)
        {
            return new LogOnCredentials(Username, Password);
        }

        return AccountStore.LoadCredentials(Username);
    }
}
