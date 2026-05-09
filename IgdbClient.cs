using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using Playnite.SDK.Data;

namespace GameRecommender
{
    /// <summary>
    /// Wraps the IGDB v4 API (via Twitch auth) to fetch:
    ///   - Game summary / storyline
    ///   - Themes  (Action, Horror, Open World, etc.)
    ///   - Game modes (Single player, Co-op, Multiplayer)
    ///   - Similar games (IGDB's own "similar_games" field)
    ///
    /// Token is refreshed automatically when expired (Twitch tokens last ~60 days).
    /// All results are cached 7 days.
    /// </summary>
    public class IgdbClient
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        private readonly DiskCache cache;
        private readonly string clientId;
        private readonly string clientSecret;

        private string accessToken;
        private DateTime tokenExpiry = DateTime.MinValue;
        private readonly SemaphoreSlim tokenLock = new SemaphoreSlim(1, 1);

        private static readonly TimeSpan GameTtl = TimeSpan.FromDays(7);

        public IgdbClient(DiskCache cache, string clientId, string clientSecret)
        {
            this.cache = cache;
            this.clientId = clientId;
            this.clientSecret = clientSecret;
        }

        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(clientId) && !string.IsNullOrWhiteSpace(clientSecret);

        // ── Token management ─────────────────────────────────────────────

        private async Task EnsureTokenAsync()
        {
            if (!string.IsNullOrWhiteSpace(accessToken) && DateTime.UtcNow < tokenExpiry)
                return;

            await tokenLock.WaitAsync();
            try
            {
                if (!string.IsNullOrWhiteSpace(accessToken) && DateTime.UtcNow < tokenExpiry)
                    return;

                // Check disk cache first
                var tokenCacheKey = $"igdb_token_{clientId}";
                if (cache.TryGet<IgdbTokenCache>(tokenCacheKey, out var cached))
                {
                    accessToken = cached.AccessToken;
                    tokenExpiry = cached.Expiry;
                    return;
                }

                var url = $"https://id.twitch.tv/oauth2/token" +
                          $"?client_id={clientId}&client_secret={clientSecret}&grant_type=client_credentials";

                var resp = await http.PostAsync(url, new StringContent(string.Empty));
                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadAsStringAsync();
                var token = Serialization.FromJson<TwitchTokenResponse>(json);

                accessToken = token.access_token;
                tokenExpiry = DateTime.UtcNow.AddSeconds(token.expires_in - 3600);

                cache.Set(tokenCacheKey, new IgdbTokenCache
                {
                    AccessToken = accessToken,
                    Expiry = tokenExpiry
                }, TimeSpan.FromSeconds(token.expires_in - 3600));

                logger.Info("IGDB: obtained new Twitch access token");
            }
            finally
            {
                tokenLock.Release();
            }
        }

        // ── Game lookup ──────────────────────────────────────────────────

        /// <summary>
        /// Fetches IGDB metadata for a game by name.
        /// Returns null if not found or IGDB is not configured.
        /// </summary>
        public async Task<IgdbGameData> GetGameDataAsync(string gameName)
        {
            if (!IsConfigured || string.IsNullOrWhiteSpace(gameName)) return null;

            var cacheKey = $"igdb_game_v2_{gameName.ToLowerInvariant()}";
            if (cache.TryGet<IgdbGameData>(cacheKey, out var cached))
                return cached;

            try
            {
                await EnsureTokenAsync();

                // Escape single quotes in name
                var safeName = gameName.Replace("'", "\\'").Replace("\"", "\\\"");

                var body = $"fields name,summary,genres.name,themes.name,keywords.name,game_modes.name,similar_games.name,websites.category,websites.url,videos.video_id,videos.name; " +
                           $"search \"{safeName}\"; limit 1;";

                var req = new HttpRequestMessage(HttpMethod.Post, "https://api.igdb.com/v4/games");
                req.Headers.Add("Client-ID", clientId);
                req.Headers.Add("Authorization", $"Bearer {accessToken}");
                req.Content = new StringContent(body, Encoding.UTF8, "text/plain");

                var resp = await http.SendAsync(req);
                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadAsStringAsync();
                var results = Serialization.FromJson<List<IgdbGameRaw>>(json);

                if (results == null || results.Count == 0)
                {
                    cache.Set(cacheKey, (IgdbGameData)null, GameTtl);
                    return null;
                }

                var raw = results[0];
                var data = new IgdbGameData
                {
                    Name = raw.name,
                    Summary = raw.summary ?? string.Empty,
                    Genres = raw.genres?.Select(t => t.name).Where(n => n != null).ToList() ?? new List<string>(),
                    Themes = raw.themes?.Select(t => t.name).Where(n => n != null).ToList() ?? new List<string>(),
                    Keywords = raw.keywords?.Select(t => t.name).Where(n => n != null).ToList() ?? new List<string>(),
                    GameModes = raw.game_modes?.Select(m => m.name).Where(n => n != null).ToList() ?? new List<string>(),
                    SimilarGameNames = raw.similar_games?.Select(g => g.name).Where(n => n != null).ToList() ?? new List<string>(),
                    Websites = raw.websites?.Select(w => new IgdbWebsiteData
                    {
                        Category = w.category,
                        Url = w.url ?? string.Empty
                    }).Where(w => !string.IsNullOrWhiteSpace(w.Url)).ToList() ?? new List<IgdbWebsiteData>(),
                    Videos = raw.videos?.Select(v => new IgdbVideoData
                    {
                        Name = v.name ?? string.Empty,
                        VideoId = v.video_id ?? string.Empty
                    }).Where(v => !string.IsNullOrWhiteSpace(v.VideoId)).ToList() ?? new List<IgdbVideoData>(),
                };

                cache.Set(cacheKey, data, GameTtl);
                return data;
            }
            catch (Exception ex)
            {
                logger.Warn(ex, $"IGDB lookup failed for: {gameName}");
                return null;
            }
        }

        // ── Models ───────────────────────────────────────────────────────

        private class IgdbTokenCache
        {
            public string AccessToken { get; set; }
            public DateTime Expiry { get; set; }
        }

        private class TwitchTokenResponse
        {
            public string access_token { get; set; }
            public int expires_in { get; set; }
        }

        private class IgdbGameRaw
        {
            public string name { get; set; }
            public string summary { get; set; }
            public List<IgdbNamedEntity> genres { get; set; }
            public List<IgdbNamedEntity> themes { get; set; }
            public List<IgdbNamedEntity> keywords { get; set; }
            public List<IgdbNamedEntity> game_modes { get; set; }
            public List<IgdbNamedEntity> similar_games { get; set; }
            public List<IgdbWebsiteRaw> websites { get; set; }
            public List<IgdbVideoRaw> videos { get; set; }
        }

        private class IgdbNamedEntity
        {
            public string name { get; set; }
        }

        private class IgdbWebsiteRaw
        {
            public int category { get; set; }
            public string url { get; set; }
        }

        private class IgdbVideoRaw
        {
            public string name { get; set; }
            public string video_id { get; set; }
        }
    }

    public class IgdbGameData
    {
        public string Name { get; set; }
        public string Summary { get; set; }
        public List<string> Genres { get; set; } = new List<string>();
        public List<string> Themes { get; set; } = new List<string>();
        public List<string> Keywords { get; set; } = new List<string>();
        public List<string> GameModes { get; set; } = new List<string>();
        public List<string> SimilarGameNames { get; set; } = new List<string>();
        public List<IgdbWebsiteData> Websites { get; set; } = new List<IgdbWebsiteData>();
        public List<IgdbVideoData> Videos { get; set; } = new List<IgdbVideoData>();
        public string FirstTrailerUrl => Videos?.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v.VideoId))?.YoutubeUrl;
    }

    public class IgdbWebsiteData
    {
        public int Category { get; set; }
        public string Url { get; set; } = string.Empty;
    }

    public class IgdbVideoData
    {
        public string Name { get; set; } = string.Empty;
        public string VideoId { get; set; } = string.Empty;
        public string YoutubeUrl => string.IsNullOrWhiteSpace(VideoId)
            ? string.Empty
            : "https://www.youtube.com/watch?v=" + VideoId.Trim();
    }
}
