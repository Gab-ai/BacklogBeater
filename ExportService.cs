using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Playnite.SDK;

namespace GameRecommender
{
    /// <summary>
    /// Generates a plain-text AI prompt block from the current enriched library data.
    /// Designed to be pasted into any AI chat for instant context.
    /// </summary>
    public class ExportService
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        public static string GenerateAiBlock(
            List<EnrichedGame> allGames,
            List<ScoredGame> recommendations,
            TasteProfile profile)
        {
            var sb = new StringBuilder();

            sb.AppendLine("## My Steam/game library context");
            sb.AppendLine();

            // ── Stats ────────────────────────────────────────────────────
            int total = allGames.Count;
            int unplayed = allGames.Count(g => !g.IsPlayed);
            int played = total - unplayed;
            long totalSecs = allGames.Sum(g => g.PlaytimeSeconds);

            sb.AppendLine($"**Library:** {total} games total — {played} played, {unplayed} unplayed");
            sb.AppendLine($"**Total playtime:** {RecommendationEngine.FormatTime(totalSecs)}");
            sb.AppendLine();

            // ── Top played ───────────────────────────────────────────────
            var topPlayed = allGames
                .Where(g => g.IsPlayed)
                .OrderByDescending(g => g.PlaytimeSeconds)
                .Take(15)
                .ToList();

            sb.AppendLine("**Top games by playtime:**");
            foreach (var g in topPlayed)
                sb.AppendLine($"- {g.Name} ({RecommendationEngine.FormatTime(g.PlaytimeSeconds)})");
            sb.AppendLine();

            // ── Dominant genres ──────────────────────────────────────────
            if (profile.DominantGenres.Any())
            {
                sb.AppendLine($"**Dominant genres:** {string.Join(", ", profile.DominantGenres)}");
                sb.AppendLine();
            }

            // ── Top genre/tag weights ────────────────────────────────────
            var topTags = profile.TagWeights
                .OrderByDescending(kvp => kvp.Value)
                .Take(10)
                .Select(kvp => kvp.Key)
                .ToList();
            if (topTags.Any())
            {
                sb.AppendLine($"**Preferred tags/features:** {string.Join(", ", topTags)}");
                sb.AppendLine();
            }

            // ── Recently played ──────────────────────────────────────────
            var recent = allGames
                .Where(g => g.LastPlayed.HasValue)
                .OrderByDescending(g => g.LastPlayed)
                .Take(5)
                .ToList();

            if (recent.Any())
            {
                sb.AppendLine("**Recently played:**");
                foreach (var g in recent)
                    sb.AppendLine($"- {g.Name} ({g.LastPlayed.Value:MMM d})");
                sb.AppendLine();
            }

            // ── Started but not finished ─────────────────────────────────
            var shallow = allGames
                .Where(g => g.PlaytimeSeconds > 0 && g.PlaytimeSeconds < 18000)
                .OrderByDescending(g => g.PlaytimeSeconds)
                .Take(10)
                .ToList();

            if (shallow.Any())
            {
                sb.AppendLine("**Started but not finished (under 5h):**");
                foreach (var g in shallow)
                    sb.AppendLine($"- {g.Name} ({RecommendationEngine.FormatTime(g.PlaytimeSeconds)})");
                sb.AppendLine();
            }

            // ── Top unplayed gems ────────────────────────────────────────
            var unplayedGems = recommendations
                .Where(r => !r.Game.IsPlayed)
                .Take(20)
                .ToList();

            if (unplayedGems.Any())
            {
                sb.AppendLine("**Top unplayed games in my library (AI-recommended):**");
                foreach (var r in unplayedGems)
                {
                    var reason = r.Reasons.FirstOrDefault();
                    var line = $"- {r.Game.Name}";
                    if (!string.IsNullOrWhiteSpace(reason))
                        line += $" — {reason}";
                    sb.AppendLine(line);
                }
                sb.AppendLine();
            }

            // ── Prompt instructions ──────────────────────────────────────
            sb.AppendLine("---");
            sb.AppendLine("Use the above context to answer questions about my game library, " +
                          "recommend what to play next, evaluate game purchases, or compare bundles. " +
                          "Reference my actual playtime and game names when relevant.");

            return sb.ToString();
        }
    }
}
