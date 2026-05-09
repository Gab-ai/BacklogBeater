using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Playnite.SDK;
using Playnite.SDK.Data;

namespace GameRecommender
{
    /// <summary>
    /// Wraps two useful Steam API endpoints:
    ///  1. IStoreService/GetRecommendedTagsForUser — Steam's own tag profile for the user
    ///  2. store.steampowered.com/recommended/morelike — similar games to a given appid
    /// Both results are cached to disk with a 7-day TTL.
    /// </summary>
    public class SteamEnrichmentClient
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        private readonly DiskCache cache;
        private readonly string apiKey;
        private readonly string steamUserId;

        private static readonly TimeSpan Ttl = TimeSpan.FromDays(7);

        public SteamEnrichmentClient(DiskCache cache, string apiKey, string steamUserId)
        {
            this.cache = cache;
            this.apiKey = apiKey;
            this.steamUserId = steamUserId;
        }

        // ── User tag profile ─────────────────────────────────────────────

        /// <summary>
        /// Returns Steam's recommended tags for this user based on their play history.
        /// These tags are what Steam itself uses for discovery — high-quality signal.
        /// </summary>
        public async Task<List<string>> GetRecommendedTagsAsync()
        {
            const string cacheKey = "steam_recommended_tags";
            if (cache.TryGet<List<string>>(cacheKey, out var cached))
                return cached;

            if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(steamUserId))
                return new List<string>();

            try
            {
                var url = $"https://api.steampowered.com/IStoreService/GetRecommendedTagsForUser/v1/" +
                          $"?key={apiKey}&steamid={steamUserId}&country_code=US&language=english";

                var json = await http.GetStringAsync(url);
                var response = Serialization.FromJson<SteamTagsResponse>(json);

                var tags = response?.response?.store_items?
                    .SelectMany(i => i.tags ?? new List<SteamTag>())
                    .Select(t => t.name)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Distinct()
                    .Take(50)
                    .ToList() ?? new List<string>();

                cache.Set(cacheKey, tags, Ttl);
                logger.Info($"Steam: fetched {tags.Count} recommended tags for user");
                return tags;
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Steam GetRecommendedTagsForUser failed");
                return new List<string>();
            }
        }

        // ── Similar games per app ────────────────────────────────────────

        /// <summary>
        /// Returns up to 10 similar app IDs for a given Steam appId.
        /// Uses the store's "More Like This" endpoint — collaborative filtering data.
        /// </summary>
        public async Task<List<string>> GetSimilarAppIdsAsync(string appId)
        {
            if (string.IsNullOrWhiteSpace(appId)) return new List<string>();

            var cacheKey = $"steam_similar_{appId}";
            if (cache.TryGet<List<string>>(cacheKey, out var cached))
                return cached;

            try
            {
                // This endpoint returns an HTML fragment; we parse the appids from it
                var url = $"https://store.steampowered.com/recommended/morelike/{appId}/?l=english&cc=US";
                var html = await http.GetStringAsync(url);

                var ids = ParseAppIdsFromHtml(html, appId);
                cache.Set(cacheKey, ids, Ttl);
                return ids;
            }
            catch (Exception ex)
            {
                logger.Warn(ex, $"Steam similar games fetch failed for appId {appId}");
                return new List<string>();
            }
        }

        public async Task<SteamReviewSummary> GetReviewSummaryAsync(string appId)
        {
            if (string.IsNullOrWhiteSpace(appId)) return null;

            var cacheKey = $"steam_reviews_{appId}";
            if (cache.TryGet<SteamReviewSummary>(cacheKey, out var cached))
                return cached;

            try
            {
                var url = $"https://store.steampowered.com/appreviews/{appId}?json=1&language=all&purchase_type=all&filter=summary";
                var json = await http.GetStringAsync(url);
                var response = Serialization.FromJson<SteamReviewResponse>(json);
                var summary = response?.query_summary;
                if (summary == null) return null;

                var result = new SteamReviewSummary
                {
                    ReviewScoreDescription = summary.review_score_desc,
                    TotalPositive = summary.total_positive,
                    TotalNegative = summary.total_negative,
                    TotalReviews = summary.total_reviews,
                    PositivePercent = summary.total_reviews > 0
                        ? (int)Math.Round(summary.total_positive * 100.0 / summary.total_reviews)
                        : (int?)null
                };
                cache.Set(cacheKey, result, Ttl);
                return result;
            }
            catch (Exception ex)
            {
                logger.Warn(ex, $"Steam review fetch failed for appId {appId}");
                return null;
            }
        }

        private static List<string> ParseAppIdsFromHtml(string html, string excludeAppId)
        {
            var ids = new List<string>();
            // The endpoint returns data-ds-appid attributes
            int start = 0;
            const string marker = "data-ds-appid=\"";
            while (true)
            {
                int idx = html.IndexOf(marker, start, StringComparison.Ordinal);
                if (idx < 0) break;
                int valStart = idx + marker.Length;
                int valEnd = html.IndexOf("\"", valStart, StringComparison.Ordinal);
                if (valEnd < 0) break;
                var id = html.Substring(valStart, valEnd - valStart).Trim();
                if (!string.IsNullOrWhiteSpace(id) && id != excludeAppId && !ids.Contains(id))
                    ids.Add(id);
                start = valEnd + 1;
                if (ids.Count >= 12) break;
            }
            return ids;
        }

        // ── JSON models ──────────────────────────────────────────────────

        private class SteamTagsResponse
        {
            public SteamTagsResponseInner response { get; set; }
        }
        private class SteamTagsResponseInner
        {
            public List<SteamStoreItem> store_items { get; set; }
        }
        private class SteamStoreItem
        {
            public List<SteamTag> tags { get; set; }
        }
        private class SteamTag
        {
            public string name { get; set; }
        }

        private class SteamReviewResponse
        {
            public SteamReviewQuerySummary query_summary { get; set; }
        }

        private class SteamReviewQuerySummary
        {
            public string review_score_desc { get; set; }
            public int total_positive { get; set; }
            public int total_negative { get; set; }
            public int total_reviews { get; set; }
        }
    }

    public class SteamReviewSummary
    {
        public string ReviewScoreDescription { get; set; }
        public int TotalPositive { get; set; }
        public int TotalNegative { get; set; }
        public int TotalReviews { get; set; }
        public int? PositivePercent { get; set; }
    }
}
