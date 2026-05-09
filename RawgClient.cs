using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Playnite.SDK;
using Playnite.SDK.Data;

namespace GameRecommender
{
    public class RawgClient
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        private static readonly TimeSpan SuccessTtl = TimeSpan.FromDays(7);
        private static readonly TimeSpan EmptyTtl = TimeSpan.FromHours(12);
        private readonly DiskCache cache;
        private readonly string apiKey;

        public RawgClient(DiskCache cache, string apiKey)
        {
            this.cache = cache;
            this.apiKey = apiKey;
        }

        public bool IsConfigured => !string.IsNullOrWhiteSpace(apiKey);

        public async Task<RawgGameData> GetGameDataAsync(string title)
        {
            if (!IsConfigured || string.IsNullOrWhiteSpace(title))
                return null;

            var cacheKey = "rawg_game_v1_" + NormalizeTitle(title);
            if (cache.TryGet<RawgGameData>(cacheKey, out var cached))
                return cached;

            try
            {
                var url = "https://api.rawg.io/api/games" +
                          "?key=" + Uri.EscapeDataString(apiKey.Trim()) +
                          "&search=" + Uri.EscapeDataString(title.Trim()) +
                          "&search_precise=true&page_size=3";
                var json = await http.GetStringAsync(url);
                var search = Serialization.FromJson<RawgSearchResponse>(json);
                var match = (search?.results ?? new List<RawgGameRaw>())
                    .Where(g => g != null && g.id > 0 && !string.IsNullOrWhiteSpace(g.name))
                    .OrderBy(g => TitleDistance(NormalizeTitle(title), NormalizeTitle(g.name)))
                    .FirstOrDefault();

                if (match == null || TitleDistance(NormalizeTitle(title), NormalizeTitle(match.name)) > 6)
                {
                    cache.Set<RawgGameData>(cacheKey, null, EmptyTtl);
                    return null;
                }

                var detail = await GetGameDetailAsync(match.id);
                var data = ToGameData(detail ?? match);
                cache.Set(cacheKey, data, SuccessTtl);
                return data;
            }
            catch (Exception ex)
            {
                logger.Warn(ex, $"RAWG lookup failed for: {title}");
                cache.Set<RawgGameData>(cacheKey, null, EmptyTtl);
                return null;
            }
        }

        public async Task TestConnectionAsync()
        {
            var data = await GetGameDataAsync("Portal");
            if (data == null)
                throw new InvalidOperationException("RAWG configured, but no test result returned.");
        }

        private async Task<RawgGameRaw> GetGameDetailAsync(int id)
        {
            var cacheKey = "rawg_detail_v1_" + id;
            if (cache.TryGet<RawgGameRaw>(cacheKey, out var cached))
                return cached;

            try
            {
                var url = "https://api.rawg.io/api/games/" + id +
                          "?key=" + Uri.EscapeDataString(apiKey.Trim());
                var json = await http.GetStringAsync(url);
                var detail = Serialization.FromJson<RawgGameRaw>(json);
                cache.Set(cacheKey, detail, SuccessTtl);
                return detail;
            }
            catch (Exception ex)
            {
                logger.Warn(ex, $"RAWG detail lookup failed for id {id}");
                cache.Set<RawgGameRaw>(cacheKey, null, EmptyTtl);
                return null;
            }
        }

        private static RawgGameData ToGameData(RawgGameRaw raw)
        {
            if (raw == null)
                return null;

            return new RawgGameData
            {
                Id = raw.id,
                Name = raw.name ?? string.Empty,
                Description = StripHtml(raw.description_raw ?? raw.description ?? string.Empty),
                Released = ParseDate(raw.released),
                Rating = raw.rating > 0 ? raw.rating : (double?)null,
                RatingsCount = raw.ratings_count > 0 ? raw.ratings_count : (int?)null,
                Metacritic = raw.metacritic > 0 ? raw.metacritic : (int?)null,
                Genres = Names(raw.genres),
                Tags = Names(raw.tags).Where(t => !IsNoisyTag(t)).Take(12).ToList(),
                Stores = StoreNames(raw.stores),
                Website = raw.website ?? string.Empty,
                RawgUrl = string.IsNullOrWhiteSpace(raw.slug) ? string.Empty : "https://rawg.io/games/" + raw.slug
            };
        }

        private static List<string> Names(IEnumerable<RawgNamedItem> items)
            => (items ?? Enumerable.Empty<RawgNamedItem>())
                .Select(i => i?.name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        private static List<string> StoreNames(IEnumerable<RawgStoreWrapper> stores)
            => (stores ?? Enumerable.Empty<RawgStoreWrapper>())
                .Select(s => s?.store?.name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        private static bool IsNoisyTag(string value)
            => string.IsNullOrWhiteSpace(value) ||
               value.Length > 40 ||
               value.IndexOf("steam", StringComparison.OrdinalIgnoreCase) >= 0 ||
               value.IndexOf("achievements", StringComparison.OrdinalIgnoreCase) >= 0;

        private static DateTime? ParseDate(string value)
        {
            if (DateTime.TryParse(value, out var parsed))
                return parsed;
            return null;
        }

        private static string StripHtml(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;
            var text = Regex.Replace(value, "<[^>]+>", " ");
            return Regex.Replace(text, @"\s+", " ").Trim();
        }

        private static string NormalizeTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return string.Empty;
            var lower = title.ToLowerInvariant();
            lower = Regex.Replace(lower, @"\b(the|a|an|edition|standard|deluxe|ultimate|goty|game of the year|remastered|definitive|enhanced|complete|digital|collector'?s?|anniversary|gold|royal|director'?s?\s+cut|bundle|pack)\b", "");
            lower = Regex.Replace(lower, @"[^a-z0-9]+", "");
            return lower.Trim();
        }

        private static int TitleDistance(string a, string b)
        {
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return 0;
            if (string.IsNullOrEmpty(a)) return b?.Length ?? 0;
            if (string.IsNullOrEmpty(b)) return a.Length;
            if (a.Contains(b) || b.Contains(a)) return Math.Abs(a.Length - b.Length);
            return Math.Max(a.Length, b.Length);
        }

        private class RawgSearchResponse
        {
            public List<RawgGameRaw> results { get; set; }
        }

        private class RawgGameRaw
        {
            public int id { get; set; }
            public string slug { get; set; }
            public string name { get; set; }
            public string released { get; set; }
            public double rating { get; set; }
            public int ratings_count { get; set; }
            public int metacritic { get; set; }
            public string description { get; set; }
            public string description_raw { get; set; }
            public string website { get; set; }
            public List<RawgNamedItem> genres { get; set; }
            public List<RawgNamedItem> tags { get; set; }
            public List<RawgStoreWrapper> stores { get; set; }
        }

        private class RawgNamedItem
        {
            public string name { get; set; }
        }

        private class RawgStoreWrapper
        {
            public RawgNamedItem store { get; set; }
        }
    }

    public class RawgGameData
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public DateTime? Released { get; set; }
        public double? Rating { get; set; }
        public int? RatingsCount { get; set; }
        public int? Metacritic { get; set; }
        public List<string> Genres { get; set; } = new List<string>();
        public List<string> Tags { get; set; } = new List<string>();
        public List<string> Stores { get; set; } = new List<string>();
        public string Website { get; set; } = string.Empty;
        public string RawgUrl { get; set; } = string.Empty;
    }
}
