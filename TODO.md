# Project Roadmap & TODOs

## Completed Tasks

- [x] **Add `-include-dlc` argument**: Retrieve DLCs from `AppInfo["common"]["extended"]["listofdlc"]`.
- [x] **Implement Refresh Token Login**: Allow login using a refresh token instead of a password.
- [x] **Add `-restore-backup`**: Enable downloading apps using backed-up data from `-backup-manifests`.
- [x] **Add `-all-branches` option**: Allow downloading of all available branches.
- [x] **Add `-backup-path` argument**: Specify input/output folder for `-backup-manifests`.
- [x] **Implement `.lua` file fallback**: Process `.lua` files before attempting to load `{AppId}.json`.
- [x] **Enhance `.lua` generation**: Add `addtoken(<appid>, "<access_token>")` section only when the access token is valid (not 0/null).

## Pending Tasks

- [x] **Update README**: Reflect recent changes and new arguments in the documentation. Add credits and disclaimers.
- [ ] **Refactor whole `ContentDownloader.cs`**: It's a mess and too long, extract logic to multiple separate files.
- [ ] **Add `-package-id` support**: 
    - Enable downloading collections of AppIDs (packages).
    - To handle "missing" DLCs that are part of a bundle but not the main AppInfo (e.g., Monster Hunter Rise Sunbreak).
    - *Investigation needed*: Determine if it's better to fetch `packageId > depotIds` directly or `packageId > appIds > depotIds`.
    - What about bundles? Cyberpunk2077 has a bundle (but not a package) containing base game + DLC? https://steamdb.info/app/1091500/subs/
- [ ] **Add path vars support to `-backup-dir`**: Currently it ignores path variables if provided.
- [x] **Add `-all-licences` / `-all-apps`**: Fetch all licenses associated with an account instead of specifying a single AppID.
- [ ] **Add Archive Support**: Implement basic 7z/gzip/zip archive support with splitting logic.
- [ ] **Support Workshop UGC/Pubfile**: Extend `-backup-manifests` and `-restore-backup` to support Workshop content.
- [x] **Manual Backup of Older Manifests**: Add support for manually backing up older depot/manifest IDs and placing them in the correct backup directory.
    - Not sure if it can be done, maybe manifest info has all the details required already to construct {appId}.json
    - Example old manifest ids: https://steamdb.info/depot/1091501/manifests/
- [ ] **Add Progress Bar**: some sort of console progress bar with ETA, similar to rclone --progress