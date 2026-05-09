using System;
using System.Collections.Generic;
using System.Linq;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace GameRecommender
{
    internal enum ModpackLibrarySyncOperationKind
    {
        Create,
        Update,
        Skip
    }

    internal class ModpackLibrarySyncEntry
    {
        public string Name { get; set; } = string.Empty;
        public string Provider { get; set; } = string.Empty;
        public string SourceLabel { get; set; } = string.Empty;
        public string ExternalId { get; set; } = string.Empty;
        public string StableIdKind { get; set; } = string.Empty;
        public string ProfilePath { get; set; } = string.Empty;
        public string ConfigPath { get; set; } = string.Empty;
        public string MinecraftVersion { get; set; } = string.Empty;
        public string Loader { get; set; } = string.Empty;
        public long? PlaytimeSeconds { get; set; }
    }

    internal class ModpackLibrarySyncOperation
    {
        public ModpackLibrarySyncOperationKind Kind { get; set; }
        public ModpackLibrarySyncEntry Entry { get; set; }
        public Guid ExistingGameId { get; set; }
        public string Reason { get; set; } = string.Empty;
    }

    internal class ModpackLibrarySyncPlan
    {
        public List<ModpackLibrarySyncOperation> Operations { get; } = new List<ModpackLibrarySyncOperation>();
        public List<string> Diagnostics { get; } = new List<string>();
        public Guid MinecraftGameId { get; set; }
        public string MinecraftGameName { get; set; } = string.Empty;
        public long ExistingMinecraftPlaytimeSeconds { get; set; }
        public long PreviousAppliedModpackSeconds { get; set; }
        public long DetectedModpackSeconds { get; set; }
        public long DeltaSeconds => Math.Max(0, DetectedModpackSeconds - PreviousAppliedModpackSeconds);
        public int DetectedProfiles { get; set; }
        public int ProfilesWithPlaytime { get; set; }
        public bool HasWrites => MinecraftGameId != Guid.Empty && DeltaSeconds > 0;

        public string Summary()
        {
            var target = string.IsNullOrWhiteSpace(MinecraftGameName)
                ? "Minecraft: Java Edition"
                : MinecraftGameName;
            return $"Minecraft playtime preview: {ProfilesWithPlaytime}/{DetectedProfiles} profile(s) with playtime, " +
                   $"{RecommendationEngine.FormatTime(DetectedModpackSeconds)} detected, " +
                   $"{RecommendationEngine.FormatTime(DeltaSeconds)} new time to add to {target}.";
        }
    }

    internal class ModpackLibrarySyncApplyResult
    {
        public int Created { get; set; }
        public int Updated { get; set; }
        public int Skipped { get; set; }
        public long AddedSeconds { get; set; }
        public string TargetGameName { get; set; } = string.Empty;
        public List<string> Diagnostics { get; } = new List<string>();

        public string Summary()
        {
            if (AddedSeconds <= 0)
                return "Minecraft playtime sync applied: no new modpack time to add.";

            return $"Minecraft playtime sync applied: added {RecommendationEngine.FormatTime(AddedSeconds)} to {TargetGameName}.";
        }
    }

    internal class MinecraftModpackLibrarySync
    {
        private const string AggregationProvider = "MinecraftModpackTime";
        private const string AggregationExternalId = "minecraft-java-modpack-time";
        private readonly IPlayniteAPI api;

        public MinecraftModpackLibrarySync(IPlayniteAPI api)
        {
            this.api = api;
        }

        public ModpackLibrarySyncPlan Preview(MinecraftModpackScanResult scanResult, IEnumerable<LibraryIntegrationRecord> records)
        {
            var plan = new ModpackLibrarySyncPlan();
            var profiles = (scanResult?.Profiles ?? new List<MinecraftModpackProfile>())
                .Where(p => p != null)
                .ToList();
            plan.DetectedProfiles = profiles.Count;

            var playableProfiles = profiles
                .Where(p => p.PlaytimeSeconds.HasValue && p.PlaytimeSeconds.Value > 0)
                .ToList();
            plan.ProfilesWithPlaytime = playableProfiles.Count;
            plan.DetectedModpackSeconds = playableProfiles.Sum(p => Math.Max(0, p.PlaytimeSeconds.Value));

            var minecraft = FindMinecraftJavaEdition();
            if (minecraft == null)
            {
                plan.Diagnostics.Add("Minecraft: Java Edition was not found in the Playnite library; no playtime will be changed.");
            }
            else
            {
                plan.MinecraftGameId = minecraft.Id;
                plan.MinecraftGameName = minecraft.Name ?? "Minecraft: Java Edition";
                plan.ExistingMinecraftPlaytimeSeconds = (long)minecraft.Playtime;
            }

            var previous = FindAggregationRecord(records);
            plan.PreviousAppliedModpackSeconds = previous?.LastKnownPlaytimeSeconds ?? 0;

            foreach (var profile in playableProfiles)
            {
                plan.Operations.Add(new ModpackLibrarySyncOperation
                {
                    Kind = plan.HasWrites ? ModpackLibrarySyncOperationKind.Update : ModpackLibrarySyncOperationKind.Skip,
                    ExistingGameId = plan.MinecraftGameId,
                    Entry = ToEntry(profile),
                    Reason = plan.HasWrites ? "Candidate playtime will be added to Minecraft: Java Edition." : "No new modpack time to add."
                });
            }

            foreach (var diagnostic in scanResult?.Diagnostics ?? Enumerable.Empty<string>())
                plan.Diagnostics.Add(diagnostic);

            return plan;
        }

        public ModpackLibrarySyncApplyResult Apply(ModpackLibrarySyncPlan plan, IList<LibraryIntegrationRecord> records)
        {
            var result = new ModpackLibrarySyncApplyResult();
            if (plan == null)
                return result;
            if (records == null)
                throw new ArgumentNullException(nameof(records));

            if (plan.MinecraftGameId == Guid.Empty || plan.DeltaSeconds <= 0)
            {
                result.Skipped = plan?.Operations?.Count ?? 0;
                result.Diagnostics.AddRange(plan.Diagnostics);
                return result;
            }

            var minecraft = api.Database.Games.FirstOrDefault(g => g.Id == plan.MinecraftGameId);
            if (minecraft == null)
            {
                result.Skipped = plan.Operations.Count;
                result.Diagnostics.Add("Minecraft: Java Edition was not found at apply time.");
                return result;
            }

            minecraft.Playtime += (ulong)plan.DeltaSeconds;
            api.Database.Games.Update(minecraft);

            UpsertAggregationRecord(records, minecraft, plan);
            result.Updated = 1;
            result.AddedSeconds = plan.DeltaSeconds;
            result.TargetGameName = minecraft.Name ?? "Minecraft: Java Edition";
            return result;
        }

        private ModpackLibrarySyncEntry ToEntry(MinecraftModpackProfile profile)
        {
            return new ModpackLibrarySyncEntry
            {
                Name = profile.Name ?? string.Empty,
                Provider = NormalizeProvider(profile.Launcher),
                SourceLabel = SourceLabelFor(profile.Launcher),
                ExternalId = profile.StableId ?? profile.ExternalId ?? string.Empty,
                StableIdKind = profile.StableIdKind ?? string.Empty,
                ProfilePath = profile.ProfilePath ?? string.Empty,
                ConfigPath = profile.ConfigPath ?? string.Empty,
                MinecraftVersion = profile.MinecraftVersion ?? string.Empty,
                Loader = profile.Loader ?? string.Empty,
                PlaytimeSeconds = profile.PlaytimeSeconds
            };
        }

        private Game FindMinecraftJavaEdition()
        {
            return api.Database.Games
                .Where(g => g != null && !g.Hidden)
                .OrderBy(MinecraftJavaRank)
                .ThenByDescending(g => g.Playtime)
                .FirstOrDefault(g => MinecraftJavaRank(g) < int.MaxValue);
        }

        private static int MinecraftJavaRank(Game game)
        {
            var normalized = NormalizeMatchText(game?.Name);
            if (normalized == "minecraftjavaedition")
                return 0;
            if (normalized == "minecraft")
                return 1;
            if (normalized == "minecraftlauncher")
                return 2;
            if (normalized.Contains("minecraft") && normalized.Contains("java"))
                return 3;
            return int.MaxValue;
        }

        private static LibraryIntegrationRecord FindAggregationRecord(IEnumerable<LibraryIntegrationRecord> records)
        {
            return (records ?? Enumerable.Empty<LibraryIntegrationRecord>())
                .FirstOrDefault(r => string.Equals(r.Provider, AggregationProvider, StringComparison.OrdinalIgnoreCase) &&
                                     string.Equals(r.ExternalId, AggregationExternalId, StringComparison.OrdinalIgnoreCase));
        }

        private static void UpsertAggregationRecord(IList<LibraryIntegrationRecord> records, Game minecraft, ModpackLibrarySyncPlan plan)
        {
            var record = FindAggregationRecord(records);
            if (record == null)
            {
                record = new LibraryIntegrationRecord();
                records.Add(record);
            }

            record.Provider = AggregationProvider;
            record.ExternalId = AggregationExternalId;
            record.PlayniteId = minecraft.Id;
            record.DisplayName = minecraft.Name ?? "Minecraft: Java Edition";
            record.SourceLabel = minecraft.Source?.Name ?? string.Empty;
            record.LastKnownPlaytimeSeconds = plan.DetectedModpackSeconds;
            record.PlaytimeConfidence = "launcher-candidate-total";
            record.SyncStatus = "Applied to Minecraft Java playtime";
            record.LastSyncedAt = DateTime.UtcNow;
        }

        private static string NormalizeProvider(string launcher)
        {
            if (string.Equals(launcher, "Modrinth", StringComparison.OrdinalIgnoreCase))
                return "Modrinth";
            if (string.Equals(launcher, "CurseForge", StringComparison.OrdinalIgnoreCase))
                return "CurseForge";
            if (string.Equals(launcher, "Prism", StringComparison.OrdinalIgnoreCase))
                return "Prism";
            return "MinecraftModpack";
        }

        private static string SourceLabelFor(string launcher)
        {
            if (string.Equals(launcher, "Modrinth", StringComparison.OrdinalIgnoreCase))
                return "Modrinth";
            if (string.Equals(launcher, "CurseForge", StringComparison.OrdinalIgnoreCase))
                return "CurseForge";
            if (string.Equals(launcher, "Prism", StringComparison.OrdinalIgnoreCase))
                return "Prism";
            return "Minecraft Modpack";
        }

        private static string NormalizeMatchText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;
            return new string(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        }
    }
}
