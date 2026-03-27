// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;
using SteamKit2.CDN;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace DepotDownloader
{
    class ContentDownloaderException(string message, DownloadErrorCode errorCode = DownloadErrorCode.AppDownloadFailed) : Exception(message)
    {
        public DownloadErrorCode ErrorCode { get; } = errorCode;
    }

    static class ContentDownloader
    {
        public const uint INVALID_APP_ID = uint.MaxValue;
        public const uint INVALID_DEPOT_ID = uint.MaxValue;
        public const ulong INVALID_MANIFEST_ID = ulong.MaxValue;
        public const string DEFAULT_BRANCH = "public";

        public static DownloadConfig Config = new();

        private static Steam3Session steam3;
        private static CDNClientPool cdnPool;

        private const string DEFAULT_DOWNLOAD_DIR = "depots";
        private const string MANIFEST_BACKUPS_DIR = "manifest_backups";
        internal const string CONFIG_DIR = ".DepotDownloader";
        private const string DEPOT_CONFIG_FILE = "depot.config";
        private static readonly string STAGING_DIR = Path.Combine(CONFIG_DIR, "staging");

        private static readonly FrozenSet<EWorkshopFileType> SupportedWorkshopFileTypes = FrozenSet.ToFrozenSet(new[]
        {
            EWorkshopFileType.Community,
            EWorkshopFileType.Art,
            EWorkshopFileType.Screenshot,
            EWorkshopFileType.Merch,
            EWorkshopFileType.IntegratedGuide,
            EWorkshopFileType.ControllerBinding,
        });

        private sealed class DepotDownloadInfo(
            uint depotid, uint appId, ulong manifestId, string branch,
            string installDir, byte[] depotKey)
        {
            public uint DepotId { get; } = depotid;
            public uint AppId { get; } = appId;
            public ulong ManifestId { get; } = manifestId;
            public string Branch { get; } = branch;
            public string InstallDir { get; } = installDir;
            public byte[] DepotKey { get; } = depotKey;
        }

        static string SanitizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return path;

            // Remove invalid characters for both Windows and Unix
            // Explicitly include separators to prevent path traversal via ".." or subdirectories
            var invalidChars = Path.GetInvalidFileNameChars()
                .Concat(Path.GetInvalidPathChars())
                .Concat(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar })
                .Distinct();

            foreach (var c in invalidChars)
            {
                path = path.Replace(c.ToString(), "");
            }

            // Prevent reserved names that could lead to traversal
            if (path == "." || path == "..")
            {
                return "_" + path;
            }

            return path;
        }

        static string ResolveInstallDirectory(uint appId, uint depotId, uint buildId, string branch, uint? parentAppId, KeyValue depotConfig)
        {
            var installDir = Config.InstallDirectory;

            if (string.IsNullOrWhiteSpace(installDir))
            {
                return null;
            }

            // If no variables are used, return the static path
            if (!installDir.Contains('{'))
            {
                return installDir;
            }

            var gameName = GetAppName(parentAppId ?? appId);
            var os = "unknown";
            var arch = "unknown";
            var language = "unknown";

            if (depotConfig != KeyValue.Invalid)
            {
                if (depotConfig["oslist"] != KeyValue.Invalid)
                    os = depotConfig["oslist"].Value;

                if (depotConfig["osarch"] != KeyValue.Invalid)
                    arch = depotConfig["osarch"].Value;

                if (depotConfig["language"] != KeyValue.Invalid)
                    language = depotConfig["language"].Value;
            }

            // Sanitize values to ensure they are valid for file paths
            gameName = SanitizePath(gameName);
            branch = SanitizePath(branch);
            os = SanitizePath(os);
            arch = SanitizePath(arch);
            language = SanitizePath(language);

            installDir = installDir.Replace("{GameName}", gameName, StringComparison.OrdinalIgnoreCase);
            installDir = installDir.Replace("{AppID}", appId.ToString(), StringComparison.OrdinalIgnoreCase);
            installDir = installDir.Replace("{DepotID}", depotId.ToString(), StringComparison.OrdinalIgnoreCase);
            installDir = installDir.Replace("{BuildID}", buildId.ToString(), StringComparison.OrdinalIgnoreCase);
            installDir = installDir.Replace("{BranchName}", branch, StringComparison.OrdinalIgnoreCase);
            installDir = installDir.Replace("{OS}", os, StringComparison.OrdinalIgnoreCase);
            installDir = installDir.Replace("{Arch}", arch, StringComparison.OrdinalIgnoreCase);
            installDir = installDir.Replace("{Language}", language, StringComparison.OrdinalIgnoreCase);

            return installDir;
        }

        static string GetManifestBaseDirectory()
        {
            if (!string.IsNullOrWhiteSpace(Config.BackupDirectory))
            {
                return Config.BackupDirectory;
            }

            var installDir = Config.InstallDirectory;
            if (string.IsNullOrWhiteSpace(installDir))
            {
                return MANIFEST_BACKUPS_DIR;
            }

            int firstVar = installDir.IndexOf('{');
            if (firstVar != -1)
            {
                int lastSeparator = installDir.LastIndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, firstVar);
                if (lastSeparator != -1)
                {
                    installDir = installDir.Substring(0, lastSeparator);
                }
                else
                {
                    return MANIFEST_BACKUPS_DIR;
                }
            }

            return Path.Combine(installDir, MANIFEST_BACKUPS_DIR);
        }

        public static string GetBaseInstallDirectory()
        {
            var installDir = Config.InstallDirectory;
            if (string.IsNullOrWhiteSpace(installDir))
            {
                return DEFAULT_DOWNLOAD_DIR;
            }

            int firstVar = installDir.IndexOf('{');
            if (firstVar != -1)
            {
                int lastSeparator = installDir.LastIndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, firstVar);
                if (lastSeparator != -1)
                {
                    return installDir.Substring(0, lastSeparator);
                }
                else
                {
                    return ".";
                }
            }

            return installDir;
        }

        static bool CreateDirectories(uint appId, uint depotId, uint depotVersion, string branch, uint? parentAppId, KeyValue depotConfig, out string installDir)
        {
            installDir = null;
            try
            {
                var resolvedPath = ResolveInstallDirectory(appId, depotId, depotVersion, branch, parentAppId, depotConfig);

                if (string.IsNullOrWhiteSpace(resolvedPath))
                {
                    Directory.CreateDirectory(DEFAULT_DOWNLOAD_DIR);

                    var depotPath = Path.Combine(DEFAULT_DOWNLOAD_DIR, depotId.ToString());
                    Directory.CreateDirectory(depotPath);

                    installDir = Path.Combine(depotPath, depotVersion.ToString());
                    Directory.CreateDirectory(installDir);

                    Directory.CreateDirectory(Path.Combine(installDir, CONFIG_DIR));
                    Directory.CreateDirectory(Path.Combine(installDir, STAGING_DIR));
                }
                else
                {
                    Directory.CreateDirectory(resolvedPath);

                    installDir = resolvedPath;

                    Directory.CreateDirectory(Path.Combine(installDir, CONFIG_DIR));
                    Directory.CreateDirectory(Path.Combine(installDir, STAGING_DIR));
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in CreateDirectories: {ex}");
                return false;
            }

            return true;
        }

        static bool TestIsFileIncluded(string filename)
        {
            if (!Config.UsingFileList)
                return true;

            filename = filename.Replace('\\', '/');

            if (Config.FilesToDownload.Contains(filename))
            {
                return true;
            }

            foreach (var rgx in Config.FilesToDownloadRegex)
            {
                var m = rgx.Match(filename);

                if (m.Success)
                    return true;
            }

            return false;
        }

        // returned appid order is random? best for the caller to sort for a consistent order
        public static async Task<HashSet<uint>> GetAllAccessibleAppIdsAsync()
        {
            var appIds = new HashSet<uint>();

            if (steam3 == null || steam3.steamUser.SteamID == null)
                return appIds;

            if (steam3.steamUser.SteamID.AccountType != EAccountType.AnonUser)
            {
                steam3.WaitUntilCallback(() => { }, () => steam3.Licenses != null);
            }

            if (steam3.Licenses == null && steam3.steamUser.SteamID.AccountType != EAccountType.AnonUser)
                return appIds;

            IEnumerable<uint> licenseQuery;
            if (steam3.steamUser.SteamID.AccountType == EAccountType.AnonUser)
            {
                licenseQuery = [17906];
            }
            else
            {
                licenseQuery = steam3.Licenses.Select(x => x.PackageID).Distinct();
            }

            await steam3.RequestPackageInfo(licenseQuery);

            foreach (var license in licenseQuery)
            {
                if (steam3.PackageInfo.TryGetValue(license, out var package) && package != null)
                {
                    if (Config.ExcludeFreeApps)
                    {
                        var billingType = (EBillingType)package.KeyValues["billingtype"].AsInteger();

                        if (billingType == EBillingType.NoCost ||
                            billingType == EBillingType.FreeOnDemand ||
                            billingType == EBillingType.FreeCommercialLicense)
                        {
                            continue;
                        }
                    }

                    foreach (var child in package.KeyValues["appids"].Children)
                    {
                        appIds.Add(child.AsUnsignedInteger());
                    }
                }
            }

            return appIds;
        }

        static async Task<bool> AccountHasAccess(uint appId, uint depotId)
        {
            if (steam3 == null || steam3.steamUser.SteamID == null || (steam3.Licenses == null && steam3.steamUser.SteamID.AccountType != EAccountType.AnonUser))
                return false;

            IEnumerable<uint> licenseQuery;
            if (steam3.steamUser.SteamID.AccountType == EAccountType.AnonUser)
            {
                licenseQuery = [17906];
            }
            else
            {
                licenseQuery = steam3.Licenses.Select(x => x.PackageID).Distinct();
            }

            await steam3.RequestPackageInfo(licenseQuery);

            foreach (var license in licenseQuery)
            {
                if (steam3.PackageInfo.TryGetValue(license, out var package) && package != null)
                {
                    if (package.KeyValues["appids"].Children.Any(child => child.AsUnsignedInteger() == depotId))
                        return true;

                    if (package.KeyValues["depotids"].Children.Any(child => child.AsUnsignedInteger() == depotId))
                        return true;
                }
            }

            // Check if this app is free to download without a license
            var info = GetSteam3AppSection(appId, EAppInfoSection.Common);
            if (info != null && info["FreeToDownload"].AsBoolean())
                return true;

            return false;
        }

        internal static KeyValue GetSteam3AppSection(uint appId, EAppInfoSection section)
        {
            if (steam3 == null || steam3.AppInfo == null)
            {
                return null;
            }

            if (!steam3.AppInfo.TryGetValue(appId, out var app) || app == null)
            {
                return null;
            }

            var appinfo = app.KeyValues;
            var section_key = section switch
            {
                EAppInfoSection.Common => "common",
                EAppInfoSection.Extended => "extended",
                EAppInfoSection.Config => "config",
                EAppInfoSection.Depots => "depots",
                _ => throw new NotImplementedException(),
            };
            var section_kv = appinfo.Children.Where(c => c.Name == section_key).FirstOrDefault();
            return section_kv;
        }

        static uint GetSteam3AppBuildNumber(uint appId, string branch)
        {
            if (appId == INVALID_APP_ID)
                return 0;


            var depots = GetSteam3AppSection(appId, EAppInfoSection.Depots);
            if (depots == null)
                return 0;

            var branches = depots["branches"];
            var node = branches[branch];

            if (node == KeyValue.Invalid)
                return 0;

            var buildid = node["buildid"];

            if (buildid == KeyValue.Invalid)
                return 0;

            return uint.Parse(buildid.Value);
        }

        static uint GetSteam3DepotProxyAppId(uint depotId, uint appId)
        {
            var depots = GetSteam3AppSection(appId, EAppInfoSection.Depots);
            if (depots == null)
                return INVALID_APP_ID;

            var depotChild = depots[depotId.ToString()];

            if (depotChild == KeyValue.Invalid)
                return INVALID_APP_ID;

            if (depotChild["depotfromapp"] == KeyValue.Invalid)
                return INVALID_APP_ID;

            return depotChild["depotfromapp"].AsUnsignedInteger();
        }

        static async Task<ulong> GetSteam3DepotManifest(uint depotId, uint appId, string branch)
        {
            var depots = GetSteam3AppSection(appId, EAppInfoSection.Depots);
            if (depots == null)
                return INVALID_MANIFEST_ID;

            var depotChild = depots[depotId.ToString()];

            if (depotChild == KeyValue.Invalid)
                return INVALID_MANIFEST_ID;

            // Shared depots can either provide manifests, or leave you relying on their parent app.
            // It seems that with the latter, "sharedinstall" will exist (and equals 2 in the one existance I know of).
            // Rather than relay on the unknown sharedinstall key, just look for manifests. Test cases: 111710, 346680.
            if (depotChild["manifests"] == KeyValue.Invalid && depotChild["depotfromapp"] != KeyValue.Invalid)
            {
                var otherAppId = depotChild["depotfromapp"].AsUnsignedInteger();
                if (otherAppId == appId)
                {
                    // This shouldn't ever happen, but ya never know with Valve. Don't infinite loop.
                    Logger.Error("App {0}, Depot {1} has depotfromapp of {2}!",
                        appId, depotId, otherAppId);
                    return INVALID_MANIFEST_ID;
                }

                await steam3.RequestAppInfo(otherAppId);

                return await GetSteam3DepotManifest(depotId, otherAppId, branch);
            }

            var manifests = depotChild["manifests"];

            if (manifests.Children.Count == 0)
                return INVALID_MANIFEST_ID;

            var node = manifests[branch]["gid"];

            // Non passworded branch, found the manifest
            if (node.Value != null)
                return ulong.Parse(node.Value);

            // If we requested public branch and it had no manifest, nothing to do
            if (string.Equals(branch, DEFAULT_BRANCH, StringComparison.OrdinalIgnoreCase))
                return INVALID_MANIFEST_ID;

            // Either the branch just doesn't exist, or it has a password
            if (string.IsNullOrEmpty(Config.BetaPassword))
            {
                Logger.Error($"Branch {branch} for depot {depotId} was not found, either it does not exist or it has a password.");
                return INVALID_MANIFEST_ID;
            }

            if (!steam3.AppBetaPasswords.ContainsKey(branch))
            {
                // Submit the password to Steam now to get encryption keys
                await steam3.CheckAppBetaPassword(appId, Config.BetaPassword);

                if (!steam3.AppBetaPasswords.ContainsKey(branch))
                {
                    Logger.Error($"Error: Password was invalid for branch {branch} (or the branch does not exist)");
                    return INVALID_MANIFEST_ID;
                }
            }

            // Got the password, request private depot section
            // TODO: We're probably repeating this request for every depot?
            var privateDepotSection = await steam3.GetPrivateBetaDepotSection(appId, branch);

            // Now repeat the same code to get the manifest gid from depot section
            depotChild = privateDepotSection[depotId.ToString()];

            if (depotChild == KeyValue.Invalid)
                return INVALID_MANIFEST_ID;

            manifests = depotChild["manifests"];

            if (manifests.Children.Count == 0)
                return INVALID_MANIFEST_ID;

            node = manifests[branch]["gid"];

            if (node.Value == null)
                return INVALID_MANIFEST_ID;

            return ulong.Parse(node.Value);
        }

        static string GetAppName(uint appId)
        {
            var info = GetSteam3AppSection(appId, EAppInfoSection.Common);
            if (info == null)
                return string.Empty;

            return info["name"].AsString();
        }

        public static bool InitializeSteam3(string username, string password)
        {
            string loginToken = null;

            if (Config.LoginToken != null)
            {
                loginToken = Config.LoginToken;
            }
            else if (username != null && Config.RememberPassword)
            {
                _ = AccountSettingsStore.Instance.LoginTokens.TryGetValue(username, out loginToken);
            }

            steam3 = new Steam3Session(
                new SteamUser.LogOnDetails
                {
                    Username = username,
                    Password = loginToken == null ? password : null,
                    ShouldRememberPassword = Config.RememberPassword,
                    AccessToken = loginToken,
                    LoginID = Config.LoginID ?? 0x534B32, // "SK2"
                }
            );

            if (!steam3.WaitForCredentials())
            {
                Logger.Error("Unable to get steam3 credentials.");
                return false;
            }

            Task.Run(steam3.TickCallbacks);

            return true;
        }

        public static void ShutdownSteam3()
        {
            if (steam3 == null)
                return;

            steam3.Disconnect();
        }

        public static ulong? GetSteamId()
        {
            return steam3?.steamUser?.SteamID?.ConvertToUInt64();
        }

        private static async Task ProcessPublishedFileAsync(uint appId, ulong publishedFileId, List<ValueTuple<string, string>> fileUrls, List<ulong> contentFileIds)
        {
            var details = await steam3.GetPublishedFileDetails(appId, publishedFileId);
            var fileType = (EWorkshopFileType)details.file_type;

            if (fileType == EWorkshopFileType.Collection)
            {
                foreach (var child in details.children)
                {
                    await ProcessPublishedFileAsync(appId, child.publishedfileid, fileUrls, contentFileIds);
                }
            }
            else if (SupportedWorkshopFileTypes.Contains(fileType))
            {
                if (!string.IsNullOrEmpty(details?.file_url))
                {
                    fileUrls.Add((details.filename, details.file_url));
                }
                else if (details?.hcontent_file > 0)
                {
                    contentFileIds.Add(details.hcontent_file);
                }
                else
                {
                    Logger.Error("Unable to locate manifest ID for published file {0}", publishedFileId);
                }
            }
            else
            {
                Logger.Debug("Published file {0} has unsupported file type {1}. Skipping file", publishedFileId, fileType);
            }
        }

        public static async Task DownloadPubfileAsync(uint appId, ulong publishedFileId)
        {
            List<ValueTuple<string, string>> fileUrls = new();
            List<ulong> contentFileIds = new();

            await ProcessPublishedFileAsync(appId, publishedFileId, fileUrls, contentFileIds);

            foreach (var item in fileUrls)
            {
                await DownloadWebFile(appId, item.Item1, item.Item2);
            }

            if (contentFileIds.Count > 0)
            {
                var depotManifestIds = contentFileIds.Select(id => (appId, id)).ToList();
                await DownloadAppAsync(appId, depotManifestIds, DEFAULT_BRANCH, null, null, null, false, true);
            }
        }

        public static async Task DownloadUGCAsync(uint appId, ulong ugcId)
        {
            SteamCloud.UGCDetailsCallback details = null;

            if (steam3.steamUser.SteamID.AccountType != EAccountType.AnonUser)
            {
                details = await steam3.GetUGCDetails(ugcId);
            }
            else
            {
                Logger.Error($"Unable to query UGC details for {ugcId} from an anonymous account");
            }

            if (!string.IsNullOrEmpty(details?.URL))
            {
                await DownloadWebFile(appId, details.FileName, details.URL);
            }
            else
            {
                await DownloadAppAsync(appId, [(appId, ugcId)], DEFAULT_BRANCH, null, null, null, false, true);
            }
        }

        private static async Task DownloadWebFile(uint appId, string fileName, string url)
        {
            if (!CreateDirectories(appId, 0, 0, DEFAULT_BRANCH, null, KeyValue.Invalid, out var installDir))
            {
                Logger.Error("Error: Unable to create install directories!");
                return;
            }

            var stagingDir = Path.Combine(installDir, STAGING_DIR);
            var fileStagingPath = Path.Combine(stagingDir, fileName);
            var fileFinalPath = Path.Combine(installDir, fileName);

            Directory.CreateDirectory(Path.GetDirectoryName(fileFinalPath));
            Directory.CreateDirectory(Path.GetDirectoryName(fileStagingPath));

            using (var file = File.OpenWrite(fileStagingPath))
            using (var client = HttpClientFactory.CreateHttpClient())
            {
                Logger.Info("Downloading {0}", fileName);
                var responseStream = await client.GetStreamAsync(url);
                await responseStream.CopyToAsync(file);
            }

            if (File.Exists(fileFinalPath))
            {
                File.Delete(fileFinalPath);
            }

            File.Move(fileStagingPath, fileFinalPath);
        }

        // TODO: refractor needed
        public static async Task RestoreAppAsync(uint appId, string buildId, string branch, string os, string arch, string language, bool lv, bool includeDlc)
        {
            // Load our configuration data containing the depots currently installed
            var configPath = GetBaseInstallDirectory();

            Directory.CreateDirectory(Path.Combine(configPath, CONFIG_DIR));
            if (DepotConfigStore.Instance == null)
            {
                DepotConfigStore.LoadFromFile(Path.Combine(configPath, CONFIG_DIR, DEPOT_CONFIG_FILE));
            }

            var backupBaseDir = Path.Combine(GetManifestBaseDirectory(), appId.ToString());
            if (!Directory.Exists(backupBaseDir))
            {
                Logger.Warning($"No backups found for app {appId}");
                return;
            }

            string backupDir;
            if (!string.IsNullOrEmpty(buildId))
            {
                backupDir = Path.Combine(backupBaseDir, buildId);
                if (!Directory.Exists(backupDir))
                {
                    Logger.Warning($"Backup for build {buildId} not found.");
                    return;
                }
            }
            else
            {
                var dirs = Directory.GetDirectories(backupBaseDir);
                var validDirs = new List<(string Path, uint BuildId)>();

                foreach (var dir in dirs)
                {
                    var buildIdVal = await IdentifyBuildFromDirectory(dir, appId);
                    if (buildIdVal.HasValue)
                    {
                        validDirs.Add((dir, buildIdVal.Value));
                    }
                    else if (uint.TryParse(Path.GetFileName(dir), out var dirBuildId))
                    {
                        validDirs.Add((dir, dirBuildId));
                    }
                }

                validDirs = validDirs.OrderByDescending(x => x.BuildId).ToList();

                if (validDirs.Count == 0)
                {
                    Logger.Info($"No valid backup directories found for app {appId}");
                    return;
                }

                backupDir = validDirs.First().Path;

                var targetOS = os ?? Util.GetSteamOS();
                var targetArch = arch ?? Util.GetSteamArch();

                string bestCompatibleMatch = null;
                string bestBranchMatch = null;
                string bestOsMatch = null;

                foreach (var dir in validDirs)
                {
                    var checkPath = Path.Combine(dir.Path, $"{appId}.json");
                    JsonNode depotsNode = null;

                    if (File.Exists(checkPath))
                    {
                        try
                        {
                            var jsonString = await File.ReadAllTextAsync(checkPath);
                            var jsonNode = JsonNode.Parse(jsonString);
                            depotsNode = jsonNode["depots"] ?? jsonNode["depot"];
                        }
                        catch { }
                    }

                    bool branchMatches = true;
                    if (!string.IsNullOrEmpty(branch))
                    {
                        branchMatches = false;
                        if (depotsNode != null)
                        {
                            var branchNode = depotsNode?["branches"]?[branch];
                            if (branchNode != null)
                            {
                                var bId = branchNode["buildid"]?.ToString();
                                if (bId == dir.BuildId.ToString())
                                {
                                    branchMatches = true;
                                }
                            }
                        }
                    }

                    bool isOsCompatible = IsBackupCompatible(dir.Path, targetOS, targetArch, depotsNode);

                    if (branchMatches)
                    {
                        if (bestBranchMatch == null)
                            bestBranchMatch = dir.Path;

                        if (isOsCompatible)
                        {
                            bestCompatibleMatch = dir.Path;
                            break;
                        }
                    }

                    if (isOsCompatible && bestOsMatch == null)
                    {
                        bestOsMatch = dir.Path;
                    }
                }

                if (bestCompatibleMatch != null)
                {
                    backupDir = bestCompatibleMatch;
                }
                else if (bestBranchMatch != null)
                {
                    backupDir = bestBranchMatch;
                }
                else if (bestOsMatch != null)
                {
                    backupDir = bestOsMatch;
                    Logger.Warning($"Warning: Branch mismatch. Restoring from backup {backupDir} because it matches target OS.");
                }
                else
                {
                    Logger.Warning($"Warning: No backup found that matches the target OS/Arch ({targetOS}/{targetArch}).");
                    Logger.Warning($"Falling back to backup: {backupDir}");
                    Logger.Warning("If this is incorrect, please specify a build ID using -restore-backup <build_id>");
                }

                Logger.Info($"Restoring from backup: {backupDir}");
            }

            uint targetBuildIdNum = 0;
            var identifiedBuildId = await IdentifyBuildFromDirectory(backupDir, appId);
            if (identifiedBuildId.HasValue)
            {
                targetBuildIdNum = identifiedBuildId.Value;
            }
            else
            {
                uint.TryParse(Path.GetFileName(backupDir), out targetBuildIdNum);
            }

            // Try to infer branch from appinfo.json if we have a specific build ID
            if (targetBuildIdNum != 0)
            {
                var branchCheckAppInfoPath = Path.Combine(backupDir, $"{appId}.json");
                if (File.Exists(branchCheckAppInfoPath))
                {
                    try
                    {
                        var jsonString = await File.ReadAllTextAsync(branchCheckAppInfoPath);
                        var jsonNode = JsonNode.Parse(jsonString);
                        var depotsNode = jsonNode["depots"] ?? jsonNode["depot"];
                        if (depotsNode != null)
                        {
                            var branchesNode = depotsNode["branches"];
                            if (branchesNode != null)
                            {
                                foreach (var child in branchesNode.AsObject())
                                {
                                    var bName = child.Key;
                                    var bNode = child.Value;
                                    if (bNode["buildid"]?.ToString() == targetBuildIdNum.ToString())
                                    {
                                        if (branch != bName)
                                        {
                                            Logger.Debug($"Inferred branch '{bName}' from build ID {targetBuildIdNum}");
                                            branch = bName;
                                        }
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }
            }

            var keyFile = Path.Combine(backupDir, "key.vdf");
            var luaFile = Path.Combine(backupDir, $"{appId}.lua");
            var depotKeys = new Dictionary<uint, byte[]>();

            if (File.Exists(keyFile))
            {
                Logger.Debug($"Loading depot keys from {keyFile}");
                var kv = KeyValue.LoadAsText(keyFile);
                var target = kv;
                if (kv.Name != "depots" && kv["depots"] != KeyValue.Invalid)
                {
                    target = kv["depots"];
                }

                foreach (var depotNode in target.Children)
                {
                    if (uint.TryParse(depotNode.Name, out var depotId))
                    {
                        var keyStr = depotNode["DecryptionKey"].Value;
                        if (!string.IsNullOrEmpty(keyStr))
                        {
                            depotKeys[depotId] = Util.DecodeHexString(keyStr);
                        }
                    }
                }
            }
            else if (File.Exists(luaFile))
            {
                Logger.Debug($"Loading depot keys from {luaFile}");
                depotKeys = LoadLuaKeys(luaFile);
            }
            else
            {
                Logger.Warning("Warning: key.vdf and .lua not found in backup. Depots might fail to decrypt.");
            }

            var manifestFiles = Directory.GetFiles(backupDir, "*.manifest");
            var depotManifests = new List<(uint depotId, ulong manifestId, string path)>();
            foreach (var file in manifestFiles)
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                var parts = fileName.Split('_');
                if (parts.Length >= 2 && uint.TryParse(parts[0], out var dId) && ulong.TryParse(parts[1], out var mId))
                {
                    depotManifests.Add((dId, mId, file));
                }
            }

            if (depotManifests.Count == 0)
            {
                Logger.Info("No manifests found in backup.");
                return;
            }

            cdnPool = new CDNClientPool(steam3, appId);

            var appInfoPath = Path.Combine(backupDir, $"{appId}.json");
            var appInfoLoaded = false;
            if (File.Exists(appInfoPath))
            {
                try
                {
                    var jsonString = await File.ReadAllTextAsync(appInfoPath);
                    var jsonNode = JsonNode.Parse(jsonString);
                    if (jsonNode is JsonObject jsonObj)
                    {
                        var kv = new KeyValue("appinfo");
                        foreach (var prop in jsonObj)
                        {
                            if (prop.Key.StartsWith("_") || prop.Key == "accesstoken") continue;

                            kv.Children.Add(JsonToKeyValue(prop.Key, prop.Value));
                        }

                        bool missingToken = false;
                        if (jsonObj.TryGetPropertyValue("_missing_token", out var missingTokenNode))
                            missingToken = missingTokenNode.GetValue<bool>();

                        uint changeNumber = 0;
                        if (jsonObj.TryGetPropertyValue("_change_number", out var changeNumberNode))
                            changeNumber = changeNumberNode.GetValue<uint>();

                        byte[] sha = null;
                        if (jsonObj.TryGetPropertyValue("_sha", out var shaNode) && shaNode != null)
                            sha = Util.DecodeHexString(shaNode.GetValue<string>());

                        var newAppInfo = CreatePICSProductInfo(appId, kv, missingToken, changeNumber, sha);

                        steam3.AppInfo[appId] = newAppInfo;
                        appInfoLoaded = true;
                        Logger.Debug($"Loaded AppInfo from {appInfoPath}");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Warning: Failed to load local AppInfo: {ex.Message}");
                }
            }

            if (!appInfoLoaded)
            {
                Logger.Warning("Warning: Local AppInfo not found or failed to load. Falling back to Steam.");
                await steam3.RequestAppInfo(appId);

                var liveBuildId = GetSteam3AppBuildNumber(appId, branch);
                if (liveBuildId != targetBuildIdNum)
                {
                    Logger.Error($"Error: Live Steam data build ID ({liveBuildId}) does not match backup build ID ({targetBuildIdNum}). Aborting restore to prevent data corruption.");
                    return;
                }
            }

            var infos = new List<DepotDownloadInfo>();
            var depots = GetSteam3AppSection(appId, EAppInfoSection.Depots);

            // Check for missing depots (potential depotfromapp)
            if (depots != null)
            {
                foreach (var child in depots.Children)
                {
                    if (uint.TryParse(child.Name, out var dId))
                    {
                        if (!depotManifests.Any(x => x.depotId == dId))
                        {
                            var depotFromApp = child["depotfromapp"];
                            if (depotFromApp != KeyValue.Invalid)
                            {
                                var otherAppId = depotFromApp.AsUnsignedInteger();
                                var otherAppBackupBase = Path.Combine(GetManifestBaseDirectory(), otherAppId.ToString());

                                if (Directory.Exists(otherAppBackupBase))
                                {
                                    // We don't know the exact manifest ID because it's not in the parent app's info.
                                    // Search for any manifest for this depot in the other app's backup.
                                    var searchPattern = $"{dId}_*.manifest";
                                    var foundFiles = Directory.GetFiles(otherAppBackupBase, searchPattern, SearchOption.AllDirectories);

                                    if (foundFiles.Length > 0)
                                    {
                                        // If multiple are found, pick the most recent one
                                        var bestFile = foundFiles.OrderByDescending(f => File.GetLastWriteTime(f)).First();
                                        var fileName = Path.GetFileNameWithoutExtension(bestFile);
                                        var parts = fileName.Split('_');

                                        if (parts.Length >= 2 && ulong.TryParse(parts[1], out var mId))
                                        {
                                            Logger.Debug($"Found manifest for depot {dId} in {otherAppId} backup: {bestFile}");
                                            depotManifests.Add((dId, mId, bestFile));
                                        }
                                    }
                                    else
                                    {
                                        Logger.Warning($"Warning: No manifests found for depot {dId} (from app {otherAppId}) in backup.");
                                    }
                                }
                                else
                                {
                                    Logger.Warning($"Warning: Backup directory for app {otherAppId} not found. Cannot restore depot {dId}.");
                                }
                            }
                        }
                    }
                }
            }

            foreach (var (depotId, manifestId, manifestPath) in depotManifests)
            {
                byte[] depotKey = null;
                depotKeys.TryGetValue(depotId, out depotKey);

                // fallbacks if key.vdf is missing
                if (depotKey == null)
                {
                    if (steam3.AppInfo.TryGetValue(appId, out var appInfo) && appInfo?.KeyValues != null)
                    {
                        var depotsNode = appInfo.KeyValues["depots"];
                        if (depotsNode != KeyValue.Invalid)
                        {
                            var depotNode = depotsNode[depotId.ToString()];
                            if (depotNode != KeyValue.Invalid)
                            {
                                var keyStr = depotNode["decryptionkey"].Value;
                                if (!string.IsNullOrEmpty(keyStr))
                                {
                                    depotKey = Util.DecodeHexString(keyStr);
                                }
                            }
                        }
                    }
                }

                if (depotKey == null)
                {
                    await steam3.RequestDepotKey(depotId, appId);
                    if (steam3.DepotKeys.TryGetValue(depotId, out var k))
                    {
                        depotKey = k;
                    }
                }

                if (depotKey == null)
                {
                    Logger.Warning($"Warning: No key found for depot {depotId}. Proceeding without key (depot might be unencrypted).");
                }

                KeyValue depotConfig = KeyValue.Invalid;
                if (depots != null && depots[depotId.ToString()] != KeyValue.Invalid)
                {
                    depotConfig = depots[depotId.ToString()]["config"];
                }

                if (depotConfig != KeyValue.Invalid)
                {
                    if (!Config.DownloadAllPlatforms &&
                        depotConfig["oslist"] != KeyValue.Invalid &&
                        !string.IsNullOrWhiteSpace(depotConfig["oslist"].Value))
                    {
                        var oslist = depotConfig["oslist"].Value.Split(',');
                        if (Array.IndexOf(oslist, os ?? Util.GetSteamOS()) == -1)
                            continue;
                    }

                    if (!Config.DownloadAllArchs &&
                        depotConfig["osarch"] != KeyValue.Invalid &&
                        !string.IsNullOrWhiteSpace(depotConfig["osarch"].Value))
                    {
                        var depotArch = depotConfig["osarch"].Value;
                        if (depotArch != (arch ?? Util.GetSteamArch()))
                            continue;
                    }

                    if (!Config.DownloadAllLanguages &&
                        depotConfig["language"] != KeyValue.Invalid &&
                        !string.IsNullOrWhiteSpace(depotConfig["language"].Value))
                    {
                        var depotLang = depotConfig["language"].Value;
                        if (depotLang != (language ?? "english"))
                            continue;
                    }

                    if (!lv &&
                        depotConfig["lowviolence"] != KeyValue.Invalid &&
                        depotConfig["lowviolence"].AsBoolean())
                        continue;
                }

                if (!CreateDirectories(appId, depotId, targetBuildIdNum, branch, null, depotConfig, out var installDir))
                {
                    Logger.Error($"Failed to create directories for depot {depotId}");
                    continue;
                }

                var configDir = Path.Combine(installDir, CONFIG_DIR);
                var destManifest = Path.Combine(configDir, Path.GetFileName(manifestPath));
                File.Copy(manifestPath, destManifest, true);

                var shaPath = manifestPath + ".sha";
                if (File.Exists(shaPath))
                {
                    File.Copy(shaPath, destManifest + ".sha", true);
                }

                var containingAppId = appId;
                var proxyAppId = GetSteam3DepotProxyAppId(depotId, appId);
                if (proxyAppId != INVALID_APP_ID)
                {
                    var common = GetSteam3AppSection(appId, EAppInfoSection.Common);
                    if (common == null || !common["FreeToDownload"].AsBoolean())
                    {
                        containingAppId = proxyAppId;
                        Logger.Debug($"depotfromapp found. Redirecting depot {depotId} to app {containingAppId}");
                    }
                }

                infos.Add(new DepotDownloadInfo(depotId, containingAppId, manifestId, branch, installDir, depotKey));
            }

            Logger.Info($"Restoring {infos.Count} depots...");
            try
            {
                await DownloadSteam3Async(infos).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Error($"Restore failed: {ex.Message}");
                throw;
            }

            if (includeDlc)
            {
                var extendedInfo = GetSteam3AppSection(appId, EAppInfoSection.Extended);
                if (extendedInfo != null && extendedInfo["listofdlc"] != KeyValue.Invalid)
                {
                    Logger.Info($"{appId} returned the following DLCs: {extendedInfo["listofdlc"].Value}");
                    var dlcString = extendedInfo["listofdlc"].Value;
                    if (!string.IsNullOrEmpty(dlcString))
                    {
                        var dlcAppIds = dlcString.Split(',').Select(uint.Parse).ToList();
                        var mainAppDepots = GetSteam3AppSection(appId, EAppInfoSection.Depots);

                        foreach (var dlcAppId in dlcAppIds)
                        {
                            try
                            {
                                if (mainAppDepots != null && mainAppDepots[dlcAppId.ToString()] != KeyValue.Invalid)
                                {
                                    Logger.Debug($"DLC {dlcAppId} is included in main app {appId}, skipping separate restore...");
                                    continue;
                                }

                                Logger.Info($"Found DLC {dlcAppId}, restoring...");
                                // DLCs have their own build IDs, so we can't use the main app's build ID.
                                // We'll restore the latest backup for the DLC instead (following IsBackupCompatible logic).
                                await RestoreAppAsync(dlcAppId, null, branch, os, arch, language, lv, includeDlc);
                            }
                            catch (Exception e)
                            {
                                Logger.Error($"Failed to restore DLC {dlcAppId}: {e.Message}");
                            }
                        }
                    }
                }
            }
        }

        public static async Task DownloadAppAsync(uint appId, List<(uint depotId, ulong manifestId)> depotManifestIds, string branch, string os, string arch, string language, bool lv, bool isUgc, bool includeDlc = false, uint? parentAppId = null)
        {
            cdnPool = new CDNClientPool(steam3, appId);

            // Load our configuration data containing the depots currently installed
            var configPath = GetBaseInstallDirectory();

            Directory.CreateDirectory(Path.Combine(configPath, CONFIG_DIR));
            if (DepotConfigStore.Instance == null)
            {
                DepotConfigStore.LoadFromFile(Path.Combine(configPath, CONFIG_DIR, DEPOT_CONFIG_FILE));
            }

            await steam3?.RequestAppInfo(appId);

            if (!await AccountHasAccess(appId, appId))
            {
                if (steam3.steamUser.SteamID.AccountType != EAccountType.AnonUser && await steam3.RequestFreeAppLicense(appId))
                {
                    Logger.Info("Obtained FreeOnDemand license for app {0}", appId);

                    // Fetch app info again in case we didn't get it fully without a license.
                    await steam3.RequestAppInfo(appId, true);
                }
                else
                {
                    var contentName = GetAppName(appId);
                    throw new ContentDownloaderException(string.Format("App {0} ({1}) is not available from this account.", appId, contentName), DownloadErrorCode.NotAvailableForAccount);
                }
            }

            if (Config.DownloadAllBranches && branch == null)
            {
                var depots = GetSteam3AppSection(appId, EAppInfoSection.Depots);
                if (depots == null)
                {
                    throw new ContentDownloaderException($"Couldn't find any depots to download for app {appId}", DownloadErrorCode.DepotNotFound);
                }

                var branches = depots["branches"];

                foreach (var branchChild in branches.Children)
                {
                    var branchName = branchChild.Name;
                    if (branchChild["pwdrequired"].AsBoolean())
                    {
                        Logger.Info("Skipping password-protected branch '{0}'.", branchName);
                        continue;
                    }

                    // Ensure cdnPool is correct for this iteration (in case DLC download changed it)
                    if (cdnPool.AppId != appId)
                    {
                        cdnPool = new CDNClientPool(steam3, appId);
                    }

                    await DownloadAppBranchAsync(appId, depotManifestIds, branchName, os, arch, language, lv, isUgc, includeDlc, parentAppId).ConfigureAwait(false);
                }
            }
            else
            {
                branch ??= DEFAULT_BRANCH;
                await DownloadAppBranchAsync(appId, depotManifestIds, branch, os, arch, language, lv, isUgc, includeDlc, parentAppId).ConfigureAwait(false);
            }
        }

        private static async Task DownloadAppBranchAsync(uint appId, List<(uint depotId, ulong manifestId)> depotManifestIds, string branch, string os, string arch, string language, bool lv, bool isUgc, bool includeDlc = false, uint? parentAppId = null)
        {
            var hasSpecificDepots = depotManifestIds.Count > 0;
            var depotIdsFound = new List<uint>();
            var depotIdsExpected = depotManifestIds.Select(x => x.depotId).ToList();
            var depots = GetSteam3AppSection(appId, EAppInfoSection.Depots);

            if (isUgc)
            {
                var workshopDepot = depots["workshopdepot"].AsUnsignedInteger();
                if (workshopDepot != 0 && !depotIdsExpected.Contains(workshopDepot))
                {
                    depotIdsExpected.Add(workshopDepot);
                    depotManifestIds = depotManifestIds.Select(pair => (workshopDepot, pair.manifestId)).ToList();
                }

                depotIdsFound.AddRange(depotIdsExpected);
            }
            else
            {
                Logger.Info("Using app branch: '{0}'.", branch);

                if (depots != null)
                {
                    foreach (var depotSection in depots.Children)
                    {
                        var id = INVALID_DEPOT_ID;
                        if (depotSection.Children.Count == 0)
                            continue;

                        if (!uint.TryParse(depotSection.Name, out id))
                            continue;

                        if (hasSpecificDepots && !depotIdsExpected.Contains(id))
                            continue;

                        if (!hasSpecificDepots)
                        {
                            var depotConfig = depotSection["config"];
                            if (depotConfig != KeyValue.Invalid)
                            {
                                if (!Config.DownloadAllPlatforms &&
                                    depotConfig["oslist"] != KeyValue.Invalid &&
                                    !string.IsNullOrWhiteSpace(depotConfig["oslist"].Value))
                                {
                                    var oslist = depotConfig["oslist"].Value.Split(',');
                                    if (Array.IndexOf(oslist, os ?? Util.GetSteamOS()) == -1)
                                        continue;
                                }

                                if (!Config.DownloadAllArchs &&
                                    depotConfig["osarch"] != KeyValue.Invalid &&
                                    !string.IsNullOrWhiteSpace(depotConfig["osarch"].Value))
                                {
                                    var depotArch = depotConfig["osarch"].Value;
                                    if (depotArch != (arch ?? Util.GetSteamArch()))
                                        continue;
                                }

                                if (!Config.DownloadAllLanguages &&
                                    depotConfig["language"] != KeyValue.Invalid &&
                                    !string.IsNullOrWhiteSpace(depotConfig["language"].Value))
                                {
                                    var depotLang = depotConfig["language"].Value;
                                    if (depotLang != (language ?? "english"))
                                        continue;
                                }

                                if (!lv &&
                                    depotConfig["lowviolence"] != KeyValue.Invalid &&
                                    depotConfig["lowviolence"].AsBoolean())
                                    continue;
                            }
                        }

                        depotIdsFound.Add(id);

                        if (!hasSpecificDepots)
                            depotManifestIds.Add((id, INVALID_MANIFEST_ID));
                    }
                }

                if (depotManifestIds.Count == 0 && !hasSpecificDepots)
                {
                    throw new ContentDownloaderException(string.Format("Couldn't find any depots to download for app {0}", appId), DownloadErrorCode.DepotNotFound);
                }

                if (depotIdsFound.Count < depotIdsExpected.Count)
                {
                    var remainingDepotIds = depotIdsExpected.Except(depotIdsFound);
                    throw new ContentDownloaderException(string.Format("Depot {0} not listed for app {1}", string.Join(", ", remainingDepotIds), appId), DownloadErrorCode.DepotNotFound);
                }
            }

            var infos = new List<DepotDownloadInfo>();

            foreach (var (depotId, manifestId) in depotManifestIds)
            {
                KeyValue depotConfig = KeyValue.Invalid;
                var depotSection = depots[depotId.ToString()];
                if (depotSection != KeyValue.Invalid)
                {
                    depotConfig = depotSection["config"];
                }

                var info = await GetDepotInfo(depotId, appId, manifestId, branch, parentAppId, depotConfig);
                if (info != null)
                {
                    infos.Add(info);
                }
            }

            Console.WriteLine();

            try
            {
                await DownloadSteam3Async(infos).ConfigureAwait(false);

                if (Config.BackupManifests)
                {
                    CreateAppBackup(appId, infos, branch);
                }
            }
            catch (OperationCanceledException)
            {
                Logger.Warning("App {0} was not completely downloaded.", appId);
                throw;
            }

            if (includeDlc)
            {
                var extendedInfo = GetSteam3AppSection(appId, EAppInfoSection.Extended);
                if (extendedInfo != null && extendedInfo["listofdlc"] != KeyValue.Invalid)
                {
                    Logger.Debug($"{appId} returned the following DLCs: {extendedInfo["listofdlc"].Value}");
                    var dlcString = extendedInfo["listofdlc"].Value;
                    if (!string.IsNullOrEmpty(dlcString))
                    {
                        var dlcAppIds = dlcString.Split(',').Select(uint.Parse).ToList();

                        foreach (var dlcAppId in dlcAppIds)
                        {
                            try
                            {
                                Logger.Info($"Found DLC {dlcAppId}, downloading...");
                                await DownloadAppAsync(dlcAppId, [], branch, os, arch, language, lv, isUgc, includeDlc, appId);
                            }
                            catch (ContentDownloaderException e)
                            {
                                Logger.Error(e.Message);
                            }
                        }
                    }
                }
            }
        }

        static async Task<DepotDownloadInfo> GetDepotInfo(uint depotId, uint appId, ulong manifestId, string branch, uint? parentAppId, KeyValue depotConfig)
        {
            if (steam3 != null && appId != INVALID_APP_ID)
            {
                await steam3.RequestAppInfo(appId);
            }

            if (!await AccountHasAccess(appId, depotId))
            {
                Logger.Debug("Depot {0} is not available from this account.", depotId);
                DownloadReporter.RecordDepotFailure(appId, depotId, manifestId, DownloadErrorCode.DepotUnavailableForAccount);
                return null;
            }

            if (manifestId == INVALID_MANIFEST_ID)
            {
                manifestId = await GetSteam3DepotManifest(depotId, appId, branch);
                if (manifestId == INVALID_MANIFEST_ID && !string.Equals(branch, DEFAULT_BRANCH, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Debug("Warning: Depot {0} does not have branch named \"{1}\". Trying {2} branch.", depotId, branch, DEFAULT_BRANCH);
                    branch = DEFAULT_BRANCH;
                    manifestId = await GetSteam3DepotManifest(depotId, appId, branch);
                }

                if (manifestId == INVALID_MANIFEST_ID)
                {
                    Logger.Debug("Depot {0} missing public subsection or manifest section.", depotId);
                    DownloadReporter.RecordDepotFailure(appId, depotId, manifestId, DownloadErrorCode.DepotMissingManifest);
                    return null;
                }
            }

            await steam3.RequestDepotKey(depotId, appId);
            if (!steam3.DepotKeys.TryGetValue(depotId, out var depotKey))
            {
                Logger.Warning("No valid depot key for {0}, unable to download.", depotId);
                DownloadReporter.RecordDepotFailure(appId, depotId, manifestId, DownloadErrorCode.InvalidDepotKey);
                return null;
            }

            var uVersion = Config.ForceBuildId ?? GetSteam3AppBuildNumber(appId, branch);

            if (!CreateDirectories(appId, depotId, uVersion, branch, parentAppId, depotConfig, out var installDir))
            {
                Logger.Error("Error: Unable to create install directories!");
                DownloadReporter.RecordDepotFailure(appId, depotId, manifestId, DownloadErrorCode.CreateDirectoryError);
                return null;
            }

            // For depots that are proxied through depotfromapp, we still need to resolve the proxy app id, unless the app is freetodownload
            var containingAppId = appId;
            var proxyAppId = GetSteam3DepotProxyAppId(depotId, appId);
            if (proxyAppId != INVALID_APP_ID)
            {
                var common = GetSteam3AppSection(appId, EAppInfoSection.Common);
                if (common == null || !common["FreeToDownload"].AsBoolean())
                {
                    containingAppId = proxyAppId;
                }
            }

            return new DepotDownloadInfo(depotId, containingAppId, manifestId, branch, installDir, depotKey);
        }

        private class ChunkMatch(DepotManifest.ChunkData oldChunk, DepotManifest.ChunkData newChunk)
        {
            public DepotManifest.ChunkData OldChunk { get; } = oldChunk;
            public DepotManifest.ChunkData NewChunk { get; } = newChunk;
        }

        private class DepotFilesData
        {
            public DepotDownloadInfo depotDownloadInfo;
            public DepotDownloadCounter depotCounter;
            public string stagingDir;
            public DepotManifest manifest;
            public DepotManifest previousManifest;
            public List<DepotManifest.FileData> filteredFiles;
            public HashSet<string> allFileNames;
        }

        private class FileStreamData
        {
            public FileStream fileStream;
            public SemaphoreSlim fileLock;
            public int chunksToDownload;
        }

        private class GlobalDownloadCounter
        {
            public ulong completeDownloadSize;
            public ulong totalBytesCompressed;
            public ulong totalBytesUncompressed;
        }

        private class DepotDownloadCounter
        {
            public ulong completeDownloadSize;
            public ulong sizeDownloaded;
            public ulong depotBytesCompressed;
            public ulong depotBytesUncompressed;
        }

        private static async Task DownloadSteam3Async(List<DepotDownloadInfo> depots)
        {
            Ansi.Progress(Ansi.ProgressState.Indeterminate);

            await cdnPool.UpdateServerList();

            var cts = new CancellationTokenSource();
            var downloadCounter = new GlobalDownloadCounter();
            var depotsToDownload = new List<DepotFilesData>(depots.Count);
            var allFileNamesAllDepots = new HashSet<string>();

            // First, fetch all the manifests for each depot (including previous manifests) and perform the initial setup
            foreach (var depot in depots)
            {
                var depotFileData = await ProcessDepotManifestAndFiles(cts, depot, downloadCounter);

                if (depotFileData != null)
                {
                    depotsToDownload.Add(depotFileData);
                    allFileNamesAllDepots.UnionWith(depotFileData.allFileNames);
                }

                cts.Token.ThrowIfCancellationRequested();
            }

            // If we're about to write all the files to the same directory, we will need to first de-duplicate any files by path
            // This is in last-depot-wins order, from Steam or the list of depots supplied by the user
            if (!string.IsNullOrWhiteSpace(Config.InstallDirectory) && depotsToDownload.Count > 0)
            {
                var claimedFileNames = new HashSet<string>();

                for (var i = depotsToDownload.Count - 1; i >= 0; i--)
                {
                    // For each depot, remove all files from the list that have been claimed by a later depot
                    depotsToDownload[i].filteredFiles.RemoveAll(file => claimedFileNames.Contains(file.FileName));

                    claimedFileNames.UnionWith(depotsToDownload[i].allFileNames);
                }
            }

            foreach (var depotFileData in depotsToDownload)
            {
                try
                {
                    await DownloadSteam3AsyncDepotFiles(cts, downloadCounter, depotFileData, allFileNamesAllDepots);
                }
                catch (Exception)
                {
                    DownloadReporter.RecordDepotFailure(depotFileData.depotDownloadInfo.AppId, depotFileData.depotDownloadInfo.DepotId, depotFileData.depotDownloadInfo.ManifestId, DownloadErrorCode.DownloadFailed);
                    throw;
                }
            }

            Ansi.Progress(Ansi.ProgressState.Hidden);


            Logger.Info("Total downloaded: {0} bytes ({1} bytes uncompressed) from {2} depots",
                downloadCounter.totalBytesCompressed, downloadCounter.totalBytesUncompressed, depots.Count);
        }

        private static async Task<DepotFilesData> ProcessDepotManifestAndFiles(CancellationTokenSource cts, DepotDownloadInfo depot, GlobalDownloadCounter downloadCounter)
        {
            var depotCounter = new DepotDownloadCounter();

            Logger.Debug("Processing depot {0}", depot.DepotId);

            DepotManifest oldManifest = null;
            DepotManifest newManifest = null;
            var configDir = Path.Combine(depot.InstallDir, CONFIG_DIR);

            var lastManifestId = INVALID_MANIFEST_ID;
            DepotConfigStore.Instance.InstalledManifestIDs.TryGetValue(depot.DepotId, out lastManifestId);

            // In case we have an early exit, this will force equiv of verifyall next run.
            DepotConfigStore.Instance.InstalledManifestIDs[depot.DepotId] = INVALID_MANIFEST_ID;
            DepotConfigStore.Save();

            if (lastManifestId != INVALID_MANIFEST_ID)
            {
                // We only have to show this warning if the old manifest ID was different
                var badHashWarning = (lastManifestId != depot.ManifestId);
                oldManifest = Util.LoadManifestFromFile(configDir, depot.DepotId, lastManifestId, badHashWarning);
            }

            if (lastManifestId == depot.ManifestId && oldManifest != null)
            {
                newManifest = oldManifest;
                Logger.Debug("Already have manifest {0} for depot {1}.", depot.ManifestId, depot.DepotId);
            }
            else
            {
                newManifest = Util.LoadManifestFromFile(configDir, depot.DepotId, depot.ManifestId, true);

                if (newManifest != null)
                {
                    Logger.Debug("Already have manifest {0} for depot {1}.", depot.ManifestId, depot.DepotId);
                }
                else
                {
                    Logger.Info($"Downloading depot {depot.DepotId} manifest");

                    ulong manifestRequestCode = 0;
                    var manifestRequestCodeExpiration = DateTime.MinValue;
                    DownloadErrorCode lastError = DownloadErrorCode.ManifestDownloadError;
                    int retries = 0;
                    do
                    {
                        if (retries > 5)
                        {
                            cts.Cancel();
                            break;
                        }

                        cts.Token.ThrowIfCancellationRequested();

                        Server connection = null;

                        try
                        {
                            connection = cdnPool.GetConnection();

                            string cdnToken = null;
                            if (steam3.CDNAuthTokens.TryGetValue((depot.DepotId, connection.Host), out var authTokenCallbackPromise))
                            {
                                var result = await authTokenCallbackPromise.Task;
                                cdnToken = result.Token;
                            }

                            var now = DateTime.Now;

                            // In order to download this manifest, we need the current manifest request code
                            // The manifest request code is only valid for a specific period in time
                            if (manifestRequestCode == 0 || now >= manifestRequestCodeExpiration)
                            {
                                manifestRequestCode = await steam3.GetDepotManifestRequestCodeAsync(
                                    depot.DepotId,
                                    depot.AppId,
                                    depot.ManifestId,
                                    depot.Branch);
                                // This code will hopefully be valid for one period following the issuing period
                                manifestRequestCodeExpiration = now.Add(TimeSpan.FromMinutes(5));

                                // If we could not get the manifest code, this is a fatal error
                                if (manifestRequestCode == 0)
                                {
                                    cts.Cancel();
                                }
                            }

                            DebugLog.WriteLine("ContentDownloader",
                                "Downloading manifest {0} from {1} with {2}",
                                depot.ManifestId,
                                connection,
                                cdnPool.ProxyServer != null ? cdnPool.ProxyServer : "no proxy");
                            newManifest = await cdnPool.CDNClient.DownloadManifestAsync(
                                depot.DepotId,
                                depot.ManifestId,
                                manifestRequestCode,
                                connection,
                                depot.DepotKey,
                                cdnPool.ProxyServer,
                                cdnToken).ConfigureAwait(false);

                            cdnPool.ReturnConnection(connection);
                        }
                        catch (TaskCanceledException)
                        {
                            Logger.Warning("Connection timeout downloading depot manifest {0} {1}. Retrying.", depot.DepotId, depot.ManifestId);
                            await Task.Delay(1000 * retries);
                            retries++;
                        }
                        catch (SteamKitWebRequestException e)
                        {
                            // If the CDN returned 403, attempt to get a cdn auth if we didn't yet
                            if (e.StatusCode == HttpStatusCode.Forbidden && !steam3.CDNAuthTokens.ContainsKey((depot.DepotId, connection.Host)))
                            {
                                await steam3.RequestCDNAuthToken(depot.AppId, depot.DepotId, connection);

                                cdnPool.ReturnConnection(connection);

                                continue;
                            }

                            cdnPool.ReturnBrokenConnection(connection);

                            if (e.StatusCode == HttpStatusCode.Unauthorized || e.StatusCode == HttpStatusCode.Forbidden)
                            {
                                lastError = DownloadErrorCode.ManifestDownload401;
                                Logger.Error("Encountered {2} for depot manifest {0} {1}. Aborting.", depot.DepotId, depot.ManifestId, (int)e.StatusCode);
                                break;
                            }

                            if (e.StatusCode == HttpStatusCode.NotFound)
                            {
                                lastError = DownloadErrorCode.ManifestDownload404;
                                Logger.Error("Encountered 404 for depot manifest {0} {1}. Aborting.", depot.DepotId, depot.ManifestId);
                                break;
                            }

                            lastError = DownloadErrorCode.ManifestDownloadError;
                            Logger.Error("Encountered error downloading depot manifest {0} {1}: {2}", depot.DepotId, depot.ManifestId, e.StatusCode);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (Exception e)
                        {
                            cdnPool.ReturnBrokenConnection(connection);
                            lastError = DownloadErrorCode.ManifestDownloadError;
                            Logger.Error("Encountered error downloading manifest for depot {0} {1}: {2}", depot.DepotId, depot.ManifestId, e.Message);
                            await Task.Delay(1000 * retries);
                            retries++;
                        }
                    } while (newManifest == null);

                    if (newManifest == null)
                    {
                        Logger.Error("\nUnable to download manifest {0} for depot {1}", depot.ManifestId, depot.DepotId);
                        DownloadReporter.RecordDepotFailure(depot.AppId, depot.DepotId, depot.ManifestId, lastError);
                        cts.Cancel();
                    }

                    // Throw the cancellation exception if requested so that this task is marked failed
                    cts.Token.ThrowIfCancellationRequested();

                    Util.SaveManifestToFile(configDir, newManifest);

                }
            }

            if (newManifest != null && Config.BackupManifests)
            {
                var backupDir = Path.Combine(GetManifestBaseDirectory(), depot.AppId.ToString());

                var buildId = Config.ForceBuildId ?? GetSteam3AppBuildNumber(depot.AppId, depot.Branch);
                if (buildId != 0)
                {
                    backupDir = Path.Combine(backupDir, buildId.ToString());
                }
                else if (steam3 != null && steam3.AppInfo.TryGetValue(depot.AppId, out var appInfo) && appInfo != null)
                {
                    backupDir = Path.Combine(backupDir, appInfo.ChangeNumber.ToString());
                }

                Directory.CreateDirectory(backupDir);
                Util.SaveManifestToFile(backupDir, newManifest);
            }

            Logger.Debug("Manifest {0} ({1})", depot.ManifestId, newManifest.CreationTime);

            if (Config.DownloadManifestOnly)
            {
                DumpManifestToTextFile(depot, newManifest);
                return null;
            }

            var stagingDir = Path.Combine(depot.InstallDir, STAGING_DIR);

            var filesAfterExclusions = newManifest.Files.AsParallel().Where(f => TestIsFileIncluded(f.FileName)).ToList();
            var allFileNames = new HashSet<string>(filesAfterExclusions.Count);

            // Pre-process
            filesAfterExclusions.ForEach(file =>
            {
                allFileNames.Add(file.FileName);

                var fileFinalPath = Path.Combine(depot.InstallDir, file.FileName);
                var fileStagingPath = Path.Combine(stagingDir, file.FileName);

                if (file.Flags.HasFlag(EDepotFileFlag.Directory))
                {
                    Directory.CreateDirectory(fileFinalPath);
                    Directory.CreateDirectory(fileStagingPath);
                }
                else
                {
                    // Some manifests don't explicitly include all necessary directories
                    Directory.CreateDirectory(Path.GetDirectoryName(fileFinalPath));
                    Directory.CreateDirectory(Path.GetDirectoryName(fileStagingPath));

                    downloadCounter.completeDownloadSize += file.TotalSize;
                    depotCounter.completeDownloadSize += file.TotalSize;
                }
            });

            return new DepotFilesData
            {
                depotDownloadInfo = depot,
                depotCounter = depotCounter,
                stagingDir = stagingDir,
                manifest = newManifest,
                previousManifest = oldManifest,
                filteredFiles = filesAfterExclusions,
                allFileNames = allFileNames
            };
        }

        private static async Task DownloadSteam3AsyncDepotFiles(CancellationTokenSource cts,
            GlobalDownloadCounter downloadCounter, DepotFilesData depotFilesData, HashSet<string> allFileNamesAllDepots)
        {
            var depot = depotFilesData.depotDownloadInfo;
            var depotCounter = depotFilesData.depotCounter;

            Logger.Info("Downloading depot {0}", depot.DepotId);

            var files = depotFilesData.filteredFiles.Where(f => !f.Flags.HasFlag(EDepotFileFlag.Directory)).ToArray();
            var networkChunkQueue = new ConcurrentQueue<(FileStreamData fileStreamData, DepotManifest.FileData fileData, DepotManifest.ChunkData chunk)>();

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Config.MaxDownloads,
                CancellationToken = cts.Token
            };

            await Parallel.ForEachAsync(files, parallelOptions, async (file, cancellationToken) =>
            {
                await Task.Yield();
                DownloadSteam3AsyncDepotFile(cts, downloadCounter, depotFilesData, file, networkChunkQueue);
            });

            await Parallel.ForEachAsync(networkChunkQueue, parallelOptions, async (q, cancellationToken) =>
            {
                await DownloadSteam3AsyncDepotFileChunk(
                    cts, downloadCounter, depotFilesData,
                    q.fileData, q.fileStreamData, q.chunk
                );
            });

            // Check for deleted files if updating the depot.
            if (depotFilesData.previousManifest != null)
            {
                var previousFilteredFiles = depotFilesData.previousManifest.Files.AsParallel().Where(f => TestIsFileIncluded(f.FileName)).Select(f => f.FileName).ToHashSet();

                // Check if we are writing to a single output directory. If not, each depot folder is managed independently
                if (string.IsNullOrWhiteSpace(Config.InstallDirectory))
                {
                    // Of the list of files in the previous manifest, remove any file names that exist in the current set of all file names
                    previousFilteredFiles.ExceptWith(depotFilesData.allFileNames);
                }
                else
                {
                    // Of the list of files in the previous manifest, remove any file names that exist in the current set of all file names across all depots being downloaded
                    previousFilteredFiles.ExceptWith(allFileNamesAllDepots);
                }

                foreach (var existingFileName in previousFilteredFiles)
                {
                    var fileFinalPath = Path.Combine(depot.InstallDir, existingFileName);

                    if (!File.Exists(fileFinalPath))
                        continue;

                    File.Delete(fileFinalPath);
                    Logger.Info("Deleted {0}", fileFinalPath);
                }
            }

            DepotConfigStore.Instance.InstalledManifestIDs[depot.DepotId] = depot.ManifestId;
            DepotConfigStore.Save();

            Logger.Info("Depot {0} - Downloaded {1} bytes ({2} bytes uncompressed)", depot.DepotId, depotCounter.depotBytesCompressed, depotCounter.depotBytesUncompressed);
        }

        private static void DownloadSteam3AsyncDepotFile(
            CancellationTokenSource cts,
            GlobalDownloadCounter downloadCounter,
            DepotFilesData depotFilesData,
            DepotManifest.FileData file,
            ConcurrentQueue<(FileStreamData, DepotManifest.FileData, DepotManifest.ChunkData)> networkChunkQueue)
        {
            cts.Token.ThrowIfCancellationRequested();

            var depot = depotFilesData.depotDownloadInfo;
            var stagingDir = depotFilesData.stagingDir;
            var depotDownloadCounter = depotFilesData.depotCounter;
            var oldProtoManifest = depotFilesData.previousManifest;
            DepotManifest.FileData oldManifestFile = null;
            if (oldProtoManifest != null)
            {
                oldManifestFile = oldProtoManifest.Files.SingleOrDefault(f => f.FileName == file.FileName);
            }

            var fileFinalPath = Path.Combine(depot.InstallDir, file.FileName);
            var fileStagingPath = Path.Combine(stagingDir, file.FileName);

            // This may still exist if the previous run exited before cleanup
            if (File.Exists(fileStagingPath))
            {
                File.Delete(fileStagingPath);
            }

            List<DepotManifest.ChunkData> neededChunks;
            var fi = new FileInfo(fileFinalPath);
            var fileDidExist = fi.Exists;
            if (!fileDidExist)
            {
                Logger.InfoOverwrite("Pre-allocating {0}", fileFinalPath);

                // create new file. need all chunks
                using var fs = File.Create(fileFinalPath);
                try
                {
                    fs.SetLength((long)file.TotalSize);
                }
                catch (IOException ex)
                {
                    throw new ContentDownloaderException(string.Format("Failed to allocate file {0}: {1}", fileFinalPath, ex.Message), DownloadErrorCode.AllocationFailed);
                }

                neededChunks = new List<DepotManifest.ChunkData>(file.Chunks);
            }
            else
            {
                // open existing
                if (oldManifestFile != null)
                {
                    neededChunks = [];

                    var hashMatches = oldManifestFile.FileHash.SequenceEqual(file.FileHash);
                    if (Config.VerifyAll || !hashMatches)
                    {
                        // we have a version of this file, but it doesn't fully match what we want
                        if (Config.VerifyAll)
                        {
                            Logger.InfoOverwrite("Validating {0}", fileFinalPath);
                        }

                        var matchingChunks = new List<ChunkMatch>();

                        foreach (var chunk in file.Chunks)
                        {
                            var oldChunk = oldManifestFile.Chunks.FirstOrDefault(c => c.ChunkID.SequenceEqual(chunk.ChunkID));
                            if (oldChunk != null)
                            {
                                matchingChunks.Add(new ChunkMatch(oldChunk, chunk));
                            }
                            else
                            {
                                neededChunks.Add(chunk);
                            }
                        }

                        var orderedChunks = matchingChunks.OrderBy(x => x.OldChunk.Offset);

                        var copyChunks = new List<ChunkMatch>();

                        using (var fsOld = File.Open(fileFinalPath, FileMode.Open))
                        {
                            foreach (var match in orderedChunks)
                            {
                                fsOld.Seek((long)match.OldChunk.Offset, SeekOrigin.Begin);

                                var adler = Util.AdlerHash(fsOld, (int)match.OldChunk.UncompressedLength);
                                if (!adler.SequenceEqual(BitConverter.GetBytes(match.OldChunk.Checksum)))
                                {
                                    neededChunks.Add(match.NewChunk);
                                }
                                else
                                {
                                    copyChunks.Add(match);
                                }
                            }
                        }

                        if (!hashMatches || neededChunks.Count > 0)
                        {
                            File.Move(fileFinalPath, fileStagingPath);

                            using (var fsOld = File.Open(fileStagingPath, FileMode.Open))
                            {
                                using var fs = File.Open(fileFinalPath, FileMode.Create);
                                try
                                {
                                    fs.SetLength((long)file.TotalSize);
                                }
                                catch (IOException ex)
                                {
                                    throw new ContentDownloaderException(string.Format("Failed to resize file to expected size {0}: {1}", fileFinalPath, ex.Message), DownloadErrorCode.ResizeFailed);
                                }

                                foreach (var match in copyChunks)
                                {
                                    fsOld.Seek((long)match.OldChunk.Offset, SeekOrigin.Begin);

                                    var tmp = new byte[match.OldChunk.UncompressedLength];
                                    fsOld.ReadExactly(tmp);

                                    fs.Seek((long)match.NewChunk.Offset, SeekOrigin.Begin);
                                    fs.Write(tmp, 0, tmp.Length);
                                }
                            }

                            File.Delete(fileStagingPath);
                        }
                    }
                }
                else
                {
                    // No old manifest or file not in old manifest. We must validate.

                    using var fs = File.Open(fileFinalPath, FileMode.Open);
                    if ((ulong)fi.Length != file.TotalSize)
                    {
                        try
                        {
                            fs.SetLength((long)file.TotalSize);
                        }
                        catch (IOException ex)
                        {
                            throw new ContentDownloaderException(string.Format("Failed to allocate file {0}: {1}", fileFinalPath, ex.Message), DownloadErrorCode.AllocationFailed);
                        }
                    }
                    Logger.InfoOverwrite("Validating {0}", fileFinalPath);
                    neededChunks = Util.ValidateSteam3FileChecksums(fs, [.. file.Chunks.OrderBy(x => x.Offset)]);
                }

                if (neededChunks.Count == 0)
                {
                    lock (depotDownloadCounter)
                    {
                        depotDownloadCounter.sizeDownloaded += file.TotalSize;
                        Logger.InfoOverwrite("{0,6:#00.00}% {1}", (depotDownloadCounter.sizeDownloaded / (float)depotDownloadCounter.completeDownloadSize) * 100.0f, fileFinalPath);
                    }

                    lock (downloadCounter)
                    {
                        downloadCounter.completeDownloadSize -= file.TotalSize;
                    }

                    return;
                }

                var sizeOnDisk = (file.TotalSize - (ulong)neededChunks.Select(x => (long)x.UncompressedLength).Sum());
                lock (depotDownloadCounter)
                {
                    depotDownloadCounter.sizeDownloaded += sizeOnDisk;
                }

                lock (downloadCounter)
                {
                    downloadCounter.completeDownloadSize -= sizeOnDisk;
                }
            }

            var fileIsExecutable = file.Flags.HasFlag(EDepotFileFlag.Executable);
            if (fileIsExecutable && (!fileDidExist || oldManifestFile == null || !oldManifestFile.Flags.HasFlag(EDepotFileFlag.Executable)))
            {
                PlatformUtilities.SetExecutable(fileFinalPath, true);
            }
            else if (!fileIsExecutable && oldManifestFile != null && oldManifestFile.Flags.HasFlag(EDepotFileFlag.Executable))
            {
                PlatformUtilities.SetExecutable(fileFinalPath, false);
            }

            var fileStreamData = new FileStreamData
            {
                fileStream = null,
                fileLock = new SemaphoreSlim(1),
                chunksToDownload = neededChunks.Count
            };

            foreach (var chunk in neededChunks)
            {
                networkChunkQueue.Enqueue((fileStreamData, file, chunk));
            }
        }

        private static async Task DownloadSteam3AsyncDepotFileChunk(
            CancellationTokenSource cts,
            GlobalDownloadCounter downloadCounter,
            DepotFilesData depotFilesData,
            DepotManifest.FileData file,
            FileStreamData fileStreamData,
            DepotManifest.ChunkData chunk)
        {
            cts.Token.ThrowIfCancellationRequested();

            var depot = depotFilesData.depotDownloadInfo;
            var depotDownloadCounter = depotFilesData.depotCounter;

            var chunkID = Convert.ToHexString(chunk.ChunkID).ToLowerInvariant();

            var written = 0;
            var retries = 0;
            var chunkBuffer = ArrayPool<byte>.Shared.Rent((int)chunk.UncompressedLength);

            try
            {
                do
                {
                    if (retries > 5)
                    {
                        cts.Cancel();
                        break;
                    }

                    cts.Token.ThrowIfCancellationRequested();

                    Server connection = null;

                    try
                    {
                        connection = cdnPool.GetConnection();

                        string cdnToken = null;
                        if (steam3.CDNAuthTokens.TryGetValue((depot.DepotId, connection.Host), out var authTokenCallbackPromise))
                        {
                            var result = await authTokenCallbackPromise.Task;
                            cdnToken = result.Token;
                        }

                        DebugLog.WriteLine("ContentDownloader", "Downloading chunk {0} from {1} with {2}", chunkID, connection, cdnPool.ProxyServer != null ? cdnPool.ProxyServer : "no proxy");
                        written = await cdnPool.CDNClient.DownloadDepotChunkAsync(
                            depot.DepotId,
                            chunk,
                            connection,
                            chunkBuffer,
                            depot.DepotKey,
                            cdnPool.ProxyServer,
                            cdnToken).ConfigureAwait(false);

                        cdnPool.ReturnConnection(connection);

                        break;
                    }
                    catch (TaskCanceledException)
                    {
                        Logger.Error("Connection timeout downloading chunk {0}", chunkID);
                        cdnPool.ReturnBrokenConnection(connection);
                        await Task.Delay(1000 * retries);
                        retries++;
                    }
                    catch (SteamKitWebRequestException e)
                    {
                        // If the CDN returned 403, attempt to get a cdn auth if we didn't yet,
                        // if auth task already exists, make sure it didn't complete yet, so that it gets awaited above
                        if (e.StatusCode == HttpStatusCode.Forbidden &&
                            (!steam3.CDNAuthTokens.TryGetValue((depot.DepotId, connection.Host), out var authTokenCallbackPromise) || !authTokenCallbackPromise.Task.IsCompleted))
                        {
                            await steam3.RequestCDNAuthToken(depot.AppId, depot.DepotId, connection);

                            cdnPool.ReturnConnection(connection);

                            continue;
                        }

                        cdnPool.ReturnBrokenConnection(connection);

                        if (e.StatusCode == HttpStatusCode.Unauthorized || e.StatusCode == HttpStatusCode.Forbidden)
                        {
                            Logger.Error("Encountered {1} for chunk {0}. Aborting.", chunkID, (int)e.StatusCode);
                            break;
                        }

                        Logger.Error("Encountered error downloading chunk {0}: {1}", chunkID, e.StatusCode);
                        await Task.Delay(1000 * retries);
                        retries++;
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception e)
                    {
                        cdnPool.ReturnBrokenConnection(connection);
                        Logger.Error("Encountered unexpected error downloading chunk {0}: {1}", chunkID, e.Message);
                        await Task.Delay(1000 * retries);
                        retries++;
                    }
                } while (written == 0);

                if (written == 0)
                {
                    Logger.Error("Failed to find any server with chunk {0} for depot {1}. Aborting.", chunkID, depot.DepotId);
                    cts.Cancel();
                }

                // Throw the cancellation exception if requested so that this task is marked failed
                cts.Token.ThrowIfCancellationRequested();

                try
                {
                    await fileStreamData.fileLock.WaitAsync().ConfigureAwait(false);

                    if (fileStreamData.fileStream == null)
                    {
                        var fileFinalPath = Path.Combine(depot.InstallDir, file.FileName);
                        fileStreamData.fileStream = File.Open(fileFinalPath, FileMode.Open);
                    }

                    fileStreamData.fileStream.Seek((long)chunk.Offset, SeekOrigin.Begin);
                    await fileStreamData.fileStream.WriteAsync(chunkBuffer.AsMemory(0, written), cts.Token);
                }
                finally
                {
                    fileStreamData.fileLock.Release();
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(chunkBuffer);
            }

            var remainingChunks = Interlocked.Decrement(ref fileStreamData.chunksToDownload);
            if (remainingChunks == 0)
            {
                fileStreamData.fileStream?.Dispose();
                fileStreamData.fileLock.Dispose();
            }

            ulong sizeDownloaded = 0;
            lock (depotDownloadCounter)
            {
                sizeDownloaded = depotDownloadCounter.sizeDownloaded + (ulong)written;
                depotDownloadCounter.sizeDownloaded = sizeDownloaded;
                depotDownloadCounter.depotBytesCompressed += chunk.CompressedLength;
                depotDownloadCounter.depotBytesUncompressed += chunk.UncompressedLength;
            }

            lock (downloadCounter)
            {
                downloadCounter.totalBytesCompressed += chunk.CompressedLength;
                downloadCounter.totalBytesUncompressed += chunk.UncompressedLength;

                Ansi.Progress(downloadCounter.totalBytesUncompressed, downloadCounter.completeDownloadSize);

            }

            if (remainingChunks == 0)
            {
                var fileFinalPath = Path.Combine(depot.InstallDir, file.FileName);
                Logger.InfoOverwrite("{0,6:#00.00}% {1}", (sizeDownloaded / (float)depotDownloadCounter.completeDownloadSize) * 100.0f, fileFinalPath);
            }
        }

        class ChunkIdComparer : IEqualityComparer<byte[]>
        {
            public bool Equals(byte[] x, byte[] y)
            {
                if (ReferenceEquals(x, y)) return true;
                if (x == null || y == null) return false;
                return x.SequenceEqual(y);
            }

            public int GetHashCode(byte[] obj)
            {
                ArgumentNullException.ThrowIfNull(obj);

                // ChunkID is SHA-1, so we can just use the first 4 bytes
                return BitConverter.ToInt32(obj, 0);
            }
        }

        static void DumpManifestToTextFile(DepotDownloadInfo depot, DepotManifest manifest)
        {
            var txtManifest = Path.Combine(depot.InstallDir, $"manifest_{depot.DepotId}_{depot.ManifestId}.txt");
            using var sw = new StreamWriter(txtManifest);

            sw.WriteLine($"Content Manifest for Depot {depot.DepotId} ");
            sw.WriteLine();
            sw.WriteLine($"Manifest ID / date     : {depot.ManifestId} / {manifest.CreationTime} ");

            var uniqueChunks = new HashSet<byte[]>(new ChunkIdComparer());

            foreach (var file in manifest.Files)
            {
                foreach (var chunk in file.Chunks)
                {
                    uniqueChunks.Add(chunk.ChunkID);
                }
            }

            sw.WriteLine($"Total number of files  : {manifest.Files.Count} ");
            sw.WriteLine($"Total number of chunks : {uniqueChunks.Count} ");
            sw.WriteLine($"Total bytes on disk    : {manifest.TotalUncompressedSize} ");
            sw.WriteLine($"Total bytes compressed : {manifest.TotalCompressedSize} ");
            sw.WriteLine();
            sw.WriteLine();
            sw.WriteLine("          Size Chunks File SHA                                 Flags Name");

            foreach (var file in manifest.Files)
            {
                var sha1Hash = Convert.ToHexString(file.FileHash).ToLower();
                sw.WriteLine($"{file.TotalSize,14:d} {file.Chunks.Count,6:d} {sha1Hash} {(int)file.Flags,5:x} {file.FileName}");
            }
        }

        static void CreateAppBackup(uint appId, List<DepotDownloadInfo> depots, string branch)
        {
            if (depots == null || depots.Count == 0)
                return;

            if (steam3 == null || !steam3.AppInfo.TryGetValue(appId, out var appInfo) || appInfo == null)
            {
                Logger.Warning("AppInfo not available for backup.");
                return;
            }

            var buildId = Config.ForceBuildId ?? GetSteam3AppBuildNumber(appId, branch);
            var buildIdStr = buildId != 0 ? buildId.ToString() : appInfo.ChangeNumber.ToString();

            var backupDir = Path.Combine(GetManifestBaseDirectory(), appId.ToString(), buildIdStr);

            Directory.CreateDirectory(backupDir);
            SaveAppInfoAsJson(appId, backupDir, depots, branch);
            SaveLuaScript(appId, depots, backupDir);
            SaveKeyVdf(depots, backupDir);
            SaveAppTokens(appId, depots, backupDir, steam3.AppTokens);

            Logger.Info("Backup created in {0}", backupDir);
        }

        static void SaveAppInfoAsJson(uint appId, string backupDir, List<DepotDownloadInfo> depots, string branch)
        {
            if (steam3 == null || !steam3.AppInfo.TryGetValue(appId, out var appInfo) || appInfo == null)
            {
                Logger.Error("AppInfo not available for backup.");
                return;
            }

            var path = Path.Combine(backupDir, $"{appId}.json");
            if (File.Exists(path)) return;

            var rootNode = KeyValueToJson(appInfo.KeyValues);
            // Inject keys
            if (rootNode is JsonObject rootObj)
            {
                rootObj["_missing_token"] = appInfo.MissingToken;
                rootObj["_sha"] = appInfo.SHAHash != null ? Convert.ToHexString(appInfo.SHAHash).ToLowerInvariant() : null;
                rootObj["_change_number"] = appInfo.ChangeNumber;

                if (rootObj["depots"] is JsonObject || rootObj["depot"] is JsonObject)
                {
                    var depotsNode = rootObj["depots"] as JsonObject ?? rootObj["depot"] as JsonObject;
                    if (depotsNode != null)
                    {
                        foreach (var kvp in steam3.DepotKeys)
                        {
                            var depotIdStr = kvp.Key.ToString();
                            if (depotsNode[depotIdStr] is JsonObject depotNode)
                            {
                                depotNode["decryptionkey"] = Convert.ToHexString(kvp.Value).ToLowerInvariant();
                            }
                        }
                    }

                    // Patch manifest IDs
                    foreach (var depot in depots)
                    {
                        if (depot.ManifestId != INVALID_MANIFEST_ID)
                        {
                            var depotIdStr = depot.DepotId.ToString();
                            if (depotsNode[depotIdStr] is JsonObject depotNode)
                            {
                                if (depotNode["manifests"] is JsonObject manifestsNode)
                                {
                                    if (manifestsNode[branch] is JsonObject branchNode)
                                    {
                                        branchNode["gid"] = depot.ManifestId.ToString();
                                    }
                                    else
                                    {
                                        manifestsNode[branch] = new JsonObject { ["gid"] = depot.ManifestId.ToString() };
                                    }
                                }
                                else
                                {
                                    depotNode["manifests"] = new JsonObject { [branch] = new JsonObject { ["gid"] = depot.ManifestId.ToString() } };
                                }
                            }
                        }
                    }

                    // Patch build ID
                    if (Config.ForceBuildId.HasValue)
                    {
                        if (depotsNode["branches"] is JsonObject branchesNode)
                        {
                            if (branchesNode[branch] is JsonObject branchNode)
                            {
                                branchNode["buildid"] = Config.ForceBuildId.Value.ToString();
                            }
                            else
                            {
                                branchesNode[branch] = new JsonObject { ["buildid"] = Config.ForceBuildId.Value.ToString() };
                            }
                        }
                        else
                        {
                            depotsNode["branches"] = new JsonObject { [branch] = new JsonObject { ["buildid"] = Config.ForceBuildId.Value.ToString() } };
                        }
                    }
                }
            }

            if (rootNode is JsonObject rootObj2)
            {
                if (steam3.AppTokens.TryGetValue(appId, out var token))
                {
                    rootObj2["accesstoken"] = token.ToString();
                }
            }

            var jsonString = rootNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true, IndentSize = 4 });
            File.WriteAllText(path, jsonString);
        }

        static JsonNode KeyValueToJson(KeyValue kv)
        {
            if (kv.Children.Count > 0)
            {
                var obj = new JsonObject();
                foreach (var child in kv.Children)
                {
                    if (!obj.ContainsKey(child.Name))
                    {
                        obj.Add(child.Name, KeyValueToJson(child));
                    }
                }
                return obj;
            }

            if (long.TryParse(kv.Value, out var lVal))
            {
                if (lVal > int.MaxValue || lVal < int.MinValue)
                    return JsonValue.Create(kv.Value);
                else
                    return JsonValue.Create(lVal);
            }

            return JsonValue.Create(kv.Value);
        }

        static KeyValue JsonToKeyValue(string name, JsonNode node)
        {
            var kv = new KeyValue(name);

            if (node is JsonObject obj)
            {
                foreach (var property in obj)
                {
                    kv.Children.Add(JsonToKeyValue(property.Key, property.Value));
                }
            }
            else if (node is JsonValue val)
            {
                kv.Value = val.ToString();
            }

            return kv;
        }

        static SteamApps.PICSProductInfoCallback.PICSProductInfo CreatePICSProductInfo(uint id, KeyValue kv, bool missingToken, uint changeNumber, byte[] sha)
        {
            var type = typeof(SteamApps.PICSProductInfoCallback.PICSProductInfo);
            var instance = (SteamApps.PICSProductInfoCallback.PICSProductInfo)RuntimeHelpers.GetUninitializedObject(type);

            void SetProp(string name, object value)
            {
                var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (prop != null && prop.CanWrite)
                {
                    prop.SetValue(instance, value);
                }
                else
                {
                    var field = type.GetField($"<{name}>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (field != null)
                    {
                        field.SetValue(instance, value);
                    }
                    else
                    {
                        // Try to find the field by name convention (lowercase or underscore prefix) if backing field not found
                        field = type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance) ??
                                type.GetField($"_{name}", BindingFlags.NonPublic | BindingFlags.Instance);

                        if (field != null)
                        {
                            field.SetValue(instance, value);
                        }
                        else
                        {
                            Logger.Critical($"Failed to set property '{name}' on PICSProductInfo via reflection. Likely steamkit2 changed it!");
                        }
                    }
                }
            }

            SetProp("ID", id);
            SetProp("KeyValues", kv);
            SetProp("MissingToken", missingToken);
            SetProp("ChangeNumber", changeNumber);
            SetProp("SHAHash", sha);

            return instance;
        }

        static void SaveLuaScript(uint appId, List<DepotDownloadInfo> depots, string backupDir)
        {
            var path = Path.Combine(backupDir, $"{appId}.lua");
            if (File.Exists(path)) return;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"addappid({appId})");

            if (steam3 != null && steam3.AppTokens.TryGetValue(appId, out var appToken))
            {
                sb.AppendLine($"addtoken({appId},\"{appToken}\")");
            }

            foreach (var depot in depots)
            {
                if (depot.DepotKey != null)
                {
                    var keyStr = Convert.ToHexString(depot.DepotKey).ToLowerInvariant();
                    sb.AppendLine($"addappid({depot.DepotId},0,\"{keyStr}\")");
                }

                if (steam3 != null && steam3.AppTokens.TryGetValue(depot.DepotId, out var depotToken))
                {
                    sb.AppendLine($"addtoken({depot.DepotId},\"{depotToken}\")");
                }

                if (depot.ManifestId != INVALID_MANIFEST_ID)
                {
                    sb.AppendLine($"setManifestid({depot.DepotId},\"{depot.ManifestId}\")");
                }
            }

            File.WriteAllText(path, sb.ToString());
        }

        static Dictionary<uint, byte[]> LoadLuaKeys(string luaPath)
        {
            var keys = new Dictionary<uint, byte[]>();
            if (!File.Exists(luaPath)) return keys;

            try
            {
                var lines = File.ReadAllLines(luaPath);
                foreach (var line in lines)
                {
                    // Format: addappid(depotId,0,"key")
                    // Example: addappid(228983,0,"77c8e812cd79e67e2d376721253ebb07e06b3646f05671c6c9517b27be14734b")

                    var trimmed = line.Trim();
                    if (!trimmed.StartsWith("addappid")) continue;

                    var parts = trimmed.Split(new[] { '(', ')', ',' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 4 && parts[0] == "addappid")
                    {
                        if (uint.TryParse(parts[1], out var depotId))
                        {
                            // parts[2] is usually 0
                            var keyStr = parts[3].Trim('"');
                            if (!string.IsNullOrEmpty(keyStr) && keyStr.Length == 64) // Basic validation for hex key
                            {
                                keys[depotId] = Util.DecodeHexString(keyStr);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Warning: Failed to parse lua file {luaPath}: {ex.Message}");
            }

            return keys;
        }

        static void SaveKeyVdf(List<DepotDownloadInfo> depots, string backupDir)
        {
            var path = Path.Combine(backupDir, "key.vdf");
            if (File.Exists(path)) return;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("\"depots\"");
            sb.AppendLine("{");

            foreach (var depot in depots)
            {
                if (depot.DepotKey != null)
                {
                    var keyStr = Convert.ToHexString(depot.DepotKey).ToLowerInvariant();
                    sb.AppendLine($"    \"{depot.DepotId}\"");
                    sb.AppendLine("    {");
                    sb.AppendLine($"        \"DecryptionKey\" \"{keyStr}\"");
                    sb.AppendLine("    }");
                }
            }
            sb.AppendLine("}");

            File.WriteAllText(path, sb.ToString());
        }

        static async Task<uint?> IdentifyBuildFromDirectory(string dir, uint appId)
        {
            var manifestFiles = Directory.GetFiles(dir, "*.manifest");
            var localManifests = new List<(uint DepotId, ulong ManifestId)>();
            foreach (var file in manifestFiles)
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                var parts = fileName.Split('_');
                if (parts.Length >= 2 && uint.TryParse(parts[0], out var dId) && ulong.TryParse(parts[1], out var mId))
                {
                    localManifests.Add((dId, mId));
                }
            }

            var appInfoPath = Path.Combine(dir, $"{appId}.json");
            if (!File.Exists(appInfoPath)) return null;

            try
            {
                var jsonString = await File.ReadAllTextAsync(appInfoPath);
                var jsonNode = JsonNode.Parse(jsonString);
                var depotsNode = jsonNode["depots"] ?? jsonNode["depot"];
                if (depotsNode == null) return null;

                var branchesNode = depotsNode["branches"];
                if (branchesNode == null) return null;

                foreach (var child in branchesNode.AsObject())
                {
                    var branchName = child.Key;
                    var branchData = child.Value;
                    var buildIdNode = branchData["buildid"];
                    if (buildIdNode == null) continue;

                    if (!uint.TryParse(buildIdNode.ToString(), out var buildId)) continue;

                    bool branchMatches = true;
                    int matchCount = 0;

                    foreach (var local in localManifests)
                    {
                        var depotNode = depotsNode[local.DepotId.ToString()];
                        if (depotNode == null) continue;

                        var manifestsNode = depotNode["manifests"];
                        if (manifestsNode == null) continue;

                        var branchNode = manifestsNode[branchName];
                        if (branchNode == null) continue;

                        var gidNode = branchNode["gid"];
                        if (gidNode == null) continue;

                        if (ulong.TryParse(gidNode.ToString(), out var remoteManifestId))
                        {
                            if (remoteManifestId == local.ManifestId)
                            {
                                matchCount++;
                            }
                            else
                            {
                                branchMatches = false;
                                break;
                            }
                        }
                    }

                    if (branchMatches && matchCount > 0)
                    {
                        return buildId;
                    }
                }
            }
            catch { }

            return null;
        }

        static void SaveAppTokens(uint appId, List<DepotDownloadInfo> depots, string backupDir, Dictionary<uint, ulong> appTokens)
        {
            if (appTokens == null || appTokens.Count == 0)
                return;

            var path = Path.Combine(backupDir, "app_tokens.json");
            if (File.Exists(path)) return;

            var obj = new JsonObject();

            // Current app token
            if (appTokens.TryGetValue(appId, out var appToken))
            {
                obj.Add(appId.ToString(), JsonValue.Create(appToken.ToString()));
            }

            // Current app depots tokens
            foreach (var depot in depots)
            {
                if (appTokens.TryGetValue(depot.DepotId, out var depotToken))
                {
                    if (!obj.ContainsKey(depot.DepotId.ToString()))
                    {
                        obj.Add(depot.DepotId.ToString(), JsonValue.Create(depotToken.ToString()));
                    }
                }
            }

            if (obj.Count == 0) return;

            var jsonString = obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true, IndentSize = 4 });
            File.WriteAllText(path, jsonString);
        }

        // Tries to find the greatest buildID that's compatible with the system config if buildID is not present in args
        static bool IsBackupCompatible(string backupDir, string os, string arch, JsonNode depotsNode)
        {
            if (depotsNode == null) return true;

            var manifestFiles = Directory.GetFiles(backupDir, "*.manifest");
            bool hasDepots = false;
            bool hasCompatibleDepot = false;

            foreach (var file in manifestFiles)
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                var parts = fileName.Split('_');
                if (parts.Length >= 2 && uint.TryParse(parts[0], out var depotId))
                {
                    hasDepots = true;
                    var depotNode = depotsNode[depotId.ToString()];
                    if (depotNode != null)
                    {
                        var config = depotNode["config"];
                        if (config != null)
                        {
                            var oslist = config["oslist"]?.ToString();
                            if (!string.IsNullOrEmpty(oslist))
                            {
                                var oses = oslist.Split(',');
                                if (Array.IndexOf(oses, os) == -1)
                                {
                                    Logger.Verbose($"Depot {depotId} incompatible: oslist '{oslist}' does not contain '{os}'");
                                    continue;
                                }
                            }

                            var osarch = config["osarch"]?.ToString();
                            if (!string.IsNullOrEmpty(osarch))
                            {
                                if (osarch != arch)
                                {
                                    Logger.Verbose($"Depot {depotId} incompatible: osarch '{osarch}' does not match '{arch}'");
                                    continue;
                                }
                            }
                        }
                    }

                    // If we reached here, the depot is compatible (or has no restrictions)
                    Logger.Verbose($"Depot {depotId} is compatible with {os}/{arch}");
                    hasCompatibleDepot = true;
                    break;
                }
            }

            if (!hasDepots) return true;
            return hasCompatibleDepot;
        }
    }
}
