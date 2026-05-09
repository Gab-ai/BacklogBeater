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
    public class GameNewsClient
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        private readonly DiskCache cache;
        private static readonly TimeSpan Ttl = TimeSpan.FromHours(6);

        public GameNewsClient(DiskCache cache)
        {
            this.cache = cache;
        }

        public async Task<List<SpotlightItem>> GetSpotlightsAsync(IEnumerable<EnrichedGame> games, TasteProfile profile)
        {
            var gameList = games?
                .Where(g => g != null && !string.IsNullOrWhiteSpace(g.SteamAppId))
                .ToList() ?? new List<EnrichedGame>();
            var cacheKey = "owned_spotlight_steam_news_v3_en_" + Math.Abs(string.Join("|", gameList.Select(g => g.SteamAppId).Take(80)).GetHashCode());
            if (cache.TryGet<List<SpotlightItem>>(cacheKey, out var cached))
                return cached;

            var items = await FetchSteamNewsAsync(gameList);

            var result = items
                .Where(i => !string.IsNullOrWhiteSpace(i.Title))
                .Where(i => !string.IsNullOrWhiteSpace(i.RelatedGame))
                .Where(i => IsEnglishLike(i.Title + " " + i.Summary))
                .Where(i => IsEnglishSource(i.Source))
                .GroupBy(i => Normalize(i.Url ?? i.Title), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderByDescending(i => i.PublishedAt)
                .Take(12)
                .ToList();

            cache.Set(cacheKey, result, Ttl);
            return result;
        }

        private async Task<List<SpotlightItem>> FetchSteamNewsAsync(List<EnrichedGame> games)
        {
            var items = new List<SpotlightItem>();
            var steamGames = games
                .Where(g => !string.IsNullOrWhiteSpace(g.SteamAppId))
                .OrderByDescending(g => g.LastPlayed ?? DateTime.MinValue)
                .ThenByDescending(g => g.PlaytimeSeconds)
                .Take(20)
                .ToList();

            foreach (var game in steamGames)
            {
                try
                {
                    var url = $"https://api.steampowered.com/ISteamNews/GetNewsForApp/v2/?appid={Uri.EscapeDataString(game.SteamAppId)}&count=3&maxlength=700&format=json&language=english";
                    var json = await http.GetStringAsync(url);
                    var parsed = Serialization.FromJson<SteamNewsResponse>(json);
                    foreach (var raw in parsed?.appnews?.newsitems ?? new List<SteamNewsItem>())
                    {
                        var date = raw.date > 0 ? UnixToDate(raw.date) : DateTime.MinValue;
                        if (date != DateTime.MinValue && (DateTime.UtcNow - date).TotalDays > 45) continue;
                        items.Add(new SpotlightItem
                        {
                            Title = Trim(CleanSteamText(raw.title), 90),
                            Summary = Trim(CleanSteamText(raw.contents), 180),
                            Source = EnglishSourceLabel(raw),
                            Url = raw.url,
                            RelatedGame = game.Name,
                            Reason = $"Owned game update: {game.Name}",
                            PublishedAt = date
                        });
                    }
                }
                catch (Exception ex)
                {
                    logger.Warn(ex, $"Steam news fetch failed for {game.Name}");
                }
            }
            return items;
        }

        private static DateTime UnixToDate(long seconds)
            => DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;

        private static string Strip(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var noHtml = Regex.Replace(value, "<.*?>", " ");
            return Regex.Replace(System.Net.WebUtility.HtmlDecode(noHtml), "\\s+", " ").Trim();
        }

        private static string Trim(string value, int max)
            => string.IsNullOrWhiteSpace(value) || value.Length <= max ? value : value.Substring(0, max).Trim() + "...";

        private static string CleanSteamText(string value)
        {
            var text = Strip(value);
            text = Regex.Replace(text, @"\[[^\]]+\]", " ");
            text = Regex.Replace(text, @"\bhttps?://\S+", " ");
            text = Regex.Replace(text, @"\b(store|steam|community)\.steampowered\.com/\S+", " ");
            text = Regex.Replace(text, @"\s+", " ").Trim();
            return text;
        }

        private static bool IsEnglishLike(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var letters = value.Where(char.IsLetter).ToList();
            if (letters.Count < 12) return true;
            var latin = letters.Count(c => c <= 0x024F);
            return latin / (double)letters.Count >= 0.85;
        }

        private static string EnglishSourceLabel(SteamNewsItem raw)
        {
            var source = CleanSteamText(raw?.feedlabel);
            if (string.IsNullOrWhiteSpace(source))
                source = CleanSteamText(raw?.feedname);
            return IsEnglishSource(source) ? source : "Steam News";
        }

        private static bool IsEnglishSource(string source)
        {
            if (string.IsNullOrWhiteSpace(source)) return true;
            var s = source.ToLowerInvariant();
            if (s.Contains("japanese") || s.Contains("korean") || s.Contains("chinese") ||
                s.Contains("russian") || s.Contains("français") || s.Contains("deutsch") ||
                s.Contains("español") || s.Contains("português") || s.Contains("polski") ||
                s.Contains("türkçe") || s.Contains("ไทย") || s.Contains("中文") ||
                s.Contains("日本") || s.Contains("한국") || s.Contains("рус"))
                return false;
            return IsEnglishLike(source);
        }

        private static string Normalize(string value)
            => Regex.Replace((value ?? string.Empty).ToLowerInvariant(), "[^a-z0-9]+", "");

        private class SteamNewsResponse
        {
            public SteamAppNews appnews { get; set; }
        }

        private class SteamAppNews
        {
            public List<SteamNewsItem> newsitems { get; set; }
        }

        private class SteamNewsItem
        {
            public string title { get; set; }
            public string contents { get; set; }
            public string url { get; set; }
            public string feedlabel { get; set; }
            public string feedname { get; set; }
            public long date { get; set; }
        }
    }
}
