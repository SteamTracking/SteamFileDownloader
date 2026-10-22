# SteamFileDownloader

> [!NOTE]
> SteamFileDownloader is a reimagination of [DepotDownloader](https://github.com/SteamRE/DepotDownloader). We try to make it as usable and as simple as possible. If something is not easy to use, [create an issue](https://github.com/SteamTracking/SteamFileDownloader/issues). Expect breaking changes.

SteamFileDownloader is a simpler version of [DepotDownloader](https://github.com/SteamRE/DepotDownloader). It downloads **specific files from Steam depots**, and it does not download the full depot. It can also download **files from old game builds** and **files in Valve Pak (VPK) archives**.

SteamFileDownloader is made for **AI agents and scripts**, for example Claude Code, Codex and continuous integration (CI) jobs:

- One command can use many manifests with one Steam login.
- The results go to stdout as rows, with a tab between the columns.
- Each error message tells you what to do next.
- The help text describes all of the commands and options.

SteamFileDownloader helps with datamining and reverse engineering of Source 2 games, for example Counter-Strike 2, Dota 2 and Deadlock. Usual tasks are:

- Download a DLL from many earlier builds.
- Find the build in which a file changed. To do this, compare the file hashes in the SteamPipe manifests.
- Extract one file from a `pak01_dir.vpk`. SteamFileDownloader downloads only the chunks that contain the file.

## Features

- **Any manifest**, current or old. You can select files by path, by name or by regular expression (regex).
- **Many targets in one run**: one run can use builds, depots and apps with one Steam login. This helps you to stay below the rate limit of Steam for logins.
- **File lists**: SteamFileDownloader can list the names, sizes and SHA-1 hashes of files. It does not download the files. Use these lists to compare builds.
- **Parts of VPK archives**: list the entries of a VPK. You can also extract entries, and SteamFileDownloader downloads only the chunks that contain them.
- **No second download of a file**: SteamFileDownloader does a check of each file that is already on the disk, and keeps the correct files. If a file is the same in another build, SteamFileDownloader copies it. If a command fails and you run it again, it downloads only the files that failed.
- **Same files as Steam**: if you give no patterns, `get` creates the empty folders of the depot. It creates symbolic links as links. On Windows, links need Developer Mode or an elevated terminal, and SteamFileDownloader skips them with a warning if it cannot create them. It skips a link that points outside the output folder. On Linux and macOS, files get the permissions 0777 and the umask removes some of them, the same as Steam.
- **Workshop items**: show the details and the version history of items, and search for items. List or download the files of an item from any version. This also applies to collections.
- **Saved logins**: log in one time with a QR code from the Steam mobile app. SteamFileDownloader encrypts the tokens for the Windows user.
- **Compatible with DepotDownloader**: `dd` runs your current DepotDownloader command lines.
- **Easy for agents to use**:
  - If you run SteamFileDownloader without arguments, it shows the full usage.
  - If you run a command without its arguments, it shows the usage and examples of that command.
  - If you type a command or an option incorrectly, it shows a possible correct name.
  - Each exit code shows the type of failure.
  - Parallel runs on one account do not disconnect each other.

## Quick start

```bash
# Save a login. Scan the QR code with the Steam mobile app, or use: login --username <name>
SteamFileDownloader login

# List the branches and depots of an app. Branch rows have the app, branch, build id and update time.
# Depot rows have <app>/<depot>:<manifest>, the maximum size and notes. Their first column is a target for ls and get.
SteamFileDownloader depots 730

# List the files of more than one build, and do not download them. The columns are depot, manifest, sha1, size and path.
SteamFileDownloader ls 730 2347779:4784444484596788209 2347779:2356538687884552308 -- source1import.exe

# Download files into <output>/<depot>/<manifest>/. The columns are depot, manifest and path.
SteamFileDownloader get 730 2347771:8344780363095656278 2347771:latest -- game/csgo/bin/win64/server.dll "regex:engine2\.dll$"

# Download from builds, depots and apps in one run with one login. SteamFileDownloader tries each pattern on each target.
# The run fails only if a pattern matches no file in any target.
SteamFileDownloader get 730 2347771:8344780363095656278 2347771:latest 2347770 570/373303:latest -- \
    game/csgo/bin/win64/server.dll engine2.dll steam.inf resourcecompiler.dll \
    "game/core/pak01_dir.vpk:scripts/scenes.vdata_c"

# List or extract VPK entries. SteamFileDownloader downloads only the archive chunks that contain them.
SteamFileDownloader ls 730 2347770 -- "game/core/pak01_dir.vpk:regex:^scripts/"
SteamFileDownloader get 730 2347770 -- "game/core/pak01_dir.vpk:scripts/scenes.vdata_c"

# If you give no patterns, SteamFileDownloader downloads all files of the depot.
# If you also give no targets, it downloads all depots that the account can access. This can be tens of gigabytes.
SteamFileDownloader get 730 2347771:8344780363095656278

# Download depots into one folder, the same as a Steam install.
SteamFileDownloader get 730 2347770 2347771 --install-dir D:/Games/CS2

# Find the depot that has a file. If you give no targets, ls searches all depots of the app.
SteamFileDownloader ls 570 -- hammer.dll

# Download from a manifest that is only on a beta branch.
SteamFileDownloader get 730 2347779:2591545799285498410@animgraph_2_beta -- resourcecompiler.dll
```

To see the full usage, run `SteamFileDownloader` without arguments, or run `SteamFileDownloader <command> --help`. Put the value of an option after a space or after an equals sign. For example: `--output D:/SteamDepots` or `--output=D:/SteamDepots`. To show the version and the commit, run `SteamFileDownloader --version`. You can also use `-V` or `version`.

## Targets and patterns

A target has the format `[<app>/]<depot>[:<manifest>|:latest][@<branch>]`. The format `<app>/<depot>/<manifest>` is also correct. If each target contains its app id, you do not need the app id before the targets. For example: `get 730/2347771 570/373303 -- resourcecompiler.dll`.

If you give no targets, `ls` and `get` use all depots of the app. They do not use shared depots, or depots that the account cannot access. `depots <app>` shows the latest manifests. To find old manifest ids, look at the history of a GameTracking `manifests/` folder.

Put the patterns after `--`. In patterns, uppercase and lowercase letters are the same. If you give no patterns, `ls` lists all files and `get` downloads all files. A pattern can be one of these:

- A path that contains a slash matches only that path. A name without a slash matches that file in any folder.
- A glob. `*` and `?` match in one folder, and `**` matches across folders. For example: `*.vpk` or `game/**/bin/**`.
- `regex:` and then a regex. The regex can match any part of the path, the same as in the file lists of DepotDownloader.
- `<path>_dir.vpk:<entry pattern>` selects entries in that VPK. The same rules apply to the two parts. SteamFileDownloader extracts the entries to a folder with the name of the `_dir.vpk` file, for example `game/core/pak01_dir/`.

SteamFileDownloader puts the files in `<output>/<depot>/<manifest>/`. The default `<output>` is the `depots` folder in the current folder. Use the same output folder each time. SteamFileDownloader does not download a file again if the file is already in that folder. It also does not download a file again if the file is the same in another manifest of the depot. The date of each file and folder is the date when its manifest was made, and this lets you sort the builds by date.

To use one output folder from all locations, save it with `config output <dir>`. `config output` shows the saved folder. `config output --reset` removes the saved folder. SteamFileDownloader saves the folder as an absolute path in `settings.json`, in the same folder as the saved logins.

For `get`, `ls`, `workshop get`, `workshop ls` and `dd`, `--output` has priority over the saved folder. For `dd`, use `-dir` in place of `--output`. `track` always needs its own `--output`.

With `get --install-dir <dir>`, SteamFileDownloader puts the files of all manifests in `<dir>/<path>`, the same as a Steam install. If two manifests have different files at the same path, the file of the later target or depot replaces the other, and a warning shows the files. If you give no targets, `get` uses all depots, for all platforms and languages, so give the depots to install. SteamFileDownloader does not delete files that are no longer in the manifests. It still saves the manifests in `<output>/manifests/`. You cannot use `<path>_dir.vpk:` patterns with `--install-dir`.

SteamFileDownloader saves each manifest to `<output>/manifests/<depot>_<manifest>.manifest`. When it needs that manifest again, it reads this file and does not download the manifest again. The `depotcache` folder of Steam uses the same file names, and you can copy its files into this folder too.

stdout gets only the results, as rows with a tab between the columns. All other text goes to stderr. If a run fails, it still shows the rows for all of the parts that did not fail.

## Workshop items

The files of a workshop item are a manifest in the workshop depot of its app. `workshop` accepts item ids or the URLs of their pages. A collection selects all of the items in that collection:

```bash
# Show the details of an item. The columns are item, app, depot, manifest, updated, size and title.
SteamFileDownloader workshop info 3070715607

# Show the versions of the item, with the newest version first. Each row is an <item>:<manifest> target for ls and get.
SteamFileDownloader workshop info 3070715607 --history

# Search for items by text. The columns are the same, with one more column for subscribers.
SteamFileDownloader workshop search 730 dust2

# List or download the files of the current version and an older version. The files go into <output>/workshop/<item>/<manifest>/.
# The first column is the item, not the depot. The ls columns are item, manifest, sha1, size and path. The get columns are item, manifest and path.
SteamFileDownloader workshop ls 3070715607
SteamFileDownloader workshop get 3070715607 3070715607:5179645662803098587 -- "*.vpk"
```

`workshop ls` and `workshop get` use the same patterns and the same `--output` as the main `ls` and `get` commands. SteamFileDownloader cannot download items from the legacy workshop. The legacy workshop did not keep files in depots.

## Logins and rate limits

To save more than one login, run `login` one time for each account. The last saved login is the default. For content that only one account owns, use `--username <name>` to select that account. For some apps that you do not have to buy, `--anonymous` is enough. If there is no saved login, SteamFileDownloader logs in with the anonymous account. `login` needs a person to scan the QR code or type the password, and an agent must ask the user to run it.

All commands that log in also accept `--username <name> --password <password>`, for example in CI. They then log in with the password on each run and do not save a login. Steam Guard prompts do not work in a run without a user. `login --username <name> --password <password>` saves a login without a typed password.

If you log in again with the QR code, SteamFileDownloader replaces the token of the account and revokes the old token. With `login --username`, if the saved login of that account still works, SteamFileDownloader only makes it the default. A token expires after about 200 days. In the last 30 days before this, each run asks Steam to renew the token.

`login list` shows each saved login, if it is the default, and the date when its token expires. `login remove <name>...` revokes the tokens with Steam and removes the saved logins. Steam then does not accept copies of these tokens. If you remove the default login, the last saved login of the other logins becomes the default.

Each login uses a random login id. Because of this, parallel runs on one account do not disconnect each other. Steam limits the rate of logins, so put many targets in one run.

## DepotDownloader commands

`dd` runs a DepotDownloader command line as `get`. With `-manifest-only`, it runs the command line as `ls`:

```bash
SteamFileDownloader dd -app 730 -depot 2347771 -manifest 8344780363095656278 -filelist files.txt
```

`dd` uses these flags: `-app`, `-depot`, `-manifest`, `-filelist`, `-dir`, `-branch`, `-username`, `-password` and `-manifest-only`. `-username` selects a saved login. With `-password`, `dd` logs in with the password and does not save a login. `dd` rejects all other flags, for example `-remember-password`, `-loginid`, `-os` and `-language`. Use `-depot` to select the depots. `dd` cannot use a branch that has a password, and `-branchpassword` does not work.

Each line in a `-filelist` file is a full path from the root of the depot, or a `regex:` pattern. Globs do not work. This is the same as in DepotDownloader. The files go into `<output>/<depot>/<manifest>/`, the same as with `get`. `-pubfile <item>` runs as `workshop get`, and the files go into `<output>/workshop/<item>/<manifest>/`. `-ugc <manifest>` runs as `get <app>/<app>:<manifest>`.

## Exit codes

| Code | Description |
|---|---|
| `0` | No failure |
| `1` | A manifest, a file or another part of the run failed |
| `2` | Usage error: an unknown option, or an argument that is not there or not correct. Correct the command line. For these errors, `track` exits with `1` |
| `3` | There is no saved login for `--username`, or Steam did not accept the login. A person must run `login`. With `--password`, do a check of the credentials |
| `4` | Steam limits the rate of logins. Wait about 15 minutes |
| `5` | All of the failures were access failures. For some targets, Steam gave no depot key or manifest request code. It is possible that another account owns them: use `--username`. It is also possible that the manifest is on another branch or in another app |
| `6` | A pattern matched no file in any target, and no other part of the run failed |

To report a problem, go to https://github.com/SteamTracking/SteamFileDownloader/issues.

## GameTracking

[GameTracking](https://github.com/SteamTracking/GameTracking) uses the `track` command and a `files.json` file to download the tracked files of the latest build:

```bash
SteamFileDownloader track 730 --output csgo --save-all-manifests
```

| Option | Description | Default |
|---|---|---|
| `<app>` or `--appid` | The Steam app id | Necessary |
| `--output` | The output folder | Necessary |
| `--username` | The Steam username, or `anonymous` | The last saved login, or `anonymous` if there is no saved login |
| `--anonymous` | The same as `--username anonymous` | |
| `--password` | The Steam password. Without it, `track` uses a saved login | A saved login |
| `--branch` | The branch to download from | `public` |
| `--manifests` | A folder with `manifest_<depot>.txt` files from an earlier run. `track` downloads that build | The latest manifests |
| `--save-manifest` | Save the manifests of the depots in `files.json` as text in `<output>/manifests/` | `false` |
| `--save-all-manifests` | Save the manifests of all depots as text in `<output>/manifests/` | `false` |

To download an older build with the same file patterns, give `--manifests` the `manifests/` folder of an earlier run. For example, use this folder from an old commit of a GameTracking repository. Each depot uses the manifest id in its `manifest_<depot>.txt` file. `track` does not download depots that have no such file. `track` does not write `steam_buildid.txt`. VPK entries also need the manifests of the depots that contain their archives, and `--save-all-manifests` saves these manifests.

### Configuration

`files.json` in the current folder gives the file patterns for each depot id:

```json
{
    "228990": [
        "steamclient.dll",
        "regex:resource/.*\\.txt"
    ],
    "373301": [
        "game/dota/pak01_dir.vpk:regex:^scripts/npc/.+\\.txt$",
        "game/dota/pak01_dir.vpk:scripts/heroes.herolist_c",
        "pak01_dir.vpk:regex:\\.(vsndevts_c|vxml_c)$"
    ]
}
```

Each entry is one of these:
- **A file path**. It matches only that path.
- **A glob** for the full path. `*` and `?` match in one folder, and `**` matches across folders. For example: `game/**/bin/win64/*.dll`.
- **`regex:`** and then a regex for the full path.
- **`<path>_dir.vpk:<entry pattern>`** for entries in that VPK. The rules are the same as for the patterns of `get`. A `pak01_dir.vpk` without a slash matches that file in any folder.

In paths, globs and regexes, uppercase and lowercase letters are different. In VPK entry patterns, uppercase and lowercase letters are the same, as in the patterns of `get` and `ls`. If a pattern matches nothing, `track` shows a warning, not an error.

For VPK entries, `track` downloads the full `_dir.vpk` file. From the `_NNN.vpk` archives, it downloads only the chunks that contain the entries. It writes these chunks into the archives at their offsets. Then [ValveResourceFormat (VRF)](https://github.com/ValveResourceFormat/ValveResourceFormat) can extract the entries from the `_dir.vpk` file as if the archives were complete:

```bash
Source2Viewer-CLI --input game/dota/pak01_dir.vpk --output game/dota/pak01_dir/ --vpk_decompile \
    --vpk_filepath "scripts/npc/,scripts/heroes.herolist_c"
```

The other parts of each archive are empty and sparse, and they use no disk space. The VRF filters must match only the entries that `files.json` matches. Other entries fail the cyclic redundancy check (CRC). `track` looks for archives in all depots of the app. Dota 2 keeps most of its archives in depots other than the depot of its `pak01_dir.vpk`.

### How it works

Use this command in new checkouts. It does not compare the files with the files from earlier runs. It does not delete files that are not in the manifest now. It does not download a file again if the file is on the disk with the correct hash.

- **Start**: `track` loads `files.json` before it connects. If `files.json` has an error, the run fails quickly.
- **Login**: `track` logs in with the anonymous account, with the username and password, or with a saved login. If there is no saved login and no `--username`, it logs in with the anonymous account. Steam Guard prompts do not work in a run without a user.
- **Content delivery network (CDN) servers**: `track` uses `SteamCache` and `CDN` servers for the cell id. It does not use proxies, servers only for China, or servers only for specific apps. Each request goes to the least busy of the best 8 servers. If a server fails 3 requests in sequence, `track` does not use that server for 30 seconds. A refused request also counts as a failure.
- **Depots**: `track` gets the depots from the PICS info of the app. It uses the depots in `files.json`. With `--save-all-manifests` or with VPK entries, it uses all depots. It does not use shared depots, which have a `depotfromapp` value. If a depot has no manifest on the branch, `track` uses the manifest from `public`. If the app does not have the branch, or the branch has a password, the run fails.
- **Manifests**: `track` downloads each manifest with the depot key and a manifest request code. If this fails, it tries again. After a 401, 403 or 404 error, it tries one more time on another server with a new request code. If a depot is not in `files.json` and its manifest fails, `track` only writes the failure to the log.
- **Files**: `track` disconnects from Steam, and then downloads the files from the CDN, 64 chunks at a time. It downloads identical files one time and copies them. It does a check of each chunk against its SHA-1, and writes the chunk into a file in the temporary folder of the system. When that file is complete, `track` moves it to its location. `track` tries failed chunks again on other servers until 2 servers refuse them, or until no chunk downloads for 15 minutes. One line shows a summary of their errors.
- **Output**: messages go to stdout. Warnings and errors go to stderr. When the `CI` environment variable has a value, they are `::warning::` and `::error::` lines. Without a terminal, `track` shows a `Progress:` line each 30 seconds.
- **VPKs**: for VPK entries, `track` first downloads and reads the `_dir.vpk` file. Then it downloads only the chunks of its archives that contain the entries. If a chunk is already in an archive with the correct SHA-1, `track` keeps it.
- **End**: if all of the parts are successful, `track` writes the build id of the branch to `steam_buildid.txt`, and the exit code is 0. If not, the exit code is 1. If `track` loses the connection and cannot log in again, the exit code is 3 or 4.
