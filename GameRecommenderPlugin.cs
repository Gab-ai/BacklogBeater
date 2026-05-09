using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace GameRecommender
{
    public class GameRecommenderPlugin : GenericPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        public override Guid Id { get; } = Guid.Parse("9d7e4b2a-1f3c-4e8d-a6b5-0c2f7e9d1a3b");

        internal DiskCache Cache { get; private set; }
        internal SettingsViewModel SettingsVm { get; private set; }

        internal EnrichmentOrchestrator Enrichment { get; private set; }
        internal WeightedScoringEngine WeightedEngine { get; } = new WeightedScoringEngine();
        internal CosineSimilarityEngine CosineEngine { get; } = new CosineSimilarityEngine();
        internal SteamGraphEngine GraphEngine { get; } = new SteamGraphEngine();
        internal ScoreFusion Fusion { get; private set; }
        internal ClaudeReranker AiReranker { get; private set; }
        internal DealRecommendationClient Deals { get; private set; }
        internal GameNewsClient News { get; private set; }

        private RecommenderView sidebarView;
        private RecommenderViewModel viewModel;

        public GameRecommenderPlugin(IPlayniteAPI api) : base(api)
        {
            Cache = new DiskCache(GetPluginUserDataPath());
            SettingsVm = new SettingsViewModel(this);
            RebuildPipeline();
            viewModel = new RecommenderViewModel(api, this);
            sidebarView = new RecommenderView(viewModel);
        }

        internal void RebuildPipeline()
        {
            var s = SettingsVm.GetSettings();
            var steamClient = new SteamEnrichmentClient(Cache, s.SteamApiKey, s.SteamUserId);
            var igdbClient = new IgdbClient(Cache, s.IgdbClientId, s.IgdbClientSecret);
            var rawgClient = new RawgClient(Cache, s.RawgApiKey);
            Enrichment = new EnrichmentOrchestrator(PlayniteApi, steamClient, igdbClient, rawgClient, s, Cache);
            Fusion = new ScoreFusion(WeightedEngine, GraphEngine, s);
            AiReranker = new ClaudeReranker(Cache, s);
            Deals = new DealRecommendationClient(Cache, s);
            News = new GameNewsClient(Cache);
        }

        internal void NotifySettingsChanged()
        {
            viewModel?.OnSettingsChanged();
        }

        internal async Task<string> TestConnectionsAsync(RecommenderSettings settings)
        {
            var checks = new List<string>();

            if (!string.IsNullOrWhiteSpace(settings.SteamApiKey) && !string.IsNullOrWhiteSpace(settings.SteamUserId))
            {
                var steam = new SteamEnrichmentClient(Cache, settings.SteamApiKey, settings.SteamUserId);
                var tags = await steam.GetRecommendedTagsAsync();
                checks.Add(tags.Any() ? $"Steam OK ({tags.Count} tags)" : "Steam configured, no tags returned");
            }

            if (!string.IsNullOrWhiteSpace(settings.IgdbClientId) && !string.IsNullOrWhiteSpace(settings.IgdbClientSecret))
            {
                var igdb = new IgdbClient(Cache, settings.IgdbClientId, settings.IgdbClientSecret);
                var game = await igdb.GetGameDataAsync("Portal");
                checks.Add(game != null ? "IGDB OK" : "IGDB configured, no test result returned");
            }

            if (!string.IsNullOrWhiteSpace(settings.RawgApiKey))
            {
                var rawg = new RawgClient(Cache, settings.RawgApiKey);
                await rawg.TestConnectionAsync();
                checks.Add("RAWG OK");
            }

            if (settings.AiRerankerEnabled)
            {
                var ai = new ClaudeReranker(Cache, settings);
                await ai.TestConnectionAsync();
                checks.Add($"{ai.ProviderName} OK");
            }

            if (!string.IsNullOrWhiteSpace(settings.ItadApiKey))
                checks.Add("ITAD key saved; deals are checked on demand");
            if (settings.EnableJastDeals)
                checks.Add("JAST sales enabled; checked on demand");
            if (settings.EnableMangaGamerDeals)
                checks.Add("MangaGamer sales enabled; checked on demand");

            return checks.Any() ? string.Join(" | ", checks) : "No external services configured";
        }

        internal ModpackLibrarySyncPlan PreviewMinecraftModpackLibrarySync(string customPaths)
        {
            var scanner = new MinecraftModpackScanner();
            var scan = scanner.Scan(customPaths);
            var sync = new MinecraftModpackLibrarySync(PlayniteApi);
            return sync.Preview(scan, SettingsVm.GetSettings().LibraryIntegrationRecords);
        }

        internal ModpackLibrarySyncApplyResult ApplyMinecraftModpackLibrarySync(ModpackLibrarySyncPlan plan)
        {
            var settings = SettingsVm.GetSettings();
            if (settings.LibraryIntegrationRecords == null)
                settings.LibraryIntegrationRecords = new List<LibraryIntegrationRecord>();

            var sync = new MinecraftModpackLibrarySync(PlayniteApi);
            var result = sync.Apply(plan, settings.LibraryIntegrationRecords);
            SavePluginSettings(settings);
            if (viewModel != null)
                viewModel.TriggerBackgroundRefresh(enrichmentRequired: true, reason: "minecraft playtime sync");
            else
                Enrichment.InvalidateCache();
            return result;
        }

        public override IEnumerable<SidebarItem> GetSidebarItems()
        {
            yield return new SidebarItem
            {
                Title = "Backlog Beater",
                Type = SiderbarItemType.View,
                Icon = new TextBlock
                {
                    Text = "B",
                    FontSize = 20,
                    FontFamily = new FontFamily("Segoe UI"),
                    FontWeight = System.Windows.FontWeights.Bold
                },
                Opened = () =>
                {
                    viewModel.RefreshIfStale();
                    return sidebarView;
                }
            };
        }

        public override ISettings GetSettings(bool firstRunSettings) => SettingsVm;
        public override UserControl GetSettingsView(bool firstRunSettings) => new SettingsView(SettingsVm);

        public override void OnLibraryUpdated(OnLibraryUpdatedEventArgs args)
        {
            viewModel?.TriggerBackgroundRefresh(enrichmentRequired: false, reason: "library updated");
        }

        public override void OnGameStopped(OnGameStoppedEventArgs args)
        {
            viewModel?.TriggerBackgroundRefresh(enrichmentRequired: false, reason: "game stopped");
        }

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            Cache.PurgeExpired();
        }
    }
}
