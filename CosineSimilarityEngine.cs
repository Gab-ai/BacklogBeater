using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace GameRecommender
{
    /// <summary>
    /// Engine 2: TF-IDF cosine similarity.
    ///
    /// Builds a term-frequency vector for every game from its description + tags + themes.
    /// Scores candidate games by cosine similarity to the average vector of the
    /// user's most-played games. This finds conceptually similar games even when
    /// they share no explicit genre labels with what you've already played.
    ///
    /// The TF-IDF matrix is built once per enrichment cycle and stored in memory.
    /// No external dependencies — pure arithmetic on dictionaries.
    /// </summary>
    public class CosineSimilarityEngine
    {
        private static readonly HashSet<string> StopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the","a","an","and","or","but","in","on","at","to","for","of","with",
            "is","was","are","were","be","been","being","have","has","had","do","does",
            "did","will","would","could","should","may","might","shall","can","need",
            "game","games","player","players","play","played","playing","new","also",
            "through","from","into","about","this","that","their","there","they",
            "its","which","when","where","what","how","who","all","more","most","one",
        };

        private Dictionary<Guid, Dictionary<string, double>> tfidfVectors;
        private List<Guid> allIds;

        // ── Build index ──────────────────────────────────────────────────

        public void BuildIndex(IEnumerable<EnrichedGame> games)
        {
            var gameList = games.ToList();
            allIds = gameList.Select(g => g.PlayniteId).ToList();

            // Step 1: compute TF for each game
            var tfDocs = new Dictionary<Guid, Dictionary<string, double>>();
            var docFreq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var game in gameList)
            {
                var terms = Tokenise(game);
                var tf = ComputeTf(terms);
                tfDocs[game.PlayniteId] = tf;
                foreach (var term in tf.Keys)
                {
                    if (!docFreq.ContainsKey(term)) docFreq[term] = 0;
                    docFreq[term]++;
                }
            }

            // Step 2: compute IDF and multiply
            int N = gameList.Count;
            tfidfVectors = new Dictionary<Guid, Dictionary<string, double>>();

            foreach (var kvp in tfDocs)
            {
                var vec = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                foreach (var term in kvp.Value)
                {
                    int df = docFreq.TryGetValue(term.Key, out int v) ? v : 1;
                    double idf = Math.Log((double)(N + 1) / (df + 1)) + 1;
                    vec[term.Key] = term.Value * idf;
                }
                tfidfVectors[kvp.Key] = L2Normalise(vec);
            }
        }

        // ── Score ────────────────────────────────────────────────────────

        public EngineScores Score(
            IEnumerable<EnrichedGame> candidates,
            IEnumerable<EnrichedGame> playedGames)
        {
            var result = new EngineScores { EngineName = "Cosine" };
            if (tfidfVectors == null || !tfidfVectors.Any()) return result;

            // Build query vector = weighted average of played game vectors
            var query = BuildQueryVector(playedGames);
            if (!query.Any()) return result;

            foreach (var game in candidates)
            {
                if (!tfidfVectors.TryGetValue(game.PlayniteId, out var vec))
                    continue;
                double sim = CosineSim(query, vec);
                result.Scores[game.PlayniteId] = sim;
            }

            return result;
        }

        private Dictionary<string, double> BuildQueryVector(IEnumerable<EnrichedGame> played)
        {
            var query = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            double totalWeight = 0;

            foreach (var game in played.Where(g => g.IsDeepPlayed))
            {
                if (!tfidfVectors.TryGetValue(game.PlayniteId, out var vec)) continue;
                double w = Math.Log(game.PlaytimeSeconds + 1);
                foreach (var term in vec)
                {
                    if (!query.ContainsKey(term.Key)) query[term.Key] = 0;
                    query[term.Key] += term.Value * w;
                }
                totalWeight += w;
            }

            if (totalWeight <= 0) return query;
            var keys = new List<string>(query.Keys);
            foreach (var k in keys) query[k] /= totalWeight;
            return L2Normalise(query);
        }

        // ── Helpers ──────────────────────────────────────────────────────

        private static List<string> Tokenise(EnrichedGame game)
        {
            var tokens = new List<string>();

            // Description words (weighted 1x)
            if (!string.IsNullOrWhiteSpace(game.Description))
                tokens.AddRange(SplitWords(game.Description));

            AddMetadataTokens(tokens, game.Tags, 4);
            AddMetadataTokens(tokens, game.AlgorithmicTags, 4);
            AddMetadataTokens(tokens, game.Keywords, 3);
            AddMetadataTokens(tokens, game.Themes, 3);
            AddMetadataTokens(tokens, game.Features, 2);
            AddMetadataTokens(tokens, game.Genres, 2);
            AddMetadataTokens(tokens, game.SteamRecommendedTags, 2);

            return tokens
                .Select(t => t.ToLowerInvariant())
                .Where(t => t.Length >= 3 && !StopWords.Contains(t))
                .ToList();
        }

        private static void AddMetadataTokens(List<string> tokens, IEnumerable<string> values, int repetitions)
        {
            foreach (var value in values ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                var effectiveRepetitions = RecommendationHeuristics.IsGenericTagSignal(value)
                    ? 1
                    : repetitions;
                var words = SplitWords(value).ToList();
                for (var i = 0; i < effectiveRepetitions; i++)
                    tokens.AddRange(words);
            }
        }

        private static IEnumerable<string> SplitWords(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) yield break;
            foreach (Match m in Regex.Matches(text, @"[a-zA-Z]{3,}"))
                yield return m.Value;
        }

        private static Dictionary<string, double> ComputeTf(List<string> terms)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in terms)
            {
                if (!counts.ContainsKey(t)) counts[t] = 0;
                counts[t]++;
            }
            double total = terms.Count;
            return counts.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value / total,
                StringComparer.OrdinalIgnoreCase);
        }

        private static double CosineSim(
            Dictionary<string, double> a,
            Dictionary<string, double> b)
        {
            double dot = 0;
            foreach (var kvp in a)
                if (b.TryGetValue(kvp.Key, out double bv))
                    dot += kvp.Value * bv;
            return dot;  // Both vectors are already L2-normalised
        }

        private static Dictionary<string, double> L2Normalise(Dictionary<string, double> vec)
        {
            double norm = Math.Sqrt(vec.Values.Sum(v => v * v));
            if (norm <= 0) return vec;
            return vec.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value / norm,
                StringComparer.OrdinalIgnoreCase);
        }
    }
}
