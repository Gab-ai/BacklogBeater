using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace GameRecommender
{
    /// <summary>
    /// Reads the Playnite game database and merges in Steam and IGDB data
    /// to produce a unified List&lt;EnrichedGame&gt; for the scoring engines.
    ///
    /// Enrichment is throttled (one IGDB call per 250ms) to respect rate limits.
    /// Full enrichment only runs on the top-200 most-played games plus all unplayed ones;
    /// games with minimal metadata get a best-effort enrichment.
    /// </summary>
    public class EnrichmentOrchestrator
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        private readonly IPlayniteAPI playnite;
        private readonly SteamEnrichmentClient steamClient;
        private readonly IgdbClient igdbClient;
        private readonly RawgClient rawgClient;
        private readonly RecommenderSettings settings;
        private readonly DiskCache cache;

        private const string EnrichedCacheKey = "enriched_games_v7_rawg";
        private static readonly TimeSpan EnrichedTtl = TimeSpan.FromHours(24);

        public EnrichmentOrchestrator(
            IPlayniteAPI playnite,
            SteamEnrichmentClient steamClient,
            IgdbClient igdbClient,
            RawgClient rawgClient,
            RecommenderSettings settings,
            DiskCache cache)
        {
            this.playnite = playnite;
            this.steamClient = steamClient;
            this.igdbClient = igdbClient;
            this.rawgClient = rawgClient;
            this.settings = settings;
            this.cache = cache;
        }

        // ── Public API ───────────────────────────────────────────────────

        /// <summary>
        /// Returns the enriched game list, using the 24h cache when available.
        /// Call InvalidateCache() to force a full refresh.
        /// </summary>
        public async Task<List<EnrichedGame>> GetEnrichedGamesAsync(
            IProgress<string> progress = null)
        {
            if (cache.TryGet<List<EnrichedGame>>(EnrichedCacheKey, out var cached))
            {
                var merged = MergeCachedWithCurrentLibrary(cached);
                logger.Info($"Enrichment: using cache ({merged.Count} current games)");
                LogSuspiciousEnrichedMetadata(merged, "cache merge");
                cache.Set(EnrichedCacheKey, merged, EnrichedTtl);
                return merged;
            }
            return await BuildEnrichedListAsync(progress);
        }

        public void InvalidateCache() => cache.Invalidate(EnrichedCacheKey);

        public async Task EnrichMissingInfoAsync(EnrichedGame game)
        {
            if (game == null || !settings.EnrichmentEnabled)
                return;

            await EnrichOneGameAsync(game, forceIgdbLookup: true);
            RecommendationHeuristics.ApplyAlgorithmicTags(game);
        }

        // ── Internal ─────────────────────────────────────────────────────

        private async Task<List<EnrichedGame>> BuildEnrichedListAsync(IProgress<string> progress)
        {
            progress?.Report("Reading Playnite library...");
            var allGames = playnite.Database.Games
                .Where(g => !g.Hidden)
                .ToList();

            logger.Info($"Enrichment: processing {allGames.Count} games");

            // Fetch Steam user tag profile once
            List<string> steamUserTags = new List<string>();
            if (settings.EnrichmentEnabled && !string.IsNullOrWhiteSpace(settings.SteamApiKey))
            {
                progress?.Report("Fetching Steam tag profile...");
                steamUserTags = await steamClient.GetRecommendedTagsAsync();
            }

            // Convert all games to base EnrichedGame records
            var enriched = AggregateMinecraftPlaytime(DeduplicateCopies(allGames.Select(g => ToBaseEnriched(g)))).ToList();

            if (!settings.EnrichmentEnabled)
            {
                foreach (var game in enriched)
                    RecommendationHeuristics.ApplyAlgorithmicTags(game);
                cache.Set(EnrichedCacheKey, enriched, EnrichedTtl);
                return enriched;
            }

            // Prioritise enrichment: deep-played games first, then all unplayed
            var toEnrich = enriched
                .OrderByDescending(g => g.PlaytimeSeconds)
                .Take(150)
                .Concat(enriched.Where(g => !g.IsPlayed))
                .Distinct()
                .ToList();

            int done = 0;
            foreach (var game in toEnrich)
            {
                progress?.Report($"Enriching {game.Name} ({++done}/{toEnrich.Count})...");
                await EnrichOneGameAsync(game);
                // Throttle to ~4 IGDB calls/sec
                await Task.Delay(250);
            }

            // Stamp Steam user tags onto all Steam games (signals user's overall taste)
            if (steamUserTags.Any())
            {
                foreach (var g in enriched.Where(g => g.SourcePlugin == "Steam"))
                {
                    foreach (var tag in steamUserTags)
                        if (!g.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                            g.SteamRecommendedTags.Add(tag);
                }
            }

            logger.Info($"Enrichment complete: {enriched.Count} games");
            foreach (var game in enriched)
                RecommendationHeuristics.ApplyAlgorithmicTags(game);
            LogSuspiciousEnrichedMetadata(enriched, "fresh rebuild");
            cache.Set(EnrichedCacheKey, enriched, EnrichedTtl);
            return enriched;
        }

        private static void LogSuspiciousEnrichedMetadata(IEnumerable<EnrichedGame> games, string source)
        {
            var flagged = (games ?? Enumerable.Empty<EnrichedGame>())
                .Where(RecommendationDiagnostics.HasSuspiciousMetadata)
                .Take(25)
                .ToList();

            if (!flagged.Any())
                return;

            logger.Warn($"Enrichment diagnostic: found {flagged.Count} games with suspicious metadata after {source}.");
            foreach (var game in flagged)
                logger.Warn($"Enrichment diagnostic: {game.Name}. {RecommendationDiagnostics.MetadataSummary(game)}");
        }

        private List<EnrichedGame> MergeCachedWithCurrentLibrary(List<EnrichedGame> cached)
        {
            var cachedById = (cached ?? new List<EnrichedGame>())
                .GroupBy(g => g.PlayniteId)
                .ToDictionary(g => g.Key, g => g.First());

            var current = DeduplicateCopies(playnite.Database.Games
                .Where(g => !g.Hidden)
                .Select(ToBaseEnriched))
                .ToList();

            foreach (var live in current)
            {
                if (!cachedById.TryGetValue(live.PlayniteId, out var old))
                    continue;

                live.Description = string.IsNullOrWhiteSpace(live.Description) ? old.Description : live.Description;
                live.Themes = MergeLists(live.Themes, old.Themes);
                live.Keywords = MergeLists(live.Keywords, old.Keywords);
                live.Genres = MergeLists(live.Genres, old.Genres);
                live.Features = MergeLists(live.Features, old.Features);
                live.SteamRecommendedTags = MergeLists(live.SteamRecommendedTags, old.SteamRecommendedTags);
                live.SteamSimilarAppIds = MergeLists(live.SteamSimilarAppIds, old.SteamSimilarAppIds);
                live.SteamReviewPercent = old.SteamReviewPercent;
                live.SteamReviewCount = old.SteamReviewCount;
                live.SteamReviewDescription = old.SteamReviewDescription;
                live.RawgId = old.RawgId;
                live.RawgRating = old.RawgRating;
                live.RawgRatingsCount = old.RawgRatingsCount;
                live.RawgMetacritic = old.RawgMetacritic;
                live.RawgReleased = old.RawgReleased;
                live.SteamStoreUrl = string.IsNullOrWhiteSpace(live.SteamStoreUrl) ? old.SteamStoreUrl : live.SteamStoreUrl;
                live.TrailerUrl = string.IsNullOrWhiteSpace(live.TrailerUrl) ? old.TrailerUrl : live.TrailerUrl;
                live.ExternalLinks = MergeExternalLinks(live.ExternalLinks, old.ExternalLinks);
                RecommendationHeuristics.ApplyAlgorithmicTags(live);
            }

            return AggregateMinecraftPlaytime(current);
        }

        private static List<EnrichedGame> AggregateMinecraftPlaytime(IEnumerable<EnrichedGame> games)
            => new MinecraftPlaytimeAggregator().Apply(games);

        private static List<string> MergeLists(IEnumerable<string> live, IEnumerable<string> cached)
        {
            var values = new List<string>();
            foreach (var value in (live ?? Enumerable.Empty<string>()).Concat(cached ?? Enumerable.Empty<string>()))
            {
                if (!string.IsNullOrWhiteSpace(value) && !values.Contains(value, StringComparer.OrdinalIgnoreCase))
                    values.Add(value);
            }
            return values;
        }

        private List<EnrichedGame> DeduplicateCopies(IEnumerable<EnrichedGame> games)
        {
            var result = new List<EnrichedGame>();
            foreach (var group in games
                .Where(g => g != null)
                .GroupBy(g => NormalizeTitleKey(g.Name))
                .Where(g => !string.IsNullOrWhiteSpace(g.Key)))
            {
                var copies = group.ToList();
                if (copies.Count == 1)
                {
                    result.Add(copies[0]);
                    continue;
                }

                var canonical = copies
                    .OrderByDescending(g => g.PlaytimeSeconds)
                    .ThenByDescending(g => g.LastPlayed ?? DateTime.MinValue)
                    .ThenByDescending(g => string.Equals(g.SourcePlugin, "Steam", StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(MetadataRichness)
                    .ThenBy(g => g.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(g => g.SourcePlugin ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .First();

                foreach (var copy in copies.Where(g => !ReferenceEquals(g, canonical)))
                    MergeDuplicateIntoCanonical(canonical, copy);

                logger.Info($"Deduped {copies.Count} copies of {canonical.Name}; keeping {canonical.SourcePlugin ?? "Unknown"} with {RecommendationEngine.FormatTime(canonical.PlaytimeSeconds)}");
                result.Add(canonical);
            }

            return result
                .OrderBy(g => g.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void MergeDuplicateIntoCanonical(EnrichedGame canonical, EnrichedGame copy)
        {
            canonical.Genres = MergeLists(canonical.Genres, copy.Genres);
            canonical.Tags = MergeLists(canonical.Tags, copy.Tags);
            canonical.Features = MergeLists(canonical.Features, copy.Features);
            canonical.Themes = MergeLists(canonical.Themes, copy.Themes);
            canonical.Keywords = MergeLists(canonical.Keywords, copy.Keywords);
            canonical.AlgorithmicTags = MergeLists(canonical.AlgorithmicTags, copy.AlgorithmicTags);
            canonical.SteamRecommendedTags = MergeLists(canonical.SteamRecommendedTags, copy.SteamRecommendedTags);
            canonical.SteamSimilarAppIds = MergeLists(canonical.SteamSimilarAppIds, copy.SteamSimilarAppIds);
            canonical.ExternalLinks = MergeExternalLinks(canonical.ExternalLinks, copy.ExternalLinks);

            if (string.IsNullOrWhiteSpace(canonical.Description))
                canonical.Description = copy.Description;
            if (string.IsNullOrWhiteSpace(canonical.SteamStoreUrl))
                canonical.SteamStoreUrl = copy.SteamStoreUrl;
            if (string.IsNullOrWhiteSpace(canonical.TrailerUrl))
                canonical.TrailerUrl = copy.TrailerUrl;
            if (!canonical.CommunityScore.HasValue)
                canonical.CommunityScore = copy.CommunityScore;
            if (!canonical.CriticScore.HasValue)
                canonical.CriticScore = copy.CriticScore;
            if (!canonical.SteamReviewPercent.HasValue)
                canonical.SteamReviewPercent = copy.SteamReviewPercent;
            if (!canonical.SteamReviewCount.HasValue)
                canonical.SteamReviewCount = copy.SteamReviewCount;
            if (string.IsNullOrWhiteSpace(canonical.SteamReviewDescription))
                canonical.SteamReviewDescription = copy.SteamReviewDescription;
            if (!canonical.RawgId.HasValue)
                canonical.RawgId = copy.RawgId;
            if (!canonical.RawgRating.HasValue)
                canonical.RawgRating = copy.RawgRating;
            if (!canonical.RawgRatingsCount.HasValue)
                canonical.RawgRatingsCount = copy.RawgRatingsCount;
            if (!canonical.RawgMetacritic.HasValue)
                canonical.RawgMetacritic = copy.RawgMetacritic;
            if (!canonical.RawgReleased.HasValue)
                canonical.RawgReleased = copy.RawgReleased;

            RecommendationHeuristics.ApplyAlgorithmicTags(canonical);
        }

        private static int MetadataRichness(EnrichedGame game)
        {
            if (game == null) return 0;
            return (string.IsNullOrWhiteSpace(game.Description) ? 0 : 2) +
                   (game.Genres?.Count ?? 0) +
                   (game.Tags?.Count ?? 0) +
                   (game.Features?.Count ?? 0) +
                   (game.Themes?.Count ?? 0) +
                   (game.Keywords?.Count ?? 0) +
                   (game.AlgorithmicTags?.Count ?? 0) +
                   (game.ExternalLinks?.Count ?? 0) +
                   (string.IsNullOrWhiteSpace(game.TrailerUrl) ? 0 : 1);
        }

        private static string NormalizeTitleKey(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return string.Empty;
            var value = title.ToLowerInvariant();
            value = Regex.Replace(value, @"\b(the|a|an)\b", " ");
            value = Regex.Replace(value, @"\b(standard|deluxe|ultimate|complete|definitive|enhanced|remastered|goty|game of the year|edition|bundle|collection)\b", " ");
            value = Regex.Replace(value, @"\b(steam|epic|gog|xbox|microsoft store|windows|pc)\b", " ");
            value = Regex.Replace(value, @"[\(\)\[\]\{\}:;,\.\-_'""!?\|/\\+&]+", " ");
            value = Regex.Replace(value, @"\s+", " ").Trim();
            return value;
        }

        private async Task EnrichOneGameAsync(EnrichedGame game, bool forceIgdbLookup = false)
        {
            try
            {
                if (game.IsModpack)
                {
                    ApplyModpackFallbackMetadata(game, cache: cache);
                    if (string.IsNullOrWhiteSpace(game.SteamAppId))
                    {
                        logger.Info($"Enrichment: modpack local fallback complete for {game.Name}");
                        return;
                    }
                }

                // Steam: similar games (only for Steam titles with appid)
                if (!string.IsNullOrWhiteSpace(game.SteamAppId))
                {
                    game.SteamStoreUrl = BuildSteamStoreUrl(game.SteamAppId);
                    AddExternalLink(game, "Steam store", game.SteamStoreUrl, "steam");

                    var similar = await steamClient.GetSimilarAppIdsAsync(game.SteamAppId);
                    game.SteamSimilarAppIds = similar;
                    var reviews = await steamClient.GetReviewSummaryAsync(game.SteamAppId);
                    if (reviews != null)
                    {
                        game.SteamReviewPercent = reviews.PositivePercent;
                        game.SteamReviewCount = reviews.TotalReviews;
                        game.SteamReviewDescription = reviews.ReviewScoreDescription;
                    }
                }

                // IGDB: description + themes + modes
                if (igdbClient.IsConfigured && (forceIgdbLookup || NeedsIgdbLinkData(game) || string.IsNullOrWhiteSpace(game.Description)))
                {
                    var igdb = await igdbClient.GetGameDataAsync(game.Name);
                    if (igdb != null)
                        MergeIgdbData(game, igdb);
                }

                if (rawgClient?.IsConfigured == true && NeedsRawgData(game))
                {
                    var rawg = await rawgClient.GetGameDataAsync(game.Name);
                    if (rawg != null)
                        MergeRawgData(game, rawg);
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, $"Enrichment failed for {game.Name}");
                if (game.IsModpack)
                    ApplyModpackFallbackMetadata(game, cache: cache);
            }
        }

        // ── Playnite → EnrichedGame ──────────────────────────────────────

        private EnrichedGame ToBaseEnriched(Game g)
        {
            var eg = new EnrichedGame
            {
                PlayniteId = g.Id,
                Name = g.Name ?? string.Empty,
                PlaytimeSeconds = (long)(g.Playtime),
                LastPlayed = g.LastActivity,
                Description = StripHtml(g.Description ?? string.Empty),
                CommunityScore = g.CommunityScore,
                CriticScore = g.CriticScore,
            };

            // Source plugin name
            if (g.Source != null)
                eg.SourcePlugin = g.Source.Name;

            // Steam app ID — stored in GameId for Steam games
            if (eg.SourcePlugin == "Steam" && !string.IsNullOrWhiteSpace(g.GameId))
            {
                eg.SteamAppId = g.GameId;
                eg.SteamStoreUrl = BuildSteamStoreUrl(g.GameId);
                AddExternalLink(eg, "Steam store", eg.SteamStoreUrl, "steam");
            }

            // Genres
            if (g.Genres != null)
                eg.Genres = g.Genres.Select(x => x.Name).Where(n => n != null).ToList();

            // Tags
            if (g.Tags != null)
                eg.Tags = g.Tags.Select(x => x.Name).Where(n => n != null).ToList();

            eg.IsModpack = eg.Tags.Contains("Modpack", StringComparer.OrdinalIgnoreCase) ||
                           (g.Notes ?? string.Empty).IndexOf("IsModpack=true", StringComparison.OrdinalIgnoreCase) >= 0;
            if (eg.IsModpack &&
                (eg.Tags.Contains("Minecraft", StringComparer.OrdinalIgnoreCase) ||
                 eg.Tags.Contains("Base:Minecraft", StringComparer.OrdinalIgnoreCase) ||
                 (g.Notes ?? string.Empty).IndexOf("BaseGameName=Minecraft", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                eg.BaseGameName = "Minecraft";
            }

            // Features
            if (g.Features != null)
                eg.Features = g.Features.Select(x => x.Name).Where(n => n != null).ToList();

            if (eg.IsModpack)
                ApplyModpackFallbackMetadata(eg, g.Notes, cache);

            RecommendationHeuristics.ApplyAlgorithmicTags(eg);
            return eg;
        }

        private static void ApplyModpackFallbackMetadata(EnrichedGame game, string notes = null, DiskCache cache = null)
        {
            if (game == null)
                return;

            AddDistinct(game.Tags, "Modpack");
            AddDistinct(game.Tags, "Minecraft");
            AddDistinct(game.Tags, "Base:Minecraft");
            game.BaseGameName = string.IsNullOrWhiteSpace(game.BaseGameName) ? "Minecraft" : game.BaseGameName;

            notes = notes ?? string.Empty;
            var minecraftVersion = ExtractModpackNoteValue(notes, "MinecraftVersion");
            var loader = ExtractModpackNoteValue(notes, "Loader");
            var provider = ExtractModpackNoteValue(notes, "Provider");
            if (!string.IsNullOrWhiteSpace(minecraftVersion))
                AddDistinct(game.Tags, "Minecraft " + minecraftVersion);
            if (!string.IsNullOrWhiteSpace(loader))
                AddDistinct(game.Features, loader);
            if (!string.IsNullOrWhiteSpace(provider))
                AddDistinct(game.Features, provider);

            if (string.IsNullOrWhiteSpace(game.Description))
            {
                var details = new List<string> { "Minecraft modpack" };
                if (!string.IsNullOrWhiteSpace(loader))
                    details.Add(loader);
                if (!string.IsNullOrWhiteSpace(minecraftVersion))
                    details.Add("Minecraft " + minecraftVersion);
                game.Description = string.Join(" | ", details);
            }

            RecommendationHeuristics.ApplyAlgorithmicTags(game);
        }

        internal static void MergeRawgData(EnrichedGame game, RawgGameData rawg)
        {
            if (game == null || rawg == null)
                return;

            game.RawgId = rawg.Id > 0 ? rawg.Id : game.RawgId;
            game.RawgRating = rawg.Rating ?? game.RawgRating;
            game.RawgRatingsCount = rawg.RatingsCount ?? game.RawgRatingsCount;
            game.RawgMetacritic = rawg.Metacritic ?? game.RawgMetacritic;
            game.RawgReleased = rawg.Released ?? game.RawgReleased;

            if (!game.CriticScore.HasValue && rawg.Metacritic.HasValue)
                game.CriticScore = rawg.Metacritic;
            if (!game.CommunityScore.HasValue && rawg.Rating.HasValue && rawg.RatingsCount.GetValueOrDefault() >= 20)
                game.CommunityScore = (int)Math.Round(Math.Max(0, Math.Min(5, rawg.Rating.Value)) * 20);

            if (string.IsNullOrWhiteSpace(game.Description) && !string.IsNullOrWhiteSpace(rawg.Description))
                game.Description = rawg.Description;

            foreach (var genre in rawg.Genres ?? new List<string>())
                AddDistinct(game.Genres, genre);
            foreach (var tag in rawg.Tags ?? new List<string>())
                AddDistinct(game.Keywords, tag);

            AddExternalLink(game, "RAWG", rawg.RawgUrl, "rawg");
            AddExternalLink(game, "Official website", rawg.Website, "official");
            RecommendationHeuristics.ApplyAlgorithmicTags(game);
        }

        private static bool NeedsRawgData(EnrichedGame game)
        {
            if (game == null)
                return false;
            return string.IsNullOrWhiteSpace(game.Description) ||
                   !game.CommunityScore.HasValue ||
                   !game.CriticScore.HasValue ||
                   (game.Genres?.Count ?? 0) == 0 ||
                   (game.Keywords?.Count ?? 0) < 3;
        }

        private static string ExtractModpackNoteValue(string notes, string key)
        {
            if (string.IsNullOrWhiteSpace(notes) || string.IsNullOrWhiteSpace(key))
                return string.Empty;

            var match = Regex.Match(notes, @"(?im)^\s*" + Regex.Escape(key) + @"\s*=\s*(.+?)\s*$");
            return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
        }

        private static void AddDistinct(List<string> values, string value)
        {
            if (values == null || string.IsNullOrWhiteSpace(value))
                return;
            if (!values.Contains(value, StringComparer.OrdinalIgnoreCase))
                values.Add(value);
        }

        private static void MergeIgdbData(EnrichedGame game, IgdbGameData igdb)
        {
            if (!string.IsNullOrWhiteSpace(igdb.Summary) && string.IsNullOrWhiteSpace(game.Description))
                game.Description = igdb.Summary;

            foreach (var genre in igdb.Genres)
                if (!game.Genres.Contains(genre, StringComparer.OrdinalIgnoreCase))
                    game.Genres.Add(genre);

            foreach (var theme in igdb.Themes)
                if (!game.Themes.Contains(theme, StringComparer.OrdinalIgnoreCase))
                    game.Themes.Add(theme);

            foreach (var keyword in igdb.Keywords)
                if (!game.Keywords.Contains(keyword, StringComparer.OrdinalIgnoreCase))
                    game.Keywords.Add(keyword);

            foreach (var mode in igdb.GameModes)
                if (!game.Features.Contains(mode, StringComparer.OrdinalIgnoreCase))
                    game.Features.Add(mode);

            foreach (var website in igdb.Websites ?? Enumerable.Empty<IgdbWebsiteData>())
            {
                var label = WebsiteLabelFor(website.Category);
                var kind = WebsiteKindFor(website.Category);
                AddExternalLink(game, label, website.Url, kind);
                if (website.Category == 13 && string.IsNullOrWhiteSpace(game.SteamStoreUrl))
                    game.SteamStoreUrl = NormalizeUrl(website.Url);
            }

            if (string.IsNullOrWhiteSpace(game.TrailerUrl) && !string.IsNullOrWhiteSpace(igdb.FirstTrailerUrl))
                game.TrailerUrl = NormalizeUrl(igdb.FirstTrailerUrl);
            AddExternalLink(game, "Trailer", game.TrailerUrl, "trailer");

            RecommendationHeuristics.ApplyAlgorithmicTags(game);
        }

        private static bool NeedsIgdbLinkData(EnrichedGame game)
            => game != null &&
               (string.IsNullOrWhiteSpace(game.TrailerUrl) ||
                game.ExternalLinks == null ||
                !game.ExternalLinks.Any(l => !string.IsNullOrWhiteSpace(l?.Url)));

        private static string BuildSteamStoreUrl(string appId)
            => string.IsNullOrWhiteSpace(appId) ? string.Empty : $"https://store.steampowered.com/app/{appId.Trim()}/";

        private static List<GameExternalLink> MergeExternalLinks(IEnumerable<GameExternalLink> first, IEnumerable<GameExternalLink> second)
        {
            var merged = new List<GameExternalLink>();
            foreach (var link in (first ?? Enumerable.Empty<GameExternalLink>()).Concat(second ?? Enumerable.Empty<GameExternalLink>()))
                AddExternalLink(merged, link?.Label, link?.Url, link?.Kind);
            return merged;
        }

        private static void AddExternalLink(EnrichedGame game, string label, string url, string kind)
        {
            if (game == null)
                return;
            if (game.ExternalLinks == null)
                game.ExternalLinks = new List<GameExternalLink>();
            AddExternalLink(game.ExternalLinks, label, url, kind);
        }

        private static void AddExternalLink(List<GameExternalLink> links, string label, string url, string kind)
        {
            if (links == null)
                return;

            var normalizedUrl = NormalizeUrl(url);
            if (string.IsNullOrWhiteSpace(normalizedUrl))
                return;

            if (links.Any(l => string.Equals(NormalizeUrl(l?.Url), normalizedUrl, StringComparison.OrdinalIgnoreCase)))
                return;

            links.Add(new GameExternalLink
            {
                Label = string.IsNullOrWhiteSpace(label) ? "Link" : label.Trim(),
                Url = normalizedUrl,
                Kind = string.IsNullOrWhiteSpace(kind) ? "link" : kind.Trim()
            });
        }

        private static string NormalizeUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return string.Empty;
            var trimmed = url.Trim();
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
                return string.Empty;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                return string.Empty;
            return uri.AbsoluteUri;
        }

        private static string WebsiteLabelFor(int category)
        {
            switch (category)
            {
                case 1: return "Official website";
                case 2: return "Wiki";
                case 3: return "Wikipedia";
                case 4: return "Facebook";
                case 5: return "Twitter/X";
                case 6: return "Twitch";
                case 8: return "Instagram";
                case 9: return "YouTube";
                case 10: return "iPhone";
                case 11: return "iPad";
                case 12: return "Android";
                case 13: return "Steam store";
                case 14: return "Reddit";
                case 15: return "itch.io";
                case 16: return "Epic Games Store";
                case 17: return "GOG";
                case 18: return "Discord";
                default: return "External link";
            }
        }

        private static string WebsiteKindFor(int category)
        {
            switch (category)
            {
                case 1: return "official";
                case 2: return "wiki";
                case 3: return "wikipedia";
                case 4: return "facebook";
                case 5: return "twitter";
                case 6: return "twitch";
                case 8: return "instagram";
                case 9: return "youtube";
                case 10: return "iphone";
                case 11: return "ipad";
                case 12: return "android";
                case 13: return "steam";
                case 14: return "reddit";
                case 15: return "itch";
                case 16: return "epic";
                case 17: return "gog";
                case 18: return "discord";
                default: return "link";
            }
        }

        private static string StripHtml(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return string.Empty;
            // Simple tag stripper — no regex dependency
            var sb = new System.Text.StringBuilder();
            bool inTag = false;
            foreach (char c in html)
            {
                if (c == '<') { inTag = true; continue; }
                if (c == '>') { inTag = false; continue; }
                if (!inTag) sb.Append(c);
            }
            return sb.ToString().Trim();
        }
    }
}
