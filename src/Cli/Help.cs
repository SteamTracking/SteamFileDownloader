using System;
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

    public static string UsageHint(string command = "") => $"Run \"{ExeName}{(command.Length > 0 ? " " + command : "")} --help\" for usage.";

    private static string Targets => """
        Targets (all in one run: each run logs in to Steam once, and Steam rate limits logins):
          <depot>[:<manifest>]   the latest manifest on the branch, or that one, old ones too (2347771:8344780363095656278)
          all                    every depot of the app except shared ones, skipping ones without access
          <app>/<depot>...       a depot of another app (570/373303:latest, or 570/373303/<manifest>)
          ...@<branch>           a manifest on that branch, needed for beta-only manifests
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
          a pattern that matches nothing in any target is an error (exit code 2). Quote patterns with characters
          the shell expands, like * ? ( | $.
        """;

    // Near the top of the help, since the full exit codes are at the end, past where a truncated read stops
    private static string ExitCodesSummary(string? details = "see the end") =>
        $"Exit codes: 0 ok, 1 failed, 2 a pattern matched nothing, 3 log in again, 4 rate limited, 5 no access{(details == null ? "" : $" ({details})")}.";

    private static string LoginOptions => """
          --username <name>   saved login to use instead of the last one
          --anonymous         log in anonymously, enough for some free apps
        """;

    private static string Options => $"""
        Options (anywhere before --):
          --output <dir>      root output folder (default: depots in the current folder)
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
          2  a pattern matched nothing in any target, and nothing failed
          3  no saved login, or Steam rejected it: a person has to run "login"
          4  Steam is rate limiting logins: wait about 15 minutes
          5  only access failed: Steam gave no depot key or manifest request code for some targets.
             Another account may own them (--username), or the manifest is on another branch
             (@<branch>) or in another app (<app>/<depot>)
        """;

    // For depots and track, which have no patterns or targets that only fail access
    private static string BasicExitCodesText => """
        Exit codes:
          0  success
          1  something failed, see stderr
          3  no saved login, or Steam rejected the login: a person has to run "login", or for track
             with --password, check --username and --password
          4  Steam is rate limiting logins: wait about 15 minutes
        """;

    public static string Overview => $"""
        {ExeName} downloads files from Steam depots, including old builds, without downloading whole depots.

        Usage:
          {ExeName} login                                     save a Steam login (QR code)
          {ExeName} depots <app>...                           list apps' depots and branches
          {ExeName} ls [<app>] <target>... [-- <pattern>...]  list files without downloading
          {ExeName} get [<app>] <target>... -- <pattern>...   download matching files (or --all-files)
          {ExeName} workshop <subcommand> ...                 workshop items: info, search, ls and get
          {ExeName} dd <DepotDownloader arguments>            run a DepotDownloader command line
          {ExeName} track --appid <app> --output <dir>        download the files in ./files.json (GameTracking)
          {ExeName} <command> --help                          details and examples
          {ExeName} --version                                 print the version

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
        """;

    public static string Track => $"""
        Downloads the files listed in ./files.json from the latest manifests of a branch, for GameTracking.

        Usage:
          {ExeName} track --appid <app> --output <dir> [--username <name> [--password <password>]]
            [--branch <name>] [--manifests <folder>] [--save-manifest] [--save-all-manifests]

        Examples:
          {ExeName} track --appid 570 --output . --save-all-manifests
          An older build into another folder, with the files.json and manifests of an old commit:
            git worktree add ../old <commit>
            cd ../old && {ExeName} track --appid 570 --output . --manifests manifests

        Options:
          --appid <app>          Steam app id
          --output <dir>         output folder
          --username <name>      Steam username, or anonymous; without --password a saved login
                                 (default: the last saved login)
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

        {BasicExitCodesText}
        """;

    public static string Ls => $"""
        Lists files of depot manifests without downloading them, over one Steam login.

        Usage:
          {ExeName} ls [<app>] <target>... [-- <pattern>...]
        stdout has a tab-separated row per file: depot  manifest  sha1  size  path. Without patterns, every file.
        {ExitCodesSummary()}

        Examples:
          {ExeName} ls 730 2347779:4784444484596788209 2347779:2356538687884552308 -- source1import.exe
          {ExeName} ls 570 all -- hammer.dll
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
          {ExeName} get [<app>] <target>... -- <pattern>...
          {ExeName} get [<app>] <target>... --all-files     every file, which can be tens of gigabytes per depot
        stdout has a tab-separated row per matching file: depot  manifest  absolute path (none with --all-files).
        {ExitCodesSummary()}

        Examples:
          {ExeName} get 730 2347771 2347773 -- server.dll "regex:lib(tier0|engine2)\.so$"
          {ExeName} get 730 2347771:8344780363095656278 2347771:latest -- game/csgo/bin/win64/server.dll
          {ExeName} get 730/2347771 570/373303 -- game/bin/win64/resourcecompiler.dll

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

        {Streams}

        {TargetNotes}

        {PatternNotes}

        {ExitCodesText}
        """;

    public static string Depots => $"""
        Lists apps' depots, to find which one holds what. Needs a Steam login.

        Usage:
          {ExeName} depots <app>... [--branch <name>]
        stdout has a tab-separated row per depot: app  depot  manifest  max size  notes

        Examples:
          {ExeName} depots 570
          {ExeName} depots 730 570 --branch beta

        Options:
          --branch <name>     branch whose manifests are listed (default: public)
        {LoginOptions}

        Output:
          manifest is the latest on the branch, or public's when the branch has none ("manifest from public"
          in notes), or - when neither has one. max size is in bytes, or -. notes lists the name, os, arch,
          language, dlc and "shared from app <app>"; a shared depot is downloaded as <that app>/<depot>.
          Branches, with build ids and dates, are on stderr.

        {BasicExitCodesText}
        """;

    public static string DepotDownloader => $"""
        Runs a DepotDownloader command line as get, or as ls with -manifest-only.

        Usage:
          {ExeName} dd -app <app> [-depot <depot> [-manifest <manifest>]]... [-filelist <file>] [-dir <dir>]
            [-branch <name>] [-username <name>] [-manifest-only]
          {ExeName} dd -pubfile <item> [-filelist <file>] [-dir <dir>] [-username <name>] [-manifest-only]
          {ExeName} dd -app <app> -ugc <manifest> [-filelist <file>] [-dir <dir>] [-username <name>] [-manifest-only]

        Example:
          {ExeName} dd -app 730 -depot 2347771 -manifest 8344780363095656278 -filelist files.txt

        -depot and -manifest pair up in order. Without -depot every depot of the app is used. File list
        lines are exact paths from the depot root, without globs, or "regex:" patterns, like in DepotDownloader,
        and a list without entries is an error. Without -filelist every file is downloaded, like get --all-files,
        so stdout has no rows. -dir sets the root output folder; files still go to <dir>/<depot>/<manifest>/<path>.
        Other flags, like -remember-password or -loginid, are skipped: a random login id is used.
        -os and -language don't filter depots, so pick them with -depot.
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

    private static string WorkshopExitCodesText => """
        Exit codes:
          0  success
          1  something failed, see stderr
          3  no saved login, or Steam rejected it: a person has to run "login"
          4  Steam is rate limiting logins: wait about 15 minutes
          5  only access failed: some items are private, or need another account (--username)
        """;

    private static string WorkshopFilesExitCodesText => """
        Exit codes:
          0  success
          1  something failed, see stderr. stdout has the rows of what succeeded; failed items have none,
             which doesn't mean their files don't exist. Running the same command again retries only what failed
          2  a pattern matched nothing in any item, and nothing failed
          3  no saved login, or Steam rejected it: a person has to run "login"
          4  Steam is rate limiting logins: wait about 15 minutes
          5  only access failed: some items are private, or need another account (--username)
        """;

    public static string Workshop => $"""
        Workshop items, whose files are manifests in the workshop depot of the app they're for.

        Usage:
          {ExeName} workshop info <item>...                                app, depot, manifest, size and title
          {ExeName} workshop info <item>... --history [--limit <n|all>]    versions, newest first
          {ExeName} workshop search <app> <text>... [--limit <n|all>]      find items by text
          {ExeName} workshop ls <item>... [-- <pattern>...]                list files without downloading
          {ExeName} workshop get <item>... -- <pattern>...                 download matching files (or --all-files)
          {ExeName} workshop <subcommand> --help                           details and examples

        Files go to <output>/workshop/<item>/<manifest>/<path>, and ls and get take the patterns of get.

        {WorkshopItems}

        {WorkshopItemNotes}
        """;

    public static string WorkshopLs => $"""
        Lists files of workshop items without downloading them, over one Steam login.

        Usage:
          {ExeName} workshop ls <item>... [-- <pattern>...]
        stdout has a tab-separated row per file: depot  manifest  sha1  size  path. Without patterns, every file.
        {ExitCodesSummary()}

        Examples:
          {ExeName} workshop ls 3070715607
          {ExeName} workshop ls 3818412809 -- "*.vpk"

        {WorkshopItems}

        {Patterns}

        Options:
          --output <dir>      root output folder for _dir.vpk files (default: depots in the current folder)
        {LoginOptions}

        Output:
          depot is the app's workshop depot. A VPK entry's row is: depot  manifest  crc32  size  <dir.vpk>:<entry path>.
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
          {ExeName} workshop get <item>... -- <pattern>...
          {ExeName} workshop get <item>... --all-files     every file of the items
        stdout has a tab-separated row per matching file: depot  manifest  absolute path (none with --all-files).
        {ExitCodesSummary()}

        Examples:
          {ExeName} workshop get 3070715607 -- "*.vpk"
          {ExeName} workshop get https://steamcommunity.com/sharedfiles/filedetails/?id=3818412809 --all-files

        {WorkshopItems}

        {Patterns}

        Options:
          --output <dir>      root output folder (default: depots in the current folder)
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
        """;

    public static string LoginRemove => $"""
        Revokes saved logins with Steam, so copies of their tokens stop working too, and forgets them.

        Usage:
          {ExeName} login remove <name>...

        Examples:
          {ExeName} login remove myaccount

        Revoking takes only the token, so it needs no Steam login. When it fails, the login is still removed
        here, with a warning that a copy of the token keeps working until it expires. Removing the default
        leaves a single remaining login as the default; with several left, log in again to pick one.

        Saved logins: {AccountStore.FilePath}

        Exit codes:
          0  success, also when revoking failed
          1  a name has no saved login, or the saved logins can't be written
        """;

    /// <summary>
    /// Prints help instead of running when there are no arguments, or --help is asked for. Returns true when handled.
    /// </summary>
    public static bool TryPrint(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]) || args[0] == "help")
        {
            var command = args.Length > 1 && !IsHelp(args[1]) ? GetCommandName(args[1..]) : null;
            Console.WriteLine(GetCommandHelp(command) ?? Overview);
            return true;
        }

        var name = GetCommandName(args);
        var commandHelp = GetCommandHelp(name);
        var bare = args.Length == name.Count(static c => c == ' ') + 1;

        // A bare command, or --help anywhere before the patterns. Commands that need no arguments run bare instead
        if ((commandHelp != null && bare && name is not ("login" or "login list")) || Array.Exists(ArgumentParser.SplitPatterns(args).Options, IsHelp))
        {
            Console.WriteLine(commandHelp ?? Overview);
            return true;
        }

        return false;
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
        _ => null,
    };

    // The first argument, or the first two for a subcommand
    private static string GetCommandName(string[] args) => ArgumentParser.GetCommandName(args[0], args[1..]);

    private static bool IsHelp(string arg) => arg is "-h" or "--help" or "-?" or "/?";
}
