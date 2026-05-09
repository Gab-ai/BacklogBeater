using System;
using System.Collections.Generic;
using System.Linq;
using Playnite.SDK;

namespace GameRecommender
{
    /// <summary>
    /// Combines scores from all three engines into a single ranked list.
    ///
    /// Process:
    ///  1. Normalise each engine's scores to [0, 1] independently
    ///  2. Compute weighted average using configurable weights
    ///  3. Apply novelty bonus to games that introduce an under-represented genre
    ///  4. Attach per-game reasons from the engines that contributed most
    /// </summary>
    public class ScoreFusion
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly WeightedScoringEngine weightedEngine;
        private readonly SteamGraphEngine graphEngine;
        private readonly RecommenderSettings settings;

        public ScoreFusion(
            WeightedScoringEngine weightedEngine,
            SteamGraphEngine graphEngine,
            RecommenderSettings settings)
        {
            this.weightedEngine = weightedEngine;
            this.graphEngine = graphEngine;
            this.settings = settings;
        }

        /// <summary>
        /// Fuses three EngineScores objects into a sorted list of ScoredGame.
        /// </summary>
        public List<ScoredGame> Fuse(
            EngineScores weightedScores,
            EngineScores cosineScores,
            EngineScores graphScores,
            IEnumerable<EnrichedGame> candidates,
            TasteProfile tasteProfile,
            IEnumerable<EnrichedGame> topPlayedGames)
        {
            // Normalise each engine independently
            weightedScores.Normalise();
            cosineScores.Normalise();
            graphScores.Normalise();

            var candidateList = candidates.ToList();
            var topPlayed = topPlayedGames.ToList();
            var result = new List<ScoredGame>();

            foreach (var game in candidateList)
            {
                var id = game.PlayniteId;
                double ws = weightedScores.Scores.TryGetValue(id, out double w) ? w : 0;
                double cs = cosineScores.Scores.TryGetValue(id, out double c) ? c : 0;
                double gs = graphScores.Scores.TryGetValue(id, out double g) ? g : 0;

                double fused = (ws * settings.WeightedEngineWeight)
                             + (cs * settings.CosineEngineWeight)
                             + (gs * settings.GraphEngineWeight);

                // Novelty bonus: boost games that introduce a genre the user hasn't played much
                double novelty = ComputeNoveltyBonus(game, tasteProfile);
                fused += novelty * settings.NoveltyBonusStrength;
                var baseQualityMultiplier = RecommendationHeuristics.QualityMultiplier(game);
                var qualityStrength = RecommendationPresetCatalog.ClampQuality(settings.QualityWeightStrength);
                var qualityMultiplier = 1.0 + ((baseQualityMultiplier - 1.0) * qualityStrength);
                fused *= Math.Max(0.1, qualityMultiplier);
                fused *= ComputeReviewFeedbackMultiplier(game, candidateList);
                fused *= ComputeRejectionReasonMultiplier(game, candidateList);

                // Build reasons list, prioritising the highest-contributing engine
                var reasons = BuildReasons(game, ws, cs, gs, tasteProfile, topPlayed);
                var primaryTag = DeterminePrimaryTag(game, tasteProfile);
                RecommendationDiagnostics.LogSuspiciousPrimaryTag(logger, game, primaryTag);

                var scored = new ScoredGame
                {
                    Game = game,
                    FusedScore = fused,
                    WeightedScore = ws,
                    CosineScore = cs,
                    GraphScore = gs,
                    NoveltyBonus = novelty,
                    Reasons = reasons,
                    PrimaryTag = primaryTag,
                    QualityLabel = RecommendationHeuristics.QualityLabel(game),
                    QualityWeightStrength = qualityStrength,
                    RecommendationPreset = RecommendationPresetCatalog.DisplayNameFor(settings.RecommendationPresetId),
                    ScoreTuningSummary = BuildTuningSummary(settings, qualityStrength),
                };
                scored.RecommendationCategory = RecommendationHeuristics.CategoryFor(scored, tasteProfile);

                result.Add(new ScoredGame
                {
                    Game = scored.Game,
                    FusedScore = scored.FusedScore,
                    WeightedScore = scored.WeightedScore,
                    CosineScore = scored.CosineScore,
                    GraphScore = scored.GraphScore,
                    NoveltyBonus = scored.NoveltyBonus,
                    Reasons = scored.Reasons,
                    PrimaryTag = scored.PrimaryTag,
                    RecommendationCategory = scored.RecommendationCategory,
                    QualityLabel = scored.QualityLabel,
                    QualityWeightStrength = scored.QualityWeightStrength,
                    RecommendationPreset = scored.RecommendationPreset,
                    ScoreTuningSummary = scored.ScoreTuningSummary,
                });
            }

            return result
                .Where(s => s.FusedScore > 0)
                .OrderByDescending(s => s.FusedScore)
                .ToList();
        }

        private static string BuildTuningSummary(RecommenderSettings settings, double qualityStrength)
        {
            var preset = RecommendationPresetCatalog.DisplayNameFor(settings?.RecommendationPresetId);
            var noveltyStrength = settings?.NoveltyBonusStrength ?? 0.0;
            return $"{preset} preset; novelty {noveltyStrength:F2}; quality influence {qualityStrength:F2}x";
        }

        // ── Rejection feedback ──────────────────────────────────────────

        private double ComputeReviewFeedbackMultiplier(
            EnrichedGame candidate,
            IEnumerable<EnrichedGame> allCandidates)
        {
            if (candidate == null || settings.RecommendationFeedback == null || !settings.RecommendationFeedback.Any())
                return 1.0;

            double multiplier = 1.0;
            foreach (var feedback in settings.RecommendationFeedback.Where(f => !string.IsNullOrWhiteSpace(f?.Action)))
            {
                var action = feedback.Action.Trim();
                if (MatchesFeedback(candidate, feedback))
                {
                    if (string.Equals(action, "Saved", StringComparison.OrdinalIgnoreCase))
                        multiplier *= 1.03;
                    else if (string.Equals(action, "MoreLikeThis", StringComparison.OrdinalIgnoreCase))
                        multiplier *= 1.08;
                    else if (string.Equals(action, "LessLikeThis", StringComparison.OrdinalIgnoreCase))
                        multiplier *= 0.75;
                    continue;
                }

                var feedbackGame = FindFeedbackCandidate(feedback, allCandidates);
                if (feedbackGame == null || !HasMeaningfulMetadataOverlap(candidate, feedbackGame))
                    continue;

                if (string.Equals(action, "MoreLikeThis", StringComparison.OrdinalIgnoreCase))
                    multiplier *= 1.04;
                else if (string.Equals(action, "LessLikeThis", StringComparison.OrdinalIgnoreCase))
                    multiplier *= 0.92;
            }

            return Math.Max(0.45, Math.Min(1.30, multiplier));
        }

        private static EnrichedGame FindFeedbackCandidate(
            RecommendationFeedback feedback,
            IEnumerable<EnrichedGame> allCandidates)
        {
            return allCandidates.FirstOrDefault(g => MatchesFeedback(g, feedback));
        }

        private static bool MatchesFeedback(EnrichedGame game, RecommendationFeedback feedback)
        {
            if (game == null || feedback == null) return false;
            if (feedback.PlayniteId != Guid.Empty && feedback.PlayniteId == game.PlayniteId)
                return true;
            if (!string.IsNullOrWhiteSpace(feedback.SteamAppId) &&
                !string.IsNullOrWhiteSpace(game.SteamAppId) &&
                string.Equals(feedback.SteamAppId, game.SteamAppId, StringComparison.OrdinalIgnoreCase))
                return true;
            return string.Equals(NormalizeMatchText(feedback.Name), NormalizeMatchText(game.Name), StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(NormalizeMatchText(feedback.SourcePlugin), NormalizeMatchText(game.SourcePlugin), StringComparison.OrdinalIgnoreCase);
        }

        private double ComputeRejectionReasonMultiplier(
            EnrichedGame candidate,
            IEnumerable<EnrichedGame> allCandidates)
        {
            if (candidate == null || settings.RejectedGames == null || !settings.RejectedGames.Any())
                return 1.0;

            var candidateText = MetadataText(candidate);
            double multiplier = 1.0;
            foreach (var rejected in settings.RejectedGames.Where(r => !string.IsNullOrWhiteSpace(r?.ReasonCode)))
            {
                if (IsSameGame(candidate, rejected))
                    continue;

                var code = rejected.ReasonCode.Trim();
                if (string.Equals(code, "too_multiplayer", StringComparison.OrdinalIgnoreCase) &&
                    ContainsAny(candidateText, "multiplayer", "co-op", "coop", "mmo", "pvp", "online co-op", "massively multiplayer"))
                {
                    multiplier *= 0.88;
                }
                else if (string.Equals(code, "too_long", StringComparison.OrdinalIgnoreCase) &&
                         ContainsAny(candidateText, "sandbox", "survival", "crafting", "base building", "open world", "live service",
                             "endgame", "grind", "management", "tycoon", "colony", "simulation", "account progression"))
                {
                    multiplier *= 0.92;
                }
                else if (string.Equals(code, "quality_concern", StringComparison.OrdinalIgnoreCase) &&
                         RecommendationHeuristics.HasBadReviewSignal(candidate))
                {
                    multiplier *= 0.90;
                }
                else if (string.Equals(code, "wrong_genre", StringComparison.OrdinalIgnoreCase))
                {
                    var rejectedGame = FindRejectedCandidate(rejected, allCandidates);
                    if (HasMeaningfulMetadataOverlap(candidate, rejectedGame))
                        multiplier *= 0.90;
                }
            }

            return Math.Max(multiplier, 0.65);
        }

        private static EnrichedGame FindRejectedCandidate(
            RejectedGameFeedback rejected,
            IEnumerable<EnrichedGame> allCandidates)
        {
            return allCandidates.FirstOrDefault(g => IsSameGame(g, rejected));
        }

        private static bool IsSameGame(EnrichedGame game, RejectedGameFeedback rejected)
        {
            if (game == null || rejected == null) return false;
            if (rejected.PlayniteId != Guid.Empty && rejected.PlayniteId == game.PlayniteId)
                return true;
            if (!string.IsNullOrWhiteSpace(rejected.SteamAppId) &&
                !string.IsNullOrWhiteSpace(game.SteamAppId) &&
                string.Equals(rejected.SteamAppId, game.SteamAppId, StringComparison.OrdinalIgnoreCase))
                return true;
            return string.Equals(NormalizeMatchText(rejected.Name), NormalizeMatchText(game.Name), StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(NormalizeMatchText(rejected.SourcePlugin), NormalizeMatchText(game.SourcePlugin), StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasMeaningfulMetadataOverlap(EnrichedGame candidate, EnrichedGame rejected)
        {
            if (candidate == null || rejected == null) return false;
            var candidateTerms = MeaningfulFeedbackTerms(candidate);
            if (!candidateTerms.Any()) return false;
            return MeaningfulFeedbackTerms(rejected).Any(candidateTerms.Contains);
        }

        private static HashSet<string> MeaningfulFeedbackTerms(EnrichedGame game)
        {
            var broadTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "action", "adventure", "casual", "indie", "single-player", "singleplayer",
                "multiplayer", "early access", "free to play"
            };

            var terms = game.AlgorithmicTags
                .Concat(game.Keywords)
                .Concat(game.Tags)
                .Concat(game.Features)
                .Concat(game.Themes)
                .Concat(game.Genres)
                .Where(t => !string.IsNullOrWhiteSpace(t) && !broadTerms.Contains(t))
                .Select(NormalizeMatchText)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();
            return new HashSet<string>(terms, StringComparer.OrdinalIgnoreCase);
        }

        private static string MetadataText(EnrichedGame game)
        {
            if (game == null) return string.Empty;
            return string.Join(" ", new[]
            {
                game.Name,
                game.Description,
                string.Join(" ", game.Genres ?? new List<string>()),
                string.Join(" ", game.Tags ?? new List<string>()),
                string.Join(" ", game.Features ?? new List<string>()),
                string.Join(" ", game.Themes ?? new List<string>()),
                string.Join(" ", game.Keywords ?? new List<string>()),
                string.Join(" ", game.AlgorithmicTags ?? new List<string>())
            }).ToLowerInvariant();
        }

        private static bool ContainsAny(string text, params string[] needles)
            => needles.Any(n => text.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0);

        private static string NormalizeMatchText(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            return value.Trim().ToLowerInvariant();
        }

        // ── Novelty ──────────────────────────────────────────────────────

        private double ComputeNoveltyBonus(EnrichedGame game, TasteProfile profile)
        {
            if (!game.Genres.Any()) return 0;
            double bonus = 0;
            foreach (var genre in game.Genres)
            {
                // Genre is novel if not in dominant genres and has some profile weight
                if (!profile.DominantGenres.Contains(genre))
                {
                    profile.GenreWeights.TryGetValue(genre, out double existing);
                    // Scale bonus — completely new genre = 1.0, weak presence = small bonus
                    double noveltyScore = 1.0 - Math.Min(existing / 0.3, 1.0);
                    bonus = Math.Max(bonus, noveltyScore);
                }
            }
            return bonus;
        }

        // ── Reasons ──────────────────────────────────────────────────────

        private List<string> BuildReasons(
            EnrichedGame game,
            double ws, double cs, double gs,
            TasteProfile profile,
            List<EnrichedGame> topPlayed)
        {
            var reasons = new List<string>();

            // Graph reason first if it was the strongest signal
            if (gs >= ws && gs >= cs && gs > 0.1)
            {
                var graphReasons = graphEngine.GetReasons(game, topPlayed);
                reasons.AddRange(graphReasons);
            }

            // Weighted reason
            if (ws > 0.1)
            {
                var weightedReasons = weightedEngine.GetReasons(game, profile);
                reasons.AddRange(weightedReasons);
            }

            // Cosine fallback
            if (!reasons.Any() && cs > 0.1)
                reasons.Add("Conceptually similar to games you've played");

            // Unplayed note
            if (!game.IsPlayed && reasons.Any())
                reasons[0] = reasons[0] + " — unplayed in your library";
            else if (!game.IsPlayed)
                reasons.Add("Unplayed — sitting in your library");
            if (RecommendationHeuristics.HasBadReviewSignal(game))
                reasons.Add(RecommendationHeuristics.QualityLabel(game));

            return reasons.Distinct().Take(3).ToList();
        }

        // ── Primary tag ──────────────────────────────────────────────────

        private static string DeterminePrimaryTag(EnrichedGame game, TasteProfile profile)
        {
            var algorithmicTag = FirstSpecificPrimaryLabel(game.AlgorithmicTags);
            if (!string.IsNullOrWhiteSpace(algorithmicTag)) return algorithmicTag;

            var keyword = FirstSpecificPrimaryLabel(game.Keywords);
            if (!string.IsNullOrWhiteSpace(keyword)) return keyword;

            var playniteTag = FirstWeightedSpecificLabel(game.Tags, profile.TagWeights);
            if (!string.IsNullOrWhiteSpace(playniteTag)) return playniteTag;

            var theme = FirstSpecificPrimaryLabel(game.Themes);
            if (!string.IsNullOrWhiteSpace(theme)) return theme;

            var steamTag = FirstWeightedSpecificLabel(game.SteamRecommendedTags, profile.TagWeights);
            if (!string.IsNullOrWhiteSpace(steamTag)) return steamTag;

            // Feature priority
            var featurePriority = new[] { "VR Support", "Controller Support", "Cloud Saves" };
            foreach (var f in featurePriority)
                if (RecommendationDiagnostics.IsUsefulPrimaryLabel(f) &&
                    game.Features.Any(feat => RecommendationDiagnostics.IsUsefulPrimaryLabel(feat) &&
                                              feat.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0))
                    return f;

            // Top-weight genre
            var usefulGenres = (game.Genres ?? new List<string>())
                .Where(RecommendationDiagnostics.IsUsefulPrimaryLabel)
                .Where(g => !RecommendationHeuristics.IsGenericTagSignal(g))
                .ToList();
            if (usefulGenres.Any())
            {
                return usefulGenres
                    .OrderByDescending(g => profile.GenreWeights.TryGetValue(g, out double w) ? w : 0)
                    .First();
            }

            return "Recommended";
        }

        private static string FirstSpecificPrimaryLabel(IEnumerable<string> values)
            => (values ?? Enumerable.Empty<string>())
                .FirstOrDefault(v =>
                    RecommendationDiagnostics.IsUsefulPrimaryLabel(v) &&
                    !RecommendationHeuristics.IsGenericTagSignal(v));

        private static string FirstWeightedSpecificLabel(IEnumerable<string> values, Dictionary<string, double> weights)
            => (values ?? Enumerable.Empty<string>())
                .Where(v =>
                    RecommendationDiagnostics.IsUsefulPrimaryLabel(v) &&
                    !RecommendationHeuristics.IsGenericTagSignal(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(v => weights != null && weights.TryGetValue(v, out var w) ? w : 0)
                .FirstOrDefault();
    }
}
