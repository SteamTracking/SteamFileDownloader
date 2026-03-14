# SteamFileDownloader

Downloads specific files from Steam depots based on a configurable file mapping. This includes downloading only the VPK archive chunks needed for the requested files, rather than entire depots.

For proper full depot downloading, use [DepotDownloader](https://github.com/SteamRE/DepotDownloader).

## Usage

```bash
SteamFileDownloader --appid 730 --username anonymous --password x --output csgo
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

## How It Works

This tool is designed for fresh checkouts. It does not diff against a previously downloaded version or clean up files that were removed from the manifest. However, if a file already exists in the output directory with a matching hash, it will be skipped.

### Startup

- Loads `files.json` to build a mapping of depot IDs to file-matching regexes (and optional VPK extension lists). This happens before connecting to Steam so invalid configuration fails fast.

### Steam Connection & Authentication

- Connects to Steam using SteamKit2. For `anonymous` usernames it logs in anonymously; otherwise it authenticates with username/password. Supports prompting for Steam Guard 2FA codes, but this doesn't work when running headless.
- A background task runs the SteamKit2 callback pump for the duration of the session.

### CDN Server Discovery

- Fetches Steam content servers for the user's cell ID, filtering to only `SteamCache` and `CDN` type servers (excluding proxy servers, China-only servers, and app-restricted servers).
- Servers are used in round-robin fashion. If a server returns errors, it is marked as bad and the next server in the list is used.

### App Info & Depot Discovery

- Requests a PICS access token for the app, then fetches the full product info (app metadata) via PICS.
- Iterates over the app's depots and keeps only those present in `files.json`. Depots with `depotfromapp` (shared/redirected depots) are skipped.
- For each depot, looks up the manifest ID for the requested branch. If the branch has no manifest and isn't `public`, falls back to the `public` branch.

### Manifest Download

- For each relevant depot, sequentially:
    - Requests the depot decryption key (needed to decrypt chunk data).
    - Requests a manifest request code (an authorization token for downloading the manifest).
    - Downloads and decrypts the depot manifest from the CDN, with retries and exponential backoff on failure. 401/403 errors cause an immediate skip.
- Optionally dumps each manifest to a human-readable text file (`--save-manifest`).

### Disconnection

- After all manifests are fetched, disconnects from Steam. The remaining work is purely CDN-based HTTP downloads that don't require an active Steam session.

### File Download

- Downloads files from all depots concurrently. Concurrency is controlled by two semaphores:
    - **Per-file semaphore**: limits how many files download simultaneously across all depots.
    - **Per-chunk semaphore**: limits how many chunk HTTP requests are in flight at once.
- For each file in the manifest that matches the depot's regex pattern:
    - **Hash check**: if the file already exists on disk with the correct size, its SHA-1 is computed. If it matches the manifest hash, the file is skipped.
    - **Chunk download**: a temporary file is created in the system temp directory. Each chunk is downloaded from the CDN in parallel, decrypted, decompressed, and written to the correct offset. Retries with exponential backoff per chunk.
    - **Integrity verification**: after all chunks are written, the temp file's SHA-1 is compared against the manifest hash. On match, the temp file is moved to the final output path. On mismatch, the temp file is deleted and the file is marked as failed.
- **VPK archive handling**: if a depot has `vpk:` entries in `files.json`, the downloader first downloads `pak01_dir.vpk` (the VPK directory file). It then parses the VPK to find which `pak01_NNN.vpk` archive files contain entries with the requested extensions, and downloads only those archive files.

### Completion

- If all depots succeeded, writes `steam_buildid.txt` to the output directory containing the branch's build ID.
- Prints elapsed time and exits with code 0 (success) or 1 (any failure).

## Exit Codes

| Code | Meaning |
|---|---|
| `0` | All files downloaded successfully |
| `1` | One or more errors occurred |
