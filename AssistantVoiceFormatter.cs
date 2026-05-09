using System;

namespace GameRecommender
{
    internal static class AssistantVoiceFormatter
    {
        public static string Ready(string voice)
        {
            switch (NormalizeVoice(voice))
            {
                case "Waifu":
                    return "Ready to scout your library, senpai. Leave the game-hunting to me.";
                case "Hostile":
                    return "Ready. Try not to make me sort this library twice.";
                default:
                    return "Ready to scout your library.";
            }
        }

        public static string RefreshStart(string voice, bool metadata)
        {
            switch (NormalizeVoice(voice))
            {
                case "Waifu":
                    return metadata
                        ? "Refreshing metadata, senpai. I will polish these game cards until they sparkle."
                        : "Re-scoring your library, senpai. I will find the good picks for you.";
                case "Hostile":
                    return metadata
                        ? "Refreshing metadata. The stale stuff was not helping."
                        : "Re-scoring your library. Maybe this time the list will behave.";
                default:
                    return metadata
                        ? "Refreshing metadata, then rebuilding your recommendations..."
                        : "Re-scoring your library and rebuilding the cards...";
            }
        }

        public static string AiStart(string voice, string provider)
        {
            provider = Clean(provider, "AI");
            switch (NormalizeVoice(voice))
            {
                case "Waifu":
                    return $"{provider} is polishing the list, senpai. I am watching closely.";
                case "Hostile":
                    return $"{provider} is re-ranking. Let us see if it can do better than the first pass.";
                default:
                    return $"{provider} is polishing the list...";
            }
        }

        public static string AiSuccess(string voice)
        {
            switch (NormalizeVoice(voice))
            {
                case "Waifu":
                    return "I found sharper picks for you, senpai. Recommendation magic complete.";
                case "Hostile":
                    return "The AI cleaned up the list. It was about time.";
                default:
                    return "I found a few sharper picks for you.";
            }
        }

        public static string AiFailure(string voice)
        {
            switch (NormalizeVoice(voice))
            {
                case "Waifu":
                    return "AI ranking stumbled, senpai, so I protected the local scoring for you.";
                case "Hostile":
                    return "AI ranking fell over. I kept the local scoring because someone had to.";
                default:
                    return "AI ranking stumbled, so I kept the local scoring.";
            }
        }

        public static string SpotlightPick(string voice, string gameName)
        {
            gameName = Clean(gameName, "Unknown game");
            switch (NormalizeVoice(voice))
            {
                case "Waifu":
                    return $"Spotlight pick, senpai: {gameName}. This one wants your attention.";
                case "Hostile":
                    return $"Spotlight pick: {gameName}. Try not to scroll past the obvious choice.";
                default:
                    return $"Spotlight pick: {gameName}";
            }
        }

        public static string NoCleanPicks(string voice)
        {
            switch (NormalizeVoice(voice))
            {
                case "Waifu":
                    return "No clean picks yet, senpai. A metadata refresh might wake the list up.";
                case "Hostile":
                    return "No clean picks yet. Refresh metadata; this list is starving for facts.";
                default:
                    return "No clean picks yet. Try metadata refresh.";
            }
        }

        public static string DealsFound(string voice, int count)
        {
            switch (NormalizeVoice(voice))
            {
                case "Waifu":
                    return $"Deal radar found {count} shiny possibilities, senpai.";
                case "Hostile":
                    return $"Found {count} deals. Try not to ignore the useful part.";
                default:
                    return $"Deal radar found {count} possibilities.";
            }
        }

        public static string DealsNone(string voice)
        {
            switch (NormalizeVoice(voice))
            {
                case "Waifu":
                    return "Deal radar is quiet right now, senpai. No treasure today.";
                case "Hostile":
                    return "Deal radar is quiet. Apparently the stores brought nothing worth your time.";
                default:
                    return "Deal radar is quiet right now.";
            }
        }

        public static string RecommendationRejected(string voice)
        {
            switch (NormalizeVoice(voice))
            {
                case "Waifu":
                    return "Got it, senpai. I will banish that game from the recommendation list.";
                case "Hostile":
                    return "Rejected. I will keep that one out, since apparently it offended you.";
                default:
                    return "Got it. I will keep that game out of the recommendation list.";
            }
        }

        public static string NewsItem(string voice, string source, string publishedText, string title)
        {
            source = Clean(source, "News");
            publishedText = Clean(publishedText, "Recent");
            title = Clean(title, "Untitled update");
            switch (NormalizeVoice(voice))
            {
                case "Waifu":
                    return $"{source}, {publishedText}: {title}. Fresh news delivery, senpai.";
                case "Hostile":
                    return $"{source}, {publishedText}: {title}. There, actual news.";
                default:
                    return $"{source}, {publishedText}: {title}";
            }
        }

        public static string NewsNone(string voice)
        {
            switch (NormalizeVoice(voice))
            {
                case "Waifu":
                    return "No recent English updates for owned Steam games, senpai. The news feed is sleepy.";
                case "Hostile":
                    return "No recent English updates for owned Steam games. Thrilling silence.";
                default:
                    return "No recent English updates for owned Steam games.";
            }
        }

        public static string NewsFailure(string voice)
        {
            switch (NormalizeVoice(voice))
            {
                case "Waifu":
                    return "News radar failed, senpai, but your recommendations are still safe.";
                case "Hostile":
                    return "News radar failed. At least recommendations still work.";
                default:
                    return "News radar failed; recommendations still work.";
            }
        }

        public static string Category(string voice, string category)
        {
            var neutral = NeutralCategory(category);
            switch (NormalizeVoice(voice))
            {
                case "Waifu":
                    return WaifuCategory(category, neutral);
                case "Hostile":
                    return HostileCategory(category, neutral);
                default:
                    return neutral;
            }
        }

        private static string NeutralCategory(string category)
        {
            switch (category)
            {
                case "Best Matches":
                    return "Best matches selected. I'll put the strongest overall fits up front.";
                case "Fresh Finds":
                    return "Fresh finds selected. I'll look for novel hooks and precise niche tags.";
                case "Shooters & Combat":
                    return "Shooter filter on. I'll keep the combat tags honest.";
                case "Strategy & Sims":
                    return "Strategy and sims selected. I'll favor systems, planning, and long-session games.";
                case "Survival & Crafting":
                    return "Survival and crafting selected. I'll favor open goals, building, and survival loops.";
                case "Story & Campaign":
                    return "Story mode. I'll focus on games you can actually finish.";
                case "Co-op / Multiplayer":
                    return "Multiplayer mode. I'll look for games that make sense with other players.";
                case "RPGs & Tactics":
                    return "RPGs and tactics selected. I'll favor party building, decisions, and tactical depth.";
                case "Risky / Mixed":
                    return "Risky picks selected. I'll keep caveats visible.";
                default:
                    return "All categories selected. I'll keep the list balanced.";
            }
        }

        private static string WaifuCategory(string category, string fallback)
        {
            switch (category)
            {
                case "Best Matches":
                    return "Best matches selected, senpai. I will place the strongest treasures up front.";
                case "Fresh Finds":
                    return "Fresh finds selected, senpai. Time to chase shiny niche gems.";
                case "Shooters & Combat":
                    return "Shooter filter on, senpai. Combat tags locked and loaded.";
                case "Strategy & Sims":
                    return "Strategy and sims selected, senpai. Big brain mode activated.";
                case "Survival & Crafting":
                    return "Survival and crafting selected, senpai. Bases, tools, and danger, yay.";
                case "Story & Campaign":
                    return "Story mode, senpai. I will find something you can actually finish.";
                case "Co-op / Multiplayer":
                    return "Multiplayer mode, senpai. I will find games worthy of the squad.";
                case "RPGs & Tactics":
                    return "RPGs and tactics selected, senpai. Party builds and battle plans incoming.";
                case "Risky / Mixed":
                    return "Risky picks selected, senpai. I will keep the danger labels visible.";
                default:
                    return "All categories selected, senpai. I will keep the list nicely balanced.";
            }
        }

        private static string HostileCategory(string category, string fallback)
        {
            switch (category)
            {
                case "Best Matches":
                    return "Best matches selected. I put the strongest fits first, obviously.";
                case "Fresh Finds":
                    return "Fresh finds selected. Maybe novelty will save this list.";
                case "Shooters & Combat":
                    return "Shooter filter on. I will keep the combat tags from lying to you.";
                case "Strategy & Sims":
                    return "Strategy and sims selected. Planning, systems, and fewer brainless clicks.";
                case "Survival & Crafting":
                    return "Survival and crafting selected. Go build a shack and call it progress.";
                case "Story & Campaign":
                    return "Story mode. I will find games you might actually finish for once.";
                case "Co-op / Multiplayer":
                    return "Multiplayer mode. Fine, I will find something tolerable with other people.";
                case "RPGs & Tactics":
                    return "RPGs and tactics selected. Try reading the stats this time.";
                case "Risky / Mixed":
                    return "Risky picks selected. I will leave the caveats where you can't miss them.";
                default:
                    return "All categories selected. Balanced, since apparently that matters.";
            }
        }

        private static string NormalizeVoice(string voice)
        {
            if (string.Equals(voice, "Waifu", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(voice, "Anime Guide", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(voice, "Playful", StringComparison.OrdinalIgnoreCase))
                return "Waifu";
            if (string.Equals(voice, "Hostile", StringComparison.OrdinalIgnoreCase))
                return "Hostile";
            return "Neutral";
        }

        private static string Clean(string value, string fallback)
            => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }
}
