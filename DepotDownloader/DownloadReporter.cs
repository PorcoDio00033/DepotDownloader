// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DepotDownloader
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum DownloadErrorCode
    {
        None,
        AppDownloadFailed,
        NotAvailableForAccount,
        DepotNotFound,
        UnhandledException,
        DepotUnavailableForAccount,
        DepotMissingManifest,
        InvalidDepotKey,
        CreateDirectoryError,
        ManifestDownload401,
        ManifestDownload404,
        ManifestDownloadError,
        DownloadFailed,
        AllocationFailed,
        ResizeFailed
    }

    public class AppError
    {
        [JsonPropertyName("error")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DownloadErrorCode? ErrorCode { get; set; }

        [JsonPropertyName("retry")]
        public bool Retry { get; set; } = true;

        [JsonPropertyName("depots")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, Dictionary<string, DownloadErrorCode>> Depots { get; set; }
    }

    public class DownloadReport
    {
        [JsonPropertyName("last_successful_app_id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public uint? LastSuccessfulAppId { get; set; }

        [JsonPropertyName("failed_apps")]
        public Dictionary<string, AppError> FailedApps { get; set; } = new Dictionary<string, AppError>();
    }

    public static class DownloadReporter
    {
        private static ulong SteamId { get; set; }
        private static string ReportPath => Path.Combine(ContentDownloader.GetBaseInstallDirectory(), ContentDownloader.CONFIG_DIR, $"failed_downloads_{SteamId}.json");

        private static readonly object _lock = new object();
        private static DownloadReport _report = new DownloadReport();

        public static void Initialize(ulong steamId)
        {
            SteamId = steamId;
            if (File.Exists(ReportPath))
            {
                try
                {
                    var json = File.ReadAllText(ReportPath);
                    _report = JsonSerializer.Deserialize<DownloadReport>(json) ?? new DownloadReport();
                }
                catch
                {
                    _report = new DownloadReport();
                }
            }
        }

        public static uint? GetLastSuccessfulAppId()
        {
            lock (_lock)
            {
                return _report.LastSuccessfulAppId;
            }
        }

        public static void RecordAppFailure(uint appId, DownloadErrorCode errorCode)
        {
            lock (_lock)
            {
                var appIdStr = appId.ToString();
                if (!_report.FailedApps.TryGetValue(appIdStr, out var appError))
                {
                    appError = new AppError();
                    _report.FailedApps[appIdStr] = appError;
                }
                appError.ErrorCode = errorCode;
                appError.Retry = IsRetriable(errorCode);
                SaveReport();
            }
        }

        public static void RecordDepotFailure(uint appId, uint depotId, ulong manifestId, DownloadErrorCode errorCode)
        {
            lock (_lock)
            {
                var appIdStr = appId.ToString();
                if (!_report.FailedApps.TryGetValue(appIdStr, out var appError))
                {
                    appError = new AppError();
                    _report.FailedApps[appIdStr] = appError;
                }

                appError.Retry = IsRetriable(errorCode);

                if (appError.Depots == null)
                {
                    appError.Depots = new Dictionary<string, Dictionary<string, DownloadErrorCode>>();
                }

                var depotIdStr = depotId.ToString();
                if (!appError.Depots.TryGetValue(depotIdStr, out var manifests))
                {
                    manifests = new Dictionary<string, DownloadErrorCode>();
                    appError.Depots[depotIdStr] = manifests;
                }

                manifests[manifestId.ToString()] = errorCode;
                SaveReport();
            }
        }

        public static void RecordSuccessfulApp(uint appId)
        {
            lock (_lock)
            {
                _report.LastSuccessfulAppId = appId;
                _report.FailedApps.Remove(appId.ToString());
                SaveReport();
            }
        }

        private static void SaveReport()
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(ReportPath, JsonSerializer.Serialize(_report, options));
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to write download report: {ex.Message}");
            }
        }

        private static bool IsRetriable(DownloadErrorCode errorCode)
        {
            return errorCode switch
            {   // no point in retrying these errors
                DownloadErrorCode.NotAvailableForAccount => false,      // account doesn't own it
                DownloadErrorCode.DepotNotFound => false,               // app has no depots, for example when a dlc has no files because it's included in main app depot
                DownloadErrorCode.DepotUnavailableForAccount => false,  // for example a password locked branch
                DownloadErrorCode.DepotMissingManifest => false,        // not sure why, still no point in retrying if missing
                DownloadErrorCode.InvalidDepotKey => false,             // steam returned no valid key for depot
                _ => true
            };
        }

        public static IEnumerable<uint> GetAppsToRetry()
        {
            lock (_lock)
            {
                return _report.FailedApps
                    .Where(kvp => kvp.Value.Retry)
                    .Select(kvp => uint.Parse(kvp.Key))
                    .ToList();
            }
        }

        public static bool HasDepotFailure(uint appId)
        {
            lock (_lock)
            {
                if (_report.FailedApps.TryGetValue(appId.ToString(), out var appError))
                {
                    return appError.Depots != null && appError.Depots.Count > 0;
                }
                return false;
            }
        }
    }
}
