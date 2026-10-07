# SteamFileDownloader

A simpler [DepotDownloader](https://github.com/SteamRE/DepotDownloader) for downloading **specific files from Steam depots**, including **old game builds** and **files inside VPK archives**, without downloading whole depots. It is built for **AI agents and scripts** like Claude Code, Codex and CI jobs: one command takes many manifests over a single Steam login, results are tab-separated lines on stdout, errors say what to do next, and the built-in help documents everything.

Typical uses are datamining and reverse engineering Source 2 games like Counter-Strike 2, Dota 2 and Deadlock: fetching a DLL from dozens of past builds, finding the build where a file changed by comparing SteamPipe manifest hashes, or extracting one file from a `pak01_dir.vpk` by downloading only the chunks it is in.

## Features

- **Any manifest**, current or old, with files matched by path, name or regex.
- **Many targets per run**: builds, depots and apps over one Steam login, which avoids Steam's login rate limit.
- **Listing without downloading**: names, sizes and SHA-1 hashes, to compare builds.
- **Partial VPK downloads**: list a VPK's entries, or extract them by downloading only the chunks they span.
- **No repeat downloads**: files already downloaded are checked and kept, and ones identical in another build are copied.
- **Saved logins**: log in once with a QR code from the Steam mobile app. Tokens are encrypted for the Windows user.
- **DepotDownloader compatible**: `dd` runs an existing DepotDownloader command line.
- **Agent friendly**: running it without arguments prints the full usage, exit codes say what went wrong, and parallel runs on one account don't disconnect each other.

## Quick start

```bash
# Save a login by scanning a QR code in the Steam mobile app (or: login --username <name>)
SteamFileDownloader login

# List an app's depots and branches: app, depot, manifest, max size, notes
SteamFileDownloader depots 730

# Everything Steam has on the depots and branches of several apps, as JSON to filter with jq
SteamFileDownloader depots 730 570 --json

# List files of several builds without downloading: depot, manifest, sha1, size, path
SteamFileDownloader ls 730 2347779:4784444484596788209 2347779:2356538687884552308 -- source1import.exe

# Download into <output>/<depot>/<manifest>/, printing: depot, manifest, path
SteamFileDownloader get 730 2347771:8344780363095656278 2347771:latest -- game/csgo/bin/win64/server.dll "regex:engine2\.dll$"

# Builds, depots and apps in one run over one login. Every pattern is tried against every target;
# only a pattern that matches nothing in any target fails the run.
SteamFileDownloader get 730 2347771:8344780363095656278 2347771:latest 2347770 570/373303:latest -- \
    game/csgo/bin/win64/server.dll engine2.dll steam.inf resourcecompiler.dll \
    "game/core/pak01_dir.vpk:scripts/scenes.vdata_c"

# List or extract VPK entries, downloading only the archive chunks they are in
SteamFileDownloader ls 730 2347770 -- "game/core/pak01_dir.vpk:regex:^scripts/"
SteamFileDownloader get 730 2347770 -- "game/core/pak01_dir.vpk:scripts/scenes.vdata_c"

# A whole depot, or every depot the account can access with "all" (can be tens of gigabytes)
SteamFileDownloader get 730 2347771:8344780363095656278 --all-files

# Find which depot has a file
SteamFileDownloader ls 570 all -- hammer.dll

# A manifest that is only on a beta branch
SteamFileDownloader get 730 2347779:2591545799285498410@animgraph_2_beta -- resourcecompiler.dll
```

Run `SteamFileDownloader` without arguments, or `SteamFileDownloader <command> --help`, for the full usage.

## Targets and patterns

Targets are `[<app>/]<depot>[:<manifest>|:latest][@<branch>]`, where `<depot>` can be `all` for every depot of the app. Patterns go after `--` and ignore case:

- A path with a slash matches exactly; a bare name matches that file in any folder.
- `regex:` matches anywhere in the path, like DepotDownloader's file lists.
- `<path>_dir.vpk:<entry pattern>` selects entries inside that VPK, with the same rules for both parts. Entries are extracted to a folder named after the directory file, like `game/core/pak01_dir/`.

Files go to `<output>/<depot>/<manifest>/`, by default under `depots` in the current folder. Reuse the same output folder, since files already in it, or identical in another manifest of the depot, aren't downloaded again.

## Logins and rate limits

Run `login` once per account to save several. The last one is the default, and `--username <name>` picks another for content only that account owns.

Each login uses a random login id, so parallel runs on one account don't disconnect each other. Steam rate limits logins, so put many targets in one run.

## DepotDownloader commands

`dd` runs a DepotDownloader command line as `get`, or as `ls` with `-manifest-only`, and prints the equivalent command:

```bash
SteamFileDownloader dd -app 730 -depot 2347771 -manifest 8344780363095656278 -filelist files.txt
```

It uses `-app`, `-depot`, `-manifest`, `-filelist`, `-dir`, `-branch`, `-username` (a saved login) and `-manifest-only`, and skips other flags like `-remember-password` or `-loginid`. `-os` and `-language` don't filter depots, so pick them with `-depot`. Password protected branches (`-branchpassword`) are not supported. Files keep the `<output>/<depot>/<manifest>/` layout.

## Exit codes

| Code | Meaning |
|---|---|
| `0` | Success |
| `1` | Something failed: arguments, a manifest or a download |
| `2` | A pattern matched nothing in any target |
| `3` | No saved login, or Steam rejected it (run `login` again) |
| `4` | Steam is rate limiting logins, wait about 15 minutes |

## GameTracking

[GameTracking](https://github.com/SteamTracking/GameTracking) runs the `track` command with a `files.json` to download the tracked files of the latest build:

```bash
SteamFileDownloader track --appid 730 --output csgo --save-all-manifests
```

| Option | Description | Default |
|---|---|---|
| `--appid` | Steam app id | Required |
| `--output` | Output directory | Required |
| `--username` | Steam username, or `anonymous` | Last saved login |
| `--password` | Steam password; without it a saved login is used | Saved login |
| `--branch` | Branch to download | `public` |
| `--save-manifest` | Save manifests of the depots in `files.json` as text to `<output>/manifests/` | `false` |
| `--save-all-manifests` | Save manifests of every depot as text to `<output>/manifests/` | `false` |

### Configuration

`files.json` in the current directory maps depot ids to file patterns:

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

Each entry is one of:
- **A file path**, matched exactly.
- **`regex:`** followed by a regex for the whole path.
- **`<path>_dir.vpk:<entry pattern>`**, entries inside that VPK, with the same rules as the patterns of `get`. A bare `pak01_dir.vpk` matches it in any folder.

For VPK entries, the `_dir.vpk` is downloaded whole, and only the chunks of its `_NNN.vpk` archives that hold the entries are written into them, at their offsets. [VRF](https://github.com/ValveResourceFormat/ValveResourceFormat) then extracts those entries from the `_dir.vpk` as if the archives were complete:

```bash
Source2Viewer-CLI --input game/dota/pak01_dir.vpk --output game/dota/pak01_dir/ --vpk_decompile \
    --vpk_filepath "scripts/npc/,scripts/heroes.herolist_c"
```

The rest of each archive is empty, and sparse, so it takes no disk space. VRF's filters must match only entries that `files.json` matches, since others fail their CRC check. Archives are looked up in every depot of the app, as Dota 2 keeps most of them in other depots than its `pak01_dir.vpk`.

### How it works

This command is meant for fresh checkouts. It doesn't diff against earlier downloads or delete files removed from the manifest, but skips files already on disk with the right hash.

- **Startup**: `files.json` is loaded before connecting, so a broken one fails fast.
- **Login**: anonymous, with the username and password, or with a saved login. Steam Guard prompts don't work headless.
- **CDN servers**: `SteamCache` and `CDN` servers for the cell id, without proxies, China-only or app-restricted ones. A server that errors is swapped for the next one.
- **Depots**: from the app's PICS info, the depots in `files.json`, or all of them with `--save-all-manifests` or VPK entries. Shared depots (`depotfromapp`) are skipped. A depot without a manifest on the branch uses `public`, but a branch the app doesn't have, or a password protected one, fails the run.
- **Manifests**: downloaded with the depot key and a manifest request code, with retries. A 401, 403 or 404 is retried once on another server with a new request code. Failures of depots not in `files.json` are only logged.
- **Download**: after disconnecting from Steam, files download concurrently from the CDN. Identical files are downloaded once and copied. Each chunk is checked against its SHA-1 and written into a file in the system temp folder, which is moved into place once complete.
- **VPKs**: for VPK entries, the `_dir.vpk` is downloaded and read first, then only the chunks of its archives that hold the entries. Chunks already in the archives with the right SHA-1 are kept.
- **Completion**: when everything succeeded, `steam_buildid.txt` gets the branch's build id and the exit code is 0, otherwise 1 (or 3 or 4 when logging in again after a lost connection failed).
