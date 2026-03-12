# SteamFileDownloader

Downloads specific files from Steam depots based on a configurable file mapping. This includes downloading only the VPK archive chunks needed for the requested files, rather than entire depots.

For proper full depot downloading, use [DepotDownloader](https://github.com/SteamRE/DepotDownloader).

## Usage

```
SteamFileDownloader --appid <id> --username <user> --password <pass> --output <dir> [--branch <name>] [--save-manifest]
```

| Option | Description | Default |
|---|---|---|
| `--appid` | Steam App ID to download | Required |
| `--username` | Steam username (use `anonymous` for anonymous login) | Required |
| `--password` | Steam password (ignored for anonymous) | Required |
| `--output` | Output directory for downloaded files | Required |
| `--branch` | Depot branch to download from | `public` |
| `--save-manifest` | Save manifest text files to `<output>/manifests/` | `false` |

## Configuration

Place a `files.json` in the same directory as the executable. It maps depot IDs to file patterns:

```json
{
    "228990": [
        "steamclient.dll",
        "regex:resource/.*\\.txt"
    ],
    "373301": [
        "game/csgo/pak01_dir.vpk",
        "vpk:vsndevts_c,vxml_c"
    ]
}
```

Each entry supports:
- **Literal filenames** — matched exactly (automatically escaped for regex)
- **`regex:` prefix** — custom regex pattern
- **`vpk:` prefix** — comma-separated file extensions to extract from VPK archives (downloads referenced `pak01_*.vpk` archives automatically)

## Exit Codes

| Code | Meaning |
|---|---|
| `0` | All files downloaded successfully |
| `1` | One or more errors occurred |
