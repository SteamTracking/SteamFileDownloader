using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SteamFileDownloader;

/// <summary>
/// Help texts, written so the tool can be used without the readme. Each starts with what a truncated read needs
/// (usage, examples, syntax, options and the exit codes in short), and the details follow.
/// </summary>
internal static class Help
{
    public static string ExeName { get; } = Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? nameof(SteamFileDownloader);

    public static string UsageHint(string command = "")
    {
        var commandPart = command.Length > 0 ? " " + command : "";

        return $"Run \"{ExeName}{commandPart} --help\" for usage.";
    }

    public const string IssuesUrl = "https://github.com/SteamTracking/SteamFileDownloader/issues";

    // Characters that bash, zsh or PowerShell expand, split on or treat as syntax
    private static readonly SearchValues<char> ShellSpecialChars = SearchValues.Create(" *?()|$&;<>'\"\\[]{}!#~`");

    /// <summary>
    /// An argument for a suggested command line, quoted when the shell would expand or split it. Single quotes keep
    /// everything literal in bash, zsh and PowerShell, so double quotes are only for an argument with a single quote.
    /// </summary>
    public static string QuoteArgument(string argument)
    {
        if (argument.AsSpan().IndexOfAny(ShellSpecialChars) < 0)
        {
            return argument;
        }

        if (argument.Contains('\'', StringComparison.Ordinal))
        {
            return $"\"{argument}\"";
        }

        return $"'{argument}'";
    }

    private static string Targets => """
        Targets (all in one run: each run logs in to Steam once, and Steam rate limits logins):
          <depot>[:<manifest>]   the latest manifest on the branch, or that one, old ones too (2347771:8344780363095656278)
          <app>/<depot>...       a depot of another app (570/373303:latest, or 570/373303/<manifest>)
          ...@<branch>           a manifest on that branch, needed for beta-only manifests
        Without targets, every depot of the app except shared ones, skipping ones without access.
        """;

    private static string TargetNotes => """
        More on targets:
          <depot> is the same as <depot>:latest. The app id before the targets can be left out when every target
          names its app. Parallel runs on one account don't disconnect each other. "depots <app>" shows the latest
          manifests; old manifest ids are in the history of a GameTracking manifests/ folder.
        """;

    private static string Patterns => """
        Patterns (after --, case-insensitive, each tried against every target):
          server.dll, game/bin/win64/engine2.dll   a file name in any folder, or an exact path from the depot root
          *.vpk, game/**/bin/**                    globs: * and ? within a folder, ** across folders
          regex:tools/.*\.dll$                     .NET regex, matched anywhere in the path
          <dir.vpk>:<pattern>                      entries inside that VPK, like "game/core/pak01_dir.vpk:regex:^scripts/"
        """;

    private static string PatternNotes => """
        More on patterns:
          Depot paths use forward slashes. In <dir.vpk>:<pattern>, both parts use the pattern rules, and an empty
          <pattern> means all entries. A target without a match is fine (like a build before the file existed);
          a pattern that matches nothing in any target is an error (exit code 6). Quote patterns with characters
          the shell expands, like * ? ( | $.
        """;

    // Near the top of the help, since the full exit codes are at the end, past where a truncated read stops
    private static string ExitCodesSummary(string? details = "see the end")
    {
        var detailsNote = details == null ? "" : $" ({details})";

        return $"Exit codes: 0 ok, 1 failed, 2 usage error, 3 log in again, 4 rate limited, 5 no access, 6 no match{detailsNote}.";
    }

    private static string LoginOptions => """
          --username <name>   saved login to use instead of the last one, or with --password <pw> the account to log in as
          --anonymous         log in anonymously, enough for some free apps, and the default without saved logins
        """;

    private static string Options => $"""
        Options (anywhere before --):
          --output <dir>      root output folder (default: as saved with config output, else depots in the current folder)
          --branch <name>     branch for targets without @<branch> (default: public)
        {LoginOptions}
        """;

    private static string Streams => "Progress and problems ([ERROR] and [WARN], or ::error:: and ::warning:: when CI is set) go to stderr.";

    private static string ExitCodesText => """
        Exit codes:
          0  success
          1  something failed, see stderr. stdout has the rows of what succeeded; failed targets have
             none, which doesn't mean their files don't exist. Running the same command again retries
             only what failed
          2  usage error: an unknown option, or a missing or invalid argument. Fix the command line
          3  no saved login, or Steam rejected it: a person has to run "login"
          4  Steam is rate limiting logins: wait about 15 minutes
          5  only access failed: Steam gave no depot key or manifest request code for some targets.
             Another account may own them (--username), or the manifest is on another branch
             (@<branch>) or in another app (<app>/<depot>)
          6  a pattern matched nothing in any target, and nothing failed
        """;

    private const string UsageErrorExitCode = "  2  usage error: an unknown option, or a missing or invalid argument. Fix the command line";

    // For depots, which has no patterns or targets that only fail access
    private static string DepotsExitCodesText => $"""
        Exit codes:
          0  success
          1  something failed, see stderr
        {UsageErrorExitCode}
          3  no saved login, or Steam rejected it: a person has to run "login"
          4  Steam is rate limiting logins: wait about 15 minutes
        """;

    // GameTracking expects exit code 1 for track's argument errors
    private static string TrackExitCodesText => """
        Exit codes:
          0  success
          1  something failed, including the command line or files.json, see stderr
          3  no saved login for --username, or Steam rejected the login: a person has to run "login",
             or with --password, check --username and --password
          4  Steam is rate limiting logins: wait about 15 minutes
        """;

    public static string Overview => $"""
        {ExeName} downloads files from Steam depots, including old builds, without downloading whole depots.

        Usage:
          {ExeName} login                                     save a Steam login (QR code)
          {ExeName} depots <app>...                           list apps' depots and branches
          {ExeName} ls <app> [<target>...] [-- <pattern>...]  list files without downloading
          {ExeName} ls <app> -- <pattern>...                  find which depot of the app has a file
          {ExeName} get <app> [<target>...] [-- <pattern>...] download files, all without patterns
          {ExeName} workshop <subcommand> ...                 workshop items: info, search, ls and get
          {ExeName} dd <DepotDownloader arguments>            run a DepotDownloader command line
          {ExeName} config output <dir>                       save the default output folder
          {ExeName} track <app> --output <dir>                download the files in ./files.json (GameTracking)
          {ExeName} <command> --help                          details and examples; --version prints the version

        Typical run, with every target in one call and rows of downloaded files on stdout:
          {ExeName} get 730 2347771 2347773 --output D:/SteamDepots -- server.dll "regex:lib(tier0|engine2)\.so$"
        {ExitCodesSummary(null)}

        {Targets}

        {Patterns}

        {Options}

        Files go to <output>/<depot>/<manifest>/<path>. Files already there, or identical in another manifest
        of the depot, aren't downloaded again, so reuse the same output folder. Manifests are saved to
        <output>/manifests/<depot>_<manifest>.manifest, where Steam's depotcache files can be copied too.

        Saved logins: {AccountStore.FilePath}
        Report bugs: {IssuesUrl}
        """;

    public static string Track => $"""
        Downloads the files listed in ./files.json from the latest manifests of a branch, for GameTracking.

        Usage:
          {ExeName} track <app> --output <dir> [--username <name> [--password <password>] | --anonymous]
            [--branch <name>] [--manifests <folder>] [--save-manifest] [--save-all-manifests]

        Examples:
          {ExeName} track 570 --output . --save-all-manifests
          An older build into another folder, with the files.json and manifests of an old commit:
            git worktree add ../old <commit>
            cd ../old && {ExeName} track 570 --output . --manifests manifests

        Options:
          <app>, --appid <app>   Steam app id
          --output <dir>         output folder
          --username <name>      Steam username, or anonymous; without --password a saved login
                                 (default: the last saved login, else anonymous)
          --anonymous            the same as --username anonymous
          --password <password>  Steam password, needs --username
          --branch <name>        branch to download (default: public), also for --manifests from a beta
          --manifests <folder>   manifest ids from the manifest_<depot>.txt files of an older run
          --save-manifest        save the manifests of the depots in files.json as text to <dir>/manifests/
          --save-all-manifests   save the manifests of every depot as text to <dir>/manifests/

        files.json maps depot ids to lists of patterns (comments and trailing commas are allowed, and
        backslashes are doubled in JSON strings):
          game/csgo/steam.inf                        exact path
          game/**/bin/win64/*.dll                    glob for the whole path: * and ? within a folder, ** across folders
          regex:.+?\.(dll|exe)                       .NET regex for the whole path
          game/dota/pak01_dir.vpk:<pattern>          entries inside that VPK, with the pattern rules of get,
                                                     like "game/dota/pak01_dir.vpk:regex:^scripts/npc/.+\.txt$"
                                                     or "pak01_dir.vpk:regex:\.(vsndevts_c|vxml_c)$" for any folder

          Paths, globs and regexes are case-sensitive, while VPK entry patterns ignore case like get's.
          A pattern that matches nothing is a warning, not an error.

        Files go to <dir>/<path>, without depot and manifest folders. Files already on disk with the
        right hash are kept. When everything succeeded, <dir>/steam_buildid.txt gets the build id.
        Messages go to stdout, and warnings and errors to stderr.

        To download an older build, pass --manifests with the manifests/ folder of an older run, like
        from an old commit of a GameTracking repository. Each depot uses the manifest id in its
        manifest_<depot>.txt, and depots without one are skipped. VPK entries need the manifests of the
        depots their archives are in too, so use a run with --save-all-manifests. steam_buildid.txt isn't written.

        For VPK entries, the _dir.vpk is downloaded whole, and its archives only get the chunks with the entries
        (the rest is sparse), so VRF filters must match only those entries; others fail their CRC check.

        {TrackExitCodesText}
        """;

    public static string Ls => $"""
        Lists files of depot manifests without downloading them, over one Steam login.

        Usage:
          {ExeName} ls <app> [<target>...] [-- <pattern>...]
          {ExeName} ls <app> -- <pattern>...     every depot of the app, to find which one has a file
        stdout has a tab-separated row per file: depot  manifest  sha1  size  path. Without patterns, every file.
        {ExitCodesSummary()}

        Examples:
          {ExeName} ls 730 2347779:4784444484596788209 2347779:2356538687884552308 -- source1import.exe
          {ExeName} ls 570 -- hammer.dll
          {ExeName} ls 730 2347770 -- "game/core/pak01_dir.vpk:regex:^scripts/"

        {Targets}

        {Patterns}

        {Options}

        Output:
          A VPK entry's row is: depot  manifest  crc32  size  <dir.vpk>:<entry path>. For those, only the
          _dir.vpk is downloaded, to <output>/<depot>/<manifest>/.
          To find the build where a file changed, list every candidate manifest in one run and compare
          the sha1 column, then get one manifest per distinct sha1. Manifest dates are on stderr.

        {Streams}

        {TargetNotes}

        {PatternNotes}

        {ExitCodesText}
        """;

    public static string Get => $"""
        Downloads matching files from depot manifests, over one Steam login.

        Usage:
          {ExeName} get <app> [<target>...] [-- <pattern>...]
        stdout has a tab-separated row per matching file: depot  manifest  absolute path. Without patterns, every
        file is downloaded, which can be tens of gigabytes per depot, and stdout has no rows.
        {ExitCodesSummary()}

        Examples:
          {ExeName} get 730 2347771 2347773 -- server.dll "regex:lib(tier0|engine2)\.so$"
          {ExeName} get 730 2347771:8344780363095656278 2347771:latest -- game/csgo/bin/win64/server.dll
          {ExeName} get 730 2347770 2347771 --install-dir D:/Games/CS2

        {Targets}

        {Patterns}

        {Options}

        Output:
          Files go to <output>/<depot>/<manifest>/<path>, dated to when their manifest was created. VPK entries
          go to a folder named after the _dir.vpk without .vpk, like
          2347770/7820179980365915207/game/core/pak01_dir/scripts/scenes.vdata_c, and only the archive chunks
          they are in are downloaded, plus the _dir.vpk, which gets no row.
          Files already on disk with the right hash are kept, and ones with the same hash in another target
          or downloaded manifest of the depot are copied. Rows include files that already were on disk.

        Install folder:
          --install-dir <dir> puts the files of every manifest in <dir>/<path>, like a Steam install. Where two
          have different files at one path, the later target's or depot's file is kept, with a warning. Without
          targets, that is every depot, of every platform and language, so name the depots to install.
          Files that are no longer in the manifests stay, and manifests are still saved in <output>/manifests.
          <dir.vpk>:<pattern> patterns can't be used with it.

        {Streams}

        {TargetNotes}

        {PatternNotes}

        {ExitCodesText}
        """;

    public static string Depots => $"""
        Lists apps' depots, to find which one holds what. Needs a Steam login.

        Usage:
          {ExeName} depots <app>... [--branch <name>]
        stdout has a tab-separated row per branch: app  branch  build id  updated
        then a row per depot: target  max size  notes

        Examples:
          {ExeName} depots 570
          {ExeName} depots 730 570 --branch beta

        Options:
          --branch <name>     branch whose manifests are listed (default: public)
        {LoginOptions}

        Output:
          Each app's branches come before its depots, all of them whatever --branch is. updated is a unix
          time, or -. A depot row's target always has a slash and a branch row's app never does.
          target is <app>/<depot>:<manifest>, ready to pass to ls or get. The manifest is the latest on the
          branch, or public's when the branch has none ("manifest from public" in notes), and a depot with
          neither is just <app>/<depot>. max size is in bytes, or -. notes lists the name, os, arch, language,
          dlc and "shared from app <app>"; a shared depot's target names the app it is shared from.

        {DepotsExitCodesText}
        """;

    public static string DepotDownloader => $"""
        Runs a DepotDownloader command line as get, or as ls with -manifest-only.

        Usage:
          {ExeName} dd -app <app> [-depot <depot> [-manifest <manifest>]]... [-filelist <file>] [-dir <dir>]
            [-branch <name>] [-username <name> [-password <pw>]] [-manifest-only]
          {ExeName} dd -pubfile <item> [-filelist <file>] [-dir <dir>] [-username <name> [-password <pw>]]
            [-manifest-only]
          {ExeName} dd -app <app> -ugc <manifest> [-filelist <file>] [-dir <dir>] [-username <name> [-password <pw>]]
            [-manifest-only]

        Example:
          {ExeName} dd -app 730 -depot 2347771 -manifest 8344780363095656278 -filelist files.txt

        -depot and -manifest pair up in order. Without -depot every depot of the app is used. File list
        lines are exact paths from the depot root, without globs, or "regex:" patterns, like in DepotDownloader,
        and a list without entries is an error. Without -filelist every file is downloaded, like get without
        patterns, so stdout has no rows. -dir sets the root output folder; files still go to <dir>/<depot>/<manifest>/<path>.
        Without -dir, it is the folder saved with config output, else depots in the current folder.
        -password logs in with it on every run, without saving a login. Other DepotDownloader flags, like
        -remember-password or -os, are unsupported, so pick depots with -depot.
        Password protected branches (-branchpassword) are not supported.
        For new command lines, use get or ls, which also take several apps, branches and VPK entries.

        -pubfile runs as workshop get (or ls), so files go to <dir>/workshop/<item>/<manifest>/<path>. -ugc is a
        manifest of the app's workshop depot, like get <app>/<app>:<manifest>; legacy workshop files aren't supported.

        {ExitCodesText}
        """;

    private static string WorkshopItems => """
        Items (all in one run, since each run logs in to Steam once):
          <item>               an item's id or its page's URL (3070715607, or .../filedetails/?id=3070715607)
          <item>:<manifest>    an older version for ls and get, which workshop info lists with its history
          <collection>         the items in a collection, skipping ones without files to download
        """;

    private static string WorkshopItemNotes => """
        More on items:
          Items of the legacy workshop, from before it kept files in depots, like those of Left 4 Dead 2, are not
          implemented.
        """;

    private static string WorkshopExitCodesText => $"""
        Exit codes:
          0  success
          1  something failed, see stderr
        {UsageErrorExitCode}
          3  no saved login, or Steam rejected it: a person has to run "login"
          4  Steam is rate limiting logins: wait about 15 minutes
          5  only access failed: some items are private, or need another account (--username)
        """;

    private static string WorkshopFilesExitCodesText => $"""
        Exit codes:
          0  success
          1  something failed, see stderr. stdout has the rows of what succeeded; failed items have none,
             which doesn't mean their files don't exist. Running the same command again retries only what failed
        {UsageErrorExitCode}
          3  no saved login, or Steam rejected it: a person has to run "login"
          4  Steam is rate limiting logins: wait about 15 minutes
          5  only access failed: some items are private, or need another account (--username)
          6  a pattern matched nothing in any item, and nothing failed
        """;

    public static string Workshop => $"""
        Workshop items, whose files are manifests in the workshop depot of the app they're for.

        Usage:
          {ExeName} workshop info <item>...                                app, depot, manifest, size and title
          {ExeName} workshop info <item>... --history [--limit <n|all>]    versions, newest first
          {ExeName} workshop search <app> <text>... [--limit <n|all>]      find items by text
          {ExeName} workshop ls <item>... [-- <pattern>...]                list files without downloading
          {ExeName} workshop get <item>... [-- <pattern>...]               download files, all without patterns
          {ExeName} workshop <subcommand> --help                           details and examples

        Files go to <output>/workshop/<item>/<manifest>/<path>, and ls and get take the patterns of get.

        {WorkshopItems}

        {WorkshopItemNotes}
        """;

    public static string WorkshopLs => $"""
        Lists files of workshop items without downloading them, over one Steam login.

        Usage:
          {ExeName} workshop ls <item>... [-- <pattern>...]
        stdout has a tab-separated row per file: item  manifest  sha1  size  path. Without patterns, every file.
        {ExitCodesSummary()}

        Examples:
          {ExeName} workshop ls 3070715607
          {ExeName} workshop ls 3818412809 -- "*.vpk"

        {WorkshopItems}

        {Patterns}

        Options:
          --output <dir>      root output folder for _dir.vpk files (default: as saved with config output, else depots)
        {LoginOptions}

        Output:
          A VPK entry's row is: item  manifest  crc32  size  <dir.vpk>:<entry path>.
          For those, only the _dir.vpk is downloaded. stderr has a line per item with its manifest and title.
          To see what changed between versions, list them in one run and compare the sha1 column.

        {Streams}

        {WorkshopItemNotes}

        {PatternNotes}

        {WorkshopFilesExitCodesText}
        """;

    public static string WorkshopGet => $"""
        Downloads matching files of workshop items, over one Steam login.

        Usage:
          {ExeName} workshop get <item>... [-- <pattern>...]
        stdout has a tab-separated row per matching file: item  manifest  absolute path. Without patterns, every
        file is downloaded, and stdout has no rows.
        {ExitCodesSummary()}

        Examples:
          {ExeName} workshop get 3070715607 -- "*.vpk"
          {ExeName} workshop get https://steamcommunity.com/sharedfiles/filedetails/?id=3818412809

        {WorkshopItems}

        {Patterns}

        Options:
          --output <dir>      root output folder (default: as saved with config output, else depots in the current folder)
        {LoginOptions}

        Output:
          Files go to <output>/workshop/<item>/<manifest>/<path>, dated to when their manifest was created.
          Files already on disk with the right hash are kept, and ones with the same hash in another downloaded
          version of the item are copied. Rows include files that already were on disk. stderr has a line per
          item with its manifest and title.

        {Streams}

        {WorkshopItemNotes}

        {PatternNotes}

        {WorkshopFilesExitCodesText}
        """;

    public static string WorkshopInfo => $"""
        Shows workshop items: their app, its workshop depot, their current manifest, update time, size and title.

        Usage:
          {ExeName} workshop info <item>...
          {ExeName} workshop info <item>... --history [--limit <n|all>]
        stdout has a tab-separated row per item: item  app  depot  manifest  updated  size  title
        and with --history, one per version of each item instead, newest first: <item>:<manifest>  updated  change note

        Examples:
          {ExeName} workshop info 3070715607 https://steamcommunity.com/sharedfiles/filedetails/?id=2852945816
          {ExeName} workshop info 3070715607 --history --limit all

        {WorkshopItems}

        Options:
          --history           list the versions of each item
          --limit <n|all>     how many versions --history lists per item (default: 20)
        {LoginOptions}

        Output:
          A collection's row is followed by one per item in it. manifest is "collection" for a collection, and
          "legacy" for a legacy item. depot is - when the app has no workshop depot. updated is in UTC, size
          in bytes.

          With --history, changes that only edited an item's text keep its manifest, so they are one version,
          dated to when the manifest was uploaded. stderr has the number of changes of each item.

        {Streams}

        {WorkshopItemNotes}

        {WorkshopExitCodesText}
        """;

    public static string WorkshopSearch => $"""
        Searches an app's workshop items by text, ranked like the workshop's search, to find their ids.

        Usage:
          {ExeName} workshop search <app> <text>... [--limit <n|all>]
        stdout has a tab-separated row per item, best match first:
          item  app  depot  manifest  updated  size  subscribers  title

        Examples:
          {ExeName} workshop search 730 de_dust2
          {ExeName} workshop search 570 custom hero chaos --limit 100

        Options:
          --limit <n|all>     how many items to list (default: 20)
        {LoginOptions}

        Output:
          manifest is - for an item whose current manifest is only in its history (see workshop info), and
          "legacy" for a legacy item. stderr has the number of matching items.

        {Streams}

        {WorkshopExitCodesText}
        """;

    public static string Login => $"""
        Logs in to Steam and saves a refresh token, so other commands never ask for a password.

        Usage:
          {ExeName} login                      show a QR code to scan in the Steam mobile app
          {ExeName} login --username <name>    use a password and Steam Guard code instead
            [--password <pw>]                  the password, instead of typing it
          {ExeName} login list                 list saved logins (see login list --help)
          {ExeName} login remove <name>...     revoke saved logins and forget them (see login remove --help)

        Scan the QR code in the Steam mobile app (Steam Guard tab, scan icon). A person has to scan it
        or type the password, so an agent should ask the user to run this.

        Log in with each account to save several. The last one is the default, and --username picks
        another for content only that account owns. Logging in again replaces the account's token and
        revokes the old one, but login --username only makes a saved login that still works the default.
        Tokens are encrypted for the Windows user, and runs renew them in their last 30 days.

        Saved logins: {AccountStore.FilePath}

        Exit codes:
          0  success
          1  something failed, see stderr
        {UsageErrorExitCode}
          3  Steam rejected the login
          4  Steam is rate limiting logins: wait about 15 minutes
        """;

    public static string LoginList => $"""
        Lists saved logins, without logging in to Steam.

        Usage:
          {ExeName} login list
        stdout has a tab-separated row per saved login: username  default or -  expiry date (UTC)

        stderr has a warning for each expired login, and each one another Windows user or machine saved;
        log in again with those accounts.

        Saved logins: {AccountStore.FilePath}

        Exit codes:
          0  success
          1  something failed, see stderr
        {UsageErrorExitCode}
        """;

    public static string LoginRemove => $"""
        Revokes saved logins with Steam, so copies of their tokens stop working too, and forgets them.

        Usage:
          {ExeName} login remove <name>...

        Examples:
          {ExeName} login remove myaccount

        Revoking takes only the token, so it needs no Steam login. When it fails, the login is still removed
        here, with a warning that a copy of the token keeps working until it expires. Removing the default
        makes the last saved of the remaining logins the default.

        Saved logins: {AccountStore.FilePath}

        Exit codes:
          0  success, also when revoking failed
          1  a name has no saved login, or the saved logins can't be written
        {UsageErrorExitCode}
        """;

    public static string Config => $"""
        Saves settings for later runs.

        Usage:
          {ExeName} config output <dir>      save the root output folder of get, ls, workshop ls and get, and dd
          {ExeName} config output            print the saved folder, or say that there is none
          {ExeName} config output --reset    forget the saved folder

        Examples:
          {ExeName} config output D:/SteamDepots

        Options:
          --reset             forget the saved folder, so files go to depots in the current folder again

        The folder is saved as an absolute path. An output folder given on the command line overrides it, and
        without either, files go to depots in the current folder. track always needs its own output folder.

        Settings: {Settings.FilePath}

        Exit codes:
          0  success
          1  the settings can't be read or written, see stderr
        {UsageErrorExitCode}
        """;

    /// <summary>
    /// Prints help instead of running when there are no arguments, or --help is asked for, and the usage of a command
    /// run without the arguments it needs. Returns the exit code when handled, or null to run the command.
    /// </summary>
    public static int? TryPrint(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]) || args[0] == "help")
        {
            var command = args.Length > 1 && !IsHelp(args[1]) ? GetCommandName(args[1..]) : null;
            Console.WriteLine(GetCommandHelp(command) ?? Overview);
            return ExitCodes.Ok;
        }

        var name = GetCommandName(args);
        var commandHelp = GetCommandHelp(name);
        var bare = args.Length == name.Count(static c => c == ' ') + 1;

        // --help anywhere before the patterns, and commands that only group subcommands
        var asksForHelp = Array.Exists(ArgumentParser.SplitPatterns(args).Options, IsHelp);
        var onlyGroupsSubcommands = bare && name is "workshop" or "config" or "login remove";

        if (asksForHelp || onlyGroupsSubcommands)
        {
            Console.WriteLine(commandHelp ?? Overview);
            return ExitCodes.Ok;
        }

        if (commandHelp == null || !bare)
        {
            return null;
        }

        // Commands that need no arguments run bare instead
        if (name is "login" or "login list" or "config output")
        {
            return null;
        }

        Console.Error.WriteLine(GetShortUsage(name, commandHelp));

        return ExitCodes.ForUsageError(name);
    }

    // The sections of a command's help that a short usage has
    private static readonly string[] ShortUsageSections = ["Usage:", "Examples:", "Example:"];

    /// <summary>
    /// The usage and examples of a command's help, and how to see all of it.
    /// </summary>
    internal static string GetShortUsage(string command, string help)
    {
        var lines = help.ReplaceLineEndings("\n").Split('\n');
        var shortUsage = new List<string>();

        foreach (var header in ShortUsageSections)
        {
            var start = Array.IndexOf(lines, header);

            if (start < 0)
            {
                continue;
            }

            if (shortUsage.Count > 0)
            {
                shortUsage.Add("");
            }

            // The section's indented lines, up to the first line that isn't
            shortUsage.Add(header);
            shortUsage.AddRange(lines.Skip(start + 1).TakeWhile(static l => l.StartsWith(' ')));
        }

        shortUsage.Add("");
        shortUsage.Add($"Run \"{ExeName} {command} --help\" for details.");

        return string.Join(Environment.NewLine, shortUsage);
    }

    private static string? GetCommandHelp(string? command) => command switch
    {
        "get" => Get,
        "ls" => Ls,
        "depots" => Depots,
        "dd" => DepotDownloader,
        "login" => Login,
        "login list" => LoginList,
        "login remove" => LoginRemove,
        "track" => Track,
        "workshop" => Workshop,
        "workshop info" => WorkshopInfo,
        "workshop search" => WorkshopSearch,
        "workshop ls" => WorkshopLs,
        "workshop get" => WorkshopGet,
        "config" or "config output" => Config,
        _ => null,
    };

    // The first argument, or the first two for a subcommand
    private static string GetCommandName(string[] args) => ArgumentParser.GetCommandName(args[0], args[1..]);

    private static bool IsHelp(string arg) => arg is "-h" or "--help" or "-?" or "/?";
}
