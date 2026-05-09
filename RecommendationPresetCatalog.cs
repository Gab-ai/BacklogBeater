using System;
using System.Collections.Generic;
using System.Linq;

namespace GameRecommender
{
    public sealed class RecommendationPreset
    {
        public string Id { get; set; }
        public string DisplayName { get; set; }
        public double WeightedEngineWeight { get; set; }
        public double CosineEngineWeight { get; set; }
        public double GraphEngineWeight { get; set; }
        public double NoveltyBonusStrength { get; set; }
        public double QualityWeightStrength { get; set; }
    }

    public static class RecommendationPresetCatalog
    {
        public const string BalancedId = "Balanced";
        public const string CustomId = "Custom";

        private static readonly List<RecommendationPreset> presets = new List<RecommendationPreset>
        {
            new RecommendationPreset
            {
                Id = BalancedId,
                DisplayName = "Balanced",
                WeightedEngineWeight = 0.35,
                CosineEngineWeight = 0.35,
                GraphEngineWeight = 0.30,
                NoveltyBonusStrength = 0.15,
                QualityWeightStrength = 1.00
            },
            new RecommendationPreset
            {
                Id = "Comfort Picks",
                DisplayName = "Comfort Picks",
                WeightedEngineWeight = 0.40,
                CosineEngineWeight = 0.40,
                GraphEngineWeight = 0.20,
                NoveltyBonusStrength = 0.05,
                QualityWeightStrength = 1.05
            },
            new RecommendationPreset
            {
                Id = "Fresh Finds",
                DisplayName = "Fresh Finds",
                WeightedEngineWeight = 0.25,
                CosineEngineWeight = 0.30,
                GraphEngineWeight = 0.25,
                NoveltyBonusStrength = 0.35,
                QualityWeightStrength = 1.00
            },
            new RecommendationPreset
            {
                Id = "Critic's Choice",
                DisplayName = "Critic's Choice",
                WeightedEngineWeight = 0.30,
                CosineEngineWeight = 0.25,
                GraphEngineWeight = 0.20,
                NoveltyBonusStrength = 0.10,
                QualityWeightStrength = 1.35
            },
            new RecommendationPreset
            {
                Id = "Deep Backlog",
                DisplayName = "Deep Backlog",
                WeightedEngineWeight = 0.35,
                CosineEngineWeight = 0.30,
                GraphEngineWeight = 0.20,
                NoveltyBonusStrength = 0.05,
                QualityWeightStrength = 1.10
            },
            new RecommendationPreset
            {
                Id = CustomId,
                DisplayName = "Custom",
                WeightedEngineWeight = 0.35,
                CosineEngineWeight = 0.35,
                GraphEngineWeight = 0.30,
                NoveltyBonusStrength = 0.15,
                QualityWeightStrength = 1.00
            }
        };

        public static IReadOnlyList<RecommendationPreset> Presets => presets;
        public static IReadOnlyList<string> DisplayNames => presets.Select(p => p.DisplayName).ToList();

        public static RecommendationPreset Find(string idOrDisplayName)
        {
            if (string.IsNullOrWhiteSpace(idOrDisplayName))
                return presets[0];

            return presets.FirstOrDefault(p =>
                       string.Equals(p.Id, idOrDisplayName, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(p.DisplayName, idOrDisplayName, StringComparison.OrdinalIgnoreCase))
                   ?? presets[0];
        }

        public static string NormalizePresetId(string idOrDisplayName)
            => Find(idOrDisplayName).Id;

        public static string DisplayNameFor(string idOrDisplayName)
            => Find(idOrDisplayName).DisplayName;

        public static double ClampUnit(double value)
            => Math.Max(0.0, Math.Min(1.0, value));

        public static double ClampNovelty(double value)
            => Math.Max(0.0, Math.Min(0.5, value));

        public static double ClampQuality(double value)
            => Math.Max(0.0, Math.Min(1.5, value));
    }
}
