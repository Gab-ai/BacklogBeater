using System;
using System.Collections.Generic;
using System.Linq;
using Playnite.SDK;

namespace GameRecommender
{
    internal static class RecommendationDiagnostics
    {
        private static readonly string[] SuspiciousExactLabels =
        {
            "xbox",
            "pc",
            "windows 10",
            "windows",
            "microsoft store",
            "steam",
            "epic",
            "gog",
            "epic games store"
        };

        private static readonly string[] SuspiciousLabelPhrases =
        {
            "xbox enhanced",
            "xbox play anywhere",
            "xbox one x enhanced",
            "optimized for xbox",
            "deluxe edition",
            "complete edition",
            "ultimate edition",
            "enhanced edition",
            "remastered"
        };

        public static bool IsSuspiciousDisplayLabel(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            var normalized = value.Trim();
            return SuspiciousExactLabels.Any(term =>
                string.Equals(normalized, term, StringComparison.OrdinalIgnoreCase)) ||
                SuspiciousLabelPhrases.Any(term =>
                normalized.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public static bool IsUsefulPrimaryLabel(string value)
            => !string.IsNullOrWhiteSpace(value) && !IsSuspiciousDisplayLabel(value);

        public static string FirstUsefulPrimaryLabel(IEnumerable<string> values)
            => (values ?? Enumerable.Empty<string>())
                .FirstOrDefault(IsUsefulPrimaryLabel);

        public static void LogSuspiciousPrimaryTag(ILogger logger, EnrichedGame game, string primaryTag)
        {
            if (!IsSuspiciousDisplayLabel(primaryTag))
                return;

            logger?.Warn(
                "Primary tag diagnostic: suspicious primary tag '" + primaryTag + "' for " +
                GameLabel(game) + ". " + MetadataSummary(game));
        }

        public static void LogSuspiciousStartedIntent(ILogger logger, ScoredGame scored, string context)
        {
            if (scored == null)
                return;

            var label = scored.StartedIntentLabel;
            if (!IsSuspiciousDisplayLabel(label))
                return;

            logger?.Warn(
                "Started intent diagnostic: suspicious started intent label '" + label + "' for " +
                GameLabel(scored.Game) + " in " + (context ?? "unknown context") +
                ". StartedIntent=" + scored.StartedIntent +
                ", PrimaryTag='" + (scored.PrimaryTag ?? string.Empty) + "'. " +
                MetadataSummary(scored.Game));
        }

        public static void LogWeakRecommendationReasons(ILogger logger, ScoredGame scored, string context)
        {
            if (scored == null)
                return;

            var reasons = scored.Reasons ?? new List<string>();
            if (!reasons.Any(r => !string.IsNullOrWhiteSpace(r)))
            {
                logger?.Warn(
                    "Reason diagnostic: missing recommendation reasons for " +
                    GameLabel(scored.Game) + " in " + (context ?? "unknown context") + ".");
                return;
            }

            var primary = reasons.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r)) ?? string.Empty;
            if (IsGenericReason(primary))
            {
                logger?.Warn(
                    "Reason diagnostic: generic primary reason '" + primary + "' for " +
                    GameLabel(scored.Game) + " in " + (context ?? "unknown context") +
                    ". " + MetadataSummary(scored.Game));
            }
        }

        public static string MetadataSummary(EnrichedGame game)
        {
            if (game == null)
                return "No game metadata available.";

            return "Source=" + (game.SourcePlugin ?? "Unknown") +
                   ", Genres=[" + Join(game.Genres) + "]" +
                   ", Tags=[" + Join(game.Tags) + "]" +
                   ", Features=[" + Join(game.Features) + "]" +
                   ", Themes=[" + Join(game.Themes) + "]" +
                   ", Keywords=[" + Join(game.Keywords) + "]" +
                   ", AlgorithmicTags=[" + Join(game.AlgorithmicTags) + "]";
        }

        public static bool HasSuspiciousMetadata(EnrichedGame game)
        {
            if (game == null)
                return false;

            return AnySuspicious(game.Tags) ||
                   AnySuspicious(game.Features) ||
                   AnySuspicious(game.Genres) ||
                   AnySuspicious(game.Themes) ||
                   AnySuspicious(game.Keywords) ||
                   AnySuspicious(game.AlgorithmicTags);
        }

        private static bool AnySuspicious(IEnumerable<string> values)
            => (values ?? Enumerable.Empty<string>()).Any(IsSuspiciousDisplayLabel);

        private static bool IsGenericReason(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return true;

            var normalized = value.Trim().ToLowerInvariant();
            return normalized == "conceptually similar to games you've played" ||
                   normalized == "unplayed - sitting in your library" ||
                   normalized == "unplayed — sitting in your library" ||
                   normalized == "recommended" ||
                   normalized == "no recommendation reason available.";
        }

        private static string GameLabel(EnrichedGame game)
        {
            if (game == null)
                return "unknown game";

            return "'" + (game.Name ?? "Unknown game") + "' (" + game.PlayniteId + ")";
        }

        private static string Join(IEnumerable<string> values)
        {
            var list = (values ?? Enumerable.Empty<string>())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Take(12)
                .ToList();
            return list.Any() ? string.Join(", ", list) : "none";
        }
    }
}
