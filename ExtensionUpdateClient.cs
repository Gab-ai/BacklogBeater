using System;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Playnite.SDK.Data;

namespace GameRecommender
{
    internal class ExtensionUpdateManifest
    {
        public string Version { get; set; } = string.Empty;
        public string ReleaseUrl { get; set; } = string.Empty;
        public string DownloadUrl { get; set; } = string.Empty;
        public string Notes { get; set; } = string.Empty;
    }

    internal class ExtensionUpdateResult
    {
        public string CurrentVersion { get; set; } = string.Empty;
        public string LatestVersion { get; set; } = string.Empty;
        public string ReleaseUrl { get; set; } = string.Empty;
        public string DownloadUrl { get; set; } = string.Empty;
        public string Notes { get; set; } = string.Empty;
        public bool IsUpdateAvailable { get; set; }
        public string StatusText { get; set; } = string.Empty;
    }

    internal class ExtensionUpdateClient
    {
        private const string UpdateManifestUrl = "https://raw.githubusercontent.com/Gab-ai/BacklogBeater/main/update.json";
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

        public async Task<ExtensionUpdateResult> CheckAsync()
        {
            var currentVersion = ReadCurrentVersion();
            try
            {
                using (var client = new HttpClient { Timeout = Timeout })
                {
                    var json = await client.GetStringAsync(UpdateManifestUrl);
                    var manifest = Serialization.FromJson<ExtensionUpdateManifest>(json);
                    if (manifest == null || string.IsNullOrWhiteSpace(manifest.Version))
                    {
                        return Status(currentVersion, "Update manifest did not include a version.");
                    }

                    var result = new ExtensionUpdateResult
                    {
                        CurrentVersion = currentVersion,
                        LatestVersion = manifest.Version.Trim(),
                        ReleaseUrl = (manifest.ReleaseUrl ?? string.Empty).Trim(),
                        DownloadUrl = (manifest.DownloadUrl ?? string.Empty).Trim(),
                        Notes = (manifest.Notes ?? string.Empty).Trim()
                    };
                    result.IsUpdateAvailable = IsNewerVersion(result.LatestVersion, currentVersion);
                    result.StatusText = result.IsUpdateAvailable
                        ? $"Update available: {currentVersion} -> {result.LatestVersion}"
                        : $"Backlog Beater is up to date ({currentVersion}).";
                    if (!string.IsNullOrWhiteSpace(result.Notes))
                        result.StatusText += " " + result.Notes;
                    return result;
                }
            }
            catch (Exception ex)
            {
                return Status(currentVersion, "Update check failed: " + ex.Message);
            }
        }

        public string ReadCurrentVersion()
        {
            foreach (var path in CandidateExtensionYamlPaths())
            {
                try
                {
                    if (!File.Exists(path))
                        continue;
                    var text = File.ReadAllText(path);
                    var match = Regex.Match(text, @"(?im)^\s*Version\s*:\s*(.+?)\s*$");
                    if (match.Success)
                        return match.Groups[1].Value.Trim();
                }
                catch
                {
                    // Keep looking in fallback locations.
                }
            }

            return "unknown";
        }

        private static ExtensionUpdateResult Status(string currentVersion, string status)
            => new ExtensionUpdateResult
            {
                CurrentVersion = currentVersion,
                LatestVersion = string.Empty,
                StatusText = status
            };

        private static bool IsNewerVersion(string latest, string current)
        {
            if (string.IsNullOrWhiteSpace(latest) || string.IsNullOrWhiteSpace(current) ||
                string.Equals(current, "unknown", StringComparison.OrdinalIgnoreCase))
                return false;

            if (Version.TryParse(NormalizeVersion(latest), out var latestVersion) &&
                Version.TryParse(NormalizeVersion(current), out var currentVersion))
            {
                return latestVersion > currentVersion;
            }

            return string.Compare(latest.Trim(), current.Trim(), StringComparison.OrdinalIgnoreCase) > 0;
        }

        private static string NormalizeVersion(string value)
        {
            var match = Regex.Match(value ?? string.Empty, @"\d+(?:\.\d+){0,3}");
            return match.Success ? match.Value : value;
        }

        private static string[] CandidateExtensionYamlPaths()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory ?? string.Empty;
            return new[]
            {
                Path.Combine(baseDir, "extension.yaml"),
                Path.Combine(Directory.GetCurrentDirectory(), "extension.yaml"),
                Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "extension.yaml")
            };
        }
    }
}
