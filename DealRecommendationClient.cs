using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using Playnite.SDK.Data;

namespace GameRecommender
{
    public class DealRecommendationClient
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        private readonly DiskCache cache;
        private readonly RecommenderSettings settings;
        private readonly RawgClient rawgClient;
        private static readonly TimeSpan Ttl = TimeSpan.FromHours(4);
        private const int MaxProfileConcurrency = 4;
        private const int MaxProfileCandidates = 80;
        private const int MaxQualityConcurrency = 4;
        private const int MaxQualityCandidates = 80;
        private const int MaxNotOwnedSimilarSourceGames = 24;
        private const int MaxNotOwnedSimilarCandidates = 120;
        private const int MinNotOwnedPrimaryCandidatesBeforeDealFallback = 30;
        private const int MaxNotOwnedFallbackDeals = 20;
        private const long MinimumSteamSimilarSourcePlaytimeSeconds = 18000;
        private const double StrongFitThreshold = 1.15;
        private const double ThinReviewFitThreshold = 1.6;
        private static readonly TimeSpan StaleEarlyAccessAge = TimeSpan.FromDays(365);
        private const string JastSaleUrl = "https://jaststore.com/sale?attributes=&catalog=&priceMax=0&priceMin=0&releaseStatus=all&sale=true&sort=featured";
        private const string MangaGamerSaleUrl = "https://mangagamer.org/winter-sale/";

        private static readonly string[] JunkTitleTerms =
        {
            "demo", "playtest", "prologue", "soundtrack", "ost", "artbook", "wallpaper",
            "season pass", "battle pass", "expansion pass", "starter pack", "founder pack",
            "currency", "coins", "gems", "crystals", "points", "skins", "costume", "cosmetic",
            "dlc", "add-on", "addon", "bonus content", "upgrade pack", "beta", "avatar",
            "profile background", "emoticon", "digital deluxe upgrade", "credits pack",
            "token pack", "booster pack", "subscription", "server rental"
        };

        private static readonly string[] DemoTitleTerms =
        {
            "demo", "playtest", "prologue", "beta", "trial"
        };

        private static readonly string[] AddOnTitleTerms =
        {
            "dlc", "add-on", "addon", "expansion", "season pass", "battle pass",
            "starter pack", "founder pack", "upgrade pack", "bonus content"
        };

        private static readonly string[] SoundtrackCosmeticTitleTerms =
        {
            "soundtrack", "ost", "artbook", "wallpaper", "avatar", "profile background",
            "emoticon", "skin", "costume", "cosmetic", "currency", "coins", "gems",
            "crystals", "credits", "tokens", "points"
        };

        private static readonly string[] UtilityEvidenceTerms =
        {
            "utility", "utilities", "tool", "tools", "crosshair", "overlay", "benchmark",
            "launcher", "companion app", "server tool", "dedicated server", "soundboard",
            "sound pad", "trainer", "mod manager"
        };

        private static readonly string[] MediaEvidenceTerms =
        {
            "video player", "media player", "vr video", "movie player", "360 video",
            "stereoscopic video", "watch videos", "video playback", "film", "movie"
        };

        private static readonly string[] EditorEvidenceTerms =
        {
            "editor", "creator", "creation tool", "avatar editor", "avatar creator",
            "rpg maker", "game maker", "game engine", "development kit", "sdk",
            "level editor", "map editor", "asset creator", "model viewer", "architect"
        };

        private static readonly HashSet<string> ConcreteFitTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "crafting", "base building", "city builder", "colony sim", "resource management",
            "management", "tactical", "shooter", "fps", "military", "realistic", "horror",
            "survival horror", "psychological horror", "loot", "party-based", "souls-like",
            "character customization", "building", "survival crafting", "tactical shooting",
            "open-world exploration", "creative sandbox", "management", "systemic", "tense"
        };

        private static readonly HashSet<string> GenericTasteTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "action", "adventure", "casual", "indie", "single-player", "single player",
            "multiplayer", "co-op", "coop", "early access", "free to play", "rpg", "strategy",
            "simulation", "shooter", "open world", "sports", "racing", "arcade", "classic",
            "2d", "3d", "colorful", "funny", "family friendly", "controller", "online",
            "local multiplayer", "local co-op", "pvp", "pve", "atmospheric", "great soundtrack"
        };

        private static readonly HashSet<string> GenericSupportTerms = new HashSet<string>(GenericTasteTerms, StringComparer.OrdinalIgnoreCase)
        {
            "fantasy", "first-person", "first person", "third person", "survival", "sandbox",
            "simulator", "role-playing", "fps", "online co-op", "mmo", "adventure"
        };

        private static readonly string[] VisualNovelTasteTerms =
        {
            "visual novel", "vn", "anime", "otome", "dating sim", "romance",
            "story rich", "choices matter", "interactive fiction", "female protagonist",
            "lgbtq+", "mystery", "slice of life", "sexual content", "nudity"
        };

        private static readonly SignalRule[] TasteClusterRules =
        {
            new SignalRule("survival crafting sandbox", "Minecraft", "Ark: Survival Evolved", "Smalland: Survive the Wilds")
            {
                CoreTerms = new[] { "survival crafting", "crafting", "base building", "open world survival craft" },
                SupportTerms = new[] { "survival", "building", "open world", "sandbox", "exploration", "co-op" }
            },
            new SignalRule("tactical multiplayer shooter", "Rainbow Six Siege", "Ready or Not", "Battlefield 4")
            {
                CoreTerms = new[] { "tactical", "shooter", "fps", "military", "realistic" },
                SupportTerms = new[] { "first-person", "first person", "co-op", "multiplayer", "pvp", "online" }
            },
            new SignalRule("creative social sandbox", "VRChat", "Rec Room", "Dreams")
            {
                CoreTerms = new[] { "social", "creative", "user generated content", "creation", "vr" },
                SupportTerms = new[] { "sandbox", "building", "multiplayer", "co-op", "online" }
            },
            new SignalRule("building management strategy", "Mini Settlers", "Against the Storm", "Frostpunk")
            {
                CoreTerms = new[] { "city builder", "colony sim", "management", "resource management", "strategy" },
                SupportTerms = new[] { "simulation", "building", "base building", "sandbox" }
            },
            new SignalRule("open-world exploration", "Minecraft", "Ark: Survival Evolved", "Warframe")
            {
                CoreTerms = new[] { "exploration", "open world exploration", "adventure" },
                SupportTerms = new[] { "open world", "survival", "third person", "action rpg" }
            },
            new SignalRule("progression RPG", "Warframe", "The First Berserker: Khazan", "Pathfinder: Wrath of the Righteous")
            {
                CoreTerms = new[] { "progression", "character customization", "loot", "party-based", "souls-like" },
                SupportTerms = new[] { "rpg", "role-playing", "fantasy", "adventure" }
            },
            new SignalRule("horror survival", "Don't Knock Twice", "Amnesia: The Bunker", "Chernobylite")
            {
                CoreTerms = new[] { "horror", "survival horror", "psychological horror" },
                SupportTerms = new[] { "atmospheric", "tense", "survival", "resource management" }
            }
        };

        private static readonly SignalRule[] MechanicRules =
        {
            new SignalRule("building", "building", "base building", "city builder", "colony sim", "crafting"),
            new SignalRule("survival crafting", "survival", "crafting", "base building", "open world survival craft"),
            new SignalRule("tactical shooting", "tactical", "shooter", "fps", "military", "realistic"),
            new SignalRule("open-world exploration", "open world", "exploration", "adventure"),
            new SignalRule("social multiplayer", "social", "multiplayer", "co-op", "online co-op", "vr"),
            new SignalRule("progression", "rpg", "role-playing", "loot", "progression", "character customization"),
            new SignalRule("management", "management", "strategy", "resource management", "city builder", "colony sim"),
            new SignalRule("creative sandbox", "sandbox", "creative", "user generated content", "creation")
        };

        private static readonly SignalRule[] MoodRules =
        {
            new SignalRule("tense", "horror", "survival horror", "tactical", "realistic", "atmospheric"),
            new SignalRule("creative", "creative", "sandbox", "building", "creation", "user generated content"),
            new SignalRule("systemic", "simulation", "management", "strategy", "resource management", "colony sim"),
            new SignalRule("social", "social", "co-op", "multiplayer", "online co-op", "vr"),
            new SignalRule("grindy progression", "loot", "progression", "rpg", "mmo", "online")
        };

        public DealRecommendationClient(DiskCache cache, RecommenderSettings settings)
        {
            this.cache = cache;
            this.settings = settings;
            rawgClient = new RawgClient(cache, settings.RawgApiKey);
        }

        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(settings.ItadApiKey) ||
            !string.IsNullOrWhiteSpace(settings.SteamUserId) ||
            settings.EnableJastDeals ||
            settings.EnableMangaGamerDeals;

        public string LastDiagnosticsSummary { get; private set; } = string.Empty;

        public async Task<List<ExternalRecommendation>> GetRecommendationsAsync(
            IEnumerable<EnrichedGame> ownedGames,
            TasteProfile profile)
        {
            if (!IsConfigured)
                throw new InvalidOperationException("Configure at least one deal source in settings first.");

            var ownedList = ownedGames?.ToList() ?? new List<EnrichedGame>();
            var owned = BuildOwnedIndex(ownedList);

            var deals = await GetDealsAsync();
            var diagnostics = new DealDiagnostics { RawDeals = deals.Count };
            var candidates = new List<ExternalRecommendation>();
            foreach (var deal in deals)
            {
                if (!IsRealGameDeal(deal))
                {
                    diagnostics.FilteredJunk++;
                    continue;
                }
                if (!HasCurrentDealSignal(deal))
                {
                    diagnostics.FilteredNoCurrentDeal++;
                    continue;
                }
                if (IsOwnedExternal(deal, owned))
                {
                    diagnostics.FilteredOwned++;
                    continue;
                }
                if (IsRejectedDeal(deal))
                {
                    diagnostics.FilteredRejected++;
                    continue;
                }
                if (IsBlacklistedDeal(deal))
                {
                    diagnostics.FilteredBlacklisted++;
                    continue;
                }
                if (MatchesBlockedTag(deal.Title))
                {
                    diagnostics.FilteredBlockedTag++;
                    continue;
                }
                if (!PassesSourceRelevanceGate(deal, profile))
                {
                    diagnostics.FilteredSourceRelevance++;
                    continue;
                }
                candidates.Add(deal);
            }
            diagnostics.PrefilteredDeals = candidates.Count;

            var clusters = BuildTasteClusters(profile, ownedList);
            await ApplyCandidateProfilesBoundedAsync(candidates, clusters, "deals");
            diagnostics.ProfiledDeals = candidates.Count(d => HasCandidateProfile(d));

            var scoredCandidates = candidates
                .Select(deal => new QualityCandidate(deal, ScoreTasteFit(deal, profile, ownedList, clusters)))
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => QualityBoost(item.Deal))
                .Take(MaxQualityCandidates)
                .ToList();
            diagnostics.QualityCandidates = scoredCandidates.Count;

            await ApplyQualityBoundedAsync(scoredCandidates);
            foreach (var item in scoredCandidates)
                ApplyFinalScore(item.Deal, item.Score);

            var credibleCandidates = new List<ExternalRecommendation>();
            foreach (var deal in scoredCandidates.Select(item => item.Deal))
            {
                if (!IsRealGameDeal(deal))
                {
                    diagnostics.PostQualityJunk++;
                    continue;
                }
                var admission = GetAdmissionRejectionReason(deal, "deals");
                if (!string.IsNullOrWhiteSpace(admission))
                {
                    diagnostics.AddAdmissionRejection(admission);
                    continue;
                }
                if (ShouldFilterEarlyAccess(deal))
                {
                    diagnostics.PostQualityEarlyAccess++;
                    continue;
                }
                if (IsRejectedDeal(deal))
                {
                    diagnostics.PostQualityRejected++;
                    continue;
                }
                if (IsOwnedExternal(deal, owned))
                {
                    diagnostics.PostQualityOwned++;
                    continue;
                }
                if (IsBlacklistedDeal(deal))
                {
                    diagnostics.PostQualityBlacklisted++;
                    continue;
                }
                if (MatchesBlockedTag(deal.Reasons))
                {
                    diagnostics.PostQualityBlockedTag++;
                    continue;
                }
                var rejection = GetCredibilityRejectionReason(deal);
                if (!string.IsNullOrWhiteSpace(rejection))
                {
                    diagnostics.AddCredibilityRejection(rejection);
                    continue;
                }
                credibleCandidates.Add(deal);
            }

            var results = credibleCandidates
                .OrderByDescending(d => d.RelevanceScore)
                .ThenByDescending(d => d.QualityScore)
                .ThenByDescending(d => d.MatchScore)
                .ThenByDescending(d => WishlistBoost(d))
                .ThenByDescending(d => d.DealScore)
                .Take(20)
                .ToList();
            diagnostics.DisplayedDeals = results.Count;

            LastDiagnosticsSummary = diagnostics.DisplaySummary("current deals");
            logger.Info(diagnostics.Format());
            return results;
        }

        public async Task<List<ExternalRecommendation>> GetNotOwnedRecommendationsAsync(
            IEnumerable<EnrichedGame> ownedGames,
            TasteProfile profile)
        {
            var ownedList = ownedGames?.ToList() ?? new List<EnrichedGame>();
            var owned = BuildOwnedIndex(ownedList);

            var diagnostics = new NotOwnedDiagnostics();
            var clusters = BuildTasteClusters(profile, ownedList);
            var rawCandidates = await GetNotOwnedDiscoveryCandidatesAsync(ownedList, clusters, diagnostics);
            diagnostics.RawCandidates = rawCandidates.Count;

            var candidates = new List<ExternalRecommendation>();
            foreach (var candidate in rawCandidates)
            {
                if (!IsRealGameDeal(candidate))
                {
                    diagnostics.FilteredJunk++;
                    continue;
                }
                if (IsOwnedExternal(candidate, owned))
                {
                    diagnostics.FilteredOwned++;
                    continue;
                }
                if (IsRejectedDeal(candidate))
                {
                    diagnostics.FilteredRejected++;
                    continue;
                }
                if (IsBlacklistedDeal(candidate))
                {
                    diagnostics.FilteredBlacklisted++;
                    continue;
                }
                if (MatchesBlockedTag(candidate.Title))
                {
                    diagnostics.FilteredBlockedTag++;
                    continue;
                }
                candidates.Add(candidate);
            }
            diagnostics.PrefilteredCandidates = candidates.Count;

            await ApplyCandidateProfilesBoundedAsync(candidates, clusters, "not-owned");
            diagnostics.ProfiledCandidates = candidates.Count(d => HasCandidateProfile(d));

            var scoredCandidates = candidates
                .Select(candidate => new QualityCandidate(candidate, ScoreTasteFit(candidate, profile, ownedList, clusters)))
                .OrderByDescending(item => item.Score)
                .Take(MaxQualityCandidates)
                .ToList();
            diagnostics.QualityCandidates = scoredCandidates.Count;

            await ApplyQualityBoundedAsync(scoredCandidates);
            foreach (var item in scoredCandidates)
            {
                ApplyFinalScore(item.Deal, item.Score);
                RemoveDealRankingSignalForNotOwned(item.Deal);
            }

            var credibleCandidates = new List<ExternalRecommendation>();
            foreach (var deal in scoredCandidates.Select(item => item.Deal))
            {
                if (!IsRealGameDeal(deal))
                {
                    diagnostics.PostQualityJunk++;
                    continue;
                }
                var admission = GetAdmissionRejectionReason(deal, "not-owned");
                if (!string.IsNullOrWhiteSpace(admission))
                {
                    diagnostics.AddAdmissionRejection(admission);
                    continue;
                }
                if (ShouldFilterEarlyAccess(deal))
                {
                    diagnostics.PostQualityEarlyAccess++;
                    continue;
                }
                if (IsRejectedDeal(deal))
                {
                    diagnostics.PostQualityRejected++;
                    continue;
                }
                if (IsOwnedExternal(deal, owned))
                {
                    diagnostics.PostQualityOwned++;
                    continue;
                }
                if (IsBlacklistedDeal(deal))
                {
                    diagnostics.PostQualityBlacklisted++;
                    continue;
                }
                if (MatchesBlockedTag(deal.Reasons))
                {
                    diagnostics.PostQualityBlockedTag++;
                    continue;
                }
                var rejection = GetCredibilityRejectionReason(deal);
                if (!string.IsNullOrWhiteSpace(rejection))
                {
                    diagnostics.AddCredibilityRejection(rejection);
                    continue;
                }
                credibleCandidates.Add(deal);
            }

            var results = credibleCandidates
                .OrderByDescending(d => d.RelevanceScore)
                .ThenByDescending(d => d.QualityScore)
                .ThenByDescending(d => d.MatchScore)
                .ThenByDescending(d => WishlistBoost(d))
                .ThenByDescending(d => d.DealScore)
                .Take(20)
                .ToList();
            diagnostics.DisplayedCandidates = results.Count;

            LastDiagnosticsSummary = diagnostics.DisplaySummary("not-owned picks");
            logger.Info(diagnostics.Format());
            return results;
        }

        private async Task ApplyQualityBoundedAsync(IEnumerable<QualityCandidate> candidates)
        {
            var items = (candidates ?? Enumerable.Empty<QualityCandidate>())
                .Where(item => item?.Deal != null)
                .ToList();
            if (!items.Any())
                return;

            var diagnostics = new ExternalQualityDiagnostics { Candidates = items.Count };
            using (var gate = new SemaphoreSlim(MaxQualityConcurrency))
            {
                var tasks = items.Select(async item =>
                {
                    await gate.WaitAsync();
                    try
                    {
                        var result = await ApplyQualityAsync(item.Deal);
                        diagnostics.Record(result);
                    }
                    catch (Exception ex)
                    {
                        diagnostics.Failed++;
                        logger.Warn(ex, $"Quality enrichment failed for {item.Deal?.Title}");
                        if (item.Deal != null && string.IsNullOrWhiteSpace(item.Deal.QualityLabel))
                            item.Deal.QualityLabel = "Quality unknown";
                    }
                    finally
                    {
                        gate.Release();
                    }
                }).ToList();
                await Task.WhenAll(tasks);
            }

            logger.Info(diagnostics.Format());
        }

        private async Task<List<ExternalRecommendation>> GetDealsAsync()
        {
            var deals = new List<ExternalRecommendation>();

            if (!string.IsNullOrWhiteSpace(settings.ItadApiKey))
                deals.AddRange(await GetDealSourceSafelyAsync("ITAD", GetItadDealsAsync));

            if (settings.EnableJastDeals)
                deals.AddRange(await GetDealSourceSafelyAsync(
                    "JAST",
                    () => GetOptionalStoreDealsAsync("JAST", "jast_deals_v1_sale", JastSaleUrl, ParseJastDeals)));

            if (settings.EnableMangaGamerDeals)
                deals.AddRange(await GetDealSourceSafelyAsync(
                    "MangaGamer",
                    () => GetOptionalStoreDealsAsync("MangaGamer", "mangagamer_deals_v1_winter_sale", MangaGamerSaleUrl, ParseMangaGamerDeals)));

            deals.AddRange((await GetWishlistCandidatesAsync())
                .Where(HasCurrentDealSignal)
                .Select(AsWishlistDeal));

            return DeduplicateDeals(deals);
        }

        private static async Task<List<ExternalRecommendation>> GetDealSourceSafelyAsync(
            string sourceName,
            Func<Task<List<ExternalRecommendation>>> fetch)
        {
            try
            {
                return await fetch();
            }
            catch (Exception ex)
            {
                logger.Warn(ex, $"{sourceName} deal source failed");
                return new List<ExternalRecommendation>();
            }
        }

        private async Task<List<ExternalRecommendation>> GetNotOwnedDiscoveryCandidatesAsync(
            List<EnrichedGame> ownedGames,
            List<TasteCluster> clusters,
            NotOwnedDiagnostics diagnostics)
        {
            var wishlist = await GetWishlistCandidatesAsync();
            var similar = await GetSteamSimilarCandidatesAsync(ownedGames, clusters);
            diagnostics.WishlistCandidates = wishlist.Count;
            diagnostics.SteamSimilarCandidates = similar.Count;

            var primary = DeduplicateDeals(wishlist.Concat(similar));
            if (primary.Count >= MinNotOwnedPrimaryCandidatesBeforeDealFallback)
                return primary;

            var fallbackDeals = (await GetDealsAsync())
                .Where(HasCurrentDealSignal)
                .OrderByDescending(d => d.IsWishlisted)
                .ThenByDescending(d => d.Cut ?? 0)
                .Take(MaxNotOwnedFallbackDeals)
                .ToList();
            foreach (var deal in fallbackDeals)
            {
                AddReason(deal, "Supplemental current deal candidate");
                AddDistinct(deal.SourceSignals, "Not-owned fallback deal");
            }
            diagnostics.FallbackDealCandidates = fallbackDeals.Count;
            return DeduplicateDeals(primary.Concat(fallbackDeals));
        }

        private async Task<List<ExternalRecommendation>> GetSteamSimilarCandidatesAsync(
            List<EnrichedGame> ownedGames,
            List<TasteCluster> clusters)
        {
            var sourceGames = SelectSteamSimilarSourceGames(ownedGames, clusters);
            if (!sourceGames.Any())
                return new List<ExternalRecommendation>();

            var candidates = new List<ExternalRecommendation>();
            var seenAppIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in sourceGames)
            {
                foreach (var appId in source.SteamSimilarAppIds ?? new List<string>())
                {
                    if (string.IsNullOrWhiteSpace(appId) || !seenAppIds.Add(appId.Trim()))
                        continue;

                    var candidate = await CreateSteamSimilarCandidateAsync(appId.Trim(), source.Name);
                    if (candidate != null)
                        candidates.Add(candidate);
                    if (candidates.Count >= MaxNotOwnedSimilarCandidates)
                        return DeduplicateDeals(candidates);
                }
            }

            return DeduplicateDeals(candidates);
        }

        private static List<EnrichedGame> SelectSteamSimilarSourceGames(
            List<EnrichedGame> ownedGames,
            List<TasteCluster> clusters)
        {
            var selected = new List<EnrichedGame>();
            var selectedIds = new HashSet<Guid>();
            var playedSteam = (ownedGames ?? new List<EnrichedGame>())
                .Where(g => g != null &&
                            g.IsPlayed &&
                            g.PlaytimeSeconds >= MinimumSteamSimilarSourcePlaytimeSeconds &&
                            !string.IsNullOrWhiteSpace(g.SteamAppId) &&
                            (g.SteamSimilarAppIds ?? new List<string>()).Any())
                .ToList();

            foreach (var cluster in clusters ?? new List<TasteCluster>())
            {
                var clusterGames = playedSteam
                    .Where(g => GameMatchesAnyTerm(g, cluster.CoreTerms))
                    .OrderByDescending(g => ClusterAnchorScore(g, cluster))
                    .ThenByDescending(g => g.PlaytimeSeconds)
                    .Take(3)
                    .ToList();
                foreach (var game in clusterGames)
                {
                    if (selectedIds.Add(game.PlayniteId))
                        selected.Add(game);
                    if (selected.Count >= MaxNotOwnedSimilarSourceGames)
                        return selected;
                }
            }

            foreach (var game in playedSteam.OrderByDescending(g => g.PlaytimeSeconds).Take(MaxNotOwnedSimilarSourceGames))
            {
                if (selectedIds.Add(game.PlayniteId))
                    selected.Add(game);
                if (selected.Count >= MaxNotOwnedSimilarSourceGames)
                    break;
            }

            return selected;
        }

        private async Task<ExternalRecommendation> CreateSteamSimilarCandidateAsync(string appId, string sourceGameName)
        {
            var profile = await GetSteamAppProfileAsync(appId);
            if (profile == null || string.IsNullOrWhiteSpace(profile.Title))
                return null;
            if (!IsSteamGameType(profile.AppType))
                return null;

            return new ExternalRecommendation
            {
                Title = profile.Title,
                Store = "Steam",
                Url = $"https://store.steampowered.com/app/{appId}/",
                SteamAppId = appId,
                DealType = profile.AppType,
                RecommendationKind = "External pick",
                CandidateDescription = profile.Description ?? string.Empty,
                CandidateTags = new List<string>(profile.Tags ?? new List<string>()),
                CandidateGenres = new List<string>(profile.Genres ?? new List<string>()),
                CandidateThemes = new List<string>(profile.Themes ?? new List<string>()),
                CandidateFeatures = new List<string>(profile.Features ?? new List<string>()),
                MechanicTags = new List<string>(profile.Mechanics ?? new List<string>()),
                MoodTags = new List<string>(profile.Moods ?? new List<string>()),
                SourceSignals = new List<string> { "Steam similar app", $"Similar to {sourceGameName}" },
                SimilarOwnedGames = string.IsNullOrWhiteSpace(sourceGameName)
                    ? new List<string>()
                    : new List<string> { sourceGameName },
                Reasons = new List<string>()
            };
        }

        private async Task<List<ExternalRecommendation>> GetWishlistCandidatesAsync()
        {
            var candidates = new List<ExternalRecommendation>();
            candidates.AddRange(await GetSteamWishlistCandidatesAsync());
            return DeduplicateDeals(candidates);
        }

        private async Task<List<ExternalRecommendation>> GetSteamWishlistCandidatesAsync()
        {
            var steamId = settings.SteamUserId?.Trim();
            if (string.IsNullOrWhiteSpace(steamId))
                return new List<ExternalRecommendation>();

            var cacheKey = "steam_wishlist_v1_" + NormalizeTitle(steamId);
            if (cache.TryGet<List<ExternalRecommendation>>(cacheKey, out var cached))
                return cached ?? new List<ExternalRecommendation>();

            try
            {
                var url = $"https://store.steampowered.com/wishlist/profiles/{Uri.EscapeDataString(steamId)}/wishlistdata/?p=0";
                var json = await http.GetStringAsync(url);
                var parsed = Serialization.FromJson<Dictionary<string, SteamWishlistItem>>(json);
                var items = (parsed ?? new Dictionary<string, SteamWishlistItem>())
                    .Select(kvp => ToWishlistRecommendation(kvp.Key, kvp.Value))
                    .Where(r => r != null && !string.IsNullOrWhiteSpace(r.Title))
                    .Take(300)
                    .ToList();

                cache.Set(cacheKey, items, TimeSpan.FromHours(8));
                logger.Info($"Steam wishlist: fetched {items.Count} item(s)");
                return items;
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Steam wishlist fetch failed");
                cache.Set(cacheKey, new List<ExternalRecommendation>(), TimeSpan.FromMinutes(45));
                return new List<ExternalRecommendation>();
            }
        }

        private static ExternalRecommendation ToWishlistRecommendation(string appId, SteamWishlistItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.name))
                return null;

            var bestSub = (item.subs ?? new List<SteamWishlistSub>())
                .OrderByDescending(s => s.discount_pct)
                .ThenBy(s => s.price <= 0 ? int.MaxValue : s.price)
                .FirstOrDefault();
            var price = PriceFromCents(bestSub?.price);
            var regular = PriceFromCents(bestSub?.discount_original_price);
            var cut = bestSub?.discount_pct > 0 ? bestSub.discount_pct : (int?)null;

            return new ExternalRecommendation
            {
                Title = item.name,
                Store = "Steam",
                Url = $"https://store.steampowered.com/app/{appId}/",
                SteamAppId = appId,
                DealType = "game",
                RecommendationKind = HasDiscount(price, regular, cut) ? "Wishlist deal" : "Wishlist pick",
                IsWishlisted = true,
                Price = price,
                RegularPrice = regular,
                Cut = cut,
                BoxArtUrl = string.IsNullOrWhiteSpace(item.capsule) ? item.capsule_image : item.capsule,
                QualityLabel = string.IsNullOrWhiteSpace(item.review_desc) ? "Quality unknown" : item.review_desc,
                ReviewPercent = item.review_percent > 0 ? item.review_percent : (int?)null,
                ReviewCount = item.review_count > 0 ? item.review_count : (int?)null,
                Reasons = new List<string> { "On your Steam wishlist" },
                SourceSignals = new List<string> { "Steam wishlist" }
            };
        }

        private static decimal? PriceFromCents(int? cents)
        {
            if (!cents.HasValue || cents.Value <= 0)
                return null;
            return cents.Value / 100m;
        }

        private static ExternalRecommendation AsWishlistDeal(ExternalRecommendation candidate)
        {
            if (candidate == null)
                return null;
            candidate.RecommendationKind = "Wishlist deal";
            if (candidate.Cut.HasValue && candidate.Cut.Value > 0)
                AddReason(candidate, $"Wishlist game currently {candidate.Cut.Value}% off");
            else
                AddReason(candidate, "Wishlist game has a current deal");
            return candidate;
        }

        private async Task<List<ExternalRecommendation>> GetItadDealsAsync()
        {
            const string cacheKey = "itad_deals_us_v3_history_lows";
            if (cache.TryGet<List<ExternalRecommendation>>(cacheKey, out var cached))
                return cached;

            var url = $"https://api.isthereanydeal.com/deals/v2?key={Uri.EscapeDataString(settings.ItadApiKey)}&country=US&limit=200&sort=-trending";
            var json = await http.GetStringAsync(url);
            var parsed = Serialization.FromJson<ItadDealsResponse>(json);

            var rawDeals = parsed?.list ?? parsed?.data?.list ?? new List<ItadDealRaw>();
            var deals = rawDeals.Select(ToRecommendation)
                .Where(d => !string.IsNullOrWhiteSpace(d.Title))
                .ToList() ?? new List<ExternalRecommendation>();

            await EnrichItadHistoricalLowsAsync(deals);
            cache.Set(cacheKey, deals, Ttl);
            logger.Info($"ITAD: fetched {deals.Count} current deals");
            return deals;
        }

        private async Task EnrichItadHistoricalLowsAsync(List<ExternalRecommendation> deals)
        {
            var ids = (deals ?? new List<ExternalRecommendation>())
                .Select(d => d?.ItadId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(200)
                .ToList();
            if (!ids.Any() || string.IsNullOrWhiteSpace(settings.ItadApiKey))
                return;

            var cacheKey = "itad_history_lows_v1_" + string.Join("_", ids.Select(NormalizeTitle));
            if (!cache.TryGet<List<ItadHistoryLowResponse>>(cacheKey, out var lows))
            {
                try
                {
                    var url = $"https://api.isthereanydeal.com/games/historylow/v1?key={Uri.EscapeDataString(settings.ItadApiKey)}&country=US";
                    using (var request = new HttpRequestMessage(HttpMethod.Post, url))
                    {
                        request.Content = new StringContent(Serialization.ToJson(ids), Encoding.UTF8, "application/json");
                        var response = await http.SendAsync(request);
                        response.EnsureSuccessStatusCode();
                        lows = Serialization.FromJson<List<ItadHistoryLowResponse>>(await response.Content.ReadAsStringAsync()) ??
                               new List<ItadHistoryLowResponse>();
                        cache.Set(cacheKey, lows, TimeSpan.FromHours(12));
                    }
                }
                catch (Exception ex)
                {
                    logger.Warn(ex, "ITAD historical low fetch failed");
                    return;
                }
            }

            var byId = (lows ?? new List<ItadHistoryLowResponse>())
                .Where(l => !string.IsNullOrWhiteSpace(l?.id) && l.low != null)
                .GroupBy(l => l.id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().low, StringComparer.OrdinalIgnoreCase);

            foreach (var deal in deals ?? new List<ExternalRecommendation>())
            {
                if (deal == null || string.IsNullOrWhiteSpace(deal.ItadId) || !byId.TryGetValue(deal.ItadId, out var low))
                    continue;

                deal.HistoricalLowPrice = low.price?.amount;
                deal.HistoricalLowRegularPrice = low.regular?.amount;
                deal.HistoricalLowCut = low.cut;
                deal.HistoricalLowStore = low.shop?.name;
                if (DateTime.TryParse(low.timestamp, out var timestamp))
                    deal.HistoricalLowDate = timestamp;
            }
        }

        private async Task<List<ExternalRecommendation>> GetOptionalStoreDealsAsync(
            string sourceName,
            string cacheKey,
            string url,
            Func<string, string, List<ExternalRecommendation>> parser)
        {
            if (cache.TryGet<List<ExternalRecommendation>>(cacheKey, out var cached))
                return cached;

            try
            {
                var html = await http.GetStringAsync(url);
                var deals = parser(html, url)
                    .Where(d => !string.IsNullOrWhiteSpace(d.Title))
                    .Where(IsCredibleParsedStoreDeal)
                    .Take(200)
                    .ToList();

                cache.Set(cacheKey, deals, Ttl);
                logger.Info($"{sourceName}: fetched {deals.Count} current sale items");
                return deals;
            }
            catch (Exception ex)
            {
                logger.Warn(ex, $"{sourceName} deal fetch failed");
                return new List<ExternalRecommendation>();
            }
        }

        private static ExternalRecommendation ToRecommendation(ItadDealRaw raw)
        {
            var title = raw.title ?? raw.plain ?? raw.game?.title ?? raw.game?.plain;
            var price = raw.price_new ?? raw.price?.amount ?? raw.deal?.price?.amount;
            var regular = raw.price_old ?? raw.regular?.amount ?? raw.deal?.regular?.amount;
            return new ExternalRecommendation
            {
                Title = title,
                Store = raw.shop?.name ?? raw.shop_name ?? raw.deal?.shop?.name,
                Url = raw.url ?? raw.urls?.game ?? raw.deal?.url,
                Price = price,
                RegularPrice = regular,
                Cut = raw.cut ?? raw.deal?.cut,
                ItadId = raw.id ?? raw.game?.id,
                ItadSlug = raw.slug ?? raw.game?.slug,
                DealType = raw.type ?? raw.game?.type,
                RecommendationKind = "Deal",
                BoxArtUrl = raw.assets?.boxart ?? raw.game?.assets?.boxart,
                BannerUrl = raw.assets?.banner145 ?? raw.assets?.banner300 ?? raw.game?.assets?.banner145 ?? raw.game?.assets?.banner300,
                Reasons = new List<string>(),
                SourceSignals = new List<string> { "ITAD current deal feed" }
            };
        }

        private static bool IsRealGameDeal(ExternalRecommendation deal)
        {
            if (deal == null || string.IsNullOrWhiteSpace(deal.Title)) return false;
            if (!string.IsNullOrWhiteSpace(deal.DealType) &&
                !IsSteamGameType(deal.DealType))
                return false;

            var title = deal.Title.ToLowerInvariant();
            if (JunkTitleTerms.Any(t => title.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0))
                return false;
            if (Regex.IsMatch(title, @"\b(chapter|episode|skin|pack|currency|credits|tokens?)\s+\d+\b"))
                return false;
            if (Regex.IsMatch(title, @"\b\d+\s*(coins|gems|crystals|credits|tokens|points)\b"))
                return false;
            return true;
        }

        private static bool IsSteamGameType(string type)
        {
            return string.IsNullOrWhiteSpace(type) ||
                   string.Equals(type, "game", StringComparison.OrdinalIgnoreCase);
        }

        private static List<ExternalRecommendation> ParseJastDeals(string html, string sourceUrl)
        {
            var deals = new List<ExternalRecommendation>();
            var lines = ToTextLinesWithLinks(html);
            string title = null;
            string url = null;

            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                var link = ExtractLink(ref line);
                if (!string.IsNullOrWhiteSpace(link))
                    url = MakeAbsoluteUrl(sourceUrl, link);

                var imageTitle = ExtractImageTitle(line);
                if (!string.IsNullOrWhiteSpace(imageTitle))
                {
                    title = imageTitle;
                    continue;
                }

                if (IsLikelyStoreTitle(line))
                    title = line.Trim();

                var cut = ParsePercent(line);
                if (!cut.HasValue || string.IsNullOrWhiteSpace(title))
                    continue;

                var priceLine = NextNonEmptyLine(lines, i + 1);
                if (!TryParsePricePair(priceLine, out var regular, out var price))
                    continue;

                deals.Add(CreateStoreDeal(title, "JAST Store", url ?? sourceUrl, price, regular, cut.Value));
                title = null;
                url = null;
            }

            return deals;
        }

        private static List<ExternalRecommendation> ParseMangaGamerDeals(string html, string sourceUrl)
        {
            var deals = new List<ExternalRecommendation>();
            var lines = ToTextLinesWithLinks(html);
            int? cut = null;
            string title = null;

            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                var currentCut = ParsePercent(line);
                if (currentCut.HasValue)
                {
                    cut = currentCut.Value;
                    title = null;
                    continue;
                }

                var linkLine = line;
                var link = ExtractLink(ref linkLine);
                if (cut.HasValue && !string.IsNullOrWhiteSpace(title) && TryParsePricePair(linkLine, out var regular, out var price))
                {
                    deals.Add(CreateStoreDeal(title, "MangaGamer", MakeAbsoluteUrl(sourceUrl, link), price, regular, cut.Value));
                    title = null;
                    continue;
                }

                if (cut.HasValue && IsLikelyStoreTitle(line))
                    title = line.Trim();
            }

            return deals;
        }

        private static ExternalRecommendation CreateStoreDeal(string title, string store, string url, decimal price, decimal regular, int cut)
        {
            return new ExternalRecommendation
            {
                Title = CleanStoreTitle(title),
                Store = store,
                Url = url,
                Price = price,
                RegularPrice = regular,
                Cut = cut,
                DealType = "game",
                RecommendationKind = "Deal",
                QualityLabel = "Store sale listing",
                Reasons = new List<string> { $"Source confidence: parsed from {store} sale page", $"Currently {cut}% off" },
                SourceSignals = new List<string> { $"{store} sale page" }
            };
        }

        private async Task ApplyCandidateProfilesBoundedAsync(
            List<ExternalRecommendation> candidates,
            List<TasteCluster> clusters,
            string context)
        {
            var items = (candidates ?? new List<ExternalRecommendation>())
                .Where(d => d != null)
                .Take(MaxProfileCandidates)
                .ToList();
            if (!items.Any())
                return;

            var diagnostics = new CandidateProfileDiagnostics(context) { Candidates = items.Count };
            using (var gate = new SemaphoreSlim(MaxProfileConcurrency))
            {
                var tasks = items.Select(async deal =>
                {
                    await gate.WaitAsync();
                    try
                    {
                        var cacheHit = await ApplyCandidateProfileAsync(deal, clusters);
                        diagnostics.RecordCache(cacheHit);
                        if (HasCandidateProfile(deal))
                            diagnostics.Profiled++;
                        else
                            diagnostics.NoProfile++;
                    }
                    catch (Exception ex)
                    {
                        diagnostics.Failed++;
                        logger.Warn(ex, $"External candidate profile failed for {deal?.Title}");
                    }
                    finally
                    {
                        gate.Release();
                    }
                }).ToList();

                await Task.WhenAll(tasks);
            }

            logger.Info(diagnostics.Format());
        }

        private async Task<bool> ApplyCandidateProfileAsync(ExternalRecommendation deal, List<TasteCluster> clusters)
        {
            if (deal == null)
                return false;

            var cacheKey = CandidateProfileCacheKey(deal);
            if (cache.TryGet<ExternalCandidateProfile>(cacheKey, out var cached) && cached != null)
            {
                ApplyCandidateProfile(deal, cached, clusters);
                return true;
            }

            var candidateProfile = BuildBaseCandidateProfile(deal);
            var appId = deal.SteamAppId;
            if (string.IsNullOrWhiteSpace(appId))
                appId = ParseSteamAppId(deal.Url);
            if (string.IsNullOrWhiteSpace(appId))
                appId = await FindSteamAppIdAsync(deal.Title);

            if (!string.IsNullOrWhiteSpace(appId))
            {
                deal.SteamAppId = appId;
                var steamProfile = await GetSteamAppProfileAsync(appId);
                MergeCandidateProfile(candidateProfile, steamProfile);
            }

            if (rawgClient.IsConfigured && NeedsRawgCandidateData(deal, candidateProfile))
            {
                var rawg = await rawgClient.GetGameDataAsync(deal.Title);
                MergeRawgCandidateProfile(candidateProfile, rawg);
                ApplyRawgCandidateData(deal, rawg);
            }

            FinalizeCandidateProfile(candidateProfile);
            cache.Set(cacheKey, candidateProfile, TimeSpan.FromDays(3));
            ApplyCandidateProfile(deal, candidateProfile, clusters);
            return false;
        }

        private static ExternalCandidateProfile BuildBaseCandidateProfile(ExternalRecommendation deal)
        {
            var profile = new ExternalCandidateProfile
            {
                Title = deal?.Title,
                Store = deal?.Store,
                Description = deal?.CandidateDescription
            };
            AddRange(profile.Genres, deal?.CandidateGenres);
            AddRange(profile.Tags, deal?.CandidateTags);
            AddRange(profile.Themes, deal?.CandidateThemes);
            AddRange(profile.Features, deal?.CandidateFeatures);
            AddRange(profile.SourceSignals, deal?.SourceSignals);
            if (deal?.IsWishlisted == true)
                AddDistinct(profile.SourceSignals, "Steam wishlist");
            if (HasCurrentDealSignal(deal))
                AddDistinct(profile.SourceSignals, "Current deal");
            if (!string.IsNullOrWhiteSpace(deal?.Store))
                AddDistinct(profile.SourceSignals, deal.Store);
            return profile;
        }

        private async Task<ExternalCandidateProfile> GetSteamAppProfileAsync(string appId)
        {
            if (string.IsNullOrWhiteSpace(appId))
                return null;

            var cacheKey = "steam_app_profile_v3_" + appId.Trim();
            if (cache.TryGet<ExternalCandidateProfile>(cacheKey, out var cached))
                return cached;

            try
            {
                var url = $"https://store.steampowered.com/api/appdetails?appids={Uri.EscapeDataString(appId)}&filters=basic,genres,categories";
                var json = await http.GetStringAsync(url);
                var parsed = Serialization.FromJson<Dictionary<string, SteamAppDetailsEnvelope>>(json);
                SteamAppDetailsEnvelope envelope;
                if (parsed == null || !parsed.TryGetValue(appId, out envelope) || envelope == null || !envelope.success || envelope.data == null)
                {
                    cache.Set<ExternalCandidateProfile>(cacheKey, null, TimeSpan.FromHours(12));
                    return null;
                }

                var data = envelope.data;
                var profile = new ExternalCandidateProfile
                {
                    Title = data.name,
                    AppType = data.type,
                    Description = StripHtml(data.short_description ?? data.detailed_description),
                    Store = "Steam"
                };
                AddRange(profile.Genres, (data.genres ?? new List<SteamAppDetailItem>()).Select(g => g.description));
                AddRange(profile.Features, (data.categories ?? new List<SteamAppDetailItem>()).Select(c => c.description));
                profile.IsEarlyAccess = IsEarlyAccessProfile(profile);
                if (profile.IsEarlyAccess)
                    profile.LastContentUpdateUtc = await GetSteamLastNewsDateAsync(appId);
                AddDistinct(profile.SourceSignals, "Steam app metadata");
                FinalizeCandidateProfile(profile);
                cache.Set(cacheKey, profile, TimeSpan.FromDays(7));
                return profile;
            }
            catch (Exception ex)
            {
                logger.Warn(ex, $"Steam app profile fetch failed for {appId}");
                cache.Set<ExternalCandidateProfile>(cacheKey, null, TimeSpan.FromHours(12));
                return null;
            }
        }

        private async Task<DateTime?> GetSteamLastNewsDateAsync(string appId)
        {
            if (string.IsNullOrWhiteSpace(appId))
                return null;

            var cacheKey = "steam_app_news_latest_v1_" + appId.Trim();
            if (cache.TryGet<DateTime?>(cacheKey, out var cached))
                return cached;

            try
            {
                var url = $"https://api.steampowered.com/ISteamNews/GetNewsForApp/v0002/?appid={Uri.EscapeDataString(appId)}&count=5&maxlength=1&format=json";
                var json = await http.GetStringAsync(url);
                var parsed = Serialization.FromJson<SteamNewsResponse>(json);
                var latestUnix = parsed?.appnews?.newsitems?
                    .Where(n => n != null && n.date > 0)
                    .Select(n => n.date)
                    .DefaultIfEmpty(0)
                    .Max() ?? 0;
                DateTime? latest = latestUnix > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(latestUnix).UtcDateTime
                    : (DateTime?)null;
                cache.Set(cacheKey, latest, TimeSpan.FromDays(3));
                return latest;
            }
            catch (Exception ex)
            {
                logger.Warn(ex, $"Steam news fetch failed for early access appId {appId}");
                cache.Set<DateTime?>(cacheKey, null, TimeSpan.FromHours(12));
                return null;
            }
        }

        private static void MergeCandidateProfile(ExternalCandidateProfile target, ExternalCandidateProfile source)
        {
            if (target == null || source == null)
                return;
            if (string.IsNullOrWhiteSpace(target.Title))
                target.Title = source.Title;
            if (string.IsNullOrWhiteSpace(target.Store))
                target.Store = source.Store;
            if (string.IsNullOrWhiteSpace(target.AppType))
                target.AppType = source.AppType;
            target.IsEarlyAccess = target.IsEarlyAccess || source.IsEarlyAccess;
            if (!target.LastContentUpdateUtc.HasValue ||
                (source.LastContentUpdateUtc.HasValue && source.LastContentUpdateUtc.Value > target.LastContentUpdateUtc.Value))
                target.LastContentUpdateUtc = source.LastContentUpdateUtc;
            if (string.IsNullOrWhiteSpace(target.Description))
                target.Description = source.Description;
            AddRange(target.Tags, source.Tags);
            AddRange(target.Genres, source.Genres);
            AddRange(target.Themes, source.Themes);
            AddRange(target.Features, source.Features);
            AddRange(target.Mechanics, source.Mechanics);
            AddRange(target.Moods, source.Moods);
            AddRange(target.SourceSignals, source.SourceSignals);
        }

        private static void MergeRawgCandidateProfile(ExternalCandidateProfile target, RawgGameData rawg)
        {
            if (target == null || rawg == null)
                return;

            if (string.IsNullOrWhiteSpace(target.Title))
                target.Title = rawg.Name;
            if (string.IsNullOrWhiteSpace(target.Description))
                target.Description = rawg.Description;
            AddRange(target.Genres, rawg.Genres);
            AddRange(target.Tags, rawg.Tags);
            AddDistinct(target.SourceSignals, "RAWG metadata");
        }

        internal static void ApplyRawgCandidateData(ExternalRecommendation deal, RawgGameData rawg)
        {
            if (deal == null || rawg == null)
                return;

            deal.RawgId = rawg.Id > 0 ? rawg.Id : deal.RawgId;
            deal.RawgRating = rawg.Rating ?? deal.RawgRating;
            deal.RawgRatingsCount = rawg.RatingsCount ?? deal.RawgRatingsCount;
            deal.RawgMetacritic = rawg.Metacritic ?? deal.RawgMetacritic;
            deal.RawgReleased = rawg.Released ?? deal.RawgReleased;
            if (string.IsNullOrWhiteSpace(deal.CandidateDescription) && !string.IsNullOrWhiteSpace(rawg.Description))
                deal.CandidateDescription = rawg.Description;
            AddRange(deal.CandidateGenres, rawg.Genres);
            AddRange(deal.CandidateTags, rawg.Tags);
            AddDistinct(deal.SourceSignals, "RAWG metadata");
            if (string.IsNullOrWhiteSpace(deal.Url))
                deal.Url = rawg.RawgUrl;
            if (string.IsNullOrWhiteSpace(deal.QualityLabel) ||
                string.Equals(deal.QualityLabel, "Quality unknown", StringComparison.OrdinalIgnoreCase))
            {
                var label = RawgQualityLabel(rawg);
                if (!string.IsNullOrWhiteSpace(label))
                    deal.QualityLabel = label;
            }
        }

        private static bool NeedsRawgCandidateData(ExternalRecommendation deal, ExternalCandidateProfile profile)
        {
            if (deal == null)
                return false;
            return string.IsNullOrWhiteSpace(deal.CandidateDescription) ||
                   !HasCandidateProfile(deal) ||
                   (profile?.EvidenceQuality ?? 0) < 4 ||
                   !deal.ReviewPercent.HasValue;
        }

        private static void FinalizeCandidateProfile(ExternalCandidateProfile profile)
        {
            if (profile == null)
                return;

            var evidence = CandidateEvidence(profile).ToList();
            AddDerivedSignals(profile.Mechanics, evidence, MechanicRules);
            AddDerivedSignals(profile.Moods, evidence, MoodRules);
            profile.EvidenceQuality =
                DistinctCount(profile.Tags) +
                DistinctCount(profile.Genres) +
                DistinctCount(profile.Themes) +
                DistinctCount(profile.Features) +
                DistinctCount(profile.Mechanics) +
                DistinctCount(profile.Moods);
        }

        private static void ApplyCandidateProfile(ExternalRecommendation deal, ExternalCandidateProfile profile, List<TasteCluster> clusters)
        {
            if (deal == null || profile == null)
                return;

            if (!string.IsNullOrWhiteSpace(profile.Description))
                deal.CandidateDescription = profile.Description;
            if (!string.IsNullOrWhiteSpace(profile.AppType))
                deal.DealType = profile.AppType;
            deal.IsEarlyAccess = deal.IsEarlyAccess || profile.IsEarlyAccess;
            if (!deal.LastContentUpdateUtc.HasValue ||
                (profile.LastContentUpdateUtc.HasValue && profile.LastContentUpdateUtc.Value > deal.LastContentUpdateUtc.Value))
                deal.LastContentUpdateUtc = profile.LastContentUpdateUtc;
            AddRange(deal.CandidateTags, profile.Tags);
            AddRange(deal.CandidateGenres, profile.Genres);
            AddRange(deal.CandidateThemes, profile.Themes);
            AddRange(deal.CandidateFeatures, profile.Features);
            AddRange(deal.MechanicTags, profile.Mechanics);
            AddRange(deal.MoodTags, profile.Moods);
            AddRange(deal.SourceSignals, profile.SourceSignals);
            deal.CandidateKind = ClassifyExternalCandidate(deal);

            foreach (var cluster in MatchTasteClusters(deal, clusters).Take(3))
                AddRange(deal.SimilarOwnedGames, cluster.AnchorGames.Take(2));
        }

        private double ScoreTasteFit(
            ExternalRecommendation deal,
            TasteProfile profile,
            List<EnrichedGame> ownedGames,
            List<TasteCluster> clusters)
        {
            var isVisualNovelStore = IsVisualNovelStore(deal?.Store);
            double score = 0;
            var reasons = new List<string>(deal.Reasons ?? new List<string>());

            if (deal?.IsWishlisted == true)
                reasons.Add("On your Steam wishlist");

            var clusterMatches = MatchTasteClusters(deal, clusters).ToList();
            foreach (var match in clusterMatches.Take(2))
            {
                score += match.Score;
                var anchors = match.AnchorGames.Any()
                    ? " from " + string.Join(", ", match.AnchorGames.Take(2))
                    : string.Empty;
                reasons.Add($"Matches your {match.Name} taste{anchors}");
            }

            var sourceCategoryScore = ScoreSourceCategoryFit(deal, profile, out var sourceCategoryReason);
            if (sourceCategoryScore > 0)
            {
                score += sourceCategoryScore;
                reasons.Add(sourceCategoryReason);
            }

            score -= RejectedTitlePenalty(deal.Title);
            if (!HasCandidateProfile(deal))
                score -= 0.45;
            if (HasOnlyGenericProfileEvidence(deal))
                score -= 0.5;
            if (IsCurrentDealOnlyEvidence(deal))
                score -= 0.4;

            deal.Reasons = reasons.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            deal.RelevanceScore = Math.Max(score, 0);
            deal.PenaltyScore = Math.Max(-score, 0);
            return score;
        }

        private async Task<QualityDiagnosticsResult> ApplyQualityAsync(ExternalRecommendation deal)
        {
            var result = new QualityDiagnosticsResult();
            deal.QualityLabel = "Quality unknown";
            var appId = string.IsNullOrWhiteSpace(deal.SteamAppId)
                ? await FindSteamAppIdAsync(deal.Title)
                : deal.SteamAppId;
            if (string.IsNullOrWhiteSpace(appId))
            {
                if (IsVisualNovelStore(deal?.Store))
                {
                    result.VndbLookups++;
                    await ApplyVndbQualityAsync(deal);
                }
                ApplyRawgQualityIfUseful(deal);
                return result;
            }

            deal.SteamAppId = appId;
            result.WithSteamAppId++;
            var reviewCacheKey = SteamReviewCacheKey(appId);
            result.ReviewCacheHit = cache.TryGet<SteamReviewSummary>(reviewCacheKey, out _);

            var reviews = await GetSteamReviewSummaryAsync(appId);
            if (reviews == null)
            {
                if (IsVisualNovelStore(deal?.Store))
                {
                    result.VndbLookups++;
                    await ApplyVndbQualityAsync(deal);
                }
                ApplyRawgQualityIfUseful(deal);
                return result;
            }

            result.ReviewCoverage++;
            ApplySteamQuality(deal, reviews);
            if (IsVisualNovelStore(deal?.Store))
            {
                result.VndbLookups++;
                await ApplyVndbQualityAsync(deal);
            }
            ApplyRawgQualityIfUseful(deal);
            return result;
        }

        private static void ApplyFinalScore(ExternalRecommendation deal, double tasteScore)
        {
            deal.RelevanceScore = Math.Max(tasteScore, 0);
            deal.QualityScore = QualityBoost(deal);
            deal.DealScore = DealBoost(deal);
            deal.PenaltyScore = Math.Max(-tasteScore, 0);
            var sourceScore = SourceConfidenceBoost(deal);
            var wishlistScore = WishlistBoost(deal);
            deal.MatchScore = tasteScore + deal.QualityScore + deal.DealScore + sourceScore + wishlistScore;

            var concreteTasteReasons = deal.Reasons
                .Where(r => r.StartsWith("Matches your", StringComparison.OrdinalIgnoreCase) ||
                            r.StartsWith("Discovered from", StringComparison.OrdinalIgnoreCase))
                .Take(1)
                .ToList();
            var wishlistReasons = deal.Reasons
                .Where(r => r.StartsWith("On your", StringComparison.OrdinalIgnoreCase))
                .Take(1)
                .ToList();
            var qualityReasons = deal.Reasons
                .Where(r => r.StartsWith("Source confidence", StringComparison.OrdinalIgnoreCase))
                .Take(1)
                .ToList();
            var saleReasons = new List<string>();
            if (deal.HistoricalLowPrice.HasValue)
            {
                var low = $"Historical low ${deal.HistoricalLowPrice.Value:0.##}";
                if (deal.HistoricalLowCut.HasValue && deal.HistoricalLowCut.Value > 0)
                    low += $" ({deal.HistoricalLowCut.Value}% off)";
                saleReasons.Add(IsAtHistoricalLow(deal) ? "At " + low.ToLowerInvariant() : low);
            }
            if (deal.Cut.HasValue && deal.Cut.Value >= 20)
                saleReasons.Add(deal.IsWishlisted
                    ? $"Wishlist game currently {deal.Cut.Value}% off"
                    : $"Currently {deal.Cut.Value}% off");
            if (deal.Price.HasValue && deal.RegularPrice.HasValue && deal.RegularPrice.Value > deal.Price.Value)
                saleReasons.Add($"Usually ${deal.RegularPrice.Value:0.##}");
            var sourceReasons = new List<string>();
            if (!string.IsNullOrWhiteSpace(deal.Store))
                sourceReasons.Add($"Available at {deal.Store}");

            deal.Reasons = concreteTasteReasons
                .Concat(wishlistReasons)
                .Concat(qualityReasons)
                .Concat(saleReasons.Take(1))
                .Concat(sourceReasons.Take(1))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(4)
                .ToList();
            if (!deal.Reasons.Any())
                deal.Reasons.Add("No strong taste match found");
        }

        private static List<ExternalRecommendation> DeduplicateDeals(IEnumerable<ExternalRecommendation> deals)
        {
            return (deals ?? Enumerable.Empty<ExternalRecommendation>())
                .Where(d => d != null && !string.IsNullOrWhiteSpace(d.Title))
                .GroupBy(DeduplicationKey, StringComparer.OrdinalIgnoreCase)
                .Where(g => !string.IsNullOrWhiteSpace(g.Key))
                .Select(SelectBestCandidate)
                .ToList();
        }

        private static ExternalRecommendation SelectBestCandidate(IEnumerable<ExternalRecommendation> group)
        {
            var items = (group ?? Enumerable.Empty<ExternalRecommendation>())
                .Where(d => d != null)
                .ToList();
            var best = items
                .OrderByDescending(d => d.Cut ?? 0)
                .ThenBy(d => d.Price ?? decimal.MaxValue)
                .ThenByDescending(d => d.IsWishlisted)
                .FirstOrDefault();
            if (best == null)
                return null;

            if (items.Any(d => d.IsWishlisted))
            {
                best.IsWishlisted = true;
                AddReason(best, "On your Steam wishlist");
                if (HasCurrentDealSignal(best))
                    best.RecommendationKind = "Wishlist deal";
                else if (string.IsNullOrWhiteSpace(best.RecommendationKind) ||
                         string.Equals(best.RecommendationKind, "External pick", StringComparison.OrdinalIgnoreCase))
                    best.RecommendationKind = "Wishlist pick";
            }

            foreach (var reason in items.SelectMany(d => d.Reasons ?? new List<string>()))
                AddReason(best, reason);
            return best;
        }

        private static bool HasCredibleRecommendationSignal(ExternalRecommendation deal)
            => string.IsNullOrWhiteSpace(GetCredibilityRejectionReason(deal));

        private static string GetCredibilityRejectionReason(ExternalRecommendation deal)
        {
            if (deal == null)
                return "null";

            var hasConcreteTaste = HasConcreteTasteReason(deal);
            var hasWishlist = deal.Reasons.Any(r => r.StartsWith("On your", StringComparison.OrdinalIgnoreCase));
            var hasAllowedWishlistFit = hasWishlist && IsStrongQuality(deal) && HasCandidateProfile(deal);

            if (!HasCandidateProfile(deal))
                return "low metadata confidence";
            if (HasOnlyGenericProfileEvidence(deal))
                return "generic-only evidence";
            if (IsCurrentDealOnlyEvidence(deal))
                return "deal-only evidence";
            if (!PassesFitGate(deal) && !hasAllowedWishlistFit)
                return "weak taste/mood fit";
            if (!hasConcreteTaste && !hasAllowedWishlistFit)
                return "no concrete taste reason";
            if (IsLowQualityDeal(deal))
                return "low quality";
            if (IsWeakQuality(deal) && deal.RelevanceScore < ThinReviewFitThreshold)
                return "weak quality";
            if (IsThinReviewCoverage(deal) && deal.RelevanceScore < ThinReviewFitThreshold)
                return "thin review coverage";
            if (!deal.ReviewPercent.HasValue && deal.RelevanceScore < MinimumNoReviewScore(deal))
                return "no review coverage";
            return null;
        }

        private static bool PassesFitGate(ExternalRecommendation deal)
        {
            if (deal == null)
                return false;
            if (deal.RelevanceScore >= StrongFitThreshold && HasConcreteTasteReason(deal))
                return true;

            var mediumEvidence = CountMediumFitEvidence(deal);
            return mediumEvidence >= 2 && deal.RelevanceScore >= 0.9;
        }

        private static int CountMediumFitEvidence(ExternalRecommendation deal)
        {
            if (deal == null)
                return 0;
            var reasons = deal.Reasons ?? new List<string>();
            var reasonEvidence = reasons.Count(r =>
                r.StartsWith("Matches your", StringComparison.OrdinalIgnoreCase));

            var mechanicMoodEvidence = DistinctCount((deal.MechanicTags ?? new List<string>())
                .Concat(deal.MoodTags ?? new List<string>())
                .Where(IsConcreteFitTerm));

            return reasonEvidence + Math.Min(mechanicMoodEvidence, 2);
        }

        private static bool HasConcreteTasteReason(ExternalRecommendation deal)
        {
            return (deal?.Reasons ?? new List<string>()).Any(r =>
                r.StartsWith("Matches your", StringComparison.OrdinalIgnoreCase) ||
                r.StartsWith("Discovered from", StringComparison.OrdinalIgnoreCase));
        }

        private static Dictionary<string, double> BuildTasteTerms(TasteProfile profile, bool visualNovelStoreOnly = false)
        {
            var terms = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            AddTasteTerms(terms, profile?.TagWeights, 1.25);
            AddTasteTerms(terms, profile?.ThemeWeights, 1.05);
            AddTasteTerms(terms, profile?.GenreWeights, 0.9);
            AddTasteTerms(terms, profile?.FeatureWeights, 0.75);
            return terms
                .Where(k => k.Key.Length >= 4 && !GenericTasteTerms.Contains(k.Key))
                .Where(k => !visualNovelStoreOnly || IsVisualNovelTasteTerm(k.Key))
                .OrderByDescending(k => k.Value)
                .Take(60)
                .ToDictionary(k => k.Key, k => k.Value, StringComparer.OrdinalIgnoreCase);
        }

        private static List<TasteCluster> BuildTasteClusters(TasteProfile profile, List<EnrichedGame> ownedGames)
        {
            var tasteTerms = BuildTasteTerms(profile)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);
            var playedGames = (ownedGames ?? new List<EnrichedGame>())
                .Where(g => g != null && g.IsPlayed)
                .OrderByDescending(g => g.PlaytimeSeconds)
                .Take(120)
                .ToList();

            var clusters = new List<TasteCluster>();
            foreach (var rule in TasteClusterRules)
            {
                var score = 0.0;
                foreach (var term in rule.CoreTerms ?? new string[0])
                    score += MatchingTasteWeight(tasteTerms, term);
                foreach (var term in rule.SupportTerms ?? new string[0])
                    score += MatchingTasteWeight(tasteTerms, term) * 0.35;

                var anchors = playedGames
                    .Where(g => GameMatchesAnyTerm(g, rule.CoreTerms))
                    .OrderByDescending(g => g.PlaytimeSeconds)
                    .Select(g => g.Name)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Take(4)
                    .ToList();
                score += Math.Min(anchors.Count * 0.25, 0.75);

                if (score < 0.2)
                    continue;

                clusters.Add(new TasteCluster
                {
                    Name = rule.Name,
                    CoreTerms = (rule.CoreTerms ?? new string[0]).ToList(),
                    SupportTerms = (rule.SupportTerms ?? new string[0]).ToList(),
                    Terms = (rule.CoreTerms ?? new string[0])
                        .Concat(rule.SupportTerms ?? new string[0])
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    AnchorGames = anchors.Any()
                        ? anchors
                        : (rule.AnchorHints ?? new string[0]).Take(2).ToList(),
                    Confidence = Math.Min(score, 2.5)
                });
            }

            return clusters
                .OrderByDescending(c => c.Confidence)
                .Take(8)
                .ToList();
        }

        private static double MatchingTasteWeight(Dictionary<string, double> tasteTerms, string term)
        {
            if (tasteTerms == null || string.IsNullOrWhiteSpace(term))
                return 0;
            return tasteTerms
                .Where(kvp => StrongTermMatch(kvp.Key, term))
                .Select(kvp => kvp.Value)
                .DefaultIfEmpty(0)
                .Max();
        }

        private static bool GameMatchesAnyTerm(EnrichedGame game, IEnumerable<string> terms)
        {
            if (game == null)
                return false;
            var evidence = GameEvidence(game).ToList();
            return (terms ?? Enumerable.Empty<string>()).Any(term => EvidenceMatches(evidence, term));
        }

        private static int ClusterAnchorScore(EnrichedGame game, TasteCluster cluster)
        {
            if (game == null || cluster == null)
                return 0;
            var evidence = GameEvidence(game).ToList();
            return (cluster.CoreTerms ?? new List<string>())
                .Count(term => EvidenceMatches(evidence, term));
        }

        private static IEnumerable<TasteCluster> MatchTasteClusters(ExternalRecommendation deal, List<TasteCluster> clusters)
        {
            var evidence = CandidateEvidence(deal).ToList();
            if (!evidence.Any())
                return new List<TasteCluster>();

            return (clusters ?? new List<TasteCluster>())
                .Select(cluster =>
                {
                    var matchedCoreTerms = (cluster.CoreTerms ?? new List<string>())
                        .Where(term => EvidenceMatches(evidence, term))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    var matchedSupportTerms = (cluster.SupportTerms ?? new List<string>())
                        .Where(term => EvidenceMatches(evidence, term))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    var coreMatches = matchedCoreTerms.Count;
                    if (coreMatches <= 0)
                        return null;

                    var supportMatches = matchedSupportTerms.Count;
                    var score = Math.Min(0.45 + coreMatches * 0.65 + supportMatches * 0.12, 1.7) +
                                Math.Min(cluster.Confidence * 0.15, 0.35);
                    return new TasteCluster
                    {
                        Name = cluster.Name,
                        Terms = cluster.Terms,
                        CoreTerms = cluster.CoreTerms,
                        SupportTerms = cluster.SupportTerms,
                        MatchedCoreTerms = matchedCoreTerms,
                        MatchedSupportTerms = matchedSupportTerms,
                        AnchorGames = cluster.AnchorGames,
                        Confidence = cluster.Confidence,
                        Score = score
                    };
                })
                .Where(c => c != null)
                .OrderByDescending(c => c.Score)
                .ToList();
        }

        private static IEnumerable<string> CandidateEvidence(ExternalRecommendation deal)
        {
            if (deal == null)
                return Enumerable.Empty<string>();
            return (deal.CandidateTags ?? new List<string>())
                .Concat(deal.CandidateGenres ?? new List<string>())
                .Concat(deal.CandidateThemes ?? new List<string>())
                .Concat(deal.CandidateFeatures ?? new List<string>())
                .Concat(deal.MechanicTags ?? new List<string>())
                .Concat(deal.MoodTags ?? new List<string>())
                .Concat(string.IsNullOrWhiteSpace(deal.CandidateDescription)
                    ? Enumerable.Empty<string>()
                    : new[] { deal.CandidateDescription });
        }

        private static IEnumerable<string> CandidateEvidence(ExternalCandidateProfile profile)
        {
            if (profile == null)
                return Enumerable.Empty<string>();
            return (profile.Tags ?? new List<string>())
                .Concat(profile.Genres ?? new List<string>())
                .Concat(profile.Themes ?? new List<string>())
                .Concat(profile.Features ?? new List<string>())
                .Concat(profile.Mechanics ?? new List<string>())
                .Concat(profile.Moods ?? new List<string>())
                .Concat(string.IsNullOrWhiteSpace(profile.Description)
                    ? Enumerable.Empty<string>()
                    : new[] { profile.Description });
        }

        private static IEnumerable<string> GameEvidence(EnrichedGame game)
        {
            if (game == null)
                return Enumerable.Empty<string>();
            return (game.Tags ?? new List<string>())
                .Concat(game.Genres ?? new List<string>())
                .Concat(game.Themes ?? new List<string>())
                .Concat(game.Features ?? new List<string>())
                .Concat(game.Keywords ?? new List<string>())
                .Concat(game.AlgorithmicTags ?? new List<string>())
                .Concat(game.SteamRecommendedTags ?? new List<string>())
                .Concat(string.IsNullOrWhiteSpace(game.Description)
                    ? Enumerable.Empty<string>()
                    : new[] { game.Description });
        }

        private static void AddDerivedSignals(List<string> target, IEnumerable<string> evidence, IEnumerable<SignalRule> rules)
        {
            var evidenceList = (evidence ?? Enumerable.Empty<string>()).ToList();
            foreach (var rule in rules ?? Enumerable.Empty<SignalRule>())
            {
                var terms = (rule.CoreTerms ?? new string[0])
                    .Concat(rule.SupportTerms ?? new string[0]);
                if (terms.Any(term => EvidenceMatches(evidenceList, term)))
                    AddDistinct(target, rule.Name);
            }
        }

        private static bool EvidenceMatches(IEnumerable<string> evidence, string term)
        {
            if (string.IsNullOrWhiteSpace(term))
                return false;
            return (evidence ?? Enumerable.Empty<string>())
                .Any(value => StrongTermMatch(value, term));
        }

        private static bool StrongTermMatch(string value, string term)
        {
            if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(term))
                return false;
            return value.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   term.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsGenericSupportTerm(string term)
        {
            if (string.IsNullOrWhiteSpace(term))
                return true;
            var normalized = Regex.Replace(term, @"[\s\-_]+", " ").Trim();
            return GenericSupportTerms.Any(generic =>
                string.Equals(normalized, Regex.Replace(generic, @"[\s\-_]+", " ").Trim(), StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsConcreteFitTerm(string term)
        {
            if (string.IsNullOrWhiteSpace(term))
                return false;
            var normalized = Regex.Replace(term, @"[\s\-_]+", " ").Trim();
            return ConcreteFitTerms.Any(concrete =>
                string.Equals(normalized, Regex.Replace(concrete, @"[\s\-_]+", " ").Trim(), StringComparison.OrdinalIgnoreCase));
        }

        private static void AddTasteTerms(Dictionary<string, double> terms, Dictionary<string, double> source, double multiplier)
        {
            if (source == null) return;
            foreach (var kvp in source)
            {
                if (string.IsNullOrWhiteSpace(kvp.Key)) continue;
                var key = kvp.Key.Trim();
                if (!terms.ContainsKey(key)) terms[key] = 0;
                terms[key] += kvp.Value * multiplier;
            }
        }

        private static bool TitleMatchesTerm(string title, HashSet<string> titleWords, string term)
        {
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(term)) return false;
            if (title.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            var words = Tokenize(term).Where(w => w.Length >= 4).ToList();
            return words.Any() && words.All(titleWords.Contains);
        }

        private static double ScoreTitleSimilarity(string dealTitle, List<EnrichedGame> ownedGames, out string similarTo)
        {
            similarTo = null;
            var dealWords = new HashSet<string>(Tokenize(dealTitle).Where(w => w.Length >= 4), StringComparer.OrdinalIgnoreCase);
            if (!dealWords.Any()) return 0;

            double best = 0;
            foreach (var game in ownedGames.Where(g => g.PlaytimeSeconds >= 18000).OrderByDescending(g => g.PlaytimeSeconds).Take(50))
            {
                var ownedWords = new HashSet<string>(Tokenize(game.Name).Where(w => w.Length >= 4), StringComparer.OrdinalIgnoreCase);
                if (!ownedWords.Any()) continue;
                var overlap = dealWords.Intersect(ownedWords).Count();
                var score = overlap / (double)Math.Max(dealWords.Count, ownedWords.Count);
                if (score > best && overlap >= 1)
                {
                    best = score;
                    similarTo = game.Name;
                }
            }
            return best >= 0.34 ? best * 0.8 : 0;
        }

        private static double ScoreRelevantTitleSimilarity(string dealTitle, List<EnrichedGame> ownedGames, out string similarTo)
        {
            similarTo = null;
            var relevantOwned = (ownedGames ?? new List<EnrichedGame>())
                .Where(HasVisualNovelGameSignal)
                .ToList();
            return ScoreTitleSimilarity(dealTitle, relevantOwned, out similarTo);
        }

        private double RejectedTitlePenalty(string dealTitle)
        {
            var dealWords = Tokenize(dealTitle).Where(w => w.Length >= 4).ToList();
            if (!dealWords.Any())
                return 0;

            foreach (var rejected in settings.RejectedGames ?? new List<RejectedGameFeedback>())
            {
                var rejectedWords = Tokenize(rejected?.Name).Where(w => w.Length >= 4).ToList();
                if (!rejectedWords.Any())
                    continue;

                var overlap = dealWords.Intersect(rejectedWords, StringComparer.OrdinalIgnoreCase).Count();
                var ratio = overlap / (double)Math.Max(dealWords.Count, rejectedWords.Count);
                if (overlap >= 2 && ratio >= 0.5)
                    return 0.45;
            }

            return 0;
        }

        private static double ScoreSourceCategoryFit(ExternalRecommendation deal, TasteProfile profile, out string reason)
        {
            reason = null;
            return 0;
        }

        private static bool PassesSourceRelevanceGate(ExternalRecommendation deal, TasteProfile profile)
        {
            if (!IsVisualNovelStore(deal?.Store))
                return true;
            return HasVisualNovelProfileSignal(profile);
        }

        private static bool HasVisualNovelProfileSignal(TasteProfile profile)
        {
            if (profile == null)
                return false;

            return VisualNovelTasteTerms.Any(term => HasProfileTaste(profile, term));
        }

        private static bool HasVisualNovelGameSignal(EnrichedGame game)
        {
            if (game == null)
                return false;

            return ContainsVisualNovelSignal(game.Tags) ||
                   ContainsVisualNovelSignal(game.Genres) ||
                   ContainsVisualNovelSignal(game.Themes) ||
                   ContainsVisualNovelSignal(game.Features) ||
                   ContainsVisualNovelSignal(game.Keywords) ||
                   ContainsVisualNovelSignal(game.AlgorithmicTags);
        }

        private static bool ContainsVisualNovelSignal(IEnumerable<string> values)
        {
            return (values ?? Enumerable.Empty<string>())
                .Any(IsVisualNovelTasteTerm);
        }

        private static bool IsVisualNovelTasteTerm(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            return VisualNovelTasteTerms.Any(term =>
                string.Equals(value, term, StringComparison.OrdinalIgnoreCase) ||
                value.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0 ||
                term.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool HasProfileTaste(TasteProfile profile, string term)
        {
            if (profile == null || string.IsNullOrWhiteSpace(term))
                return false;

            return HasWeightedTaste(profile.TagWeights, term) ||
                   HasWeightedTaste(profile.ThemeWeights, term) ||
                   HasWeightedTaste(profile.GenreWeights, term) ||
                   HasWeightedTaste(profile.FeatureWeights, term);
        }

        private static bool HasWeightedTaste(Dictionary<string, double> weights, string term)
        {
            if (weights == null)
                return false;

            return weights.Any(kvp =>
                kvp.Value > 0.03 &&
                (string.Equals(kvp.Key, term, StringComparison.OrdinalIgnoreCase) ||
                 kvp.Key.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0 ||
                 term.IndexOf(kvp.Key, StringComparison.OrdinalIgnoreCase) >= 0));
        }

        private async Task<string> FindSteamAppIdAsync(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return null;
            var key = "steam_deal_lookup_" + NormalizeTitle(title);
            if (cache.TryGet<string>(key, out var cached))
                return cached;

            try
            {
                var url = $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(title)}&cc=US&l=english";
                var json = await http.GetStringAsync(url);
                var parsed = Serialization.FromJson<SteamSearchResponse>(json);
                var match = parsed?.items?
                    .Where(i => i != null && i.id > 0 && !string.IsNullOrWhiteSpace(i.name))
                    .OrderBy(i => TitleDistance(NormalizeTitle(title), NormalizeTitle(i.name)))
                    .FirstOrDefault();

                var appId = match != null && TitleDistance(NormalizeTitle(title), NormalizeTitle(match.name)) <= 4
                    ? match.id.ToString()
                    : string.Empty;
                cache.Set(key, appId, TimeSpan.FromDays(7));
                return appId;
            }
            catch (Exception ex)
            {
                logger.Warn(ex, $"Steam deal lookup failed for {title}");
                cache.Set(key, string.Empty, TimeSpan.FromHours(2));
                return null;
            }
        }

        private async Task<SteamReviewSummary> GetSteamReviewSummaryAsync(string appId)
        {
            if (string.IsNullOrWhiteSpace(appId)) return null;
            var cacheKey = SteamReviewCacheKey(appId);
            if (cache.TryGet<SteamReviewSummary>(cacheKey, out var cached))
                return cached;

            try
            {
                var url = $"https://store.steampowered.com/appreviews/{appId}?json=1&language=all&purchase_type=all&filter=summary";
                var json = await http.GetStringAsync(url);
                var response = Serialization.FromJson<SteamReviewResponse>(json);
                var summary = response?.query_summary;
                if (summary == null)
                {
                    cache.Set<SteamReviewSummary>(cacheKey, null, TimeSpan.FromHours(2));
                    return null;
                }

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
                cache.Set(cacheKey, result, TimeSpan.FromDays(7));
                return result;
            }
            catch (Exception ex)
            {
                logger.Warn(ex, $"Steam review fetch failed for deal appId {appId}");
                cache.Set<SteamReviewSummary>(cacheKey, null, TimeSpan.FromHours(2));
                return null;
            }
        }

        private static string SteamReviewCacheKey(string appId)
            => $"steam_reviews_{appId}";

        private static void ApplySteamQuality(ExternalRecommendation deal, SteamReviewSummary reviews)
        {
            if (deal == null || reviews == null)
                return;

            deal.ReviewPercent = reviews.PositivePercent;
            deal.ReviewCount = reviews.TotalReviews;
            deal.QualityLabel = reviews.ReviewScoreDescription ?? BuildQualityLabel(reviews);
        }

        private static void ApplyRawgQualityIfUseful(ExternalRecommendation deal)
        {
            if (deal == null || !deal.RawgRating.HasValue)
                return;
            var hasSteamQuality = deal.ReviewPercent.HasValue && deal.ReviewCount.GetValueOrDefault() >= 30;
            var hasVndbQuality = deal.VndbRating.HasValue && deal.VndbVoteCount.GetValueOrDefault() >= 30;
            if (hasSteamQuality || hasVndbQuality)
                return;

            if (!deal.ReviewPercent.HasValue && deal.RawgRatingsCount.GetValueOrDefault() >= 20)
            {
                deal.ReviewPercent = (int)Math.Round(Math.Max(0, Math.Min(5, deal.RawgRating.Value)) * 20);
                deal.ReviewCount = deal.RawgRatingsCount;
            }

            var label = RawgQualityLabel(deal);
            if (!string.IsNullOrWhiteSpace(label))
                deal.QualityLabel = label;
        }

        private static string RawgQualityLabel(ExternalRecommendation deal)
            => deal?.RawgRating.HasValue == true
                ? RawgQualityLabel(new RawgGameData
                {
                    Rating = deal.RawgRating,
                    RatingsCount = deal.RawgRatingsCount,
                    Metacritic = deal.RawgMetacritic
                })
                : string.Empty;

        private static string RawgQualityLabel(RawgGameData rawg)
        {
            if (rawg?.Rating.HasValue != true)
                return string.Empty;
            var label = $"RAWG {rawg.Rating.Value:0.0}/5";
            if (rawg.RatingsCount.HasValue)
                label += $" from {rawg.RatingsCount.Value} ratings";
            if (rawg.Metacritic.HasValue)
                label += $", Metacritic {rawg.Metacritic.Value}";
            return label;
        }

        private async Task ApplyVndbQualityAsync(ExternalRecommendation deal)
        {
            var match = await FindVndbMatchAsync(deal?.Title);
            if (match == null)
                return;

            deal.VndbId = match.id;
            deal.VndbTitle = match.title;
            deal.VndbRating = match.rating;
            deal.VndbVoteCount = match.votecount;

            if (!deal.VndbRating.HasValue)
                return;

            var label = deal.VndbVoteCount.HasValue
                ? $"VNDB {deal.VndbRating.Value / 10.0:0.0}/10 from {deal.VndbVoteCount.Value} votes"
                : $"VNDB {deal.VndbRating.Value / 10.0:0.0}/10";

            if (string.Equals(deal.QualityLabel, "Quality unknown", StringComparison.OrdinalIgnoreCase))
                deal.QualityLabel = label;

            if (deal.VndbVoteCount.GetValueOrDefault() >= 30 && deal.VndbRating.Value >= 75)
                deal.Reasons.Add("Strong VNDB rating: " + label);
            else if (deal.VndbVoteCount.GetValueOrDefault() >= 30 && deal.VndbRating.Value < 60)
                deal.Reasons.Add("Risky VNDB rating: " + label);
        }

        private async Task<VndbVisualNovel> FindVndbMatchAsync(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return null;

            var cleanTitle = CleanVndbSearchTitle(title);
            if (string.IsNullOrWhiteSpace(cleanTitle))
                return null;

            var cacheKey = "vndb_vn_quality_" + NormalizeTitle(cleanTitle);
            if (cache.TryGet<VndbVisualNovel>(cacheKey, out var cached))
                return cached;

            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, "https://api.vndb.org/kana/vn"))
                {
                    request.Headers.UserAgent.ParseAdd("BacklogBeater/1.0");
                    request.Content = new StringContent(Serialization.ToJson(new
                    {
                        filters = new object[] { "search", "=", cleanTitle },
                        fields = "id,title,rating,votecount",
                        sort = "searchrank",
                        results = 3
                    }), Encoding.UTF8, "application/json");

                    var response = await http.SendAsync(request);
                    response.EnsureSuccessStatusCode();
                    var parsed = Serialization.FromJson<VndbSearchResponse>(await response.Content.ReadAsStringAsync());
                    var match = (parsed?.results ?? new List<VndbVisualNovel>())
                        .Where(v => !string.IsNullOrWhiteSpace(v?.title))
                        .OrderBy(v => TitleDistance(NormalizeTitle(cleanTitle), NormalizeTitle(v.title)))
                        .FirstOrDefault();

                    if (match != null && TitleDistance(NormalizeTitle(cleanTitle), NormalizeTitle(match.title)) <= 8)
                    {
                        cache.Set(cacheKey, match, TimeSpan.FromDays(7));
                        return match;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, $"VNDB lookup failed for {title}");
            }

            cache.Set<VndbVisualNovel>(cacheKey, null, TimeSpan.FromDays(7));
            return null;
        }

        private static double DealBoost(ExternalRecommendation deal)
        {
            var cut = Math.Min((deal.Cut ?? 0) / 100.0, 0.7) * 0.14;
            var price = deal.Price.HasValue && deal.Price.Value <= 10 ? 0.03 : 0;
            var history = IsAtHistoricalLow(deal) ? 0.12 : 0;
            var nearHistoricalLow = !IsAtHistoricalLow(deal) &&
                                    deal.Price.HasValue &&
                                    deal.HistoricalLowPrice.HasValue &&
                                    deal.Price.Value <= deal.HistoricalLowPrice.Value * 1.10m
                ? 0.06
                : 0;
            return cut + price + history + nearHistoricalLow;
        }

        private static void RemoveDealRankingSignalForNotOwned(ExternalRecommendation deal)
        {
            if (deal == null)
                return;
            deal.MatchScore -= deal.DealScore;
            deal.DealScore = 0;
        }

        private static double WishlistBoost(ExternalRecommendation deal)
            => deal?.IsWishlisted == true ? 0.22 : 0;

        private static bool HasCurrentDealSignal(ExternalRecommendation deal)
            => deal != null && HasDiscount(deal.Price, deal.RegularPrice, deal.Cut);

        private static bool HasDiscount(decimal? price, decimal? regular, int? cut)
            => cut.GetValueOrDefault() > 0 ||
               price.HasValue &&
               regular.HasValue &&
               regular.Value > price.Value;

        private static void AddReason(ExternalRecommendation deal, string reason)
        {
            if (deal == null || string.IsNullOrWhiteSpace(reason))
                return;
            if (deal.Reasons == null)
                deal.Reasons = new List<string>();
            if (!deal.Reasons.Contains(reason, StringComparer.OrdinalIgnoreCase))
                deal.Reasons.Add(reason);
        }

        private static void AddRange(List<string> target, IEnumerable<string> values)
        {
            if (target == null || values == null)
                return;
            foreach (var value in values)
                AddDistinct(target, value);
        }

        private static void AddDistinct(List<string> target, string value)
        {
            if (target == null || string.IsNullOrWhiteSpace(value))
                return;
            var clean = Regex.Replace(value, @"\s+", " ").Trim();
            if (!string.IsNullOrWhiteSpace(clean) && !target.Contains(clean, StringComparer.OrdinalIgnoreCase))
                target.Add(clean);
        }

        private static int DistinctCount(IEnumerable<string> values)
            => (values ?? Enumerable.Empty<string>())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

        private static bool HasCandidateProfile(ExternalRecommendation deal)
            => deal != null &&
               DistinctCount((deal.CandidateTags ?? new List<string>())
                   .Concat(deal.CandidateGenres ?? new List<string>())
                   .Concat(deal.CandidateThemes ?? new List<string>())
                   .Concat(deal.CandidateFeatures ?? new List<string>())
                   .Concat(deal.MechanicTags ?? new List<string>())
                   .Concat(deal.MoodTags ?? new List<string>())) > 0;

        private static ExternalCandidateKind ClassifyExternalCandidate(ExternalRecommendation deal)
        {
            if (deal == null)
                return ExternalCandidateKind.Unknown;

            if (!IsSteamGameType(deal.DealType))
                return ExternalCandidateKind.MediaPlayerOrUtility;

            var title = deal.Title ?? string.Empty;
            var evidence = CandidateEvidence(deal)
                .Concat(deal.SourceSignals ?? new List<string>())
                .Concat(new[] { title, deal.Store, deal.RecommendationKind })
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .ToList();

            if (ContainsAny(title, DemoTitleTerms) || EvidenceContainsAny(evidence, DemoTitleTerms))
                return ExternalCandidateKind.DemoPlaytestPrologue;
            if (ContainsAny(title, AddOnTitleTerms))
                return ExternalCandidateKind.DLCOrAddOn;
            if (ContainsAny(title, SoundtrackCosmeticTitleTerms))
                return ExternalCandidateKind.SoundtrackArtbookCosmetic;
            if (EvidenceContainsAny(evidence, MediaEvidenceTerms))
                return ExternalCandidateKind.MediaPlayerOrUtility;
            if (EvidenceContainsAny(evidence, UtilityEvidenceTerms))
                return ExternalCandidateKind.GameAdjacentTool;
            if (EvidenceContainsAny(evidence, EditorEvidenceTerms))
                return ExternalCandidateKind.EditorCreatorTool;

            return HasCandidateProfile(deal)
                ? ExternalCandidateKind.PlayableGame
                : ExternalCandidateKind.Unknown;
        }

        private static bool IsPlayableCandidateKind(ExternalRecommendation deal)
            => deal != null && deal.CandidateKind == ExternalCandidateKind.PlayableGame;

        private static bool EvidenceContainsAny(IEnumerable<string> evidence, IEnumerable<string> terms)
        {
            var values = (evidence ?? Enumerable.Empty<string>())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .ToList();
            return (terms ?? Enumerable.Empty<string>()).Any(term =>
                values.Any(value => value.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0));
        }

        private static bool ContainsAny(string value, IEnumerable<string> terms)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;
            return (terms ?? Enumerable.Empty<string>()).Any(term =>
                value.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool IsSteamSimilarCandidate(ExternalRecommendation deal)
            => (deal?.SourceSignals ?? new List<string>()).Any(s =>
                s.IndexOf("Steam similar", StringComparison.OrdinalIgnoreCase) >= 0);

        private static bool HasConcreteMechanicOrMood(ExternalRecommendation deal)
            => (deal?.MechanicTags ?? new List<string>())
                .Concat(deal?.MoodTags ?? new List<string>())
                .Any(IsConcreteFitTerm);

        private static bool HasStrongMetadataConfidence(ExternalRecommendation deal)
            => HasCandidateProfile(deal) &&
               DistinctCount((deal.CandidateGenres ?? new List<string>())
                   .Concat(deal.CandidateTags ?? new List<string>())
                   .Concat(deal.CandidateFeatures ?? new List<string>())
                   .Concat(deal.MechanicTags ?? new List<string>())
                   .Concat(deal.MoodTags ?? new List<string>())) >= 3;

        private static string GetAdmissionRejectionReason(ExternalRecommendation deal, string context)
        {
            if (deal == null)
                return "null";

            if (deal.CandidateKind == ExternalCandidateKind.Unknown)
                deal.CandidateKind = ClassifyExternalCandidate(deal);

            if (deal.CandidateKind == ExternalCandidateKind.Unknown &&
                deal.IsWishlisted &&
                IsStrongQuality(deal) &&
                HasConcreteTasteReason(deal))
                return null;

            if (!IsPlayableCandidateKind(deal))
                return "candidate kind: " + deal.CandidateKind;

            if (IsSteamSimilarCandidate(deal))
            {
                if (!HasStrongMetadataConfidence(deal))
                    return "weak Steam-similar metadata";
                if (!HasConcreteMechanicOrMood(deal))
                    return "weak Steam-similar relation";
                if (!HasConcreteTasteReason(deal))
                    return "weak Steam-similar reason";
            }

            return null;
        }

        private bool ShouldFilterEarlyAccess(ExternalRecommendation deal)
        {
            if (deal == null || !deal.IsEarlyAccess)
                return false;
            if (settings.HideEarlyAccessRecommendations)
                return true;
            return settings.HideStaleEarlyAccessRecommendations && IsStaleEarlyAccess(deal);
        }

        private static bool IsStaleEarlyAccess(ExternalRecommendation deal)
        {
            return !deal.LastContentUpdateUtc.HasValue ||
                   DateTime.UtcNow - deal.LastContentUpdateUtc.Value.ToUniversalTime() > StaleEarlyAccessAge;
        }

        private static bool IsEarlyAccessProfile(ExternalCandidateProfile profile)
        {
            if (profile == null)
                return false;

            var evidence = CandidateEvidence(profile)
                .Concat(new[] { profile.Title, profile.Description })
                .Where(v => !string.IsNullOrWhiteSpace(v));

            return evidence.Any(v =>
                v.IndexOf("early access", StringComparison.OrdinalIgnoreCase) >= 0 ||
                v.IndexOf("early-access", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool HasOnlyGenericProfileEvidence(ExternalRecommendation deal)
        {
            if (deal == null || !HasCandidateProfile(deal))
                return false;

            var evidence = (deal.CandidateTags ?? new List<string>())
                .Concat(deal.CandidateGenres ?? new List<string>())
                .Concat(deal.CandidateThemes ?? new List<string>())
                .Concat(deal.CandidateFeatures ?? new List<string>())
                .Concat(deal.MechanicTags ?? new List<string>())
                .Concat(deal.MoodTags ?? new List<string>())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return evidence.Any() && evidence.All(IsGenericSupportTerm);
        }

        private static bool IsCurrentDealOnlyEvidence(ExternalRecommendation deal)
        {
            if (deal == null)
                return false;
            var sourceOnly = (deal.SourceSignals ?? new List<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .All(s => s.IndexOf("deal", StringComparison.OrdinalIgnoreCase) >= 0 ||
                          s.IndexOf("sale", StringComparison.OrdinalIgnoreCase) >= 0 ||
                          s.IndexOf("ITAD", StringComparison.OrdinalIgnoreCase) >= 0 ||
                          s.IndexOf("store", StringComparison.OrdinalIgnoreCase) >= 0);
            return HasCurrentDealSignal(deal) &&
                   sourceOnly &&
                   !deal.IsWishlisted &&
                   !HasConcreteTasteReason(deal);
        }

        private static string CandidateProfileCacheKey(ExternalRecommendation deal)
        {
            if (!string.IsNullOrWhiteSpace(deal?.SteamAppId))
                return "external_candidate_profile_v3_steam_" + deal.SteamAppId.Trim();
            if (!string.IsNullOrWhiteSpace(deal?.ItadId))
                return "external_candidate_profile_v3_itad_" + NormalizeTitle(deal.ItadId);
            return "external_candidate_profile_v3_" + NormalizeTitle((deal?.Store ?? string.Empty) + "_" + (deal?.Title ?? string.Empty));
        }

        private static string ParseSteamAppId(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return null;
            var match = Regex.Match(url, @"/app/(?<id>\d+)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups["id"].Value : null;
        }

        private static string StripHtml(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;
            var text = Regex.Replace(value, "<[^>]+>", " ");
            text = WebUtility.HtmlDecode(text);
            return Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();
        }

        private static bool IsAtHistoricalLow(ExternalRecommendation deal)
        {
            if (deal == null || !deal.Price.HasValue || !deal.HistoricalLowPrice.HasValue)
                return false;
            return deal.Price.Value <= deal.HistoricalLowPrice.Value + 0.01m;
        }

        private static double SourceConfidenceBoost(ExternalRecommendation deal)
        {
            if (!IsVisualNovelStore(deal?.Store))
                return 0;
            if (!deal.Price.HasValue || !deal.RegularPrice.HasValue || !deal.Cut.HasValue)
                return 0;
            if (string.IsNullOrWhiteSpace(deal.Url))
                return 0;
            return 0.22;
        }

        private static double MinimumNoReviewScore(ExternalRecommendation deal)
            => IsVisualNovelStore(deal?.Store) ? 1.05 : 1.2;

        private static double QualityBoost(ExternalRecommendation deal)
        {
            var vndb = VndbQualityBoost(deal);
            if (!deal.ReviewPercent.HasValue || deal.ReviewCount.GetValueOrDefault() < 25)
                return vndb;

            double steam;
            if (deal.ReviewPercent.Value >= 85) steam = 0.55;
            else if (deal.ReviewPercent.Value >= 75) steam = 0.35;
            else if (deal.ReviewPercent.Value >= 65) steam = 0.15;
            else if (deal.ReviewPercent.Value < 55) steam = -0.7;
            else steam = -0.25;
            return Math.Max(steam, vndb);
        }

        private static double VndbQualityBoost(ExternalRecommendation deal)
        {
            if (deal?.VndbRating == null || deal.VndbVoteCount.GetValueOrDefault() < 30)
                return 0;
            if (deal.VndbRating.Value >= 80) return 0.45;
            if (deal.VndbRating.Value >= 75) return 0.32;
            if (deal.VndbRating.Value >= 65) return 0.12;
            if (deal.VndbRating.Value < 55) return -0.7;
            return -0.25;
        }

        private static bool IsStrongQuality(ExternalRecommendation deal)
            => deal.ReviewPercent.HasValue && deal.ReviewCount.GetValueOrDefault() >= 50 && deal.ReviewPercent.Value >= 75 ||
               deal.VndbRating.HasValue && deal.VndbVoteCount.GetValueOrDefault() >= 30 && deal.VndbRating.Value >= 75;

        private static bool IsWeakQuality(ExternalRecommendation deal)
            => deal.ReviewPercent.HasValue && deal.ReviewCount.GetValueOrDefault() >= 25 && deal.ReviewPercent.Value < 60 ||
               deal.VndbRating.HasValue && deal.VndbVoteCount.GetValueOrDefault() >= 30 && deal.VndbRating.Value < 60;

        private static bool IsLowQualityDeal(ExternalRecommendation deal)
            => deal.ReviewPercent.HasValue && deal.ReviewCount.GetValueOrDefault() >= 25 && deal.ReviewPercent.Value < 55 ||
               deal.VndbRating.HasValue && deal.VndbVoteCount.GetValueOrDefault() >= 30 && deal.VndbRating.Value < 55;

        private static bool IsThinReviewCoverage(ExternalRecommendation deal)
            => deal != null &&
               deal.ReviewPercent.HasValue &&
               deal.ReviewCount.GetValueOrDefault() > 0 &&
               deal.ReviewCount.GetValueOrDefault() < 50 &&
               !IsStrongQuality(deal);

        private static string BuildQualityLabel(SteamReviewSummary reviews)
            => reviews.PositivePercent.HasValue ? $"{reviews.PositivePercent}% positive" : "Quality unknown";

        private static bool IsVisualNovelStore(string store)
            => string.Equals(store, "JAST Store", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(store, "MangaGamer", StringComparison.OrdinalIgnoreCase);

        private static bool IsCredibleParsedStoreDeal(ExternalRecommendation deal)
        {
            if (deal == null || string.IsNullOrWhiteSpace(deal.Title) || string.IsNullOrWhiteSpace(deal.Url))
                return false;
            if (!deal.Price.HasValue || !deal.RegularPrice.HasValue || !deal.Cut.HasValue)
                return false;
            if (deal.Price.Value <= 0 || deal.RegularPrice.Value <= 0 || deal.Price.Value >= deal.RegularPrice.Value)
                return false;
            if (deal.Cut.Value <= 0 || deal.Cut.Value > 95)
                return false;
            return IsRealGameDeal(deal);
        }

        private static HashSet<string> Tokenize(string value)
            => new HashSet<string>(
                Regex.Matches((value ?? string.Empty).ToLowerInvariant(), "[a-z0-9]+")
                    .Cast<Match>()
                    .Select(m => m.Value)
                    .Where(w => w.Length > 2 && !GenericTasteTerms.Contains(w)),
                StringComparer.OrdinalIgnoreCase);

        private static List<string> ToTextLinesWithLinks(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
                return new List<string>();

            var text = Regex.Replace(
                html,
                "<a\\s+[^>]*href=[\"'](?<url>[^\"']+)[\"'][^>]*>",
                m => Environment.NewLine + "[[URL:" + WebUtility.HtmlDecode(m.Groups["url"].Value) + "]]",
                RegexOptions.IgnoreCase);
            text = Regex.Replace(text, "</a>", "[[/URL]]" + Environment.NewLine, RegexOptions.IgnoreCase);
            text = Regex.Replace(text, "<br\\s*/?>", Environment.NewLine, RegexOptions.IgnoreCase);
            text = Regex.Replace(text, "</(h\\d|p|div|li|section|article)>", Environment.NewLine, RegexOptions.IgnoreCase);
            text = Regex.Replace(text, "<[^>]+>", " ");
            text = WebUtility.HtmlDecode(text);
            return Regex.Split(text, @"\r?\n")
                .Select(l => Regex.Replace(l, @"\s+", " ").Trim())
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();
        }

        private static string ExtractLink(ref string line)
        {
            if (line == null)
                return string.Empty;

            var match = Regex.Match(line, @"\[\[URL:(?<url>[^\]]+)\]\]");
            if (!match.Success)
                return string.Empty;

            line = Regex.Replace(line, @"\[\[URL:[^\]]+\]\]|\[\[/URL\]\]", string.Empty).Trim();
            return match.Groups["url"].Value.Trim();
        }

        private static string ExtractImageTitle(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return string.Empty;

            var match = Regex.Match(line, @"Image:\s*(?<title>.+)$", RegexOptions.IgnoreCase);
            return match.Success ? CleanStoreTitle(match.Groups["title"].Value) : string.Empty;
        }

        private static int? ParsePercent(string line)
        {
            var match = Regex.Match(line ?? string.Empty, @"-?\s*(?<cut>\d{1,2})\s*%\s*(?:OFF)?", RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups["cut"].Value, out var cut) && cut > 0)
                return cut;
            return null;
        }

        private static bool TryParsePricePair(string line, out decimal regular, out decimal price)
        {
            regular = 0;
            price = 0;
            var matches = Regex.Matches(line ?? string.Empty, @"\$?\s*(?<price>\d+\.\d{2})");
            if (matches.Count < 2)
                return false;

            return decimal.TryParse(matches[0].Groups["price"].Value, out regular) &&
                   decimal.TryParse(matches[1].Groups["price"].Value, out price);
        }

        private static string NextNonEmptyLine(List<string> lines, int start)
        {
            for (var i = start; i < (lines?.Count ?? 0); i++)
            {
                var line = lines[i];
                if (!string.IsNullOrWhiteSpace(line))
                    return line;
            }
            return string.Empty;
        }

        private static bool IsLikelyStoreTitle(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return false;
            var value = line.Trim();
            if (value.Length < 3 || value.Length > 120)
                return false;
            if (value.IndexOf("[[URL:", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            if (ParsePercent(value).HasValue || Regex.IsMatch(value, @"\$?\d+\.\d{2}"))
                return false;

            var lower = value.ToLowerInvariant();
            var blocked = new[]
            {
                "store", "discover", "search", "support", "english / usd", "homepage", "cart", "account",
                "filters", "featured", "reset filters", "show only", "on sale now", "winter sale",
                "browse by", "hot this week", "newly released games", "upcoming games", "highly rated games"
            };
            return !blocked.Any(b => lower == b || lower.StartsWith(b + " "));
        }

        private static string CleanStoreTitle(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;
            value = Regex.Replace(value, @"\[\[/URL\]\]", string.Empty);
            value = Regex.Replace(value, @"\s+", " ").Trim();
            return value;
        }

        private static string CleanVndbSearchTitle(string value)
        {
            value = CleanStoreTitle(value);
            value = Regex.Replace(value, @"\b(complete|uncensored|adult|steam|mangagamer|jast|store|edition|deluxe|bundle)\b", string.Empty, RegexOptions.IgnoreCase);
            value = Regex.Replace(value, @"\s+", " ").Trim(' ', '-', ':');
            return value;
        }

        private static string MakeAbsoluteUrl(string sourceUrl, string link)
        {
            if (string.IsNullOrWhiteSpace(link))
                return sourceUrl ?? string.Empty;
            if (Uri.TryCreate(link, UriKind.Absolute, out var absolute))
                return absolute.AbsoluteUri;
            if (Uri.TryCreate(sourceUrl, UriKind.Absolute, out var baseUri) &&
                Uri.TryCreate(baseUri, link, out var combined))
                return combined.AbsoluteUri;
            return link;
        }

        public List<ExternalRecommendation> ApplyPostAiSafety(
            IEnumerable<ExternalRecommendation> recommendations,
            IEnumerable<EnrichedGame> ownedGames,
            out int removed)
        {
            removed = 0;
            var owned = BuildOwnedIndex(ownedGames);
            var kept = new List<ExternalRecommendation>();
            foreach (var recommendation in recommendations ?? Enumerable.Empty<ExternalRecommendation>())
            {
                if (recommendation == null ||
                    !IsRealGameDeal(recommendation) ||
                    IsOwnedExternal(recommendation, owned) ||
                    IsRejectedDeal(recommendation) ||
                    IsBlacklistedDeal(recommendation) ||
                    MatchesBlockedTag(recommendation.Title) ||
                    MatchesBlockedTag(recommendation.Reasons) ||
                    ShouldFilterEarlyAccess(recommendation) ||
                    !string.IsNullOrWhiteSpace(GetAdmissionRejectionReason(recommendation, "post-ai")) ||
                    HasInvalidVisibleExternalReason(recommendation))
                {
                    removed++;
                    continue;
                }

                kept.Add(recommendation);
            }
            return kept;
        }

        internal static bool TestIsRealGameDeal(ExternalRecommendation deal)
            => IsRealGameDeal(deal);

        internal static ExternalCandidateKind TestClassifyExternalCandidate(ExternalRecommendation deal)
            => ClassifyExternalCandidate(deal);

        internal static string TestGetAdmissionRejectionReason(ExternalRecommendation deal, string context = "test")
            => GetAdmissionRejectionReason(deal, context);

        internal static bool TestHasInvalidVisibleExternalReason(ExternalRecommendation recommendation)
            => HasInvalidVisibleExternalReason(recommendation);

        internal static bool TestIsOwnedExternal(ExternalRecommendation deal, IEnumerable<EnrichedGame> ownedGames)
            => IsOwnedExternal(deal, BuildOwnedIndex(ownedGames));

        private static bool HasInvalidVisibleExternalReason(ExternalRecommendation recommendation)
        {
            var primary = recommendation?.PrimaryReason;
            if (string.IsNullOrWhiteSpace(primary))
                return true;
            if (primary.StartsWith("Similar profile signal", StringComparison.OrdinalIgnoreCase) ||
                primary.StartsWith("Similar title signal", StringComparison.OrdinalIgnoreCase))
                return true;
            if (primary.IndexOf("loosely", StringComparison.OrdinalIgnoreCase) >= 0 ||
                primary.IndexOf("weak fit", StringComparison.OrdinalIgnoreCase) >= 0 ||
                primary.IndexOf("mostly a utility", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            return false;
        }

        private static string NormalizeTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return string.Empty;
            var lower = title.ToLowerInvariant();
            lower = Regex.Replace(lower, @"\b(the|a|an|edition|standard|deluxe|ultimate|goty|game of the year|remastered|definitive|enhanced|complete|digital|collector'?s?|anniversary|gold|royal|director'?s?\s+cut|bundle|pack)\b", "");
            lower = Regex.Replace(lower, @"[^a-z0-9]+", "");
            return lower.Trim();
        }

        private static OwnedLibraryIndex BuildOwnedIndex(IEnumerable<EnrichedGame> ownedGames)
        {
            var index = new OwnedLibraryIndex();
            foreach (var game in ownedGames ?? Enumerable.Empty<EnrichedGame>())
            {
                if (game == null)
                    continue;

                var normalized = NormalizeTitle(game.Name);
                if (!string.IsNullOrWhiteSpace(normalized))
                    index.Titles.Add(normalized);

                if (!string.IsNullOrWhiteSpace(game.SteamAppId))
                    index.SteamAppIds.Add(game.SteamAppId.Trim());
            }
            return index;
        }

        private static bool IsOwnedExternal(ExternalRecommendation deal, OwnedLibraryIndex owned)
        {
            if (deal == null || owned == null)
                return false;

            if (!string.IsNullOrWhiteSpace(deal.SteamAppId) &&
                owned.SteamAppIds.Contains(deal.SteamAppId.Trim()))
                return true;

            var normalized = NormalizeTitle(deal.Title);
            if (string.IsNullOrWhiteSpace(normalized))
                return false;
            if (owned.Titles.Contains(normalized))
                return true;

            return owned.Titles.Any(title => IsEditionVariantTitleMatch(title, normalized));
        }

        private static bool IsEditionVariantTitleMatch(string ownedTitle, string candidateTitle)
        {
            if (string.IsNullOrWhiteSpace(ownedTitle) || string.IsNullOrWhiteSpace(candidateTitle))
                return false;
            if (ownedTitle.Length < 8 || candidateTitle.Length < 8)
                return false;
            if (ownedTitle.StartsWith(candidateTitle, StringComparison.OrdinalIgnoreCase))
                return ownedTitle.Length - candidateTitle.Length <= 14;
            if (candidateTitle.StartsWith(ownedTitle, StringComparison.OrdinalIgnoreCase))
                return candidateTitle.Length - ownedTitle.Length <= 14;
            return false;
        }

        private static string DeduplicationKey(ExternalRecommendation deal)
        {
            if (!string.IsNullOrWhiteSpace(deal?.SteamAppId))
                return "steam:" + deal.SteamAppId.Trim();
            if (!string.IsNullOrWhiteSpace(deal?.ItadId))
                return "itad:" + deal.ItadId.Trim();
            if (!string.IsNullOrWhiteSpace(deal?.Url))
                return "url:" + NormalizeUrlForKey(deal.Url);
            return NormalizeTitle(deal?.Title);
        }

        private static string NormalizeUrlForKey(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return string.Empty;
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
                return NormalizeTitle(url);
            return uri.GetLeftPart(UriPartial.Path).TrimEnd('/').ToLowerInvariant();
        }

        private bool IsRejectedDeal(ExternalRecommendation deal)
        {
            if (deal == null) return false;
            return (settings.RejectedGames ?? new List<RejectedGameFeedback>())
                .Any(r => MatchesFeedback(deal, r.Name, r.SteamAppId));
        }

        private bool IsBlacklistedDeal(ExternalRecommendation deal)
        {
            if (deal == null) return false;
            return (settings.BlacklistedGames ?? new List<BlacklistedGame>())
                .Any(g => MatchesFeedback(deal, g.Name, g.SteamAppId));
        }

        private bool MatchesBlockedTag(string value)
            => MatchesBlockedTag(new[] { value });

        private bool MatchesBlockedTag(IEnumerable<string> values)
        {
            var blockedTags = settings.BlacklistedTags ?? new List<string>();
            if (!blockedTags.Any()) return false;
            return blockedTags.Any(tag => values.Any(value => StrongTextMatch(value, tag)));
        }

        private static bool MatchesFeedback(ExternalRecommendation deal, string title, string steamAppId)
        {
            if (!string.IsNullOrWhiteSpace(steamAppId) &&
                !string.IsNullOrWhiteSpace(deal.SteamAppId) &&
                string.Equals(steamAppId, deal.SteamAppId, StringComparison.OrdinalIgnoreCase))
                return true;

            var blockedTitle = NormalizeTitle(title);
            return !string.IsNullOrWhiteSpace(blockedTitle) &&
                   string.Equals(blockedTitle, NormalizeTitle(deal.Title), StringComparison.OrdinalIgnoreCase);
        }

        private static bool StrongTextMatch(string value, string blockedTerm)
        {
            if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(blockedTerm))
                return false;

            var normalizedValue = value.ToLowerInvariant();
            var normalizedTerm = blockedTerm.Trim().ToLowerInvariant();
            if (normalizedTerm.Length < 4)
                return false;
            if (normalizedValue.IndexOf(normalizedTerm, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            var termWords = Tokenize(normalizedTerm).Where(w => w.Length >= 4).ToList();
            if (termWords.Count == 0)
                return false;
            var valueWords = Tokenize(normalizedValue);
            return termWords.All(valueWords.Contains);
        }

        private static int TitleDistance(string a, string b)
        {
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return 0;
            if (string.IsNullOrEmpty(a)) return b?.Length ?? 0;
            if (string.IsNullOrEmpty(b)) return a.Length;
            if (a.Contains(b) || b.Contains(a)) return Math.Abs(a.Length - b.Length);
            return Math.Max(a.Length, b.Length);
        }

        private class ItadDealsResponse
        {
            public List<ItadDealRaw> list { get; set; }
            public ItadDealsData data { get; set; }
        }

        private class ItadDealsData
        {
            public List<ItadDealRaw> list { get; set; }
        }

        private class ItadDealRaw
        {
            public string id { get; set; }
            public string slug { get; set; }
            public string type { get; set; }
            public string title { get; set; }
            public string plain { get; set; }
            public string url { get; set; }
            public string shop_name { get; set; }
            public int? cut { get; set; }
            public decimal? price_new { get; set; }
            public decimal? price_old { get; set; }
            public ItadAssets assets { get; set; }
            public ItadShop shop { get; set; }
            public ItadUrls urls { get; set; }
            public ItadGame game { get; set; }
            public ItadPrice price { get; set; }
            public ItadPrice regular { get; set; }
            public ItadDealNested deal { get; set; }
        }

        private class ItadDealNested
        {
            public string url { get; set; }
            public int? cut { get; set; }
            public ItadShop shop { get; set; }
            public ItadPrice price { get; set; }
            public ItadPrice regular { get; set; }
        }

        private class ItadGame
        {
            public string id { get; set; }
            public string slug { get; set; }
            public string type { get; set; }
            public string title { get; set; }
            public string plain { get; set; }
            public ItadAssets assets { get; set; }
        }

        private class ItadAssets
        {
            public string boxart { get; set; }
            public string banner145 { get; set; }
            public string banner300 { get; set; }
        }

        private class ItadShop
        {
            public string name { get; set; }
        }

        private class ItadUrls
        {
            public string game { get; set; }
        }

        private class ItadPrice
        {
            public decimal? amount { get; set; }
        }

        private class ItadHistoryLowResponse
        {
            public string id { get; set; }
            public ItadHistoryLow low { get; set; }
        }

        private class ItadHistoryLow
        {
            public ItadShop shop { get; set; }
            public ItadPrice price { get; set; }
            public ItadPrice regular { get; set; }
            public int? cut { get; set; }
            public string timestamp { get; set; }
        }

        private class VndbSearchResponse
        {
            public List<VndbVisualNovel> results { get; set; }
        }

        private class VndbVisualNovel
        {
            public string id { get; set; }
            public string title { get; set; }
            public double? rating { get; set; }
            public int? votecount { get; set; }
        }

        private class SteamSearchResponse
        {
            public List<SteamSearchItem> items { get; set; }
        }

        private class SteamWishlistItem
        {
            public string name { get; set; }
            public string capsule { get; set; }
            public string capsule_image { get; set; }
            public string review_desc { get; set; }
            public int review_percent { get; set; }
            public int review_count { get; set; }
            public List<SteamWishlistSub> subs { get; set; }
        }

        private class SteamWishlistSub
        {
            public int price { get; set; }
            public int discount_original_price { get; set; }
            public int discount_pct { get; set; }
        }

        private class SteamAppDetailsEnvelope
        {
            public bool success { get; set; }
            public SteamAppDetailsData data { get; set; }
        }

        private class SteamAppDetailsData
        {
            public string name { get; set; }
            public string type { get; set; }
            public string short_description { get; set; }
            public string detailed_description { get; set; }
            public List<SteamAppDetailItem> genres { get; set; }
            public List<SteamAppDetailItem> categories { get; set; }
        }

        private class SteamAppDetailItem
        {
            public int id { get; set; }
            public string description { get; set; }
        }

        private class SteamSearchItem
        {
            public int id { get; set; }
            public string name { get; set; }
            public string tiny_image { get; set; }
        }

        private class SteamReviewResponse
        {
            public SteamReviewQuerySummary query_summary { get; set; }
        }

        private class SteamNewsResponse
        {
            public SteamNewsApp appnews { get; set; }
        }

        private class SteamNewsApp
        {
            public List<SteamNewsItem> newsitems { get; set; }
        }

        private class SteamNewsItem
        {
            public long date { get; set; }
        }

        private class QualityCandidate
        {
            public QualityCandidate(ExternalRecommendation deal, double score)
            {
                Deal = deal;
                Score = score;
            }

            public ExternalRecommendation Deal { get; }
            public double Score { get; }
        }

        private class ExternalCandidateProfile
        {
            public string Title { get; set; }
            public string Store { get; set; }
            public string AppType { get; set; }
            public string Description { get; set; }
            public bool IsEarlyAccess { get; set; }
            public DateTime? LastContentUpdateUtc { get; set; }
            public List<string> Tags { get; set; } = new List<string>();
            public List<string> Genres { get; set; } = new List<string>();
            public List<string> Themes { get; set; } = new List<string>();
            public List<string> Features { get; set; } = new List<string>();
            public List<string> Mechanics { get; set; } = new List<string>();
            public List<string> Moods { get; set; } = new List<string>();
            public List<string> SourceSignals { get; set; } = new List<string>();
            public int EvidenceQuality { get; set; }
        }

        private class TasteCluster
        {
            public string Name { get; set; }
            public List<string> Terms { get; set; } = new List<string>();
            public List<string> CoreTerms { get; set; } = new List<string>();
            public List<string> SupportTerms { get; set; } = new List<string>();
            public List<string> MatchedCoreTerms { get; set; } = new List<string>();
            public List<string> MatchedSupportTerms { get; set; } = new List<string>();
            public List<string> AnchorGames { get; set; } = new List<string>();
            public double Confidence { get; set; }
            public double Score { get; set; }
        }

        private class SignalRule
        {
            public SignalRule(string name, params string[] terms)
            {
                Name = name;
                CoreTerms = terms ?? new string[0];
                SupportTerms = new string[0];
                AnchorHints = new string[0];
            }

            public string Name { get; set; }
            public string[] CoreTerms { get; set; }
            public string[] SupportTerms { get; set; }
            public string[] AnchorHints { get; set; }
        }

        private class CandidateProfileDiagnostics
        {
            public CandidateProfileDiagnostics(string context)
            {
                Context = context ?? "external";
            }

            public string Context { get; }
            public int Candidates { get; set; }
            public int Profiled { get; set; }
            public int NoProfile { get; set; }
            public int Failed { get; set; }
            public int CacheHits { get; set; }
            public int CacheMisses { get; set; }

            public void RecordCache(bool hit)
            {
                if (hit)
                    CacheHits++;
                else
                    CacheMisses++;
            }

            public string Format()
                => $"External candidate profiles ({Context}): candidates={Candidates}, profiled={Profiled}, noProfile={NoProfile}, failed={Failed}, cacheHits={CacheHits}, cacheMisses={CacheMisses}";
        }

        private class QualityDiagnosticsResult
        {
            public int WithSteamAppId { get; set; }
            public int ReviewCoverage { get; set; }
            public int VndbLookups { get; set; }
            public bool ReviewCacheHit { get; set; }
        }

        private class ExternalQualityDiagnostics
        {
            public int Candidates { get; set; }
            public int WithSteamAppId { get; set; }
            public int ReviewCoverage { get; set; }
            public int VndbLookups { get; set; }
            public int ReviewCacheHits { get; set; }
            public int ReviewCacheMisses { get; set; }
            public int Failed { get; set; }

            public void Record(QualityDiagnosticsResult result)
            {
                if (result == null)
                    return;
                WithSteamAppId += result.WithSteamAppId;
                ReviewCoverage += result.ReviewCoverage;
                VndbLookups += result.VndbLookups;
                if (result.WithSteamAppId > 0)
                {
                    if (result.ReviewCacheHit)
                        ReviewCacheHits++;
                    else
                        ReviewCacheMisses++;
                }
            }

            public string Format()
                => $"External quality enrichment: candidates={Candidates}, steamAppIds={WithSteamAppId}, reviewCoverage={ReviewCoverage}, vndbLookups={VndbLookups}, reviewCacheHits={ReviewCacheHits}, reviewCacheMisses={ReviewCacheMisses}, failed={Failed}";
        }

        private class OwnedLibraryIndex
        {
            public HashSet<string> Titles { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> SteamAppIds { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private class DealDiagnostics
        {
            private readonly Dictionary<string, int> credibilityRejections =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            public int RawDeals { get; set; }
            public int FilteredJunk { get; set; }
            public int FilteredNoCurrentDeal { get; set; }
            public int FilteredOwned { get; set; }
            public int FilteredRejected { get; set; }
            public int FilteredBlacklisted { get; set; }
            public int FilteredBlockedTag { get; set; }
            public int FilteredSourceRelevance { get; set; }
            public int PrefilteredDeals { get; set; }
            public int ProfiledDeals { get; set; }
            public int QualityCandidates { get; set; }
            public int PostQualityJunk { get; set; }
            public int PostQualityEarlyAccess { get; set; }
            public int PostQualityRejected { get; set; }
            public int PostQualityOwned { get; set; }
            public int PostQualityBlacklisted { get; set; }
            public int PostQualityBlockedTag { get; set; }
            public int DisplayedDeals { get; set; }

            public void AddCredibilityRejection(string reason)
            {
                if (string.IsNullOrWhiteSpace(reason))
                    return;
                if (!credibilityRejections.ContainsKey(reason))
                    credibilityRejections[reason] = 0;
                credibilityRejections[reason]++;
            }

            public void AddAdmissionRejection(string reason)
            {
                AddCredibilityRejection("admission " + reason);
            }

            public string Format()
            {
                var postQualityFilters = new List<string>
                {
                    $"junk={PostQualityJunk}",
                    $"earlyAccess={PostQualityEarlyAccess}",
                    $"rejected={PostQualityRejected}",
                    $"owned={PostQualityOwned}",
                    $"blacklisted={PostQualityBlacklisted}",
                    $"blockedTags={PostQualityBlockedTag}"
                };
                postQualityFilters.AddRange(credibilityRejections
                    .OrderBy(kvp => kvp.Key)
                    .Select(kvp => $"{kvp.Key}={kvp.Value}"));

                return "Deal diagnostics: " +
                       $"raw={RawDeals}, " +
                       $"prefiltered={PrefilteredDeals}, " +
                       $"profiled={ProfiledDeals}, " +
                       $"qualityEnriched={QualityCandidates}, " +
                       $"displayed={DisplayedDeals}, " +
                       $"preFilters[junk={FilteredJunk}, noCurrentDeal={FilteredNoCurrentDeal}, owned={FilteredOwned}, rejected={FilteredRejected}, blacklisted={FilteredBlacklisted}, blockedTags={FilteredBlockedTag}, sourceRelevance={FilteredSourceRelevance}], " +
                       $"postQualityFilters[{string.Join(", ", postQualityFilters)}]";
            }

            public string DisplaySummary(string label)
            {
                var filtered = Math.Max(0, RawDeals - DisplayedDeals);
                return $"Found {DisplayedDeals} strong {label}; filtered {filtered} weak or blocked candidates.";
            }
        }

        private class NotOwnedDiagnostics
        {
            private readonly Dictionary<string, int> credibilityRejections =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            public int RawCandidates { get; set; }
            public int WishlistCandidates { get; set; }
            public int SteamSimilarCandidates { get; set; }
            public int FallbackDealCandidates { get; set; }
            public int FilteredJunk { get; set; }
            public int FilteredOwned { get; set; }
            public int FilteredRejected { get; set; }
            public int FilteredBlacklisted { get; set; }
            public int FilteredBlockedTag { get; set; }
            public int PrefilteredCandidates { get; set; }
            public int ProfiledCandidates { get; set; }
            public int QualityCandidates { get; set; }
            public int PostQualityJunk { get; set; }
            public int PostQualityEarlyAccess { get; set; }
            public int PostQualityRejected { get; set; }
            public int PostQualityOwned { get; set; }
            public int PostQualityBlacklisted { get; set; }
            public int PostQualityBlockedTag { get; set; }
            public int DisplayedCandidates { get; set; }

            public void AddCredibilityRejection(string reason)
            {
                if (string.IsNullOrWhiteSpace(reason))
                    return;
                if (!credibilityRejections.ContainsKey(reason))
                    credibilityRejections[reason] = 0;
                credibilityRejections[reason]++;
            }

            public void AddAdmissionRejection(string reason)
            {
                AddCredibilityRejection("admission " + reason);
            }

            public string Format()
            {
                var postQualityFilters = new List<string>
                {
                    $"junk={PostQualityJunk}",
                    $"earlyAccess={PostQualityEarlyAccess}",
                    $"rejected={PostQualityRejected}",
                    $"owned={PostQualityOwned}",
                    $"blacklisted={PostQualityBlacklisted}",
                    $"blockedTags={PostQualityBlockedTag}"
                };
                postQualityFilters.AddRange(credibilityRejections
                    .OrderBy(kvp => kvp.Key)
                    .Select(kvp => $"{kvp.Key}={kvp.Value}"));

                return "Not-owned diagnostics: " +
                       $"raw={RawCandidates}, " +
                       $"sources[wishlist={WishlistCandidates}, steamSimilar={SteamSimilarCandidates}, fallbackDeals={FallbackDealCandidates}], " +
                       $"prefiltered={PrefilteredCandidates}, " +
                       $"profiled={ProfiledCandidates}, " +
                       $"qualityEnriched={QualityCandidates}, " +
                       $"displayed={DisplayedCandidates}, " +
                       $"preFilters[junk={FilteredJunk}, owned={FilteredOwned}, rejected={FilteredRejected}, blacklisted={FilteredBlacklisted}, blockedTags={FilteredBlockedTag}], " +
                       $"postQualityFilters[{string.Join(", ", postQualityFilters)}]";
            }

            public string DisplaySummary(string label)
            {
                var filtered = Math.Max(0, RawCandidates - DisplayedCandidates);
                return $"Found {DisplayedCandidates} strong {label}; filtered {filtered} weak or blocked candidates.";
            }
        }

        private class SteamReviewQuerySummary
        {
            public string review_score_desc { get; set; }
            public int total_positive { get; set; }
            public int total_negative { get; set; }
            public int total_reviews { get; set; }
        }
    }
}
