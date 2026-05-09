using System;
using System.Collections.Generic;
using GameRecommender;

namespace GameRecommender.Tests
{
    internal static class Program
    {
        private static int Main()
        {
            var tests = new Action[]
            {
                RejectsObviousAddOns,
                ClassifiesConcreteGameAsPlayable,
                RejectsWeakSteamSimilarCandidate,
                AllowsStrongPlayableCandidate,
                DetectsOwnedExternalBySteamAppId,
                RejectsInvalidVisibleReasons,
                RawgOwnedMergeFillsGapsWithoutOverwriting,
                RawgExternalMergeKeepsExistingProfileAndAddsSupplementalQuality
            };

            foreach (var test in tests)
            {
                test();
                Console.WriteLine("PASS " + test.Method.Name);
            }

            return 0;
        }

        private static void RejectsObviousAddOns()
        {
            var deal = new ExternalRecommendation
            {
                Title = "Space Quest Soundtrack",
                DealType = "game"
            };

            AssertFalse(DealRecommendationClient.TestIsRealGameDeal(deal), "Soundtrack should not pass the real-game gate.");
        }

        private static void ClassifiesConcreteGameAsPlayable()
        {
            var deal = StrongPlayableCandidate();

            AssertEqual(
                ExternalCandidateKind.PlayableGame,
                DealRecommendationClient.TestClassifyExternalCandidate(deal),
                "Concrete candidate should classify as playable.");
        }

        private static void RejectsWeakSteamSimilarCandidate()
        {
            var deal = new ExternalRecommendation
            {
                Title = "Thin Similar Pick",
                DealType = "game",
                SourceSignals = new List<string> { "Steam similar to Anchor Game" },
                CandidateTags = new List<string> { "Action" },
                Reasons = new List<string> { "Discovered from Anchor Game" },
                RelevanceScore = 1.3,
                ReviewPercent = 85,
                ReviewCount = 1000
            };

            var reason = DealRecommendationClient.TestGetAdmissionRejectionReason(deal);
            AssertEqual("weak Steam-similar metadata", reason, "Thin Steam-similar candidates need stronger metadata.");
        }

        private static void AllowsStrongPlayableCandidate()
        {
            var deal = StrongPlayableCandidate();

            AssertEqual(null, DealRecommendationClient.TestGetAdmissionRejectionReason(deal), "Strong playable candidate should pass admission.");
        }

        private static void DetectsOwnedExternalBySteamAppId()
        {
            var deal = new ExternalRecommendation
            {
                Title = "Different Display Name",
                SteamAppId = "12345"
            };
            var owned = new[]
            {
                new EnrichedGame
                {
                    Name = "Owned Game",
                    SteamAppId = "12345"
                }
            };

            AssertTrue(DealRecommendationClient.TestIsOwnedExternal(deal, owned), "Steam app ID should block owned duplicates.");
        }

        private static void RejectsInvalidVisibleReasons()
        {
            var deal = StrongPlayableCandidate();
            deal.Reasons = new List<string> { "Similar profile signal: Action" };

            AssertTrue(DealRecommendationClient.TestHasInvalidVisibleExternalReason(deal), "Generic visible reasons should be rejected after AI.");
        }

        private static void RawgOwnedMergeFillsGapsWithoutOverwriting()
        {
            var game = new EnrichedGame
            {
                Name = "Owned Game",
                Description = "Existing description",
                CommunityScore = 88,
                CriticScore = 91,
                Genres = new List<string> { "Strategy" }
            };

            EnrichmentOrchestrator.MergeRawgData(game, new RawgGameData
            {
                Id = 123,
                Description = "RAWG description",
                Rating = 4.6,
                RatingsCount = 250,
                Metacritic = 70,
                Genres = new List<string> { "Strategy", "Simulation" },
                Tags = new List<string> { "Management" }
            });

            AssertEqual("Existing description", game.Description, "RAWG should not overwrite existing owned descriptions.");
            AssertEqual(88, game.CommunityScore, "RAWG should not overwrite existing community score.");
            AssertEqual(91, game.CriticScore, "RAWG should not overwrite existing critic score.");
            AssertTrue(game.Genres.Contains("Simulation"), "RAWG should add missing genre metadata.");
            AssertTrue(game.Keywords.Contains("Management"), "RAWG tags should be supplemental keyword evidence.");
        }

        private static void RawgExternalMergeKeepsExistingProfileAndAddsSupplementalQuality()
        {
            var deal = new ExternalRecommendation
            {
                Title = "External Game",
                CandidateDescription = "Existing external profile",
                CandidateGenres = new List<string> { "Action" },
                QualityLabel = "Quality unknown"
            };

            DealRecommendationClient.ApplyRawgCandidateData(deal, new RawgGameData
            {
                Id = 456,
                Description = "RAWG profile",
                Genres = new List<string> { "Adventure" },
                Tags = new List<string> { "Exploration" },
                Rating = 4.2,
                RatingsCount = 120,
                Metacritic = 82
            });

            AssertEqual("Existing external profile", deal.CandidateDescription, "RAWG should not overwrite external candidate descriptions.");
            AssertTrue(deal.CandidateGenres.Contains("Adventure"), "RAWG should supplement external candidate genres.");
            AssertTrue(deal.CandidateTags.Contains("Exploration"), "RAWG should add external candidate tags.");
            AssertTrue(deal.QualityLabel.StartsWith("RAWG 4.2/5", StringComparison.Ordinal), "RAWG should add supplemental quality label when quality is unknown.");
        }

        private static ExternalRecommendation StrongPlayableCandidate()
        {
            return new ExternalRecommendation
            {
                Title = "Ready Tactics",
                DealType = "game",
                CandidateTags = new List<string> { "Tactical", "Realistic" },
                CandidateGenres = new List<string> { "Action" },
                CandidateFeatures = new List<string> { "Single-player" },
                MechanicTags = new List<string> { "tactical shooting" },
                MoodTags = new List<string> { "tense" },
                SourceSignals = new List<string> { "Steam app metadata" },
                Reasons = new List<string> { "Matches your tactical multiplayer shooter taste from Rainbow Six Siege" },
                RelevanceScore = 1.5,
                ReviewPercent = 90,
                ReviewCount = 1000
            };
        }

        private static void AssertTrue(bool value, string message)
        {
            if (!value)
                throw new InvalidOperationException(message);
        }

        private static void AssertFalse(bool value, string message)
        {
            if (value)
                throw new InvalidOperationException(message);
        }

        private static void AssertEqual<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new InvalidOperationException($"{message} Expected '{expected}', got '{actual}'.");
        }
    }
}
