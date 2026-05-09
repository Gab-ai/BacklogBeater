using System;
using System.Collections.Generic;
using System.Linq;
using Playnite.SDK;

namespace GameRecommender
{
    internal class MinecraftPlaytimeAggregator
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly string[] MinecraftNameCandidates =
        {
            "minecraft",
            "minecraftjavaedition",
            "minecraftlauncher"
        };

        public List<EnrichedGame> Apply(IEnumerable<EnrichedGame> games)
        {
            var input = (games ?? Enumerable.Empty<EnrichedGame>())
                .Where(g => g != null)
                .ToList();

            var modpacks = input
                .Where(IsMinecraftModpack)
                .ToList();

            if (!modpacks.Any())
                return input;

            var playedModpacks = modpacks
                .Where(g => g.PlaytimeSeconds > 0)
                .ToList();
            var modpackSeconds = playedModpacks.Sum(g => Math.Max(0, g.PlaytimeSeconds));
            var regularGames = input
                .Where(g => !IsMinecraftModpack(g))
                .ToList();

            if (modpackSeconds <= 0)
            {
                logger.Info($"Minecraft playtime aggregation: {modpacks.Count} modpack(s) detected, none with positive playtime.");
                return regularGames;
            }

            var minecraft = SelectBaseMinecraftEntry(regularGames);
            if (minecraft == null)
            {
                logger.Info("Minecraft playtime aggregation: Minecraft: Java Edition was not found; dropping separate modpack entries from recommendations.");
                return regularGames
                    .OrderBy(g => g.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            var beforeSeconds = minecraft.PlaytimeSeconds;
            minecraft.PlaytimeSeconds += modpackSeconds;
            minecraft.LastPlayed = LatestDate(minecraft.LastPlayed, playedModpacks.Select(m => m.LastPlayed));
            AddUnique(minecraft.Tags, "Minecraft");
            AddUnique(minecraft.Tags, "Sandbox");
            AddUnique(minecraft.Tags, "Survival");
            AddUnique(minecraft.Tags, "Crafting");
            AddUnique(minecraft.Tags, "Open World");
            AddUnique(minecraft.AlgorithmicTags, "Minecraft modpack playtime");
            RecommendationHeuristics.ApplyAlgorithmicTags(minecraft);

            logger.Info(
                $"Minecraft playtime aggregation: added {RecommendationEngine.FormatTime(modpackSeconds)} from {playedModpacks.Count} modpack(s) into {minecraft.Name}. " +
                $"Before={RecommendationEngine.FormatTime(beforeSeconds)}, after={RecommendationEngine.FormatTime(minecraft.PlaytimeSeconds)}.");

            foreach (var modpack in playedModpacks.OrderByDescending(g => g.PlaytimeSeconds).Take(20))
            {
                logger.Info(
                    $"Minecraft playtime aggregation: counted {modpack.Name} ({RecommendationEngine.FormatTime(modpack.PlaytimeSeconds)}, source={modpack.SourcePlugin ?? "Unknown"}).");
            }

            var skippedCount = modpacks.Count - playedModpacks.Count;
            if (skippedCount > 0)
                logger.Info($"Minecraft playtime aggregation: skipped {skippedCount} modpack(s) with no positive playtime.");

            return regularGames
                .OrderBy(g => g.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool IsMinecraftModpack(EnrichedGame game)
        {
            if (game == null || !game.IsModpack)
                return false;

            if (string.Equals(game.BaseGameName, "Minecraft", StringComparison.OrdinalIgnoreCase))
                return true;

            return HasAny(game.Tags, "Minecraft", "Base:Minecraft") ||
                   HasAny(game.AlgorithmicTags, "Minecraft");
        }

        private static bool IsBaseMinecraftEntry(EnrichedGame game)
        {
            if (game == null || game.IsModpack)
                return false;

            var normalizedName = Normalize(game.Name);
            if (MinecraftNameCandidates.Contains(normalizedName, StringComparer.OrdinalIgnoreCase))
                return true;

            if (normalizedName.Contains("minecraft") &&
                (HasAny(game.Tags, "Minecraft") ||
                 HasAny(game.SourcePlugin, "Minecraft", "Xbox", "Microsoft", "Prism", "Modrinth", "CurseForge")))
                return true;

            return false;
        }

        private static EnrichedGame SelectBaseMinecraftEntry(IEnumerable<EnrichedGame> games)
        {
            return (games ?? Enumerable.Empty<EnrichedGame>())
                .Where(IsBaseMinecraftEntry)
                .OrderBy(BaseMinecraftRank)
                .ThenByDescending(g => g.PlaytimeSeconds)
                .ThenBy(g => g.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        private static int BaseMinecraftRank(EnrichedGame game)
        {
            var normalizedName = Normalize(game?.Name);
            if (string.Equals(normalizedName, "minecraftjavaedition", StringComparison.OrdinalIgnoreCase))
                return 0;
            if (string.Equals(normalizedName, "minecraft", StringComparison.OrdinalIgnoreCase))
                return 1;
            if (string.Equals(normalizedName, "minecraftlauncher", StringComparison.OrdinalIgnoreCase))
                return 2;
            if (normalizedName.Contains("javaedition"))
                return 3;
            if (HasAny(game?.SourcePlugin, "Minecraft", "Microsoft", "Xbox"))
                return 4;
            return 5;
        }

        private static DateTime? LatestDate(DateTime? current, IEnumerable<DateTime?> candidates)
        {
            var latest = current;
            foreach (var candidate in candidates ?? Enumerable.Empty<DateTime?>())
            {
                if (candidate.HasValue && (!latest.HasValue || candidate.Value > latest.Value))
                    latest = candidate;
            }
            return latest;
        }

        private static void AddUnique(ICollection<string> values, string value)
        {
            if (values == null || string.IsNullOrWhiteSpace(value))
                return;
            if (!values.Contains(value, StringComparer.OrdinalIgnoreCase))
                values.Add(value);
        }

        private static bool HasAny(IEnumerable<string> values, params string[] needles)
        {
            return (values ?? Enumerable.Empty<string>())
                .Any(value => HasAny(value, needles));
        }

        private static bool HasAny(string value, params string[] needles)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            return needles.Any(needle => value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;
            return new string(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        }
    }
}
