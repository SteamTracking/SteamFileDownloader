using System;
using System.IO;

namespace SteamFileDownloader;

/// <summary>
/// Help texts, written so the tool can be used without the readme.
/// </summary>
internal static class Help
{
    public static string ExeName { get; } = Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? nameof(SteamFileDownloader);

    public static string UsageHint(string command = "") => $"Run \"{ExeName}{(command.Length > 0 ? " " + command : "")} --help\" for usage.";

    private static string Targets => """
        Targets:
          <depot>              the latest manifest on the branch, same as <depot>:latest
          <depot>:<manifest>   a specific manifest, including old ones (2347771:8344780363095656278)
          all                  every depot of the app except shared ones, skipping ones without access
          <app>/<depot>...     a depot of another app (570/373303:latest, or 570/373303/<manifest>)
          ...@<branch>         a manifest on that branch, needed for beta-only manifests
                               (2347779:2591545799285498410@animgraph_2_beta)

          The app id before the targets can be left out when every target names its app.
          Each run logs in to Steam once, and Steam rate limits logins, so put all targets in one run.
          Parallel runs on one account don't disconnect each other.
          "depots <app>" shows the latest manifests; old manifest ids are in the history of a
          GameTracking manifests/ folder.
        """;

    private static string Patterns => """
        Patterns (after --, case-insensitive, each tried against every target):
          game/bin/win64/engine2.dll          exact path from the depot root, when it has a slash
          server.dll                          that file name in any folder
          *.vpk, game/**/bin/**               globs: * and ? within a folder, ** across folders
          regex:tools/.*\.dll$                .NET regex, matched anywhere in the path
          game/core/pak01_dir.vpk:<pattern>   entries inside that VPK, both parts use the rules above;
                                              an empty <pattern> means all entries

          Depot paths use forward slashes. A target without a match is fine (like a build before the
          file existed); a pattern that matches nothing in any target is an error (exit code 2).
        """;

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

    // Follows the description of the rows on stdout
    private static string Streams => """
        Everything else goes to stderr: progress, as a "Progress:" line every 30 seconds when stderr
        isn't a terminal, and problems, which start with [ERROR] or [WARN] (::error:: and ::warning::
        when the CI environment variable is set).
        """;

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
          {ExeName} depots <app>... [--json]                  list apps' depots and branches
          {ExeName} ls [<app>] <target>... [-- <pattern>...]  list files without downloading
          {ExeName} get [<app>] <target>... -- <pattern>...   download matching files (or --all-files)
          {ExeName} dd <DepotDownloader arguments>            run a DepotDownloader command line
          {ExeName} track --appid <app> --output <dir>        download the files in ./files.json (GameTracking)
          {ExeName} <command> --help                          details and examples
          {ExeName} --version                                 print the version

        {Targets}

        {Patterns}

        {Options}

        Output:
          stdout has only results, as tab-separated rows:
            ls       depot  manifest  sha1  size  path
                     depot  manifest  crc32  size  <dir.vpk>:<entry path>   (VPK entries)
            get      depot  manifest  absolute path                         (none with --all-files)
            depots   app  depot  manifest  max size  notes                  (one JSON object with --json)
          {Streams.ReplaceLineEndings(Environment.NewLine + "  ")}

          Files go to <output>/<depot>/<manifest>/<path>, dated to when their manifest was created.
          Files already there, or identical in another manifest of the depot, aren't downloaded again,
          so reuse the same output folder.

        {ExitCodesText}

        Examples:
          {ExeName} depots 730
          {ExeName} ls 730 2347779:4784444484596788209 2347779:2356538687884552308 -- source1import.exe
          {ExeName} get 730 2347771:8344780363095656278 2347771:latest -- game/csgo/bin/win64/server.dll engine2.dll
          {ExeName} get 730 2347770 -- "game/core/pak01_dir.vpk:scripts/scenes.vdata_c"

        Saved logins: {AccountStore.FilePath}
        """;

    public static string Track => $"""
        Downloads the files listed in ./files.json from the latest manifests of a branch, for GameTracking.

        Usage:
          {ExeName} track --appid <app> --output <dir> [--username <name> [--password <password>]]
            [--branch <name>] [--manifests <folder>] [--save-manifest] [--save-all-manifests]

        Files go to <dir>/<path>, without depot and manifest folders. Files already on disk with the
        right hash are kept. When everything succeeded, <dir>/steam_buildid.txt gets the build id.
        Messages go to stdout, and warnings and errors to stderr.

        To download an older build, pass --manifests with the manifests/ folder of an older run, like
        from an old commit of a GameTracking repository. Each depot uses the manifest id in its
        manifest_<depot>.txt, and depots without one are skipped. VPK entries need the manifests of the
        depots their archives are in too, so use a run with --save-all-manifests. steam_buildid.txt isn't written.

        files.json maps depot ids to lists of patterns (comments and trailing commas are allowed, and
        backslashes are doubled in JSON strings):
          game/csgo/steam.inf                        exact path
          game/**/bin/win64/*.dll                    glob for the whole path: * and ? within a folder,
                                                     ** across folders
          regex:.+?\.(dll|exe)                       .NET regex for the whole path
          game/dota/pak01_dir.vpk:<pattern>          entries inside that VPK, with the pattern rules of get,
                                                     like "game/dota/pak01_dir.vpk:regex:^scripts/npc/.+\.txt$"
                                                     or "pak01_dir.vpk:regex:\.(vsndevts_c|vxml_c)$" for any folder

          Paths, globs and regexes are case-sensitive, while VPK entry patterns ignore case like get's.
          A pattern that matches nothing is a warning, not an error.

          For VPK entries, the _dir.vpk is downloaded whole, and only the chunks of its archives that hold
          the entries are written into them, at their offsets. VRF then extracts those entries from the _dir.vpk
          as if the archives were complete. The rest of each archive is empty (sparse), so VRF's filters
          must not match other entries, which fail their CRC check. Archives are found in every depot of the app.

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

        {BasicExitCodesText}

        Examples:
          {ExeName} track --appid 570 --output . --save-all-manifests
          An older build into another folder, with the files.json and manifests of an old commit:
            git worktree add ../old <commit>
            cd ../old && {ExeName} track --appid 570 --output . --manifests manifests
        """;

    public static string Ls => $"""
        Lists files of depot manifests without downloading them, over one Steam login.

        Usage:
          {ExeName} ls [<app>] <target>... [-- <pattern>...]

        Without patterns every file is listed. stdout has one tab-separated row per file:
          depot  manifest  sha1  size  path
        and one per VPK entry, for which only the _dir.vpk is downloaded, to <output>/<depot>/<manifest>/:
          depot  manifest  crc32  size  <dir.vpk>:<entry path>
        {Streams}

        To find the build where a file changed, list every candidate manifest in one run and compare
        the sha1 column, then get one manifest per distinct sha1. Manifest dates are on stderr.

        {Targets}

        {Patterns}

        {Options}

        {ExitCodesText}

        Examples:
          Where did source1import.exe change between builds?
            {ExeName} ls 730 2347779:4784444484596788209 2347779:2356538687884552308 -- source1import.exe
          Which depot of Dota 2 has hammer.dll?
            {ExeName} ls 570 all -- hammer.dll
          Which scripts are in the core VPK?
            {ExeName} ls 730 2347770 -- "game/core/pak01_dir.vpk:regex:^scripts/"
        """;

    public static string Get => $"""
        Downloads matching files from depot manifests, over one Steam login.

        Usage:
          {ExeName} get [<app>] <target>... -- <pattern>...
          {ExeName} get [<app>] <target>... --all-files     every file, which can be tens of gigabytes per depot

        Files go to <output>/<depot>/<manifest>/<path>. VPK entries go to a folder named after
        the _dir.vpk without .vpk, like 2347770/7820179980365915207/game/core/pak01_dir/scripts/scenes.vdata_c,
        and only the archive chunks they are in are downloaded, plus the _dir.vpk, which gets no row.
        Files already on disk with the right hash are kept, and ones with the same hash in another target
        or downloaded manifest of the depot are copied.

        stdout has one tab-separated row per matching file on disk, including ones that already were,
        and none with --all-files:
          depot  manifest  absolute path
        {Streams}

        {Targets}

        {Patterns}

        {Options}

        {ExitCodesText}

        Examples:
          server.dll and engine2.dll from three builds, plus a VPK entry:
            {ExeName} get 730 2347771:8344780363095656278 2347771:3583953281387649656 2347771:latest 2347770 -- \
              game/csgo/bin/win64/server.dll engine2.dll "game/core/pak01_dir.vpk:scripts/scenes.vdata_c"
          resourcecompiler.dll from CS2 and Dota 2:
            {ExeName} get 730/2347771 570/373303 -- game/bin/win64/resourcecompiler.dll
          Linux shared libraries of the latest build:
            {ExeName} get 730 2347773 -- "regex:\.so$"
          A whole depot:
            {ExeName} get 730 2347771:8344780363095656278 --all-files
        """;

    public static string Depots => $"""
        Lists apps' depots, to find which one holds what. Needs a Steam login.

        Usage:
          {ExeName} depots <app>... [--branch <name>]
          {ExeName} depots <app>... --json

        stdout has one tab-separated row per depot:
          app  depot  manifest  max size  notes
        manifest is the latest on the branch, or public's when the branch has none ("manifest from public"
        in notes), or - when neither has one. max size is in bytes, or -. notes lists the name, os, arch,
        language, dlc and "shared from app <app>"; a shared depot is downloaded as <that app>/<depot>.
        Branches, with build ids and dates, are on stderr.

        With --json, stdout is one JSON object with each app's whole "depots" section from Steam, for
        filtering with jq or a script: every depot with all its keys (config like oslist, osarch and
        language, maxsize, dlcappid, depotfromapp, ...) and its manifest on every branch, and every branch
        under "branches" (buildid, timeupdated, pwdrequired, ...). Values are strings, as Steam sends them.

        Options:
          --branch <name>     branch whose manifests are listed (default: public)
          --json              everything about the depots and branches as JSON
        {LoginOptions}

        {BasicExitCodesText}

        Examples:
          {ExeName} depots 570
          {ExeName} depots 730 570 --json
        """;

    public static string DepotDownloader => $"""
        Runs a DepotDownloader command line as get, or as ls with -manifest-only.

        Usage:
          {ExeName} dd -app <app> [-depot <depot> [-manifest <manifest>]]... [-filelist <file>] [-dir <dir>]
            [-branch <name>] [-username <name>] [-manifest-only]

        -depot and -manifest pair up in order. Without -depot every depot of the app is used. File list
        lines are exact paths from the depot root, without globs, or "regex:" patterns, like in DepotDownloader,
        and a list without entries is an error. Without -filelist every file is downloaded, like get --all-files,
        so stdout has no rows. -dir sets the root output folder; files still go to <dir>/<depot>/<manifest>/<path>.
        -username picks a saved login. Other flags, like -remember-password or -loginid, are skipped:
        a random login id is used. -os and -language don't filter depots, so pick them with -depot.
        Password protected branches (-branchpassword) are not supported.
        For new command lines, use get or ls, which also take several apps, branches and VPK entries.

        {ExitCodesText}

        Example:
          {ExeName} dd -app 730 -depot 2347771 -manifest 8344780363095656278 -filelist files.txt
        """;

    public static string Login => $"""
        Logs in to Steam and saves a refresh token, so other commands never ask for a password.

        Usage:
          {ExeName} login                      show a QR code to scan in the Steam mobile app
          {ExeName} login --username <name>    use a password and Steam Guard code instead

        Scan the QR code in the Steam mobile app (Steam Guard tab, scan icon). A person has to scan it
        or type the password, so an agent should ask the user to run this.

        Log in with each account to save several. The last one is the default, and --username picks
        another for content only that account owns. Logging in again replaces the account's token.
        Tokens are encrypted for the Windows user.

        Saved logins: {AccountStore.FilePath}

        Exit codes:
          0  success
          1  something failed, see stderr
          3  Steam rejected the login
          4  Steam is rate limiting logins: wait about 15 minutes
        """;

    /// <summary>
    /// Prints help instead of running when there are no arguments, or --help is asked for. Returns true when handled.
    /// </summary>
    public static bool TryPrint(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]) || args[0] == "help")
        {
            var command = args.Length > 1 && !IsHelp(args[1]) ? args[1] : null;
            Console.WriteLine(GetCommandHelp(command) ?? Overview);
            return true;
        }

        var commandHelp = GetCommandHelp(args[0]);

        // A bare command, or --help anywhere before the patterns. A bare login is the QR code login, not a request for help
        if ((commandHelp != null && args.Length == 1 && args[0] != "login") || Array.Exists(ArgumentParser.SplitPatterns(args).Options, IsHelp))
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
        "track" => Track,
        _ => null,
    };

    private static bool IsHelp(string arg) => arg is "-h" or "--help" or "-?" or "/?";
}
