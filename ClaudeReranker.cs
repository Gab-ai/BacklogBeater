using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Playnite.SDK;
using Playnite.SDK.Data;

namespace GameRecommender
{
    /// <summary>
    /// Sends the top N fused candidates + the user's taste profile to the configured AI provider
    /// and asks it to re-rank them with natural-language reasoning per game.
    ///
    /// Claude doesn't replace the scoring — it refines the final ranking using
    /// holistic judgment: noticing duplicate clusters, surfacing genre variety,
    /// writing reasons that explain *why* a game fits this specific person.
    ///
    /// Result is cached for 24 hours. Only runs when the selected provider is configured.
    /// </summary>
    public class ClaudeReranker
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        private readonly DiskCache cache;
        private readonly RecommenderSettings settings;

        private const string ClaudeModel = "claude-sonnet-4-20250514";
        private const string ReasonPromptVersion = "reason_v2_plain_match_explanations";
        private const string StartedReasonVersion = "started_reason_v2_continue_playing";
        private const string ExternalReasonVersion = "external_reason_v1_fit_first";
        private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

        public ClaudeReranker(DiskCache cache, RecommenderSettings settings)
        {
            this.cache = cache;
            this.settings = settings;
        }

        public bool IsEnabled =>
            settings.AiRerankerEnabled &&
            ((IsOpenAi && !string.IsNullOrWhiteSpace(settings.OpenAiApiKey) && !string.IsNullOrWhiteSpace(settings.OpenAiModel)) ||
             (!IsOpenAi && !string.IsNullOrWhiteSpace(settings.AnthropicApiKey)));

        public string ProviderName => IsOpenAi ? "OpenAI" : "Claude";
        private bool IsOpenAi => string.Equals(settings.AiProvider, "OpenAI", StringComparison.OrdinalIgnoreCase);

        public async Task TestConnectionAsync()
        {
            if (!IsEnabled)
                throw new InvalidOperationException($"{ProviderName} re-ranking is not fully configured.");
            var response = IsOpenAi
                ? await CallOpenAiAsync("Reply with only this JSON array: []")
                : await CallClaudeAsync("Reply with only this JSON array: []");
            if (string.IsNullOrWhiteSpace(response))
                throw new InvalidOperationException($"{ProviderName} returned an empty response.");
        }

        /// <summary>
        /// Re-ranks the top candidates. Returns the original list unchanged if
        /// AI is disabled, the key is missing, or the API call fails.
        /// </summary>
        public async Task<List<ScoredGame>> ReRankAsync(
            List<ScoredGame> candidates,
            TasteProfile tasteProfile)
        {
            if (!IsEnabled || !candidates.Any()) return candidates;

            var topN = candidates.Take(settings.AiCandidateCount).ToList();
            var cacheKey = BuildCacheKey(topN, tasteProfile);

            if (cache.TryGet<AiRerankerCache>(cacheKey, out var cached))
            {
                logger.Info("Claude reranker: using 24h cache");
                return ApplyCachedRanking(candidates, cached);
            }

            try
            {
                logger.Info($"{ProviderName} reranker: calling API for {topN.Count} candidates");
                var prompt = BuildPrompt(topN, tasteProfile);
                var response = IsOpenAi ? await CallOpenAiAsync(prompt) : await CallClaudeAsync(prompt);
                var ranked = ParseResponse(response);

                if (ranked == null || !ranked.Any())
                {
                    throw new InvalidOperationException($"{ProviderName} returned an empty or unparseable ranking");
                }

                var aiCache = new AiRerankerCache { GeneratedAt = DateTime.UtcNow, Items = ranked };
                cache.Set(cacheKey, aiCache, CacheTtl);

                return ApplyCachedRanking(candidates, aiCache);
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"{ProviderName} reranker: API call failed");
                throw;
            }
        }

        public async Task<List<AiStartedReasonItem>> GetStartedGameReasonsAsync(
            List<ScoredGame> candidates,
            TasteProfile tasteProfile)
        {
            if (!IsEnabled || candidates == null || !candidates.Any())
                return new List<AiStartedReasonItem>();

            var items = candidates.Take(12).ToList();
            var cacheKey = BuildStartedReasonCacheKey(items, tasteProfile);
            if (cache.TryGet<AiStartedReasonCache>(cacheKey, out var cached))
            {
                logger.Info($"{ProviderName} started-game reasons: using 24h cache");
                return cached.Items ?? new List<AiStartedReasonItem>();
            }

            try
            {
                var prompt = BuildStartedReasonPrompt(items, tasteProfile);
                var response = IsOpenAi ? await CallOpenAiAsync(prompt) : await CallClaudeAsync(prompt);
                var reasons = ParseStartedReasonResponse(response);
                if (reasons == null || !reasons.Any())
                    return new List<AiStartedReasonItem>();

                cache.Set(cacheKey, new AiStartedReasonCache
                {
                    GeneratedAt = DateTime.UtcNow,
                    Items = reasons
                }, CacheTtl);
                return reasons;
            }
            catch (Exception ex)
            {
                logger.Warn(ex, $"{ProviderName} section reasons failed");
                return new List<AiStartedReasonItem>();
            }
        }

        public async Task<List<ExternalRecommendation>> ReRankExternalAsync(
            List<ExternalRecommendation> candidates,
            TasteProfile tasteProfile,
            List<EnrichedGame> ownedGames,
            string contextLabel)
        {
            if (!IsEnabled || candidates == null || !candidates.Any())
                return candidates ?? new List<ExternalRecommendation>();

            var topN = candidates.Take(40).ToList();
            var cacheKey = BuildExternalCacheKey(topN, tasteProfile, contextLabel);
            if (cache.TryGet<AiRerankerCache>(cacheKey, out var cached))
            {
                logger.Info($"{ProviderName} external reranker: using 24h cache for {contextLabel}");
                return ApplyExternalCachedRanking(candidates, cached);
            }

            try
            {
                logger.Info($"{ProviderName} external reranker: calling API for {topN.Count} {contextLabel} candidates");
                var prompt = BuildExternalPrompt(topN, tasteProfile, ownedGames, contextLabel);
                var response = IsOpenAi ? await CallOpenAiAsync(prompt) : await CallClaudeAsync(prompt);
                var ranked = ParseResponse(response);
                if (ranked == null || !ranked.Any())
                    return candidates;

                var allowed = new HashSet<string>(topN.Select(c => c.Title), StringComparer.OrdinalIgnoreCase);
                ranked = ranked
                    .Where(i => i != null && !string.IsNullOrWhiteSpace(i.Name) && allowed.Contains(i.Name))
                    .OrderBy(i => i.Rank <= 0 ? int.MaxValue : i.Rank)
                    .Take(20)
                    .ToList();
                if (!ranked.Any())
                    return candidates;

                var aiCache = new AiRerankerCache { GeneratedAt = DateTime.UtcNow, Items = ranked };
                cache.Set(cacheKey, aiCache, CacheTtl);
                return ApplyExternalCachedRanking(candidates, aiCache);
            }
            catch (Exception ex)
            {
                logger.Warn(ex, $"{ProviderName} external reranker failed for {contextLabel}");
                return candidates;
            }
        }

        // ── Prompt construction ──────────────────────────────────────────

        private static string BuildPrompt(List<ScoredGame> candidates, TasteProfile profile)
        {
            var sb = new StringBuilder();

            sb.AppendLine("You are a game recommendation assistant with deep knowledge of video games.");
            sb.AppendLine();
            sb.AppendLine("## The user's play history (most played first):");
            foreach (var name in profile.TopPlayedNames)
                sb.AppendLine($"- {name}");

            sb.AppendLine();
            sb.AppendLine("## Candidate games to re-rank (pre-scored by algorithm):");
            for (int i = 0; i < candidates.Count; i++)
            {
                var g = candidates[i].Game;
                sb.AppendLine($"{i + 1}. {g.Name}");
                if (g.Genres.Any()) sb.AppendLine($"   Genres: {string.Join(", ", g.Genres)}");
                if (g.AlgorithmicTags.Any()) sb.AppendLine($"   Niche tags: {string.Join(", ", g.AlgorithmicTags)}");
                if (g.Keywords.Any()) sb.AppendLine($"   Keywords: {string.Join(", ", g.Keywords.Take(6))}");
                if (g.Features.Any()) sb.AppendLine($"   Features: {string.Join(", ", g.Features.Take(3))}");
                if (!string.IsNullOrWhiteSpace(g.Description))
                    sb.AppendLine($"   Description: {g.Description.Substring(0, Math.Min(120, g.Description.Length))}...");
                sb.AppendLine($"   Platform: {g.SourcePlugin ?? "Unknown"} | Played: {RecommendationEngine.FormatTime(g.PlaytimeSeconds)}");
                sb.AppendLine($"   Quality: {candidates[i].QualityLabel} | Category: {candidates[i].RecommendationCategory}");
            }

            sb.AppendLine();
            sb.AppendLine("## Your task:");
            sb.AppendLine("Re-rank these games for this specific user. Consider:");
            sb.AppendLine("- How well each game matches the user's demonstrated preferences");
            sb.AppendLine("- Genre diversity (don't cluster 5 co-op shooters at the top)");
            sb.AppendLine("- Quality of match (a perfect fit beats a marginal one regardless of genre)");
            sb.AppendLine("- Review quality and reliability; do not rescue weakly reviewed, playtest, beta, demo, or temporary builds");
            sb.AppendLine("- Niche tags and keywords; prefer precise matches over broad surface genres");
            sb.AppendLine("- Unplayed games that are very likely to be hits should rank high");
            sb.AppendLine("- Use plain recommendation language; do not mention internal scoring signals");
            sb.AppendLine();
            sb.AppendLine("Respond ONLY with a JSON array. No preamble, no markdown fences, no explanation outside the JSON.");
            sb.AppendLine("Format:");
            sb.AppendLine("[");
            sb.AppendLine("  {\"name\": \"Game Name\", \"rank\": 1, \"reason\": \"One second-person sentence using you/your\"},");
            sb.AppendLine("  ...");
            sb.AppendLine("]");
            sb.AppendLine();
            sb.AppendLine("Include ALL games from the list. Reasons must be specific to this user's history, not generic.");
            sb.AppendLine("Write every reason directly to the user in second person: use you/your. Do not refer to the user as they, them, or their.");

            return sb.ToString();
        }

        private static string BuildStartedReasonPrompt(List<ScoredGame> candidates, TasteProfile profile)
        {
            var sb = new StringBuilder();
            sb.AppendLine("You write short reasons for already-started games in a Continue Playing section.");
            sb.AppendLine();
            sb.AppendLine("## User's most-played games:");
            foreach (var name in profile.TopPlayedNames)
                sb.AppendLine($"- {name}");

            sb.AppendLine();
            sb.AppendLine("Explain why the user should continue or return to each already-started game.");
            sb.AppendLine("Focus on the actual use case: finishing a finite story, quick session, co-op night, open-ended goals, repeatable missions, builds, exploration, progression, or competitive play.");
            sb.AppendLine("Do not mention signal, hook, score, category, metadata, algorithm, or tags as internal concepts.");
            sb.AppendLine("Intent must be one of: Finishable, SessionGame, CoopMultiplayer, LongTermProgression, SandboxSim, ReturnLater.");
            sb.AppendLine();
            sb.AppendLine("## Games:");
            for (int i = 0; i < candidates.Count; i++)
            {
                var scored = candidates[i];
                var game = scored.Game;
                sb.AppendLine($"{i + 1}. {game.Name}");
                sb.AppendLine($"   Played: {RecommendationEngine.FormatTime(game.PlaytimeSeconds)} | Source: {game.SourcePlugin ?? "Unknown"}");
                sb.AppendLine($"   Local intent: {scored.StartedIntent}");
                if (game.AlgorithmicTags.Any()) sb.AppendLine($"   Descriptors: {string.Join(", ", game.AlgorithmicTags.Take(6))}");
                if (game.Genres.Any()) sb.AppendLine($"   Genres: {string.Join(", ", game.Genres.Take(5))}");
                if (game.Features.Any()) sb.AppendLine($"   Features: {string.Join(", ", game.Features.Take(5))}");
                if (scored.Reasons.Any()) sb.AppendLine($"   Local reasons: {string.Join(" | ", scored.Reasons.Take(3))}");
            }

            sb.AppendLine();
            sb.AppendLine("Respond ONLY with a JSON array:");
            sb.AppendLine("[");
            sb.AppendLine("  {\"name\":\"Game Name\",\"reason\":\"One concise sentence to the user\",\"intent\":\"Finishable\"}");
            sb.AppendLine("]");
            return sb.ToString();
        }

        private static string BuildExternalPrompt(
            List<ExternalRecommendation> candidates,
            TasteProfile profile,
            List<EnrichedGame> ownedGames,
            string contextLabel)
        {
            var sb = new StringBuilder();
            sb.AppendLine("You are re-ranking external game recommendations for a Playnite user.");
            sb.AppendLine("Only rank games from the candidate list. Do not invent games, prices, tags, ownership, or review data.");
            sb.AppendLine("These candidates were already filtered by deterministic rules. You may return fewer than 20 if some are weak.");
            sb.AppendLine();
            sb.AppendLine("## Priority order");
            sb.AppendLine("1. Fit to the user's actual taste, mechanics, mood, and play context.");
            sb.AppendLine("2. Review quality and evidence confidence.");
            sb.AppendLine("3. Wishlist/deal value as secondary tie-breakers only.");
            sb.AppendLine("Demote broad/generic-only matches such as fantasy, first-person, survival, sandbox, simulator, or RPG unless paired with stronger evidence.");
            sb.AppendLine("Omit tools, utilities, video players, editors, avatar tools, activity apps, DLC, demos, soundtracks, and anything that is not clearly a playable game.");
            sb.AppendLine("Use only the listed similar owned anchors, mechanics, mood, tags, and deterministic reasons as evidence. Do not cite an owned game as an anchor unless it appears under Similar owned anchors for that candidate.");
            sb.AppendLine("If the bridge between a candidate and the user's taste feels weak, omit that candidate instead of writing a confident but flimsy reason.");
            if (string.Equals(contextLabel, "deals", StringComparison.OrdinalIgnoreCase))
                sb.AppendLine("For deals, discount size is never enough by itself. A strong fit with a modest discount beats a weak fit with a large discount.");

            sb.AppendLine();
            var recent = (ownedGames ?? new List<EnrichedGame>())
                .Where(g => g?.LastPlayed != null)
                .OrderByDescending(g => g.LastPlayed)
                .Take(8)
                .Select(g => g.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();
            if (recent.Any())
            {
                sb.AppendLine();
                sb.AppendLine("## Recently played");
                foreach (var name in recent)
                    sb.AppendLine($"- {name}");
            }

            sb.AppendLine();
            sb.AppendLine("## Dominant taste signals");
            AppendTopWeights(sb, "Genres", profile.GenreWeights, 8);
            AppendTopWeights(sb, "Tags", profile.TagWeights, 12);
            AppendTopWeights(sb, "Themes", profile.ThemeWeights, 8);
            AppendTopWeights(sb, "Features", profile.FeatureWeights, 8);
            sb.AppendLine("Only cite an owned game by name when it appears in that candidate's Similar owned anchors.");

            sb.AppendLine();
            sb.AppendLine($"## Candidate {contextLabel} games");
            for (int i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                sb.AppendLine($"{i + 1}. {c.Title}");
                sb.AppendLine($"   Store/source: {c.Store ?? "Unknown"} | Kind: {c.RecommendationKindText} | Candidate kind: {c.CandidateKind}");
                if (string.Equals(contextLabel, "deals", StringComparison.OrdinalIgnoreCase))
                    sb.AppendLine($"   Scores: fit={c.RelevanceScore:0.00}, quality={c.QualityScore:0.00}, deal={c.DealScore:0.00}, match={c.MatchScore:0.00}");
                else
                    sb.AppendLine($"   Scores: fit={c.RelevanceScore:0.00}, quality={c.QualityScore:0.00}, match={c.MatchScore:0.00}");
                if (c.IsWishlisted) sb.AppendLine("   Wishlist: yes");
                if (string.Equals(contextLabel, "deals", StringComparison.OrdinalIgnoreCase) &&
                    (c.Cut.HasValue || c.Price.HasValue || c.RegularPrice.HasValue))
                    sb.AppendLine($"   Deal: price={FormatNullablePrice(c.Price)}, regular={FormatNullablePrice(c.RegularPrice)}, discount={(c.Cut.HasValue ? c.Cut + "%" : "unknown")}");
                if (!string.IsNullOrWhiteSpace(c.QualityLabel))
                    sb.AppendLine($"   Quality: {c.QualityLabel}; reviews={c.ReviewPercent?.ToString() ?? "unknown"}%/{c.ReviewCount?.ToString() ?? "unknown"}");
                AppendList(sb, "Candidate genres", c.CandidateGenres, 8);
                AppendList(sb, "Candidate tags", c.CandidateTags, 10);
                AppendList(sb, "Mechanics", c.MechanicTags, 8);
                AppendList(sb, "Mood", c.MoodTags, 8);
                AppendList(sb, "Similar owned anchors", c.SimilarOwnedGames, 5);
                if (c.Reasons.Any()) sb.AppendLine($"   Deterministic reasons: {string.Join(" | ", c.Reasons.Take(4))}");
                if (!string.IsNullOrWhiteSpace(c.CandidateDescription))
                    sb.AppendLine($"   Description: {TrimForPrompt(c.CandidateDescription, 220)}");
            }

            sb.AppendLine();
            sb.AppendLine("Respond ONLY with a JSON array, no markdown fences:");
            sb.AppendLine("[");
            sb.AppendLine("  {\"name\":\"Exact Candidate Title\",\"rank\":1,\"reason\":\"One concrete second-person sentence tied to the user's anchors/mechanics/mood\"}");
            sb.AppendLine("]");
            sb.AppendLine("Return at most 20 items. Use exact candidate titles. Reasons must use you/your and must not mention internal scores, algorithm, candidate, metadata, or prompt.");
            return sb.ToString();
        }

        // ── API call ─────────────────────────────────────────────────────

        private async Task<string> CallClaudeAsync(string prompt)
        {
            var body = new
            {
                model = ClaudeModel,
                max_tokens = 2000,
                messages = new[] { new { role = "user", content = prompt } }
            };

            var json = Serialization.ToJson(body);
            var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
            req.Headers.Add("x-api-key", settings.AnthropicApiKey);
            req.Headers.Add("anthropic-version", "2023-06-01");
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");

            var resp = await http.SendAsync(req);
            resp.EnsureSuccessStatusCode();

            var responseJson = await resp.Content.ReadAsStringAsync();
            var parsed = Serialization.FromJson<AnthropicResponse>(responseJson);

            return parsed?.content?.FirstOrDefault()?.text ?? string.Empty;
        }

        private async Task<string> CallOpenAiAsync(string prompt)
        {
            var body = new
            {
                model = settings.OpenAiModel,
                input = prompt
            };

            var json = Serialization.ToJson(body);
            var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
            req.Headers.Add("Authorization", $"Bearer {settings.OpenAiApiKey}");
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");

            var resp = await http.SendAsync(req);
            resp.EnsureSuccessStatusCode();

            var responseJson = await resp.Content.ReadAsStringAsync();
            var parsed = Serialization.FromJson<OpenAiResponse>(responseJson);
            if (!string.IsNullOrWhiteSpace(parsed?.output_text)) return parsed.output_text;

            return parsed?.output?
                .SelectMany(o => o.content ?? new List<OpenAiContent>())
                .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.text))
                ?.text ?? string.Empty;
        }

        // ── Response parsing ─────────────────────────────────────────────

        private static List<AiRankedItem> ParseResponse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            try
            {
                // Strip potential markdown fences
                var clean = text.Trim();
                int start = clean.IndexOf('[');
                int end = clean.LastIndexOf(']');
                if (start < 0 || end < 0) return null;
                clean = clean.Substring(start, end - start + 1);
                return Serialization.FromJson<List<AiRankedItem>>(clean);
            }
            catch (Exception ex)
            {
                LogManager.GetLogger().Warn(ex, "Failed to parse Claude reranker JSON");
                return null;
            }
        }

        private static List<AiStartedReasonItem> ParseStartedReasonResponse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            try
            {
                var clean = text.Trim();
                int start = clean.IndexOf('[');
                int end = clean.LastIndexOf(']');
                if (start < 0 || end < 0) return null;
                clean = clean.Substring(start, end - start + 1);
                return Serialization.FromJson<List<AiStartedReasonItem>>(clean);
            }
            catch (Exception ex)
            {
                LogManager.GetLogger().Warn(ex, "Failed to parse section reason JSON");
                return null;
            }
        }

        // ── Apply AI ranking to original list ────────────────────────────

        private static List<ScoredGame> ApplyCachedRanking(
            List<ScoredGame> candidates,
            AiRerankerCache aiCache)
        {
            // Build rank lookup by name
            var rankByName = aiCache.Items
                .ToDictionary(i => i.Name, i => i, StringComparer.OrdinalIgnoreCase);

            // Apply AI reasons and ranks to matching candidates
            foreach (var game in candidates)
            {
                if (rankByName.TryGetValue(game.Game.Name, out var aiItem))
                {
                    var reason = NormalizeSecondPerson(aiItem.Reason);
                    if (!string.IsNullOrWhiteSpace(reason))
                    {
                        game.Reasons.Insert(0, reason.Trim());
                        game.Reasons = game.Reasons
                            .Where(r => !string.IsNullOrWhiteSpace(r))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Take(3)
                            .ToList();
                        game.AiRanked = true;
                    }
                }
            }

            // Re-sort: AI-ranked items first (by AI rank), then the rest by fused score
            var aiRanked = candidates
                .Where(g => rankByName.ContainsKey(g.Game.Name))
                .OrderBy(g => rankByName[g.Game.Name].Rank)
                .ToList();

            var rest = candidates
                .Where(g => !rankByName.ContainsKey(g.Game.Name))
                .OrderByDescending(g => g.FusedScore)
                .ToList();

            return aiRanked.Concat(rest).ToList();
        }

        private static string NormalizeSecondPerson(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) return reason;
            var text = reason;
            text = System.Text.RegularExpressions.Regex.Replace(text, "\\b[Tt]heir\\b", "your");
            text = System.Text.RegularExpressions.Regex.Replace(text, "\\b[Tt]hem\\b", "you");
            text = System.Text.RegularExpressions.Regex.Replace(text, "\\b[Tt]hey\\b", "you");
            text = System.Text.RegularExpressions.Regex.Replace(text, "\\bthis player'?s\\b", "your", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            text = System.Text.RegularExpressions.Regex.Replace(text, "\\bthis player\\b", "you", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return text;
        }

        private static List<ExternalRecommendation> ApplyExternalCachedRanking(
            List<ExternalRecommendation> candidates,
            AiRerankerCache aiCache)
        {
            var rankByName = (aiCache?.Items ?? new List<AiRankedItem>())
                .Where(i => !string.IsNullOrWhiteSpace(i?.Name))
                .GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var acceptedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var rec in candidates ?? new List<ExternalRecommendation>())
            {
                if (rec == null || !rankByName.TryGetValue(rec.Title, out var item))
                    continue;

                var reason = NormalizeSecondPerson(item.Reason)?.Trim();
                if (!IsUsableExternalReason(reason, rec))
                    continue;

                acceptedNames.Add(rec.Title);
                rec.AiRerankReason = reason;
                rec.AiRerankScore = item.Rank > 0 ? Math.Max(0, 21 - item.Rank) / 20.0 : 0;
                rec.Reasons = rec.Reasons
                    .Where(r => !IsExternalTasteReason(r))
                    .ToList();
                rec.Reasons.Insert(0, reason);
                rec.Reasons = rec.Reasons
                    .Where(r => !string.IsNullOrWhiteSpace(r))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(4)
                    .ToList();
            }

            var aiRanked = (candidates ?? new List<ExternalRecommendation>())
                .Where(c => c != null && acceptedNames.Contains(c.Title))
                .OrderBy(c => rankByName[c.Title].Rank <= 0 ? int.MaxValue : rankByName[c.Title].Rank)
                .ToList();

            if (!aiRanked.Any())
                return (candidates ?? new List<ExternalRecommendation>())
                    .Where(c => c != null)
                    .OrderByDescending(c => c.RelevanceScore)
                    .ThenByDescending(c => c.QualityScore)
                    .ThenByDescending(c => c.MatchScore)
                    .Take(20)
                    .ToList();

            return aiRanked.Take(20).ToList();
        }

        private static bool IsExternalTasteReason(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
                return false;
            return reason.StartsWith("Matches your", StringComparison.OrdinalIgnoreCase) ||
                   reason.StartsWith("Discovered from", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsUsableExternalReason(string reason, ExternalRecommendation recommendation)
        {
            if (string.IsNullOrWhiteSpace(reason))
                return false;
            var lower = reason.ToLowerInvariant();
            if (lower.Contains("algorithm") || lower.Contains("metadata") || lower.Contains("candidate") || lower.Contains("score"))
                return false;
            if (lower.Contains("loosely") || lower.Contains("weak fit") || lower.Contains("mostly a utility"))
                return false;
            if (!lower.Contains("you") && !lower.Contains("your"))
                return false;
            if (recommendation != null && recommendation.CandidateKind != ExternalCandidateKind.PlayableGame)
                return false;

            var evidence = (recommendation?.SimilarOwnedGames ?? new List<string>())
                .Concat(recommendation?.MechanicTags ?? new List<string>())
                .Concat(recommendation?.MoodTags ?? new List<string>())
                .Concat(recommendation?.CandidateTags ?? new List<string>())
                .Concat(recommendation?.CandidateGenres ?? new List<string>())
                .Concat(recommendation?.CandidateFeatures ?? new List<string>())
                .Where(v => !string.IsNullOrWhiteSpace(v) && v.Length >= 3)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (!evidence.Any())
                return true;

            return evidence.Any(item => lower.Contains(item.ToLowerInvariant()));
        }

        private static void AppendTopWeights(StringBuilder sb, string label, Dictionary<string, double> weights, int count)
        {
            var items = (weights ?? new Dictionary<string, double>())
                .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key))
                .OrderByDescending(kvp => kvp.Value)
                .Take(count)
                .Select(kvp => kvp.Key)
                .ToList();
            if (items.Any())
                sb.AppendLine($"{label}: {string.Join(", ", items)}");
        }

        private static void AppendList(StringBuilder sb, string label, IEnumerable<string> values, int count)
        {
            var items = (values ?? Enumerable.Empty<string>())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(count)
                .ToList();
            if (items.Any())
                sb.AppendLine($"   {label}: {string.Join(", ", items)}");
        }

        private static string FormatNullablePrice(decimal? price)
            => price.HasValue ? "$" + price.Value.ToString("0.##") : "unknown";

        private static string TrimForPrompt(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;
            var clean = value.Replace("\r", " ").Replace("\n", " ").Trim();
            return clean.Length <= maxLength ? clean : clean.Substring(0, maxLength) + "...";
        }

        // ── Cache key ────────────────────────────────────────────────────

        private string BuildCacheKey(List<ScoredGame> topN, TasteProfile profile)
        {
            var profileBits = string.Join("|",
                profile.GenreWeights.OrderBy(k => k.Key).Take(30).Select(k => $"g:{k.Key}:{k.Value:F3}")
                .Concat(profile.TagWeights.OrderBy(k => k.Key).Take(30).Select(k => $"t:{k.Key}:{k.Value:F3}"))
                .Concat(profile.ThemeWeights.OrderBy(k => k.Key).Take(20).Select(k => $"h:{k.Key}:{k.Value:F3}")));
            var raw = $"{ReasonPromptVersion}|{ProviderName}|{settings.OpenAiModel}|{string.Join("|", topN.Select(g => $"{g.Game.Name}:{g.FusedScore:F3}"))}|{profileBits}";
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
                var hash = BitConverter.ToString(bytes).Replace("-", "").Substring(0, 24).ToLowerInvariant();
                return $"ai_rerank_{hash}";
            }
        }

        private string BuildStartedReasonCacheKey(List<ScoredGame> candidates, TasteProfile profile)
        {
            var profileBits = string.Join("|", profile.TopPlayedNames.Take(10));
            var raw = $"{StartedReasonVersion}|{ProviderName}|{settings.OpenAiModel}|{string.Join("|", candidates.Select(g => $"{g.Game.Name}:{g.Game.PlaytimeSeconds}:{g.PrimaryTag}:{g.StartedIntent}"))}|{profileBits}";
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
                var hash = BitConverter.ToString(bytes).Replace("-", "").Substring(0, 24).ToLowerInvariant();
                return $"ai_section_reason_{hash}";
            }
        }

        private string BuildExternalCacheKey(List<ExternalRecommendation> candidates, TasteProfile profile, string contextLabel)
        {
            var profileBits = string.Join("|",
                profile.GenreWeights.OrderBy(k => k.Key).Take(20).Select(k => $"g:{k.Key}:{k.Value:F3}")
                .Concat(profile.TagWeights.OrderBy(k => k.Key).Take(30).Select(k => $"t:{k.Key}:{k.Value:F3}"))
                .Concat(profile.ThemeWeights.OrderBy(k => k.Key).Take(20).Select(k => $"h:{k.Key}:{k.Value:F3}")));
            var raw = $"{ExternalReasonVersion}|{ProviderName}|{settings.OpenAiModel}|{contextLabel}|{string.Join("|", candidates.Select(c => $"{c.Title}:{c.RelevanceScore:F2}:{c.QualityScore:F2}:{c.DealScore:F2}:{c.Cut}"))}|{profileBits}";
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
                var hash = BitConverter.ToString(bytes).Replace("-", "").Substring(0, 24).ToLowerInvariant();
                return $"ai_external_rerank_{hash}";
            }
        }

        // ── Response models ──────────────────────────────────────────────

        private class AnthropicResponse
        {
            public List<AnthropicContent> content { get; set; }
        }

        private class AnthropicContent
        {
            public string type { get; set; }
            public string text { get; set; }
        }

        private class OpenAiResponse
        {
            public string output_text { get; set; }
            public List<OpenAiOutput> output { get; set; }
        }

        private class OpenAiOutput
        {
            public List<OpenAiContent> content { get; set; }
        }

        private class OpenAiContent
        {
            public string type { get; set; }
            public string text { get; set; }
        }
    }
}
