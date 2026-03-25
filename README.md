DepotDownloader
===============

Steam depot downloader utilizing the SteamKit2 library. Supports .NET 8.0

This program must be run from a console, it has no GUI.

> **DISCLAIMER:** This fork is intended for backing up metadata of **owned** and **purchased** games. This is **NOT** a piracy tool. Please read the full [DISCLAIMER](DISCLAIMER.md).

## Installation

### Directly from GitHub

Download a binary from [the releases page](https://github.com/SteamRE/DepotDownloader/releases/latest).

### From Source

See [BUILD.md](BUILD.md) for instructions on how to build from source.

## Usage

### Downloading one or all depots for an app
```powershell
./DepotDownloader -app <id> [-depot <id> [-manifest <id>]]
                 [-username <username> [-password <password>]] [other options]
```

For example: `./DepotDownloader -app 730 -depot 731 -manifest 7617088375292372759`

By default it will use anonymous account ([view which apps are available on it here](https://steamdb.info/sub/17906/)).

To use your account, specify the `-username <username>` parameter. Password will be asked interactively if you do
not use specify the `-password` parameter.

### Downloading a workshop item using pubfile id
```powershell
./DepotDownloader -app <id> -pubfile <id> [-username <username> [-password <password>]]
```

For example: `./DepotDownloader -app 730 -pubfile 1885082371`

### Downloading a workshop item using ugc id
```powershell
./DepotDownloader -app <id> -ugc <id> [-username <username> [-password <password>]]
```

For example: `./DepotDownloader -app 730 -ugc 770604181014286929`

### Backup and Restore

**Full manifests/metadata backup:**
```powershell
# Remove -manifest-only if you want to save full depot files to disk
./DepotDownloader -app 1091500 -backup-manifests -manifest-only -all-platforms -all-archs -all-languages -all-branches -lowviolence -include-dlc -backup-dir "backups" -username "username" -password "password"
```

**Restore game from metadata backup:**
```powershell
# To target a specific buildID, use the -buildid parameter
# If -buildid is not specified, it will pick the higher one filtered by system compatibility info
./DepotDownloader -app 1091500 -restore-backup -include-dlc -all-languages -validate -dir "output/{GameName}/{BuildID}" -backup-dir "backups" 
```

## Parameters

#### Authentication

Parameter               | Description
----------------------- | -----------
`-username <user>`      | the username of the account to login to for restricted content.
`-password <pass>`      | the password of the account to login to for restricted content.
`-remember-password`    | if set, remember the password for subsequent logins of this user. (Use `-username <username> -remember-password` as login credentials)
`-qr`                   | display a login QR code to be scanned with the Steam mobile app
`-no-mobile`            | prefer entering a 2FA code instead of prompting to accept in the Steam mobile app.
`-loginid <#>`          | a unique 32-bit integer Steam LogonID in decimal, required if running multiple instances of DepotDownloader concurrently.
`-token <token>`        | the refresh token of the account to login to for restricted content (requires `-username`).

#### Downloading

Parameter                | Description
------------------------ | -----------
`-app <#>`               | the AppID to download.
`-depot <#>`             | the DepotID to download.
`-manifest <id>`         | manifest id of content to download (requires `-depot`, default: current for branch).
`-buildid <id>`          | build id of the content to download (useful for backing up older manifests).
`-ugc <#>`               | the UGC ID to download.
`-pubfile <#>`           | the PublishedFileId to download. (Will automatically resolve to UGC id)
`-branch <branchname>`   | download from specified branch if available (default: Public).
`-branchpassword <pass>` | branch password if applicable.
`-all-branches`          | download all available branches.
`-include-dlc`           | if set, also download all DLCs for the given app.
`-minimal-output`        | suppress file-by-file download progress.

#### Download configuration

Parameter               | Description
----------------------- | -----------
`-all-platforms`        | downloads all platform-specific depots when `-app` is used.
`-os <os>`              | the operating system for which to download the game (windows, macos or linux, default: OS the program is currently running on)
`-osarch <arch>`        | the architecture for which to download the game (32 or 64, default: the host's architecture)
`-all-archs`            | download all architecture-specific depots when `-app` is used.
`-all-languages`        | download all language-specific depots when `-app` is used.
`-language <lang>`      | the language for which to download the game (default: english)
`-lowviolence`          | download low violence depots when `-app` is used.
`-dir <installdir>`     | the directory in which to place downloaded files. Supports path variables: `{GameName}`, `{AppID}`, `{DepotID}`, `{BuildID}`, `{BranchName}`, `{OS}`, `{Arch}`, `{Language}`.
`-filelist <file.txt>`  | the name of a local file that contains a list of files to download (from the manifest). prefix file path with `regex:` if you want to match with regex. each file path should be on their own line.
`-validate`             | include checksum verification of files already downloaded.
`-manifest-only`        | downloads a human readable manifest for any depots that would be downloaded.
`-cellid <#>`           | the overridden CellID of the content server to download from.
`-max-downloads <#>`    | maximum number of chunks to download concurrently. (default: 8).
`-use-lancache`         | forces downloads over the local network via a Lancache instance.
`-backup-manifests`     | saves manifests and app info in a new `manifest_backups/{appId}/{buildid}/` dir.
`-backup-dir <dir>`     | the directory in which to place/read backups (default: `manifest_backups` inside working directory). DOES NOT support path variables.
`-restore-backup`       | restore from a backup. If `-buildid` is not specified, the latest backup compatible with system config is used.

#### Other

Parameter               | Description
----------------------- | -----------
`-debug`                | enable verbose debug logging.
`-V` or `--version`     | print version and runtime.

## Frequently Asked Questions

### Why am I prompted to enter a 2-factor code every time I run the app?
Your 2-factor code authenticates a Steam session. You need to "remember" your session with `-remember-password` which persists the login key for your Steam session.

### Can I run DepotDownloader while an account is already connected to Steam?
Any connection to Steam will be closed if they share a LoginID. You can specify a different LoginID with `-loginid`.

### Why doesn't my password containing special characters work? Do I have to specify the password on the command line?
If you pass the `-password` parameter with a password that contains special characters, you will need to escape the command appropriately for the shell you are using. You do not have to include the `-password` parameter on the command line as long as you include a `-username`. You will be prompted to enter your password interactively.

### I am getting error 401 or no manifest code returned for old manifest ids
Try logging in with a Steam account, this may happen when using anonymous account.

Steam allows developers to block downloading old manifests, in which case no manifest code is returned even when parameters appear correct.

### Why am I getting slow download speeds and frequent connection timeouts?
When downloading old builds, cache server may not have the chunks readily available which makes downloading slower.
Try increasing `-max-downloads` to saturate the network more.

## Credits

*   **[SteamRE/DepotDownloader](https://github.com/SteamRE/DepotDownloader)** - The original project.
*   **[SteamRE/SteamKit](https://github.com/SteamRE/SteamKit)** - The library used for interacting with steam.
*   **[SteamDB](https://steamdb.info/)** - For searching and gathering info about steam.
