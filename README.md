# SteamFileDownloader

Download individual files from Steam depots, from any build, without downloading the whole depot.

It is a rethink of [DepotDownloader](https://github.com/SteamRE/DepotDownloader), made for datamining, scripts and AI agents. Use it to:

- get one DLL from twenty old builds
- find the build where a file changed
- extract one file from a `pak01_dir.vpk`, and download only the chunks that hold it

> [!NOTE]
> Expect breaking changes. If something is awkward to use, [open an issue](https://github.com/SteamTracking/SteamFileDownloader/issues).

## Highlights

- **Any build.** Pick files from current or old manifests by path, name, glob or regex.
- **Many targets, one login.** Mix builds, depots and apps in a single run. This keeps you below Steam's login rate limit.
- **Inside VPKs.** List or extract VPK entries, downloading only the archive chunks that hold them.
- **Never downloads twice.** It keeps files that are already on disk, copies files that are identical in another build, and resumes interrupted downloads.
- **Game installs.** `download` installs an app into one folder with the depots Steam would pick for your machine.
- **Workshop.** Inspect items and their version history, search, and download any version.
- **Script friendly.** Results go to stdout as tab-separated rows, everything else goes to stderr, and exit codes tell failures apart.
- **DepotDownloader compatible.** `dd` runs existing DepotDownloader command lines.

## Quick start

```bash
# Save a login by scanning a QR code with the Steam mobile app
SteamFileDownloader login

# See an app's branches and depots
SteamFileDownloader depots 730

# Find which depot has a file
SteamFileDownloader ls 570 -- hammer.dll

# Compare a file across two builds without downloading it (prints sha1 and size)
SteamFileDownloader ls 730 2347779:4784444484596788209 2347779:2356538687884552308 -- source1import.exe

# Download a file from an old build and the latest one
SteamFileDownloader get 730 2347771:8344780363095656278 2347771 -- game/csgo/bin/win64/server.dll

# Extract VPK entries
SteamFileDownloader get 730 2347770 -- "game/core/pak01_dir.vpk:regex:^scripts/"

# Install a game
SteamFileDownloader download 730 --dir D:/Games/CS2
```

Every command has its own help with examples and all of its options: run `SteamFileDownloader <command> --help`, or run it without arguments for an overview.

## Commands

| Command | What it does |
|---|---|
| `login` | Save a Steam login (QR code, or `--username`) |
| `depots <app>...` | List branches, depots and their latest manifests |
| `ls <app> [targets] [-- patterns]` | List files with their sha1 and size, without downloading |
| `get <app> [targets] [-- patterns]` | Download files into `<output>/<depot>/<manifest>/` |
| `download <app>` | Install an app into one folder, like Steam |
| `workshop info\|search\|ls\|get` | Workshop items and their files |
| `dd <DepotDownloader args>` | Run a DepotDownloader command line |
| `config output <dir>` | Save the default output folder |
| `track <app> --output <dir>` | Download the files listed in `files.json`, for GameTracking |

## Targets and patterns

A **target** is a depot, optionally with a manifest, an app and a branch:

```
2347771                        latest manifest of the depot
2347771:8344780363095656278    a specific (possibly old) manifest
570/373303                     a depot of another app
2347779:2591545799285498410@animgraph_2_beta    a manifest only on a beta branch
```

With no targets, `ls` and `get` use every depot of the app you have access to. Old manifest ids can be found in the history of a [GameTracking](https://github.com/SteamTracking/GameTracking) repo's `manifests/` folder.

**Patterns** come after `--` and ignore case:

```
server.dll                              that file name in any folder
game/csgo/bin/win64/server.dll          exactly that path
*.vpk  game/**/bin/**                   globs
regex:engine2\.dll$                     a regex matched anywhere in the path
game/core/pak01_dir.vpk:<pattern>       entries inside that VPK
```

Each pattern applies to every target. A target with no match is fine, because the file may not exist in that build yet. A pattern that matches nothing in any target is an error.

Files go to `<output>/<depot>/<manifest>/<path>`, dated to their build. The default `<output>` is `./depots`. Use the same output folder every time, so that no file downloads twice. To save a default folder, run `config output D:/SteamDepots`.

## Installing a game

`download` puts an app into one folder, picking depots the way Steam does: for your OS, CPU and language, plus DLC you own. stderr lists the depots it skipped and why, and `depots <app>` shows the same reasons.

```bash
SteamFileDownloader download 730 --os linux --language french --dir D:/Games/CS2-linux
SteamFileDownloader download 90 --include <depot>      # add a depot it skipped, like a mod
SteamFileDownloader download 730 2347770 2347771       # exactly these depots, no filtering
```

See `download --help` for `--oscpu`, `--steamdeck`, `--all-platforms`, `--exclude` and the rest.

## Workshop

```bash
SteamFileDownloader workshop info 3070715607 --history     # versions, newest first
SteamFileDownloader workshop search 730 dust2
SteamFileDownloader workshop get 3070715607 -- "*.vpk"
```

Items can be ids or page URLs, and collections expand to their items. `workshop ls` and `workshop get` take the same patterns as `ls` and `get`, and `<item>:<manifest>` selects an older version.

## Logins

`login` saves a refresh token, encrypted for your Windows user, so later runs never ask for a password. Run it once per account. The last account becomes the default, and `--username` selects another. `--anonymous` is enough for some free apps and dedicated servers.

Logging in needs a person to scan the QR code or type the password, so agents should ask the user to run it. For CI, pass `--username` and `--password` on each run instead.

Steam rate limits logins, so put many targets into one run rather than making many runs. Parallel runs on one account don't disconnect each other.

## DepotDownloader compatibility

```bash
SteamFileDownloader dd -app 730 -depot 2347771 -manifest 8344780363095656278 -dir D:/Games/CS2
```

`dd` runs as `download`, or as `ls` with `-manifest-only`. `dd` does not support `-filelist`. Use `get` with patterns instead. See `dd --help` for the supported flags.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | Something failed (rerunning retries only what failed) |
| 2 | Usage error |
| 3 | No saved login, or Steam rejected it: run `login` |
| 4 | Rate limited: wait about 15 minutes |
| 5 | No access: another account may own it, or it is on another branch or in another app |
| 6 | A pattern matched nothing |

## GameTracking

[GameTracking](https://github.com/SteamTracking/GameTracking) runs `track`, which downloads the files listed in `files.json` from the latest build:

```bash
SteamFileDownloader track 730 --output . --save-all-manifests
```

`files.json` maps depot ids to patterns. Unlike `get`, they match the full path and are case-sensitive:

```json
{
    "228990": [
        "steamclient.dll",
        "regex:resource/.*\\.txt"
    ],
    "373301": [
        "game/dota/pak01_dir.vpk:regex:^scripts/npc/.+\\.txt$",
        "pak01_dir.vpk:regex:\\.(vsndevts_c|vxml_c)$"
    ]
}
```

Files go straight into `--output` with no depot or manifest folders. To rebuild an older build, pass `--manifests` the `manifests/` folder from an old commit.

`track` writes VPK entries into their archives at the correct offsets, and leaves the rest of each archive sparse. Then [Source2Viewer-CLI](https://github.com/ValveResourceFormat/ValveResourceFormat) can extract them as if the archives were complete:

```bash
Source2Viewer-CLI --input game/dota/pak01_dir.vpk --output game/dota/pak01_dir/ --vpk_decompile \
    --vpk_filepath "scripts/npc/,scripts/heroes.herolist_c"
```

See `track --help` for every option.
