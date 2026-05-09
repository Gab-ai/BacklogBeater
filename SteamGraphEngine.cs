using System;
using System.Collections.Generic;
using System.Linq;

namespace GameRecommender
{
    /// <summary>
    /// Engine 3: Steam "similar games" graph walk.
    ///
    /// For each of the user's top-10 most-played Steam games, we have a list of
    /// appIDs that Steam considers similar (from the SteamEnrichmentClient).
    ///
    /// A candidate game scores points for every top-played game that lists it as similar.
    /// Games appearing in multiple similarity lists score higher — strong collaborative
    /// filtering signal. This breaks the local-maximum problem because Steam's similarity
    /// is based on real player co-purchase/co-play data, not just metadata tags.
    ///
    /// Non-Steam games get a zero graph score (they fall back to the other two engines).
    /// </summary>
    public class SteamGraphEngine
    {
        private const int TopPlayedCount = 10;

        public EngineScores Score(
            IEnumerable<EnrichedGame> candidates,
            IEnumerable<EnrichedGame> allGames)
        {
            var result = new EngineScores { EngineName = "SteamGraph" };

            // Build a map from Steam appId → EnrichedGame for all games
            var appIdToGame = new Dictionary<string, EnrichedGame>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in allGames)
                if (!string.IsNullOrWhiteSpace(g.SteamAppId))
                    appIdToGame[g.SteamAppId] = g;

            // Top-N played Steam games
            var topPlayed = allGames
                .Where(g => g.IsDeepPlayed && !string.IsNullOrWhiteSpace(g.SteamAppId))
                .OrderByDescending(g => g.PlaytimeSeconds)
                .Take(TopPlayedCount)
                .ToList();

            if (!topPlayed.Any()) return result;

            // Score each candidate by how many top-played games list it as similar.
            // Weight by the playtime of the source game — a game similar to your 200h
            // title should score more than one similar to your 5h title.
            double maxPlaytime = topPlayed.Max(g => (double)g.PlaytimeSeconds);

            foreach (var candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate.SteamAppId)) continue;

                double score = 0;
                int appearanceCount = 0;

                foreach (var played in topPlayed)
                {
                    if (played.SteamSimilarAppIds.Contains(candidate.SteamAppId,
                        StringComparer.OrdinalIgnoreCase))
                    {
                        // Weight by relative playtime
                        double relativeWeight = played.PlaytimeSeconds / (maxPlaytime + 1);
                        score += relativeWeight;
                        appearanceCount++;
                    }
                }

                // Bonus for appearing in multiple similarity lists
                if (appearanceCount >= 2) score *= (1.0 + (appearanceCount - 1) * 0.25);

                result.Scores[candidate.PlayniteId] = score;
            }

            return result;
        }

        /// <summary>
        /// Returns human-readable reasons for the graph score of a candidate.
        /// </summary>
        public List<string> GetReasons(
            EnrichedGame candidate,
            IEnumerable<EnrichedGame> topPlayed)
        {
            var reasons = new List<string>();
            if (string.IsNullOrWhiteSpace(candidate.SteamAppId)) return reasons;

            foreach (var played in topPlayed)
            {
                if (played.SteamSimilarAppIds.Contains(candidate.SteamAppId,
                    StringComparer.OrdinalIgnoreCase))
                {
                    reasons.Add($"Steam players of {played.Name} also play this");
                    if (reasons.Count >= 2) break;
                }
            }
            return reasons;
        }
    }
}
