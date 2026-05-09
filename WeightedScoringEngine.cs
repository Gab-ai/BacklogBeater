using System;
using System.Collections.Generic;
using System.Linq;

namespace GameRecommender
{
    /// <summary>
    /// Engine 1: Weighted genre/tag/feature/theme scorer.
    /// Builds a TasteProfile from played games and scores candidates against it.
    /// Same algorithm as before, now operating on EnrichedGame records.
    /// </summary>
    public class WeightedScoringEngine
    {
        private const long DeepPlaySeconds = 18000;  // 5h

        // Genres too generic to be meaningful taste signals
        private static readonly HashSet<string> NoiseGenres = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Adventure", "Indie", "Casual", "Free to Play", "Early Access"
        };

        public TasteProfile BuildTasteProfile(IEnumerable<EnrichedGame> games)
        {
            var profile = new TasteProfile();
            var played = games.Where(g => g.PlaytimeSeconds >= DeepPlaySeconds).ToList();
            if (!played.Any()) return profile;

            long total = played.Sum(g => g.PlaytimeSeconds);
            profile.TotalPlaytimeSeconds = total;
            profile.TopPlayedNames = played
                .OrderByDescending(g => g.PlaytimeSeconds)
                .Take(10)
                .Select(g => $"{g.Name} ({RecommendationEngine.FormatTime(g.PlaytimeSeconds)})")
                .ToList();

            foreach (var game in played)
            {
                double w = total > 0
                    ? Math.Log(game.PlaytimeSeconds + 1) / Math.Log(total + 1)
                    : 1.0;
                w = Math.Max(w, 0.01);

                AddWeights(profile.GenreWeights, game.Genres.Where(g => !IsGenericSignal(g)), w);
                AddWeightedSignals(profile.TagWeights, game.Tags, w * 1.20);
                AddWeightedSignals(profile.TagWeights, game.Keywords, w * 0.95);
                AddWeightedSignals(profile.TagWeights, game.AlgorithmicTags, w * 1.15);
                AddWeightedSignals(profile.FeatureWeights, game.Features, w * 0.90);
                AddWeightedSignals(profile.ThemeWeights, game.Themes, w * 0.95);

                // Also fold in Steam recommended tags with half weight
                AddWeightedSignals(profile.TagWeights, game.SteamRecommendedTags, w * 0.45);
            }

            // Build dominant genres (>= 10% of total weight)
            double totalGenreWeight = profile.GenreWeights.Values.Sum();
            foreach (var kvp in profile.GenreWeights)
                if (kvp.Value / (totalGenreWeight + 0.0001) >= 0.10)
                    profile.DominantGenres.Add(kvp.Key);

            return profile;
        }

        public EngineScores Score(IEnumerable<EnrichedGame> candidates, TasteProfile profile)
        {
            var result = new EngineScores { EngineName = "Weighted" };

            foreach (var game in candidates)
            {
                double score = 0;

                score += WeightedMatch(profile.GenreWeights, game.Genres.Where(g => !IsGenericSignal(g)), 40);
                score += WeightedSignalMatch(profile.TagWeights, game.Tags, 30);
                score += WeightedSignalMatch(profile.TagWeights, game.Keywords, 24);
                score += WeightedSignalMatch(profile.TagWeights, game.AlgorithmicTags, 32);
                score += WeightedSignalMatch(profile.FeatureWeights, game.Features, 24);
                score += WeightedSignalMatch(profile.ThemeWeights, game.Themes, 22);
                score += WeightedSignalMatch(profile.TagWeights, game.SteamRecommendedTags, 10);

                if (game.CommunityScore.HasValue) score += game.CommunityScore.Value / 10.0;
                if (game.CriticScore.HasValue) score += game.CriticScore.Value / 12.0;
                if (!game.IsPlayed) score += 5;
                score *= RecommendationHeuristics.QualityMultiplier(game);

                result.Scores[game.PlayniteId] = Math.Max(score, 0);
            }

            return result;
        }

        public List<string> GetReasons(EnrichedGame game, TasteProfile profile)
        {
            var reasons = new List<string>();
            var playniteTags = MatchingSignals(game.Tags, profile.TagWeights, 0.05)
                .Take(2)
                .ToList();
            if (playniteTags.Any())
                reasons.Add($"Matches your {string.Join(" / ", playniteTags)} tags");

            foreach (var genre in game.Genres.Where(g => !IsGenericSignal(g)))
                if (profile.GenreWeights.TryGetValue(genre, out double w) && w > 0.05)
                    reasons.Add($"You play a lot of {genre}");
            foreach (var feature in game.Features)
                if (!IsGenericSignal(feature) && profile.FeatureWeights.TryGetValue(feature, out double w) && w > 0.05)
                    reasons.Add($"Matches your {feature} preference");
            foreach (var keyword in game.Keywords.Concat(game.Themes))
                if (!IsGenericSignal(keyword) &&
                    (profile.TagWeights.TryGetValue(keyword, out double tw) && tw > 0.05 ||
                    profile.ThemeWeights.TryGetValue(keyword, out double hw) && hw > 0.05)
                )
                    reasons.Add($"Matches your {keyword} niche");
            foreach (var tag in game.AlgorithmicTags)
                if (!IsGenericSignal(tag) && profile.TagWeights.TryGetValue(tag, out double aw) && aw > 0.05)
                    reasons.Add($"Niche match: {tag}");
            var steamTags = MatchingSignals(game.SteamRecommendedTags, profile.TagWeights, 0.05)
                .Where(t => !playniteTags.Contains(t, StringComparer.OrdinalIgnoreCase))
                .Take(2)
                .ToList();
            if (steamTags.Any())
                reasons.Add($"Also fits your {string.Join(" / ", steamTags)} store tags");
            if (RecommendationHeuristics.HasBadReviewSignal(game))
                reasons.Add(RecommendationHeuristics.QualityLabel(game));
            return reasons.Take(2).ToList();
        }

        private static double WeightedMatch(Dictionary<string, double> weights, IEnumerable<string> items, double multiplier)
        {
            double score = 0;
            foreach (var item in items)
                if (weights.TryGetValue(item, out double w))
                    score += w * multiplier;
            return score;
        }

        private static double WeightedSignalMatch(Dictionary<string, double> weights, IEnumerable<string> items, double multiplier)
        {
            double score = 0;
            foreach (var item in items ?? Enumerable.Empty<string>())
                if (!string.IsNullOrWhiteSpace(item) && weights.TryGetValue(item, out double w))
                    score += w * multiplier * SignalWeightMultiplier(item);
            return score;
        }

        private static void AddWeights(Dictionary<string, double> dict, IEnumerable<string> items, double w)
        {
            foreach (var item in items ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(item)) continue;
                if (!dict.ContainsKey(item)) dict[item] = 0;
                dict[item] += w;
            }
        }

        private static void AddWeightedSignals(Dictionary<string, double> dict, IEnumerable<string> items, double w)
        {
            foreach (var item in items ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(item)) continue;
                if (!dict.ContainsKey(item)) dict[item] = 0;
                dict[item] += w * SignalWeightMultiplier(item);
            }
        }

        private static IEnumerable<string> MatchingSignals(IEnumerable<string> items, Dictionary<string, double> weights, double threshold)
            => (items ?? Enumerable.Empty<string>())
                .Where(i => !string.IsNullOrWhiteSpace(i) && !IsGenericSignal(i))
                .Where(i => weights.TryGetValue(i, out var w) && w > threshold)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(i => weights.TryGetValue(i, out var w) ? w : 0);

        private static double SignalWeightMultiplier(string item)
            => IsGenericSignal(item) ? 0.25 : 1.0;

        private static bool IsGenericSignal(string value)
            => NoiseGenres.Contains(value ?? string.Empty) || RecommendationHeuristics.IsGenericTagSignal(value);
    }
}
