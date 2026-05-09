using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace GameRecommender
{
    internal class MinecraftModpackProfile
    {
        public string Launcher { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string ProfilePath { get; set; } = string.Empty;
        public string ConfigPath { get; set; } = string.Empty;
        public string ExternalId { get; set; } = string.Empty;
        public string StableId { get; set; } = string.Empty;
        public string StableIdKind { get; set; } = string.Empty;
        public string ModrinthProjectId { get; set; } = string.Empty;
        public string CurseForgeProjectId { get; set; } = string.Empty;
        public string Slug { get; set; } = string.Empty;
        public string MinecraftVersion { get; set; } = string.Empty;
        public string Loader { get; set; } = string.Empty;
        public bool IsImportCandidate { get; set; }
        public string ValidationStatus { get; set; } = string.Empty;
        public long? PlaytimeSeconds { get; set; }
        public bool HasCandidatePlaytime => PlaytimeSeconds.HasValue;
    }

    internal class MinecraftModpackScanResult
    {
        public List<MinecraftModpackProfile> Profiles { get; } = new List<MinecraftModpackProfile>();
        public List<string> Diagnostics { get; } = new List<string>();
        public int MissingDirectoryCount { get; set; }
        public int SkippedCount { get; set; }
        public int MalformedCount { get; set; }

        public string Summary()
        {
            var playtimeCount = Profiles.Count(p => p.HasCandidatePlaytime);
            var importCandidateCount = Profiles.Count(p => p.IsImportCandidate);
            return $"Minecraft modpack scan: {Profiles.Count} profile(s), {importCandidateCount} import candidate(s), {playtimeCount} with candidate playtime, {SkippedCount} skipped, {MalformedCount} malformed, {MissingDirectoryCount} missing path(s).";
        }
    }

    internal class MinecraftModpackScanner
    {
        private sealed class LauncherRoot
        {
            public string Launcher { get; set; } = string.Empty;
            public string Path { get; set; } = string.Empty;
        }

        private static readonly string[] ConfigFileNames =
        {
            "instance.cfg",
            "mmc-pack.json",
            "profile.json",
            "metadata.json",
            "manifest.json",
            "minecraftinstance.json",
            "modrinth.index.json"
        };

        public MinecraftModpackScanResult Scan(string customPaths)
        {
            var result = new MinecraftModpackScanResult();
            var roots = GetDefaultRoots().Concat(GetCustomRoots(customPaths)).ToList();
            var seenProfiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var root in roots)
                ScanRoot(root.Launcher, root.Path, result, seenProfiles);

            result.Profiles.Sort((left, right) =>
            {
                var launcherCompare = string.Compare(left.Launcher, right.Launcher, StringComparison.OrdinalIgnoreCase);
                return launcherCompare != 0
                    ? launcherCompare
                    : string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
            });
            return result;
        }

        private static IEnumerable<LauncherRoot> GetDefaultRoots()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            if (!string.IsNullOrWhiteSpace(appData))
            {
                yield return new LauncherRoot { Launcher = "Prism", Path = Path.Combine(appData, "PrismLauncher", "instances") };
                yield return new LauncherRoot { Launcher = "Modrinth", Path = Path.Combine(appData, "com.modrinth.theseus", "profiles") };
            }

            if (!string.IsNullOrWhiteSpace(userProfile))
                yield return new LauncherRoot { Launcher = "CurseForge", Path = Path.Combine(userProfile, "curseforge", "minecraft", "Instances") };
        }

        private static IEnumerable<LauncherRoot> GetCustomRoots(string customPaths)
        {
            if (string.IsNullOrWhiteSpace(customPaths))
                yield break;

            foreach (var rawPath in customPaths.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var path = Environment.ExpandEnvironmentVariables(rawPath.Trim().Trim('"'));
                if (!string.IsNullOrWhiteSpace(path))
                    yield return new LauncherRoot { Launcher = "Custom", Path = path };
            }
        }

        private static void ScanRoot(string launcher, string rootPath, MinecraftModpackScanResult result, HashSet<string> seenProfiles)
        {
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
            {
                result.MissingDirectoryCount++;
                result.Diagnostics.Add($"{launcher}: missing {rootPath}");
                return;
            }

            var candidateDirectories = new List<string> { rootPath };
            try
            {
                candidateDirectories.AddRange(Directory.EnumerateDirectories(rootPath));
            }
            catch (Exception ex)
            {
                result.MalformedCount++;
                result.Diagnostics.Add($"{launcher}: could not enumerate {rootPath}: {ex.Message}");
                return;
            }

            foreach (var directory in candidateDirectories)
            {
                var normalizedPath = Path.GetFullPath(directory);
                if (!seenProfiles.Add(normalizedPath))
                    continue;

                var configPath = FindConfigPath(directory);
                if (configPath == null)
                {
                    if (!string.Equals(directory, rootPath, StringComparison.OrdinalIgnoreCase))
                        result.SkippedCount++;
                    continue;
                }

                var profile = ReadProfile(launcher, directory, configPath, result);
                if (profile != null)
                    result.Profiles.Add(profile);
            }
        }

        private static string FindConfigPath(string directory)
        {
            foreach (var fileName in ConfigFileNames)
            {
                var path = Path.Combine(directory, fileName);
                if (File.Exists(path))
                    return path;
            }

            return null;
        }

        private static MinecraftModpackProfile ReadProfile(string launcher, string directory, string configPath, MinecraftModpackScanResult result)
        {
            string text;
            try
            {
                text = File.ReadAllText(configPath);
            }
            catch (Exception ex)
            {
                result.MalformedCount++;
                result.Diagnostics.Add($"{launcher}: could not read {configPath}: {ex.Message}");
                return null;
            }

            var name = ExtractName(text);
            if (string.IsNullOrWhiteSpace(name))
                name = Path.GetFileName(directory);
            if (string.IsNullOrWhiteSpace(name))
            {
                result.MalformedCount++;
                result.Diagnostics.Add($"{launcher}: profile name unavailable for {directory}");
                return null;
            }

            var modrinthProjectId = ExtractFirstValue(text, "project_id", "projectId", "projectID", "modrinthProjectId");
            var curseForgeProjectId = ExtractFirstValue(text, "projectID", "projectId", "addonID", "addonId", "curseForgeProjectId", "curseforgeProjectId");
            var slug = ExtractFirstValue(text, "slug", "project_slug", "projectSlug");
            var minecraftVersion = ExtractMinecraftVersion(text);
            var loader = ExtractLoader(text);
            var stableId = BuildStableId(launcher, directory, modrinthProjectId, curseForgeProjectId, slug, out var stableIdKind);

            var profile = new MinecraftModpackProfile
            {
                Launcher = launcher,
                Name = name.Trim(),
                ProfilePath = directory,
                ConfigPath = configPath,
                ExternalId = stableId,
                StableId = stableId,
                StableIdKind = stableIdKind,
                ModrinthProjectId = modrinthProjectId,
                CurseForgeProjectId = curseForgeProjectId,
                Slug = slug,
                MinecraftVersion = minecraftVersion,
                Loader = loader,
                IsImportCandidate = IsImportCandidate(launcher, directory, stableIdKind),
                ValidationStatus = BuildValidationStatus(stableIdKind, minecraftVersion, loader),
                PlaytimeSeconds = ExtractPlaytimeSeconds(text)
            };

            return profile;
        }

        private static string ExtractName(string text)
        {
            var cfgMatch = Regex.Match(text, @"(?im)^\s*name\s*=\s*(.+?)\s*$");
            if (cfgMatch.Success)
                return cfgMatch.Groups[1].Value.Trim();

            foreach (var key in new[] { "name", "displayName", "title", "projectName" })
            {
                var jsonMatch = Regex.Match(text, $"\"{Regex.Escape(key)}\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
                if (jsonMatch.Success)
                    return jsonMatch.Groups[1].Value.Trim();
            }

            return string.Empty;
        }

        private static string ExtractFirstValue(string text, params string[] keys)
        {
            foreach (var key in keys)
            {
                var quoted = Regex.Match(text, $"\"{Regex.Escape(key)}\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
                if (quoted.Success)
                    return quoted.Groups[1].Value.Trim();

                var number = Regex.Match(text, $"\"{Regex.Escape(key)}\"\\s*:\\s*(\\d+)", RegexOptions.IgnoreCase);
                if (number.Success)
                    return number.Groups[1].Value.Trim();

                var cfg = Regex.Match(text, $"(?im)^\\s*{Regex.Escape(key)}\\s*=\\s*(.+?)\\s*$", RegexOptions.IgnoreCase);
                if (cfg.Success)
                    return cfg.Groups[1].Value.Trim();
            }

            return string.Empty;
        }

        private static string ExtractMinecraftVersion(string text)
        {
            var version = ExtractFirstValue(text, "minecraftVersion", "gameVersion", "mcVersion");
            if (!string.IsNullOrWhiteSpace(version))
                return version;

            var componentMatch = Regex.Match(text, "\"cachedName\"\\s*:\\s*\"Minecraft\"[\\s\\S]{0,300}?\"version\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
            return componentMatch.Success ? componentMatch.Groups[1].Value.Trim() : string.Empty;
        }

        private static string ExtractLoader(string text)
        {
            var loader = ExtractFirstValue(text, "loader", "modLoader", "modloader", "loaderType");
            if (!string.IsNullOrWhiteSpace(loader))
                return NormalizeLoader(loader);

            foreach (var candidate in new[] { "fabric", "forge", "neoforge", "quilt" })
            {
                if (Regex.IsMatch(text, $"\"(?:uid|name|id|modLoader)\"\\s*:\\s*\"[^\"]*{candidate}[^\"]*\"", RegexOptions.IgnoreCase))
                    return NormalizeLoader(candidate);
            }

            return string.Empty;
        }

        private static string NormalizeLoader(string loader)
        {
            if (string.IsNullOrWhiteSpace(loader))
                return string.Empty;

            var value = loader.Trim();
            if (value.IndexOf("neoforge", StringComparison.OrdinalIgnoreCase) >= 0)
                return "NeoForge";
            if (value.IndexOf("forge", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Forge";
            if (value.IndexOf("fabric", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Fabric";
            if (value.IndexOf("quilt", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Quilt";
            return value;
        }

        private static string BuildStableId(string launcher, string directory, string modrinthProjectId, string curseForgeProjectId, string slug, out string stableIdKind)
        {
            if (!string.IsNullOrWhiteSpace(modrinthProjectId))
            {
                stableIdKind = "ModrinthProjectId";
                return "modrinth:" + modrinthProjectId;
            }

            if (string.Equals(launcher, "CurseForge", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(curseForgeProjectId))
            {
                stableIdKind = "CurseForgeProjectId";
                return "curseforge:" + curseForgeProjectId;
            }

            if (!string.IsNullOrWhiteSpace(slug))
            {
                stableIdKind = "Slug";
                return launcher.ToLowerInvariant() + ":slug:" + slug;
            }

            stableIdKind = "ProfilePath";
            return launcher.ToLowerInvariant() + ":path:" + NormalizeExternalId(directory);
        }

        private static bool IsImportCandidate(string launcher, string directory, string stableIdKind)
        {
            if (string.IsNullOrWhiteSpace(launcher) || string.IsNullOrWhiteSpace(directory))
                return false;

            return string.Equals(stableIdKind, "ModrinthProjectId", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(stableIdKind, "CurseForgeProjectId", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(stableIdKind, "Slug", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(stableIdKind, "ProfilePath", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildValidationStatus(string stableIdKind, string minecraftVersion, string loader)
        {
            var parts = new List<string> { "identity=" + stableIdKind };
            if (!string.IsNullOrWhiteSpace(minecraftVersion))
                parts.Add("mc=" + minecraftVersion);
            if (!string.IsNullOrWhiteSpace(loader))
                parts.Add("loader=" + loader);
            return string.Join(", ", parts);
        }

        private static long? ExtractPlaytimeSeconds(string text)
        {
            foreach (var key in new[] { "playtimeSeconds", "playTimeSeconds", "secondsPlayed", "totalSecondsPlayed", "totalPlaytimeSeconds" })
            {
                var value = FindLongValue(text, key);
                if (value.HasValue)
                    return value.Value;
            }

            foreach (var key in new[] { "playtime", "playTime", "totalPlaytime", "totalTimePlayed", "timePlayed" })
            {
                var value = FindLongValue(text, key);
                if (value.HasValue)
                    return value.Value;
            }

            return null;
        }

        private static long? FindLongValue(string text, string key)
        {
            var jsonMatch = Regex.Match(text, $"\"{Regex.Escape(key)}\"\\s*:\\s*(\\d+)", RegexOptions.IgnoreCase);
            if (jsonMatch.Success && long.TryParse(jsonMatch.Groups[1].Value, out var jsonValue))
                return jsonValue;

            var cfgMatch = Regex.Match(text, $"(?im)^\\s*{Regex.Escape(key)}\\s*=\\s*(\\d+)\\s*$", RegexOptions.IgnoreCase);
            if (cfgMatch.Success && long.TryParse(cfgMatch.Groups[1].Value, out var cfgValue))
                return cfgValue;

            return null;
        }

        private static string NormalizeExternalId(string path)
        {
            return Regex.Replace(path.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        }
    }
}
