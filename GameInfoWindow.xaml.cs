using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GameRecommender
{
    public partial class GameInfoWindow : Window
    {
        private readonly ScoredGame scored;
        private readonly Func<Task> enrichMissingInfoAsync;
        private readonly Action<ScoredGame> launchAction;
        private readonly Action<ScoredGame> goToLibraryAction;
        private readonly Func<ScoredGame, bool> rejectAction;
        private bool loaded;

        public GameInfoWindow(
            ScoredGame scored,
            Func<Task> enrichMissingInfoAsync,
            Action<ScoredGame> launchAction,
            Action<ScoredGame> goToLibraryAction,
            Func<ScoredGame, bool> rejectAction)
        {
            InitializeComponent();
            this.scored = scored ?? throw new ArgumentNullException(nameof(scored));
            this.enrichMissingInfoAsync = enrichMissingInfoAsync;
            this.launchAction = launchAction;
            this.goToLibraryAction = goToLibraryAction;
            this.rejectAction = rejectAction;
            Title = scored.Game?.Name ?? "Game info";
            Render();
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (loaded)
                return;
            loaded = true;

            if (enrichMissingInfoAsync == null)
                return;

            LinksStatusText.Text = "Loading links...";
            try
            {
                await enrichMissingInfoAsync();
                RenderLinks();
            }
            catch (Exception ex)
            {
                LinksStatusText.Text = "Could not load more links: " + ex.Message;
            }
        }

        private void Render()
        {
            var game = scored.Game;
            TitleText.Text = game?.Name ?? "Unknown game";
            SourceText.Text = $"{game?.SourcePlugin ?? "Unknown source"} | {RecommendationEngine.FormatTime(game?.PlaytimeSeconds ?? 0)} | {scored.QualityLabel}";

            RenderReasons();
            RenderScores();
            RenderMetadata();
            DescriptionText.Text = string.IsNullOrWhiteSpace(game?.Description)
                ? "No description available."
                : game.Description.Trim();
            RenderLinks();
        }

        private void RenderReasons()
        {
            ReasonsPanel.Children.Clear();
            var reasons = scored.Reasons?.Where(r => !string.IsNullOrWhiteSpace(r)).ToList() ?? new List<string>();
            if (!reasons.Any())
            {
                ReasonsPanel.Children.Add(MetaText("No recommendation reason available."));
                return;
            }

            var groups = BuildReasonGroups(reasons);
            foreach (var group in groups)
            {
                ReasonsPanel.Children.Add(GroupTitle(group.Key));
                foreach (var reason in group.Value)
                    ReasonsPanel.Children.Add(MetaText("- " + reason.Trim()));
            }
        }

        private void RenderScores()
        {
            ScoresPanel.Children.Clear();
            AddScore("Final", scored.FusedScore);
            AddScore("Weighted", scored.WeightedScore);
            AddScore("Similarity", scored.CosineScore);
            AddScore("Steam graph", scored.GraphScore);
            AddScore("Novelty", scored.NoveltyBonus);
            if (!string.IsNullOrWhiteSpace(scored.ScoreTuningSummary))
                ScoresPanel.Children.Add(MetaText(scored.ScoreTuningSummary));
            ScoresPanel.Children.Add(MetaText("Category: " + scored.RecommendationCategory));
        }

        private void AddScore(string label, double value)
            => ScoresPanel.Children.Add(MetaText($"{label}: {value:F2}"));

        private void RenderMetadata()
        {
            MetadataPanel.Children.Clear();
            var game = scored.Game;
            AddMetadata("Primary tag", scored.PrimaryTag);
            AddMetadata("Started intent", scored.StartedIntentLabel);
            AddMetadata("Genres", game?.Genres);
            AddMetadata("Tags", game?.Tags);
            AddMetadata("Themes", game?.Themes);
            AddMetadata("Keywords", game?.Keywords);
            AddMetadata("Features", game?.Features);
            AddMetadata("Algorithmic tags", game?.AlgorithmicTags);
            if (game?.SteamReviewPercent.HasValue == true)
            {
                var review = $"{game.SteamReviewDescription ?? "Reviews"} ({game.SteamReviewPercent}% positive, {game.SteamReviewCount ?? 0} reviews)";
                AddMetadata("Steam reviews", review);
            }

            if (MetadataPanel.Children.Count == 0)
                MetadataPanel.Children.Add(MetaText("No metadata available."));
        }

        private void AddMetadata(string label, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;
            MetadataPanel.Children.Add(MetaText($"{label}: {value.Trim()}"));
        }

        private void AddMetadata(string label, IEnumerable<string> values)
        {
            var clean = (values ?? Enumerable.Empty<string>())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (clean.Any())
                AddMetadata(label, string.Join(", ", clean));
        }

        private void RenderLinks()
        {
            LinksPanel.Children.Clear();
            var links = BuildLinks().ToList();
            var trailerUrl = NormalizeUrl(scored.Game?.TrailerUrl);
            TrailerButton.Visibility = string.IsNullOrWhiteSpace(trailerUrl)
                ? Visibility.Collapsed
                : Visibility.Visible;
            TrailerButton.ToolTip = trailerUrl;

            if (!links.Any())
            {
                LinksStatusText.Text = "No links found.";
                return;
            }

            LinksStatusText.Text = string.Empty;
            foreach (var link in links)
            {
                var button = new Button
                {
                    Content = NormalizeLinkLabel(link),
                    Tag = link.Url,
                    ToolTip = link.Url
                };
                button.Click += LinkButton_Click;
                LinksPanel.Children.Add(button);
            }
        }

        private IEnumerable<GameExternalLink> BuildLinks()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var link in scored.Game?.ExternalLinks ?? Enumerable.Empty<GameExternalLink>())
            {
                var normalizedUrl = NormalizeUrl(link?.Url);
                if (string.IsNullOrWhiteSpace(normalizedUrl) || !seen.Add(DedupeUrlKey(normalizedUrl)))
                    continue;
                yield return new GameExternalLink
                {
                    Label = NormalizeLinkLabel(link),
                    Url = normalizedUrl,
                    Kind = link?.Kind ?? string.Empty
                };
            }
        }

        private void TrailerButton_Click(object sender, RoutedEventArgs e)
            => OpenUrl(scored.Game?.TrailerUrl);

        private void LinkButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button)
                OpenUrl(button.Tag as string);
        }

        private void LaunchButton_Click(object sender, RoutedEventArgs e)
            => launchAction?.Invoke(scored);

        private void GoToLibraryButton_Click(object sender, RoutedEventArgs e)
            => goToLibraryAction?.Invoke(scored);

        private void RejectButton_Click(object sender, RoutedEventArgs e)
        {
            if (rejectAction?.Invoke(scored) == true)
                Close();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
            => Close();

        private static void OpenUrl(string url)
        {
            UrlOpener.OpenHttpUrl(url);
        }

        private static bool IsUsableUrl(string url)
            => UrlOpener.TryNormalizeHttpUrl(url, out _);

        private static string NormalizeUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return string.Empty;
            return UrlOpener.TryNormalizeHttpUrl(url, out var normalized)
                ? normalized
                : string.Empty;
        }

        private static string DedupeUrlKey(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return string.Empty;

            var builder = new UriBuilder(uri)
            {
                Fragment = string.Empty,
                Host = uri.Host.ToLowerInvariant(),
                Path = (uri.AbsolutePath ?? string.Empty).TrimEnd('/')
            };
            if (builder.Path.Length == 0)
                builder.Path = "/";
            return builder.Uri.AbsoluteUri;
        }

        private static string NormalizeLinkLabel(GameExternalLink link)
        {
            var url = NormalizeUrl(link?.Url);
            var label = (link?.Label ?? string.Empty).Trim();
            if (!IsGenericLinkLabel(label))
                return label;

            var domainLabel = LabelFromUrl(url);
            return string.IsNullOrWhiteSpace(domainLabel) ? "Link" : domainLabel;
        }

        private static bool IsGenericLinkLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label))
                return true;
            if (IsUsableUrl(label))
                return true;

            var normalized = Regex.Replace(label.Trim().ToLowerInvariant(), @"[^a-z0-9]+", string.Empty);
            return normalized == "link" ||
                   normalized == "links" ||
                   normalized == "website" ||
                   normalized == "url" ||
                   normalized == "store" ||
                   normalized == "external" ||
                   normalized == "unknown";
        }

        private static string LabelFromUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return string.Empty;

            var host = uri.Host.ToLowerInvariant();
            var path = uri.AbsolutePath ?? string.Empty;
            if (host == "store.steampowered.com")
                return "Steam store";
            if (host.EndsWith("steamcommunity.com", StringComparison.OrdinalIgnoreCase))
                return "Steam community";
            if (host.EndsWith("igdb.com", StringComparison.OrdinalIgnoreCase))
                return "IGDB";
            if (host.EndsWith("pcgamingwiki.com", StringComparison.OrdinalIgnoreCase))
                return "PCGamingWiki";
            if (host.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith("youtu.be", StringComparison.OrdinalIgnoreCase))
                return path.IndexOf("trailer", StringComparison.OrdinalIgnoreCase) >= 0 ? "YouTube trailer" : "YouTube";
            if (host.EndsWith("twitch.tv", StringComparison.OrdinalIgnoreCase))
                return "Twitch";
            if (host.EndsWith("gog.com", StringComparison.OrdinalIgnoreCase))
                return "GOG";
            if (host.EndsWith("epicgames.com", StringComparison.OrdinalIgnoreCase))
                return "Epic Games";
            if (host.EndsWith("itch.io", StringComparison.OrdinalIgnoreCase))
                return "itch.io";
            if (host.EndsWith("wikipedia.org", StringComparison.OrdinalIgnoreCase))
                return "Wikipedia";
            return SimplifyHost(host);
        }

        private static string SimplifyHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
                return string.Empty;

            host = host.Trim().ToLowerInvariant();
            if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                host = host.Substring(4);

            var parts = host.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3)
            {
                var secondLevel = parts[parts.Length - 2];
                var topLevel = parts[parts.Length - 1];
                if (secondLevel.Length > 2 || topLevel.Length > 2)
                    return secondLevel + "." + topLevel;
            }
            return host;
        }

        private static TextBlock MetaText(string text)
        {
            return new TextBlock
            {
                Text = text ?? string.Empty,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(169, 181, 198)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4)
            };
        }

        private static TextBlock GroupTitle(string text)
        {
            return new TextBlock
            {
                Text = text ?? string.Empty,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(238, 243, 248)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 3)
            };
        }

        private List<KeyValuePair<string, List<string>>> BuildReasonGroups(List<string> reasons)
        {
            var positive = new List<string>();
            var quality = new List<string>();
            var novelty = new List<string>();
            var penalties = new List<string>();

            foreach (var reason in reasons)
            {
                var bucket = ReasonBucket(reason);
                if (bucket == "Quality")
                    quality.Add(reason);
                else if (bucket == "Novelty")
                    novelty.Add(reason);
                else if (bucket == "Penalty")
                    penalties.Add(reason);
                else
                    positive.Add(reason);
            }

            if (scored.NoveltyBonus > 0.05 && !novelty.Any())
                novelty.Add($"Adds variety to your usual library profile ({scored.NoveltyBonus:F2}).");

            if (!string.IsNullOrWhiteSpace(scored.QualityLabel) &&
                !quality.Any(q => q.IndexOf(scored.QualityLabel, StringComparison.OrdinalIgnoreCase) >= 0))
                quality.Add(scored.QualityLabel);

            var groups = new List<KeyValuePair<string, List<string>>>();
            AddGroup(groups, "Match signals", positive);
            AddGroup(groups, "Quality signals", quality);
            AddGroup(groups, "Novelty signals", novelty);
            AddGroup(groups, "Penalty signals", penalties);
            return groups.Any() ? groups : new List<KeyValuePair<string, List<string>>>
            {
                new KeyValuePair<string, List<string>>("Match signals", reasons)
            };
        }

        private static void AddGroup(List<KeyValuePair<string, List<string>>> groups, string label, List<string> values)
        {
            var clean = (values ?? new List<string>())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (clean.Any())
                groups.Add(new KeyValuePair<string, List<string>>(label, clean));
        }

        private static string ReasonBucket(string reason)
        {
            var normalized = (reason ?? string.Empty).ToLowerInvariant();
            if (normalized.Contains("review") ||
                normalized.Contains("quality") ||
                normalized.Contains("critic") ||
                normalized.Contains("score"))
                return "Quality";
            if (normalized.Contains("novel") ||
                normalized.Contains("fresh") ||
                normalized.Contains("variety") ||
                normalized.Contains("new genre"))
                return "Novelty";
            if (normalized.Contains("poor") ||
                normalized.Contains("mixed") ||
                normalized.Contains("weak") ||
                normalized.Contains("penalty") ||
                normalized.Contains("concern"))
                return "Penalty";
            return "Match";
        }
    }
}
