using System;
using System.Collections.Generic;
using System.Linq;

namespace GameRecommender
{
    public static class RecommendationHeuristics
    {
        private static readonly string[] TemporaryBuildTerms =
        {
            "playtest", "beta", "demo", "prologue", "test server", "technical test",
            "public test", "server test", "alpha"
        };

        private static readonly string[] EndlessTerms =
        {
            "survival", "sandbox", "multiplayer", "mmo", "battle royale", "pvp",
            "online co-op", "co-op", "crafting", "base building", "colony sim",
            "simulation", "simulator", "space sim", "space simulation", "live service",
            "open world survival", "shooter", "hero shooter", "tactical shooter",
            "extraction shooter", "arena shooter", "looter shooter", "repeatable mission",
            "missions", "endgame", "grind", "account progression", "open-ended",
            "persistent", "massively multiplayer"
        };

        private static readonly string[] FinishableTerms =
        {
            "story rich", "single-player", "single player", "campaign", "narrative",
            "adventure", "visual novel", "puzzle", "metroidvania", "souls-like",
            "soulslike", "point & click", "linear"
        };

        private static readonly string[] WeakFinishableTerms =
        {
            "adventure", "narrative"
        };

        private static readonly string[] DerivedTags =
        {
            "Co-op survival craft",
            "Base building",
            "Extraction shooter",
            "Tactical shooter",
            "Hero shooter",
            "Boomer shooter",
            "Immersive sim",
            "Deckbuilder roguelike",
            "Bullet heaven",
            "Metroidvania",
            "Soulslike",
            "CRPG",
            "JRPG",
            "Isometric ARPG",
            "Factory automation",
            "Colony sim",
            "Management sim",
            "Cozy sim",
            "4X strategy",
            "Narrative adventure",
            "Psychological horror",
            "Precision platformer",
            "Open world survival",
            "Co-op campaign",
            "Action roguelite",
            "Arena shooter",
            "Anime shooter"
        };

        private static readonly HashSet<string> GenericTagSignals = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Singleplayer",
            "Single-player",
            "Multiplayer",
            "Co-op",
            "Cooperative",
            "Online Co-Op",
            "Local Co-Op",
            "Indie",
            "Adventure",
            "Action",
            "Casual",
            "Early Access",
            "Free to Play",
            "Great Soundtrack",
            "Atmospheric"
        };

        public static bool IsGenericTagSignal(string value)
            => !string.IsNullOrWhiteSpace(value) && GenericTagSignals.Contains(value.Trim());

        public static void ApplyAlgorithmicTags(EnrichedGame game)
        {
            if (game == null) return;
            var tags = new HashSet<string>(game.AlgorithmicTags ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (var tag in DerivedTags)
                tags.Remove(tag);

            var text = MetadataText(game);

            AddIfAll(tags, text, "Co-op survival craft", "survival", "craft");
            AddIfAny(tags, text, "Base building", "base building", "base-building");
            AddIfAny(tags, text, "Immersive sim", "immersive sim");
            AddIfAny(tags, text, "Bullet heaven", "bullet heaven", "survivors-like", "survivor-like");
            AddIfAny(tags, text, "Metroidvania", "metroidvania");
            AddIfAny(tags, text, "Soulslike", "souls-like", "soulslike");
            AddIfAny(tags, text, "Precision platformer", "precision platformer", "difficult platformer");

            if (IsExtractionShooter(text))
                tags.Add("Extraction shooter");
            if (ContainsAny(text, "hero shooter") || (ContainsAny(text, "hero", "champion") && ContainsAny(text, "shooter") && ContainsAny(text, "team-based", "team based")))
                tags.Add("Hero shooter");
            if (ContainsAny(text, "tactical shooter") || (ContainsAny(text, "tactical") && ContainsAny(text, "shooter") && !tags.Contains("Hero shooter")))
                tags.Add("Tactical shooter");
            if (ContainsAny(text, "arena shooter", "boomer shooter", "retro shooter"))
                tags.Add(ContainsAny(text, "boomer shooter", "retro shooter") ? "Boomer shooter" : "Arena shooter");
            if (ContainsAny(text, "deckbuilding roguelike", "deckbuilder roguelike", "deck-building roguelike") ||
                (ContainsAny(text, "deckbuild", "deck-build", "card battler") && ContainsAny(text, "roguelike", "roguelite")))
                tags.Add("Deckbuilder roguelike");
            if (ContainsAny(text, "crpg", "computer role-playing") || (ContainsAny(text, "party-based", "party based") && ContainsAny(text, "rpg", "role-playing")))
                tags.Add("CRPG");
            if (ContainsAny(text, "jrpg", "japanese role-playing") || (ContainsAny(text, "turn-based", "turn based") && ContainsAny(text, "rpg", "role-playing") && ContainsAny(text, "anime", "japanese")))
                tags.Add("JRPG");
            if (ContainsAny(text, "anime") && ContainsAny(text, "shooter", "third-person shooter", "tps"))
                tags.Add("Anime shooter");
            if (ContainsAny(text, "isometric") && ContainsAny(text, "action rpg", "arpg"))
                tags.Add("Isometric ARPG");
            if (ContainsAny(text, "automation", "factory", "production line") && !ContainsAny(text, "office automation"))
                tags.Add("Factory automation");
            if (ContainsAny(text, "colony sim", "colony simulation", "rimworld-like") || (ContainsAny(text, "colony", "settlement") && ContainsAny(text, "management", "simulation", "survival")))
                tags.Add("Colony sim");
            if (ContainsAny(text, "management sim", "tycoon") || (ContainsAny(text, "management") && ContainsAny(text, "simulation", "sim")))
                tags.Add("Management sim");
            if (ContainsAny(text, "cozy", "farming sim", "life sim"))
                tags.Add("Cozy sim");
            if (ContainsAny(text, "4x", "grand strategy"))
                tags.Add("4X strategy");
            if (ContainsAny(text, "story rich", "narrative adventure") || (ContainsAny(text, "narrative") && ContainsAny(text, "adventure")))
                tags.Add("Narrative adventure");
            if (ContainsAny(text, "psychological horror") || (ContainsAny(text, "psychological") && ContainsAny(text, "horror")))
                tags.Add("Psychological horror");

            if (ContainsAny(text, "survival") && ContainsAny(text, "open world"))
                tags.Add("Open world survival");
            if (ContainsAny(text, "co-op", "coop") && ContainsAny(text, "campaign"))
                tags.Add("Co-op campaign");
            if (ContainsAny(text, "roguelike", "roguelite") && ContainsAny(text, "action"))
                tags.Add("Action roguelite");

            game.AlgorithmicTags = tags.OrderBy(t => t).ToList();
        }

        public static bool IsTemporaryBuild(EnrichedGame game)
        {
            var name = game?.Name ?? string.Empty;
            return TemporaryBuildTerms.Any(t => name.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public static bool IsEndlessOrServiceGame(EnrichedGame game)
        {
            var text = MetadataText(game);
            return ContainsAny(text, EndlessTerms);
        }

        public static bool IsFinishableGame(EnrichedGame game)
        {
            var text = MetadataText(game);
            if (IsEndlessOrServiceGame(game)) return false;
            return ContainsAny(text, FinishableTerms) && !OnlyHasWeakFinishableSignal(text);
        }

        public static StartedGameIntent StartedIntentFor(ScoredGame scored)
        {
            var game = scored?.Game;
            if (game == null || game.PlaytimeSeconds <= 0)
                return StartedGameIntent.None;
            if (game.PlaytimeSeconds >= 18000)
                return StartedGameIntent.None;

            var text = MetadataText(game);
            if (ContainsAny(text, "warframe", "looter shooter", "grind", "endgame", "account progression", "repeatable mission", "builds"))
                return StartedGameIntent.LongTermProgression;
            if (ContainsAny(text, "co-op", "coop", "online co-op", "multiplayer", "mmo", "massively multiplayer"))
                return StartedGameIntent.CoopMultiplayer;
            if (ContainsAny(text, "space sim", "space simulation", "simulation", "simulator", "sandbox", "survival", "crafting", "base building", "management", "tycoon", "colony"))
                return StartedGameIntent.SandboxSim;
            if (ContainsAny(text, "pvp", "team-based", "team based", "hero shooter", "arena shooter", "competitive", "shooter"))
                return StartedGameIntent.SessionGame;
            if (IsFinishableGame(game))
                return StartedGameIntent.Finishable;
            return StartedGameIntent.ReturnLater;
        }

        public static bool HasBadReviewSignal(EnrichedGame game)
        {
            var quality = QualityScore(game);
            if (!quality.HasValue) return false;
            if (quality.Value < 55) return true;
            if (game.SteamReviewPercent.HasValue && game.SteamReviewCount.GetValueOrDefault() >= 25 &&
                game.SteamReviewPercent.Value < 60)
                return true;
            return game.CommunityScore.HasValue && game.CriticScore.HasValue &&
                   game.CommunityScore.Value < 62 && game.CriticScore.Value < 62;
        }

        public static bool ShouldSuppressFromRecommendations(EnrichedGame game)
        {
            if (game?.IsModpack == true) return true;
            if (IsTemporaryBuild(game)) return true;
            var quality = QualityScore(game);
            return quality.HasValue && quality.Value < 45;
        }

        public static double QualityMultiplier(EnrichedGame game)
        {
            if (IsTemporaryBuild(game)) return 0;
            var quality = QualityScore(game);
            if (!quality.HasValue) return 0.92;
            if (quality.Value >= 85) return 1.12;
            if (quality.Value >= 75) return 1.04;
            if (quality.Value >= 65) return 0.95;
            if (quality.Value >= 55) return 0.70;
            return 0.35;
        }

        public static string QualityLabel(EnrichedGame game)
        {
            if (IsTemporaryBuild(game)) return "Excluded: temporary playtest/demo build";
            var quality = QualityScore(game);
            if (!quality.HasValue) return "No review score available";
            if (quality.Value >= 85) return $"Strong review signal ({quality.Value:F0}/100)";
            if (quality.Value >= 70) return $"Positive review signal ({quality.Value:F0}/100)";
            if (quality.Value >= 55) return $"Mixed review signal ({quality.Value:F0}/100)";
            return $"Weak review signal ({quality.Value:F0}/100)";
        }

        public static string CategoryFor(ScoredGame scored, TasteProfile profile)
        {
            var game = scored.Game;
            if (HasBadReviewSignal(game)) return "Risky / Mixed";
            if (HasAny(game.AlgorithmicTags.Concat(game.Tags).Concat(game.Keywords).Concat(game.Genres), "Hero shooter", "Tactical shooter", "Arena shooter", "Boomer shooter", "Anime shooter", "Shooter", "FPS", "TPS", "Action", "Fighting", "Beat 'em up", "Hack and Slash"))
                return "Shooters & Combat";
            if (HasAny(game.AlgorithmicTags.Concat(game.Tags).Concat(game.Keywords).Concat(game.Genres), "Factory automation", "Colony sim", "Management sim", "Cozy sim", "4X strategy", "Simulation", "Strategy", "Tycoon"))
                return "Strategy & Sims";
            if (HasAny(game.AlgorithmicTags.Concat(game.Tags).Concat(game.Keywords), "Co-op survival craft", "Open world survival", "Base building", "Survival", "Sandbox"))
                return "Survival & Crafting";
            if (HasAny(game.AlgorithmicTags.Concat(game.Tags).Concat(game.Features), "Co-op campaign", "Co-op", "Online Co-Op", "Multiplayer"))
                return "Co-op / Multiplayer";
            if (HasAny(game.AlgorithmicTags.Concat(game.Tags).Concat(game.Themes).Concat(game.Features), "Narrative adventure", "Story Rich", "Single-player", "Campaign"))
                return "Story & Campaign";
            if (HasAny(game.AlgorithmicTags.Concat(game.Tags).Concat(game.Keywords).Concat(game.Genres), "CRPG", "JRPG", "RPG", "Tactical RPG", "Strategy RPG", "Tactics", "Turn-Based Tactics", "Role-playing"))
                return "RPGs & Tactics";
            if (scored.FusedScore >= 0.75 && QualityMultiplier(game) >= 1.0) return "Best Matches";
            if (scored.NoveltyBonus >= 0.75 || HasAny(game.AlgorithmicTags, "Immersive sim", "Deckbuilder roguelike", "Bullet heaven", "Metroidvania", "Soulslike", "Precision platformer", "Isometric ARPG"))
                return "Fresh Finds";
            return "Best Matches";
        }

        public static bool BelongsToCategory(ScoredGame scored, string category)
        {
            if (string.IsNullOrWhiteSpace(category) || category == "All") return true;
            return string.Equals(scored.RecommendationCategory, category, StringComparison.OrdinalIgnoreCase);
        }

        private static double? QualityScore(EnrichedGame game)
        {
            var values = new List<int>();
            if (game?.SteamReviewPercent.HasValue == true && game.SteamReviewCount.GetValueOrDefault() >= 10)
                values.Add(game.SteamReviewPercent.Value);
            if (game?.CommunityScore.HasValue == true && game.CommunityScore.Value > 0) values.Add(game.CommunityScore.Value);
            if (game?.CriticScore.HasValue == true && game.CriticScore.Value > 0) values.Add(game.CriticScore.Value);
            if (!values.Any()) return null;
            return values.Average();
        }

        private static string MetadataText(EnrichedGame game)
        {
            if (game == null) return string.Empty;
            return string.Join(" ", new[]
            {
                game.Name,
                game.Description,
                string.Join(" ", game.Genres ?? new List<string>()),
                string.Join(" ", game.Tags ?? new List<string>()),
                string.Join(" ", game.Features ?? new List<string>()),
                string.Join(" ", game.Themes ?? new List<string>()),
                string.Join(" ", game.Keywords ?? new List<string>()),
                string.Join(" ", game.AlgorithmicTags ?? new List<string>())
            }).ToLowerInvariant();
        }

        private static void AddIf(HashSet<string> tags, string text, string tag, params string[] needles)
        {
            if (ContainsAny(text, needles)) tags.Add(tag);
        }

        private static void AddIfAny(HashSet<string> tags, string text, string tag, params string[] needles)
        {
            if (ContainsAny(text, needles)) tags.Add(tag);
        }

        private static void AddIfAll(HashSet<string> tags, string text, string tag, params string[] needles)
        {
            if (needles.All(n => text.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0)) tags.Add(tag);
        }

        private static bool IsExtractionShooter(string text)
        {
            if (ContainsAny(text, "hero shooter", "arena shooter", "team-based shooter", "team based shooter", "champion shooter"))
                return false;

            if (ContainsAny(text, "extraction shooter", "extraction-based shooter", "extract-and-survive", "extract and survive", "pvpve extraction"))
                return true;

            var hasExtractionLoop = ContainsAny(text, "extract with", "loot and extract", "extract loot", "extract your loot", "extract resources", "raid and extract", "extraction point");
            var hasShooterCombat = ContainsAny(text, "shooter", "fps", "first-person shooter", "third-person shooter", "gunplay");
            var hasRaidRisk = ContainsAny(text, "raid", "pvpve", "lose your gear", "high-stakes", "high stakes", "stash");
            return hasExtractionLoop && hasShooterCombat && hasRaidRisk;
        }

        private static bool ContainsAny(string text, params string[] needles)
            => needles.Any(n => text.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0);

        private static bool OnlyHasWeakFinishableSignal(string text)
            => WeakFinishableTerms.Any(t => text.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0) &&
               !FinishableTerms.Except(WeakFinishableTerms, StringComparer.OrdinalIgnoreCase)
                   .Any(t => text.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0);

        private static bool HasAny(IEnumerable<string> values, params string[] needles)
            => values.Any(v => needles.Any(n => string.Equals(v, n, StringComparison.OrdinalIgnoreCase) ||
                                                v.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0));
    }
}
