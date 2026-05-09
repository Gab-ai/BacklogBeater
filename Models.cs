using System;
using System.Collections.Generic;
using System.Linq;
using Playnite.SDK.Models;

namespace GameRecommender
{
    public enum StartedGameIntent
    {
        None,
        Finishable,
        SessionGame,
        CoopMultiplayer,
        LongTermProgression,
        SandboxSim,
        ReturnLater
    }

    // ── Enrichment ───────────────────────────────────────────────────────

    /// <summary>
    /// A Playnite game record merged with data from Steam and IGDB.
    /// This is the canonical unit the scoring engines operate on.
    /// </summary>
    public class EnrichedGame
    {
        public Guid PlayniteId { get; set; }
        public string Name { get; set; }
        public long PlaytimeSeconds { get; set; }
        public DateTime? LastPlayed { get; set; }
        public string SourcePlugin { get; set; }   // "Steam", "Epic", "JAST", etc.
        public string SteamAppId { get; set; }     // null for non-Steam games
        public bool IsModpack { get; set; }
        public string BaseGameName { get; set; } = string.Empty;

        // Merged metadata (Playnite + IGDB fill-in)
        public List<string> Genres { get; set; } = new List<string>();
        public List<string> Tags { get; set; } = new List<string>();
        public List<string> Features { get; set; } = new List<string>();  // Co-op, VR, etc.
        public List<string> Themes { get; set; } = new List<string>();    // IGDB: Action, Horror...
        public List<string> Keywords { get; set; } = new List<string>();  // IGDB niche descriptors
        public List<string> AlgorithmicTags { get; set; } = new List<string>();
        public string Description { get; set; } = string.Empty;

        // Quality signals
        public int? CommunityScore { get; set; }
        public int? CriticScore { get; set; }
        public int? RawgId { get; set; }
        public double? RawgRating { get; set; }
        public int? RawgRatingsCount { get; set; }
        public int? RawgMetacritic { get; set; }
        public DateTime? RawgReleased { get; set; }

        // Steam-specific enrichment
        public List<string> SteamRecommendedTags { get; set; } = new List<string>();
        public List<string> SteamSimilarAppIds { get; set; } = new List<string>();
        public int? SteamReviewPercent { get; set; }
        public int? SteamReviewCount { get; set; }
        public string SteamReviewDescription { get; set; }
        public string SteamStoreUrl { get; set; }
        public string TrailerUrl { get; set; }
        public List<GameExternalLink> ExternalLinks { get; set; } = new List<GameExternalLink>();

        // Derived
        public bool IsPlayed => PlaytimeSeconds > 0;
        public bool IsDeepPlayed => PlaytimeSeconds >= 18000;  // 5+ hours
        public double PlaytimeHours => PlaytimeSeconds / 3600.0;
    }

    // ── Taste profile ────────────────────────────────────────────────────

    /// <summary>
    /// A user's inferred preferences, built from their played games.
    /// </summary>
    public class TasteProfile
    {
        public Dictionary<string, double> GenreWeights { get; set; } = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, double> TagWeights { get; set; } = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, double> FeatureWeights { get; set; } = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, double> ThemeWeights { get; set; } = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Set of genres the user has meaningful playtime in (>= 10h total).</summary>
        public HashSet<string> DominantGenres { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public long TotalPlaytimeSeconds { get; set; }
        public double TotalPlaytimeHours => TotalPlaytimeSeconds / 3600.0;

        /// <summary>Top-N played game names, used for the AI prompt.</summary>
        public List<string> TopPlayedNames { get; set; } = new List<string>();
    }

    // ── Scoring ──────────────────────────────────────────────────────────

    /// <summary>Raw scores from a single engine, keyed by PlayniteId.</summary>
    public class EngineScores
    {
        public string EngineName { get; set; }
        public Dictionary<Guid, double> Scores { get; set; } = new Dictionary<Guid, double>();

        /// <summary>Normalise all scores to 0–1 range in-place.</summary>
        public void Normalise()
        {
            double max = 0;
            foreach (var v in Scores.Values)
                if (v > max) max = v;
            if (max <= 0) return;
            var keys = new List<Guid>(Scores.Keys);
            foreach (var k in keys)
                Scores[k] = Scores[k] / max;
        }
    }

    /// <summary>A candidate game after fusion scoring.</summary>
    public class ScoredGame
    {
        public EnrichedGame Game { get; set; }
        public double FusedScore { get; set; }

        // Per-engine contributions (for debug / tooltip)
        public double WeightedScore { get; set; }
        public double CosineScore { get; set; }
        public double GraphScore { get; set; }
        public double NoveltyBonus { get; set; }
        public double QualityWeightStrength { get; set; } = 1.0;
        public string RecommendationPreset { get; set; } = "Balanced";
        public string ScoreTuningSummary { get; set; } = string.Empty;

        // Reason shown in UI
        public List<string> Reasons { get; set; } = new List<string>();
        public string PrimaryTag { get; set; }
        public string RecommendationCategory { get; set; } = "Best Matches";
        public StartedGameIntent StartedIntent { get; set; } = StartedGameIntent.None;
        public string StartedIntentLabel => StartedIntent == StartedGameIntent.None
            ? PrimaryTag
            : SplitPascalCase(StartedIntent.ToString());
        public string QualityLabel { get; set; } = "Quality unknown";
        public bool AiRanked { get; set; }
        public string PrimaryReason => Reasons != null && Reasons.Count > 0 ? Reasons[0] : string.Empty;
        public string SecondaryReason => Reasons != null && Reasons.Count > 1 ? Reasons[1] : string.Empty;
        public string DetailText
        {
            get
            {
                var lines = new List<string>
                {
                    Game?.Name ?? "Unknown game",
                    $"Score: {FusedScore:F2}  Weighted: {WeightedScore:F2}  Similarity: {CosineScore:F2}  Graph: {GraphScore:F2}  Novelty: {NoveltyBonus:F2}",
                    $"Played: {RecommendationEngine.FormatTime(Game?.PlaytimeSeconds ?? 0)}  Source: {Game?.SourcePlugin ?? "Unknown"}"
                };

                if (!string.IsNullOrWhiteSpace(ScoreTuningSummary))
                    lines.Add("Tuning: " + ScoreTuningSummary);
                if (Game?.Genres?.Count > 0) lines.Add("Genres: " + string.Join(", ", Game.Genres));
                if (Game?.Tags?.Count > 0) lines.Add("Tags: " + string.Join(", ", Game.Tags));
                if (Game?.Themes?.Count > 0) lines.Add("Themes: " + string.Join(", ", Game.Themes));
                if (Game?.Keywords?.Count > 0) lines.Add("Keywords: " + string.Join(", ", Game.Keywords));
                if (Game?.AlgorithmicTags?.Count > 0) lines.Add("Algorithmic tags: " + string.Join(", ", Game.AlgorithmicTags));
                if (Game?.Features?.Count > 0) lines.Add("Features: " + string.Join(", ", Game.Features));
                lines.Add("Quality: " + QualityLabel);
                if (Game?.SteamReviewPercent.HasValue == true)
                    lines.Add($"Steam reviews: {Game.SteamReviewDescription ?? "Reviews"} ({Game.SteamReviewPercent}% positive, {Game.SteamReviewCount ?? 0} reviews)");
                lines.Add("Category: " + RecommendationCategory);
                if (Reasons?.Count > 0) lines.Add("Why: " + string.Join(" | ", Reasons));
                if (!string.IsNullOrWhiteSpace(Game?.Description)) lines.Add(Environment.NewLine + Game.Description);
                return string.Join(Environment.NewLine, lines);
            }
        }

        private static string SplitPascalCase(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            return System.Text.RegularExpressions.Regex.Replace(value, "([a-z])([A-Z])", "$1 $2");
        }
    }

    public enum ExternalCandidateKind
    {
        Unknown,
        PlayableGame,
        GameAdjacentTool,
        MediaPlayerOrUtility,
        EditorCreatorTool,
        DLCOrAddOn,
        DemoPlaytestPrologue,
        SoundtrackArtbookCosmetic
    }

    public class ExternalRecommendation
    {
        public string Title { get; set; }
        public string Store { get; set; }
        public string Url { get; set; }
        public decimal? Price { get; set; }
        public decimal? RegularPrice { get; set; }
        public int? Cut { get; set; }
        public double MatchScore { get; set; }
        public string ItadId { get; set; }
        public string ItadSlug { get; set; }
        public string SteamAppId { get; set; }
        public string DealType { get; set; }
        public string BoxArtUrl { get; set; }
        public string BannerUrl { get; set; }
        public string RecommendationKind { get; set; } = "Deal";
        public bool IsWishlisted { get; set; }
        public string CandidateDescription { get; set; } = string.Empty;
        public List<string> CandidateTags { get; set; } = new List<string>();
        public List<string> CandidateGenres { get; set; } = new List<string>();
        public List<string> CandidateThemes { get; set; } = new List<string>();
        public List<string> CandidateFeatures { get; set; } = new List<string>();
        public List<string> MoodTags { get; set; } = new List<string>();
        public List<string> MechanicTags { get; set; } = new List<string>();
        public List<string> SourceSignals { get; set; } = new List<string>();
        public List<string> SimilarOwnedGames { get; set; } = new List<string>();
        public string AiRerankReason { get; set; } = string.Empty;
        public double AiRerankScore { get; set; }
        public ExternalCandidateKind CandidateKind { get; set; } = ExternalCandidateKind.Unknown;
        public string AdmissionRejectionReason { get; set; } = string.Empty;
        public bool IsEarlyAccess { get; set; }
        public DateTime? LastContentUpdateUtc { get; set; }
        public string QualityLabel { get; set; } = "Quality unknown";
        public int? ReviewPercent { get; set; }
        public int? ReviewCount { get; set; }
        public double RelevanceScore { get; set; }
        public double QualityScore { get; set; }
        public double DealScore { get; set; }
        public double PenaltyScore { get; set; }
        public decimal? HistoricalLowPrice { get; set; }
        public decimal? HistoricalLowRegularPrice { get; set; }
        public int? HistoricalLowCut { get; set; }
        public string HistoricalLowStore { get; set; }
        public DateTime? HistoricalLowDate { get; set; }
        public int? RawgId { get; set; }
        public double? RawgRating { get; set; }
        public int? RawgRatingsCount { get; set; }
        public int? RawgMetacritic { get; set; }
        public DateTime? RawgReleased { get; set; }
        public string VndbId { get; set; }
        public string VndbTitle { get; set; }
        public double? VndbRating { get; set; }
        public int? VndbVoteCount { get; set; }
        public List<string> Reasons { get; set; } = new List<string>();
        public string PrimaryReason => Reasons != null && Reasons.Count > 0 ? Reasons[0] : string.Empty;
        public string SecondaryReason => Reasons != null && Reasons.Count > 1 ? Reasons[1] : string.Empty;
        public bool IsDeal => Price.HasValue || RegularPrice.HasValue || Cut.HasValue ||
                              string.Equals(RecommendationKind, "Deal", StringComparison.OrdinalIgnoreCase);
        public string RecommendationKindText => string.IsNullOrWhiteSpace(RecommendationKind) ? "External pick" : RecommendationKind;

        public string PriceText
        {
            get
            {
                if (!IsDeal)
                    return RecommendationKindText;
                var price = Price.HasValue ? "$" + Price.Value.ToString("0.##") : "Deal";
                if (Cut.HasValue && Cut.Value > 0) return $"{price} ({Cut.Value}% off)";
                return price;
            }
        }
    }

    public class SpotlightItem
    {
        public string Title { get; set; }
        public string Summary { get; set; }
        public string Source { get; set; }
        public string Url { get; set; }
        public string RelatedGame { get; set; }
        public string Reason { get; set; }
        public DateTime PublishedAt { get; set; }
        public string PublishedText => PublishedAt == DateTime.MinValue ? "Recent" : PublishedAt.ToString("MMM d");
    }

    public class GameExternalLink
    {
        public string Label { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
    }

    // ── AI layer ─────────────────────────────────────────────────────────

    /// <summary>A single entry in the AI re-ranked result list.</summary>
    public class AiRankedItem
    {
        public string Name { get; set; }
        public int Rank { get; set; }
        public string Reason { get; set; }
        public bool AiRanked { get; set; } = true;
    }

    /// <summary>Cached result from an AI API call.</summary>
    public class AiRerankerCache
    {
        public DateTime GeneratedAt { get; set; }
        public List<AiRankedItem> Items { get; set; } = new List<AiRankedItem>();
    }

    public class AiStartedReasonItem
    {
        public string Name { get; set; }
        public string Reason { get; set; }
        public string Intent { get; set; }
    }

    public class AiStartedReasonCache
    {
        public DateTime GeneratedAt { get; set; }
        public List<AiStartedReasonItem> Items { get; set; } = new List<AiStartedReasonItem>();
    }

    // ── User feedback ───────────────────────────────────────────────────

    public class RejectedGameFeedback
    {
        public Guid PlayniteId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string SourcePlugin { get; set; } = string.Empty;
        public string SteamAppId { get; set; } = string.Empty;
        public DateTime RejectedAt { get; set; } = DateTime.UtcNow;
        public string ReasonCode { get; set; } = string.Empty;
        public string ReasonText { get; set; } = string.Empty;
    }

    public class RecommendationFeedback
    {
        public Guid PlayniteId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string SourcePlugin { get; set; } = string.Empty;
        public string SteamAppId { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public string ReasonText { get; set; } = string.Empty;
    }

    public class BlacklistedGame
    {
        public Guid? PlayniteId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string SourcePlugin { get; set; } = string.Empty;
        public string SteamAppId { get; set; } = string.Empty;
        public DateTime AddedAt { get; set; } = DateTime.UtcNow;
        public string ReasonText { get; set; } = string.Empty;
    }

    public class LibraryIntegrationRecord
    {
        public string Provider { get; set; } = string.Empty;
        public string ExternalId { get; set; } = string.Empty;
        public Guid PlayniteId { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public string SourceLabel { get; set; } = string.Empty;
        public string ProfilePath { get; set; } = string.Empty;
        public string ConfigPath { get; set; } = string.Empty;
        public long LastKnownPlaytimeSeconds { get; set; }
        public string PlaytimeConfidence { get; set; } = string.Empty;
        public string SyncStatus { get; set; } = string.Empty;
        public DateTime LastSyncedAt { get; set; } = DateTime.UtcNow;
    }

    // ── Settings ─────────────────────────────────────────────────────────

    public class RecommenderSettings
    {
        public string SteamApiKey { get; set; } = string.Empty;
        public string SteamUserId { get; set; } = string.Empty;
        public string IgdbClientId { get; set; } = string.Empty;
        public string IgdbClientSecret { get; set; } = string.Empty;
        public string RawgApiKey { get; set; } = string.Empty;
        public string AnthropicApiKey { get; set; } = string.Empty;
        public string OpenAiApiKey { get; set; } = string.Empty;
        public string OpenAiModel { get; set; } = "gpt-5.4-mini";
        public string ItadApiKey { get; set; } = string.Empty;
        public bool EnableJastDeals { get; set; } = false;
        public bool EnableMangaGamerDeals { get; set; } = false;
        public bool HideEarlyAccessRecommendations { get; set; } = false;
        public bool HideStaleEarlyAccessRecommendations { get; set; } = true;

        public bool AiRerankerEnabled { get; set; } = false;
        public string AiProvider { get; set; } = "Claude";
        public bool EnrichmentEnabled { get; set; } = true;
        public bool SpotlightEnabled { get; set; } = true;
        public bool AnimeAssistantEnabled { get; set; } = true;
        public string AssistantVoice { get; set; } = "Waifu";
        public bool SoundEffectsEnabled { get; set; } = false;
        public double SoundEffectsVolume { get; set; } = 0.45;

        // Fusion weights (must sum to 1.0)
        public double WeightedEngineWeight { get; set; } = 0.35;
        public double CosineEngineWeight { get; set; } = 0.35;
        public double GraphEngineWeight { get; set; } = 0.30;

        // Novelty bonus applied to games that introduce a new genre
        public double NoveltyBonusStrength { get; set; } = 0.15;
        public double QualityWeightStrength { get; set; } = 1.0;
        public string RecommendationPresetId { get; set; } = "Balanced";

        // How many candidates to send to Claude
        public int AiCandidateCount { get; set; } = 20;

        public List<RejectedGameFeedback> RejectedGames { get; set; } = new List<RejectedGameFeedback>();
        public List<RecommendationFeedback> RecommendationFeedback { get; set; } = new List<RecommendationFeedback>();
        public List<string> BlacklistedPlatforms { get; set; } = new List<string>();
        public List<string> BlacklistedTags { get; set; } = new List<string>();
        public List<BlacklistedGame> BlacklistedGames { get; set; } = new List<BlacklistedGame>();
        public string MinecraftLauncherPaths { get; set; } = string.Empty;
        public List<LibraryIntegrationRecord> LibraryIntegrationRecords { get; set; } = new List<LibraryIntegrationRecord>();
    }
}
