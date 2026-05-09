using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Input;
using Playnite.SDK;

namespace GameRecommender
{
    public class SettingsViewModel : INotifyPropertyChanged, ISettings
    {
        private readonly GameRecommenderPlugin plugin;
        private RecommenderSettings editingSettings;
        private RecommenderSettings savedSettings;
        private string settingsStatusText = string.Empty;
        private string blacklistedPlatformsText = string.Empty;
        private string blacklistedTagsText = string.Empty;
        private string newBlacklistedGameName = string.Empty;
        private ObservableCollection<RejectedGameFeedback> rejectedGames = new ObservableCollection<RejectedGameFeedback>();
        private ObservableCollection<BlacklistedGame> blacklistedGames = new ObservableCollection<BlacklistedGame>();
        private ModpackLibrarySyncPlan pendingMinecraftPlaytimePlan;
        private bool applyingRecommendationPreset;
        public event PropertyChangedEventHandler PropertyChanged;
        public ICommand TestConnectionsCommand { get; }
        public ICommand RestoreRejectedGameCommand { get; }
        public ICommand PreviewMinecraftModpacksCommand { get; }
        public ICommand ApplyMinecraftModpacksCommand { get; }
        public ICommand AddBlacklistedGameCommand { get; }
        public ICommand RemoveBlacklistedGameCommand { get; }
        public ICommand CheckForUpdatesCommand { get; }
        public ICommand OpenUpdatePageCommand { get; }
        private readonly ExtensionUpdateClient updateClient = new ExtensionUpdateClient();
        private string updateStatusText = string.Empty;
        private string updateActionUrl = string.Empty;
        private bool isCheckingForUpdates;

        // ── Bound properties ─────────────────────────────────────────────

        public string SteamApiKey
        {
            get => editingSettings.SteamApiKey;
            set { editingSettings.SteamApiKey = value; OnPropertyChanged(); NotifyFeatureStatusChanged(); }
        }

        public string SteamUserId
        {
            get => editingSettings.SteamUserId;
            set { editingSettings.SteamUserId = value; OnPropertyChanged(); NotifyFeatureStatusChanged(); }
        }

        public string IgdbClientId
        {
            get => editingSettings.IgdbClientId;
            set { editingSettings.IgdbClientId = value; OnPropertyChanged(); NotifyFeatureStatusChanged(); }
        }

        public string IgdbClientSecret
        {
            get => editingSettings.IgdbClientSecret;
            set { editingSettings.IgdbClientSecret = value; OnPropertyChanged(); NotifyFeatureStatusChanged(); }
        }

        public string RawgApiKey
        {
            get => editingSettings.RawgApiKey;
            set { editingSettings.RawgApiKey = value; OnPropertyChanged(); NotifyFeatureStatusChanged(); }
        }

        public string AnthropicApiKey
        {
            get => editingSettings.AnthropicApiKey;
            set { editingSettings.AnthropicApiKey = value; OnPropertyChanged(); NotifyFeatureStatusChanged(); }
        }

        public string OpenAiApiKey
        {
            get => editingSettings.OpenAiApiKey;
            set { editingSettings.OpenAiApiKey = value; OnPropertyChanged(); NotifyFeatureStatusChanged(); }
        }

        public string OpenAiModel
        {
            get => editingSettings.OpenAiModel;
            set { editingSettings.OpenAiModel = value; OnPropertyChanged(); NotifyFeatureStatusChanged(); }
        }

        public string ItadApiKey
        {
            get => editingSettings.ItadApiKey;
            set { editingSettings.ItadApiKey = value; OnPropertyChanged(); NotifyFeatureStatusChanged(); }
        }

        public bool EnableJastDeals
        {
            get => editingSettings.EnableJastDeals;
            set { editingSettings.EnableJastDeals = value; OnPropertyChanged(); NotifyFeatureStatusChanged(); }
        }

        public bool EnableMangaGamerDeals
        {
            get => editingSettings.EnableMangaGamerDeals;
            set { editingSettings.EnableMangaGamerDeals = value; OnPropertyChanged(); NotifyFeatureStatusChanged(); }
        }

        public bool HideEarlyAccessRecommendations
        {
            get => editingSettings.HideEarlyAccessRecommendations;
            set { editingSettings.HideEarlyAccessRecommendations = value; OnPropertyChanged(); NotifyFeatureStatusChanged(); }
        }

        public bool HideStaleEarlyAccessRecommendations
        {
            get => editingSettings.HideStaleEarlyAccessRecommendations;
            set { editingSettings.HideStaleEarlyAccessRecommendations = value; OnPropertyChanged(); NotifyFeatureStatusChanged(); }
        }

        public string MinecraftLauncherPaths
        {
            get => editingSettings.MinecraftLauncherPaths;
            set { editingSettings.MinecraftLauncherPaths = value; OnPropertyChanged(); NotifyFeatureStatusChanged(); }
        }

        public bool AiRerankerEnabled
        {
            get => editingSettings.AiRerankerEnabled;
            set { editingSettings.AiRerankerEnabled = value; OnPropertyChanged(); NotifyFeatureStatusChanged(); }
        }

        public string AiProvider
        {
            get => editingSettings.AiProvider;
            set { editingSettings.AiProvider = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsClaudeSelected)); OnPropertyChanged(nameof(IsOpenAiSelected)); NotifyFeatureStatusChanged(); }
        }

        public bool IsClaudeSelected
        {
            get => string.Equals(editingSettings.AiProvider, "Claude", StringComparison.OrdinalIgnoreCase);
            set { if (value) AiProvider = "Claude"; }
        }

        public bool IsOpenAiSelected
        {
            get => string.Equals(editingSettings.AiProvider, "OpenAI", StringComparison.OrdinalIgnoreCase);
            set { if (value) AiProvider = "OpenAI"; }
        }

        public bool EnrichmentEnabled
        {
            get => editingSettings.EnrichmentEnabled;
            set { editingSettings.EnrichmentEnabled = value; OnPropertyChanged(); NotifyFeatureStatusChanged(); }
        }

        public bool SpotlightEnabled
        {
            get => editingSettings.SpotlightEnabled;
            set { editingSettings.SpotlightEnabled = value; OnPropertyChanged(); NotifyFeatureStatusChanged(); }
        }

        public bool AnimeAssistantEnabled
        {
            get => editingSettings.AnimeAssistantEnabled;
            set { editingSettings.AnimeAssistantEnabled = value; OnPropertyChanged(); NotifyFeatureStatusChanged(); }
        }

        public string AssistantVoice
        {
            get => editingSettings.AssistantVoice;
            set { editingSettings.AssistantVoice = NormalizeAssistantVoice(value); OnPropertyChanged(); }
        }

        public bool SoundEffectsEnabled
        {
            get => editingSettings.SoundEffectsEnabled;
            set { editingSettings.SoundEffectsEnabled = value; OnPropertyChanged(); NotifyFeatureStatusChanged(); }
        }

        public double SoundEffectsVolume
        {
            get => editingSettings.SoundEffectsVolume;
            set { editingSettings.SoundEffectsVolume = ClampVolume(value); OnPropertyChanged(); }
        }

        public double WeightedEngineWeight
        {
            get => editingSettings.WeightedEngineWeight;
            set { editingSettings.WeightedEngineWeight = RecommendationPresetCatalog.ClampUnit(value); OnPropertyChanged(); MarkCustomPreset(); }
        }

        public double CosineEngineWeight
        {
            get => editingSettings.CosineEngineWeight;
            set { editingSettings.CosineEngineWeight = RecommendationPresetCatalog.ClampUnit(value); OnPropertyChanged(); MarkCustomPreset(); }
        }

        public double GraphEngineWeight
        {
            get => editingSettings.GraphEngineWeight;
            set { editingSettings.GraphEngineWeight = RecommendationPresetCatalog.ClampUnit(value); OnPropertyChanged(); MarkCustomPreset(); }
        }

        public double NoveltyBonusStrength
        {
            get => editingSettings.NoveltyBonusStrength;
            set { editingSettings.NoveltyBonusStrength = RecommendationPresetCatalog.ClampNovelty(value); OnPropertyChanged(); MarkCustomPreset(); }
        }

        public double QualityWeightStrength
        {
            get => editingSettings.QualityWeightStrength;
            set { editingSettings.QualityWeightStrength = RecommendationPresetCatalog.ClampQuality(value); OnPropertyChanged(); MarkCustomPreset(); }
        }

        public IReadOnlyList<string> RecommendationPresetOptions => RecommendationPresetCatalog.DisplayNames;

        public string SelectedRecommendationPreset
        {
            get => RecommendationPresetCatalog.DisplayNameFor(editingSettings.RecommendationPresetId);
            set
            {
                var preset = RecommendationPresetCatalog.Find(value);
                editingSettings.RecommendationPresetId = preset.Id;
                OnPropertyChanged();
                if (!string.Equals(preset.Id, RecommendationPresetCatalog.CustomId, StringComparison.OrdinalIgnoreCase))
                    ApplyRecommendationPreset(preset);
            }
        }

        public int AiCandidateCount
        {
            get => editingSettings.AiCandidateCount;
            set { editingSettings.AiCandidateCount = value; OnPropertyChanged(); }
        }

        public string FeatureStatusText => BuildFeatureStatusText();

        public string SettingsStatusText
        {
            get => settingsStatusText;
            set { settingsStatusText = value; OnPropertyChanged(); }
        }

        public string CurrentExtensionVersion => updateClient.ReadCurrentVersion();

        public string UpdateStatusText
        {
            get => updateStatusText;
            set { updateStatusText = value ?? string.Empty; OnPropertyChanged(); }
        }

        public bool IsCheckingForUpdates
        {
            get => isCheckingForUpdates;
            set
            {
                if (isCheckingForUpdates == value)
                    return;
                isCheckingForUpdates = value;
                OnPropertyChanged();
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public bool ShowOpenUpdatePage => !string.IsNullOrWhiteSpace(updateActionUrl);

        public string BlacklistedPlatformsText
        {
            get => blacklistedPlatformsText;
            set
            {
                blacklistedPlatformsText = value ?? string.Empty;
                editingSettings.BlacklistedPlatforms = ParseDelimitedList(blacklistedPlatformsText);
                OnPropertyChanged();
                OnPropertyChanged(nameof(BlacklistSummary));
            }
        }

        public string BlacklistedTagsText
        {
            get => blacklistedTagsText;
            set
            {
                blacklistedTagsText = value ?? string.Empty;
                editingSettings.BlacklistedTags = ParseDelimitedList(blacklistedTagsText);
                OnPropertyChanged();
                OnPropertyChanged(nameof(BlacklistSummary));
            }
        }

        public string NewBlacklistedGameName
        {
            get => newBlacklistedGameName;
            set { newBlacklistedGameName = value ?? string.Empty; OnPropertyChanged(); }
        }

        public ObservableCollection<RejectedGameFeedback> RejectedGames
        {
            get => rejectedGames;
            private set { rejectedGames = value; OnPropertyChanged(); OnPropertyChanged(nameof(RejectedGamesCount)); OnPropertyChanged(nameof(ShowRejectedGamesEmptyState)); }
        }

        public int RejectedGamesCount => RejectedGames?.Count ?? 0;
        public bool ShowRejectedGamesEmptyState => RejectedGamesCount == 0;

        public ObservableCollection<BlacklistedGame> BlacklistedGames
        {
            get => blacklistedGames;
            private set { blacklistedGames = value; OnPropertyChanged(); OnPropertyChanged(nameof(BlacklistedGamesCount)); OnPropertyChanged(nameof(ShowBlacklistedGamesEmptyState)); OnPropertyChanged(nameof(BlacklistSummary)); }
        }

        public int BlacklistedGamesCount => BlacklistedGames?.Count ?? 0;
        public bool ShowBlacklistedGamesEmptyState => BlacklistedGamesCount == 0;
        public string BlacklistSummary
        {
            get
            {
                var platformCount = editingSettings?.BlacklistedPlatforms?.Count ?? 0;
                var tagCount = editingSettings?.BlacklistedTags?.Count ?? 0;
                return $"{platformCount} sources, {tagCount} tags, {BlacklistedGamesCount} games blocked";
            }
        }

        private void ApplyRecommendationPreset(RecommendationPreset preset)
        {
            if (preset == null)
                return;

            applyingRecommendationPreset = true;
            try
            {
                editingSettings.WeightedEngineWeight = preset.WeightedEngineWeight;
                editingSettings.CosineEngineWeight = preset.CosineEngineWeight;
                editingSettings.GraphEngineWeight = preset.GraphEngineWeight;
                editingSettings.NoveltyBonusStrength = preset.NoveltyBonusStrength;
                editingSettings.QualityWeightStrength = preset.QualityWeightStrength;
                OnPropertyChanged(nameof(WeightedEngineWeight));
                OnPropertyChanged(nameof(CosineEngineWeight));
                OnPropertyChanged(nameof(GraphEngineWeight));
                OnPropertyChanged(nameof(NoveltyBonusStrength));
                OnPropertyChanged(nameof(QualityWeightStrength));
            }
            finally
            {
                applyingRecommendationPreset = false;
            }
        }

        private void MarkCustomPreset()
        {
            if (applyingRecommendationPreset || editingSettings == null)
                return;
            if (string.Equals(editingSettings.RecommendationPresetId, RecommendationPresetCatalog.CustomId, StringComparison.OrdinalIgnoreCase))
                return;

            editingSettings.RecommendationPresetId = RecommendationPresetCatalog.CustomId;
            OnPropertyChanged(nameof(SelectedRecommendationPreset));
        }

        // ── ISettings ────────────────────────────────────────────────────

        public void BeginEdit()
        {
            // Clone current settings for editing
            editingSettings = new RecommenderSettings
            {
                SteamApiKey = savedSettings.SteamApiKey,
                SteamUserId = savedSettings.SteamUserId,
                IgdbClientId = savedSettings.IgdbClientId,
                IgdbClientSecret = savedSettings.IgdbClientSecret,
                RawgApiKey = savedSettings.RawgApiKey,
                AnthropicApiKey = savedSettings.AnthropicApiKey,
                OpenAiApiKey = savedSettings.OpenAiApiKey,
                OpenAiModel = string.IsNullOrWhiteSpace(savedSettings.OpenAiModel) ? "gpt-5.4-mini" : savedSettings.OpenAiModel,
                ItadApiKey = savedSettings.ItadApiKey,
                EnableJastDeals = savedSettings.EnableJastDeals,
                EnableMangaGamerDeals = savedSettings.EnableMangaGamerDeals,
                HideEarlyAccessRecommendations = savedSettings.HideEarlyAccessRecommendations,
                HideStaleEarlyAccessRecommendations = savedSettings.HideStaleEarlyAccessRecommendations,
                AiRerankerEnabled = savedSettings.AiRerankerEnabled,
                AiProvider = string.IsNullOrWhiteSpace(savedSettings.AiProvider) ? "Claude" : savedSettings.AiProvider,
                EnrichmentEnabled = savedSettings.EnrichmentEnabled,
                SpotlightEnabled = savedSettings.SpotlightEnabled,
                AnimeAssistantEnabled = savedSettings.AnimeAssistantEnabled,
                AssistantVoice = NormalizeAssistantVoice(savedSettings.AssistantVoice),
                SoundEffectsEnabled = savedSettings.SoundEffectsEnabled,
                SoundEffectsVolume = ClampVolume(savedSettings.SoundEffectsVolume),
                WeightedEngineWeight = savedSettings.WeightedEngineWeight,
                CosineEngineWeight = savedSettings.CosineEngineWeight,
                GraphEngineWeight = savedSettings.GraphEngineWeight,
                NoveltyBonusStrength = savedSettings.NoveltyBonusStrength,
                QualityWeightStrength = RecommendationPresetCatalog.ClampQuality(savedSettings.QualityWeightStrength),
                RecommendationPresetId = RecommendationPresetCatalog.NormalizePresetId(savedSettings.RecommendationPresetId),
                AiCandidateCount = savedSettings.AiCandidateCount,
                RejectedGames = CloneRejectedGames(savedSettings.RejectedGames),
                RecommendationFeedback = CloneRecommendationFeedback(savedSettings.RecommendationFeedback),
                BlacklistedPlatforms = CloneStringList(savedSettings.BlacklistedPlatforms),
                BlacklistedTags = CloneStringList(savedSettings.BlacklistedTags),
                BlacklistedGames = CloneBlacklistedGames(savedSettings.BlacklistedGames),
                MinecraftLauncherPaths = savedSettings.MinecraftLauncherPaths ?? string.Empty,
                LibraryIntegrationRecords = CloneLibraryIntegrationRecords(savedSettings.LibraryIntegrationRecords),
            };
            RefreshRejectedGames();
            RefreshBlacklistFields();
            OnPropertyChanged(nameof(SelectedRecommendationPreset));
            OnPropertyChanged(nameof(QualityWeightStrength));
            NotifyFeatureStatusChanged();
        }

        public void CancelEdit()
        {
            BeginEdit();
        }

        public void EndEdit()
        {
            savedSettings = editingSettings;
            plugin.SavePluginSettings(savedSettings);
            plugin.RebuildPipeline();
            plugin.NotifySettingsChanged();
            SettingsStatusText = "Settings saved and active";
            NotifyFeatureStatusChanged();
        }

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();
            double sum = editingSettings.WeightedEngineWeight
                       + editingSettings.CosineEngineWeight
                       + editingSettings.GraphEngineWeight;
            if (sum <= 0.05)
                errors.Add("At least one scoring engine weight must be above zero");
            if (editingSettings.AiRerankerEnabled)
            {
                if (string.Equals(editingSettings.AiProvider, "OpenAI", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(editingSettings.OpenAiApiKey))
                        errors.Add("OpenAI API key is required when OpenAI re-ranking is enabled");
                    if (string.IsNullOrWhiteSpace(editingSettings.OpenAiModel))
                        errors.Add("OpenAI model is required when OpenAI re-ranking is enabled");
                }
                else if (string.IsNullOrWhiteSpace(editingSettings.AnthropicApiKey))
                {
                    errors.Add("Anthropic API key is required when Claude re-ranking is enabled");
                }
            }
            if (!string.IsNullOrWhiteSpace(editingSettings.IgdbClientId) &&
                string.IsNullOrWhiteSpace(editingSettings.IgdbClientSecret))
                errors.Add("IGDB client secret is required when IGDB client ID is set");
            if (!string.IsNullOrWhiteSpace(editingSettings.IgdbClientSecret) &&
                string.IsNullOrWhiteSpace(editingSettings.IgdbClientId))
                errors.Add("IGDB client ID is required when IGDB client secret is set");
            if (!string.IsNullOrWhiteSpace(editingSettings.SteamApiKey) &&
                string.IsNullOrWhiteSpace(editingSettings.SteamUserId))
                errors.Add("Steam user ID is required when Steam API key is set");
            return errors.Count == 0;
        }
        public SettingsViewModel(GameRecommenderPlugin plugin)
        {
            this.plugin = plugin;
            savedSettings = plugin.LoadPluginSettings<RecommenderSettings>() ?? new RecommenderSettings();
            if (string.IsNullOrWhiteSpace(savedSettings.AiProvider)) savedSettings.AiProvider = "Claude";
            if (string.IsNullOrWhiteSpace(savedSettings.OpenAiModel)) savedSettings.OpenAiModel = "gpt-5.4-mini";
            savedSettings.RecommendationPresetId = RecommendationPresetCatalog.NormalizePresetId(savedSettings.RecommendationPresetId);
            savedSettings.QualityWeightStrength = RecommendationPresetCatalog.ClampQuality(savedSettings.QualityWeightStrength);
            if (savedSettings.RejectedGames == null) savedSettings.RejectedGames = new List<RejectedGameFeedback>();
            if (savedSettings.RecommendationFeedback == null) savedSettings.RecommendationFeedback = new List<RecommendationFeedback>();
            if (savedSettings.BlacklistedPlatforms == null) savedSettings.BlacklistedPlatforms = new List<string>();
            if (savedSettings.BlacklistedTags == null) savedSettings.BlacklistedTags = new List<string>();
            if (savedSettings.BlacklistedGames == null) savedSettings.BlacklistedGames = new List<BlacklistedGame>();
            if (savedSettings.LibraryIntegrationRecords == null) savedSettings.LibraryIntegrationRecords = new List<LibraryIntegrationRecord>();
            if (string.IsNullOrWhiteSpace(savedSettings.AssistantVoice))
            {
                savedSettings.AssistantVoice = "Waifu";
                savedSettings.SpotlightEnabled = true;
                savedSettings.AnimeAssistantEnabled = true;
            }
            savedSettings.AssistantVoice = NormalizeAssistantVoice(savedSettings.AssistantVoice);
            savedSettings.SoundEffectsVolume = ClampVolume(savedSettings.SoundEffectsVolume);
            editingSettings = savedSettings;
            TestConnectionsCommand = new RelayCommand(() => _ = TestConnectionsAsync());
            RestoreRejectedGameCommand = new RelayCommand<RejectedGameFeedback>(RestoreRejectedGameFromSettings);
            PreviewMinecraftModpacksCommand = new RelayCommand(PreviewMinecraftModpacks);
            ApplyMinecraftModpacksCommand = new RelayCommand(ApplyMinecraftModpacks, () => pendingMinecraftPlaytimePlan?.HasWrites == true);
            AddBlacklistedGameCommand = new RelayCommand(AddBlacklistedGame);
            RemoveBlacklistedGameCommand = new RelayCommand<BlacklistedGame>(RemoveBlacklistedGame);
            CheckForUpdatesCommand = new RelayCommand(() => _ = CheckForUpdatesAsync(), () => !IsCheckingForUpdates);
            OpenUpdatePageCommand = new RelayCommand(OpenUpdatePage, () => ShowOpenUpdatePage);
            UpdateStatusText = $"Current version: {CurrentExtensionVersion}";
            RefreshRejectedGames();
            RefreshBlacklistFields();
        }

        public RecommenderSettings GetSettings() => savedSettings;

        public IReadOnlyList<RejectedGameFeedback> GetRejectedGames()
            => savedSettings.RejectedGames ?? new List<RejectedGameFeedback>();

        public IReadOnlyList<RecommendationFeedback> GetRecommendationFeedback()
            => savedSettings.RecommendationFeedback ?? new List<RecommendationFeedback>();

        public bool IsRejected(EnrichedGame game)
        {
            if (game == null) return false;
            return GetRejectedGames().Any(r => MatchesRejectedGame(r, game));
        }

        public bool IsBlacklisted(EnrichedGame game)
        {
            if (game == null) return false;
            var settings = savedSettings ?? new RecommenderSettings();
            return MatchesBlockedSource(settings.BlacklistedPlatforms, game) ||
                   MatchesBlockedTag(settings.BlacklistedTags, game) ||
                   (settings.BlacklistedGames ?? new List<BlacklistedGame>()).Any(b => MatchesBlacklistedGame(b, game));
        }

        public RejectedGameFeedback RejectGame(ScoredGame scored, string reasonCode = null, string reasonText = null)
        {
            var game = scored?.Game;
            if (game == null)
                throw new ArgumentNullException(nameof(scored));

            if (savedSettings.RejectedGames == null)
                savedSettings.RejectedGames = new List<RejectedGameFeedback>();

            var existing = savedSettings.RejectedGames.FirstOrDefault(r => MatchesRejectedGame(r, game));
            if (existing == null)
            {
                existing = new RejectedGameFeedback
                {
                    PlayniteId = game.PlayniteId,
                    Name = game.Name ?? string.Empty,
                    SourcePlugin = game.SourcePlugin ?? string.Empty,
                    SteamAppId = game.SteamAppId ?? string.Empty
                };
                savedSettings.RejectedGames.Add(existing);
            }

            existing.RejectedAt = DateTime.UtcNow;
            existing.ReasonCode = reasonCode?.Trim() ?? string.Empty;
            existing.ReasonText = reasonText?.Trim() ?? string.Empty;
            plugin.SavePluginSettings(savedSettings);
            BeginEdit();
            return existing;
        }

        public RecommendationFeedback RecordRecommendationFeedback(ScoredGame scored, string action, string reasonText = null)
        {
            var game = scored?.Game;
            if (game == null)
                throw new ArgumentNullException(nameof(scored));

            if (savedSettings.RecommendationFeedback == null)
                savedSettings.RecommendationFeedback = new List<RecommendationFeedback>();

            action = (action ?? string.Empty).Trim();
            var existing = savedSettings.RecommendationFeedback
                .FirstOrDefault(f => MatchesRecommendationFeedback(f, game) &&
                                     string.Equals(f.Action, action, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                existing = new RecommendationFeedback
                {
                    PlayniteId = game.PlayniteId,
                    Name = game.Name ?? string.Empty,
                    SourcePlugin = game.SourcePlugin ?? string.Empty,
                    SteamAppId = game.SteamAppId ?? string.Empty,
                    Action = action
                };
                savedSettings.RecommendationFeedback.Add(existing);
            }

            existing.CreatedAt = DateTime.UtcNow;
            existing.ReasonText = reasonText?.Trim() ?? string.Empty;
            plugin.SavePluginSettings(savedSettings);
            BeginEdit();
            return existing;
        }

        public bool RestoreRejectedGame(RejectedGameFeedback rejected)
        {
            if (rejected == null || savedSettings.RejectedGames == null)
                return false;

            var removed = savedSettings.RejectedGames.RemoveAll(r => MatchesRejectedFeedback(r, rejected)) > 0;
            if (removed)
            {
                plugin.SavePluginSettings(savedSettings);
                BeginEdit();
            }
            return removed;
        }

        private void RestoreRejectedGameFromSettings(RejectedGameFeedback rejected)
        {
            if (rejected == null) return;
            if (RestoreRejectedGame(rejected))
                SettingsStatusText = $"Restored {rejected.Name}; it can appear after the next refresh.";
        }

        private async Task TestConnectionsAsync()
        {
            try
            {
                SettingsStatusText = "Testing configured services...";
                SettingsStatusText = await plugin.TestConnectionsAsync(editingSettings);
            }
            catch (Exception ex)
            {
                SettingsStatusText = "Connection test failed: " + ex.Message;
            }
        }

        private async Task CheckForUpdatesAsync()
        {
            IsCheckingForUpdates = true;
            updateActionUrl = string.Empty;
            OnPropertyChanged(nameof(ShowOpenUpdatePage));
            try
            {
                UpdateStatusText = "Checking for updates...";
                var result = await updateClient.CheckAsync();
                UpdateStatusText = result.StatusText;
                if (result.IsUpdateAvailable)
                {
                    updateActionUrl = !string.IsNullOrWhiteSpace(result.ReleaseUrl)
                        ? result.ReleaseUrl
                        : result.DownloadUrl;
                    if (string.IsNullOrWhiteSpace(updateActionUrl))
                        UpdateStatusText += " No release URL was provided.";
                }
            }
            finally
            {
                IsCheckingForUpdates = false;
                OnPropertyChanged(nameof(ShowOpenUpdatePage));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private void OpenUpdatePage()
        {
            if (string.IsNullOrWhiteSpace(updateActionUrl))
                return;
            UrlOpener.OpenHttpUrl(updateActionUrl);
        }

        private void PreviewMinecraftModpacks()
        {
            try
            {
                pendingMinecraftPlaytimePlan = plugin.PreviewMinecraftModpackLibrarySync(MinecraftLauncherPaths);
                var examples = pendingMinecraftPlaytimePlan.Operations
                    .Take(4)
                    .Select(o => $"{o.Entry?.SourceLabel}: {o.Entry?.Name} ({RecommendationEngine.FormatTime(o.Entry?.PlaytimeSeconds ?? 0)})")
                    .ToList();
                var diagnostics = pendingMinecraftPlaytimePlan.Diagnostics.Take(2).ToList();
                SettingsStatusText = pendingMinecraftPlaytimePlan.Summary() +
                    (examples.Any() ? " Profiles: " + string.Join("; ", examples) : string.Empty) +
                    (diagnostics.Any() ? " Diagnostics: " + string.Join("; ", diagnostics) : string.Empty);
                CommandManager.InvalidateRequerySuggested();
            }
            catch (Exception ex)
            {
                pendingMinecraftPlaytimePlan = null;
                SettingsStatusText = "Minecraft playtime preview failed: " + ex.Message;
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private void ApplyMinecraftModpacks()
        {
            if (pendingMinecraftPlaytimePlan == null)
            {
                SettingsStatusText = "Preview Minecraft playtime before applying.";
                return;
            }

            try
            {
                savedSettings.MinecraftLauncherPaths = MinecraftLauncherPaths;
                if (savedSettings.LibraryIntegrationRecords == null)
                    savedSettings.LibraryIntegrationRecords = new List<LibraryIntegrationRecord>();
                var result = plugin.ApplyMinecraftModpackLibrarySync(pendingMinecraftPlaytimePlan);
                pendingMinecraftPlaytimePlan = null;
                BeginEdit();
                SettingsStatusText = result.Summary();
                CommandManager.InvalidateRequerySuggested();
            }
            catch (Exception ex)
            {
                SettingsStatusText = "Minecraft playtime apply failed: " + ex.Message;
            }
        }

        private void AddBlacklistedGame()
        {
            var name = NewBlacklistedGameName?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                SettingsStatusText = "Enter a game name to blacklist.";
                return;
            }

            if (editingSettings.BlacklistedGames == null)
                editingSettings.BlacklistedGames = new List<BlacklistedGame>();

            var normalized = NormalizeGameTitle(name);
            if (editingSettings.BlacklistedGames.Any(g => NormalizeGameTitle(g.Name) == normalized))
            {
                SettingsStatusText = $"{name} is already blacklisted.";
                return;
            }

            editingSettings.BlacklistedGames.Add(new BlacklistedGame
            {
                Name = name,
                AddedAt = DateTime.UtcNow,
                ReasonText = "Manually blocked in settings"
            });
            NewBlacklistedGameName = string.Empty;
            RefreshBlacklistedGames();
            SettingsStatusText = $"Blacklisted {name}. Save settings to apply.";
        }

        private void RemoveBlacklistedGame(BlacklistedGame game)
        {
            if (game == null || editingSettings.BlacklistedGames == null)
                return;

            var removed = editingSettings.BlacklistedGames.RemoveAll(g => MatchesBlacklistedFeedback(g, game)) > 0;
            if (removed)
            {
                RefreshBlacklistedGames();
                SettingsStatusText = $"Removed {game.Name} from blacklist. Save settings to apply.";
            }
        }

        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private void NotifyFeatureStatusChanged()
            => OnPropertyChanged(nameof(FeatureStatusText));

        private string BuildFeatureStatusText()
        {
            var s = editingSettings ?? new RecommenderSettings();
            var parts = new List<string>();

            var steamReady = !string.IsNullOrWhiteSpace(s.SteamApiKey) && !string.IsNullOrWhiteSpace(s.SteamUserId);
            var igdbReady = !string.IsNullOrWhiteSpace(s.IgdbClientId) && !string.IsNullOrWhiteSpace(s.IgdbClientSecret);
            var rawgReady = !string.IsNullOrWhiteSpace(s.RawgApiKey);
            if (!s.EnrichmentEnabled)
                parts.Add("Enrichment off");
            else if (steamReady && igdbReady)
                parts.Add(rawgReady ? "Steam, IGDB, and RAWG enrichment ready" : "Steam and IGDB enrichment ready");
            else if (steamReady)
                parts.Add(rawgReady ? "Steam and RAWG ready, IGDB optional/missing" : "Steam ready, IGDB optional/missing");
            else if (igdbReady)
                parts.Add(rawgReady ? "IGDB and RAWG ready, Steam optional/missing" : "IGDB ready, Steam optional/missing");
            else if (rawgReady)
                parts.Add("RAWG supplemental enrichment ready");
            else
                parts.Add("Local recommendations only until Steam, IGDB, or RAWG is configured");

            if (s.AiRerankerEnabled)
            {
                var openAi = string.Equals(s.AiProvider, "OpenAI", StringComparison.OrdinalIgnoreCase);
                var aiReady = openAi
                    ? !string.IsNullOrWhiteSpace(s.OpenAiApiKey) && !string.IsNullOrWhiteSpace(s.OpenAiModel)
                    : !string.IsNullOrWhiteSpace(s.AnthropicApiKey);
                parts.Add(aiReady ? $"{s.AiProvider} re-ranking ready" : $"{s.AiProvider} re-ranking needs its API key");
            }
            else
            {
                parts.Add("AI re-ranking off");
            }

            var dealSources = new List<string>();
            if (!string.IsNullOrWhiteSpace(s.ItadApiKey))
                dealSources.Add("ITAD");
            if (!string.IsNullOrWhiteSpace(s.SteamUserId))
                dealSources.Add("Steam wishlist");
            if (s.EnableJastDeals)
                dealSources.Add("JAST");
            if (s.EnableMangaGamerDeals)
                dealSources.Add("MangaGamer");
            parts.Add(dealSources.Count > 0 ? "Deals: " + string.Join(", ", dealSources) : "Deals need ITAD, Steam user ID, or a sale-source toggle");

            if (s.HideEarlyAccessRecommendations)
                parts.Add("Early Access hidden");
            else if (s.HideStaleEarlyAccessRecommendations)
                parts.Add("Stale Early Access hidden");

            parts.Add(string.IsNullOrWhiteSpace(s.MinecraftLauncherPaths)
                ? "Minecraft uses automatic launcher paths"
                : "Minecraft custom paths configured");

            return string.Join(" | ", parts);
        }

        private void RefreshRejectedGames()
        {
            RejectedGames = new ObservableCollection<RejectedGameFeedback>(
                GetRejectedGames()
                    .OrderByDescending(r => r.RejectedAt)
                    .ThenBy(r => r.Name));
        }

        private void RefreshBlacklistFields()
        {
            blacklistedPlatformsText = FormatDelimitedList(editingSettings.BlacklistedPlatforms);
            blacklistedTagsText = FormatDelimitedList(editingSettings.BlacklistedTags);
            OnPropertyChanged(nameof(BlacklistedPlatformsText));
            OnPropertyChanged(nameof(BlacklistedTagsText));
            RefreshBlacklistedGames();
            OnPropertyChanged(nameof(BlacklistSummary));
        }

        private void RefreshBlacklistedGames()
        {
            BlacklistedGames = new ObservableCollection<BlacklistedGame>(
                (editingSettings.BlacklistedGames ?? new List<BlacklistedGame>())
                    .OrderBy(g => g.Name));
        }

        private static List<RejectedGameFeedback> CloneRejectedGames(IEnumerable<RejectedGameFeedback> rejectedGames)
        {
            return (rejectedGames ?? Enumerable.Empty<RejectedGameFeedback>())
                .Where(r => r != null)
                .Select(r => new RejectedGameFeedback
                {
                    PlayniteId = r.PlayniteId,
                    Name = r.Name ?? string.Empty,
                    SourcePlugin = r.SourcePlugin ?? string.Empty,
                    SteamAppId = r.SteamAppId ?? string.Empty,
                    RejectedAt = r.RejectedAt,
                    ReasonCode = r.ReasonCode ?? string.Empty,
                    ReasonText = r.ReasonText ?? string.Empty
                })
                .ToList();
        }

        private static List<RecommendationFeedback> CloneRecommendationFeedback(IEnumerable<RecommendationFeedback> feedback)
        {
            return (feedback ?? Enumerable.Empty<RecommendationFeedback>())
                .Where(f => f != null)
                .Select(f => new RecommendationFeedback
                {
                    PlayniteId = f.PlayniteId,
                    Name = f.Name ?? string.Empty,
                    SourcePlugin = f.SourcePlugin ?? string.Empty,
                    SteamAppId = f.SteamAppId ?? string.Empty,
                    Action = f.Action ?? string.Empty,
                    CreatedAt = f.CreatedAt,
                    ReasonText = f.ReasonText ?? string.Empty
                })
                .ToList();
        }

        private static List<LibraryIntegrationRecord> CloneLibraryIntegrationRecords(IEnumerable<LibraryIntegrationRecord> records)
        {
            return (records ?? Enumerable.Empty<LibraryIntegrationRecord>())
                .Where(r => r != null)
                .Select(r => new LibraryIntegrationRecord
                {
                    Provider = r.Provider ?? string.Empty,
                    ExternalId = r.ExternalId ?? string.Empty,
                    PlayniteId = r.PlayniteId,
                    DisplayName = r.DisplayName ?? string.Empty,
                    SourceLabel = r.SourceLabel ?? string.Empty,
                    ProfilePath = r.ProfilePath ?? string.Empty,
                    ConfigPath = r.ConfigPath ?? string.Empty,
                    LastKnownPlaytimeSeconds = r.LastKnownPlaytimeSeconds,
                    PlaytimeConfidence = r.PlaytimeConfidence ?? string.Empty,
                    SyncStatus = r.SyncStatus ?? string.Empty,
                    LastSyncedAt = r.LastSyncedAt
                })
                .ToList();
        }

        private static List<BlacklistedGame> CloneBlacklistedGames(IEnumerable<BlacklistedGame> blacklistedGames)
        {
            return (blacklistedGames ?? Enumerable.Empty<BlacklistedGame>())
                .Where(g => g != null)
                .Select(g => new BlacklistedGame
                {
                    PlayniteId = g.PlayniteId,
                    Name = g.Name ?? string.Empty,
                    SourcePlugin = g.SourcePlugin ?? string.Empty,
                    SteamAppId = g.SteamAppId ?? string.Empty,
                    AddedAt = g.AddedAt,
                    ReasonText = g.ReasonText ?? string.Empty
                })
                .ToList();
        }

        private static List<string> CloneStringList(IEnumerable<string> values)
            => ParseDelimitedList(string.Join(";", values ?? Enumerable.Empty<string>()));

        private static bool MatchesRejectedGame(RejectedGameFeedback rejected, EnrichedGame game)
        {
            if (rejected == null || game == null) return false;
            if (rejected.PlayniteId != Guid.Empty && rejected.PlayniteId == game.PlayniteId)
                return true;
            if (!string.IsNullOrWhiteSpace(rejected.SteamAppId) &&
                !string.IsNullOrWhiteSpace(game.SteamAppId) &&
                string.Equals(rejected.SteamAppId, game.SteamAppId, StringComparison.OrdinalIgnoreCase))
                return true;

            var rejectedName = NormalizeMatchText(rejected.Name);
            var gameName = NormalizeMatchText(game.Name);
            var rejectedSource = NormalizeMatchText(rejected.SourcePlugin);
            var gameSource = NormalizeMatchText(game.SourcePlugin);
            return !string.IsNullOrWhiteSpace(rejectedName) &&
                   rejectedName == gameName &&
                   rejectedSource == gameSource;
        }

        private static bool MatchesRecommendationFeedback(RecommendationFeedback feedback, EnrichedGame game)
        {
            if (feedback == null || game == null) return false;
            if (feedback.PlayniteId != Guid.Empty && feedback.PlayniteId == game.PlayniteId)
                return true;
            if (!string.IsNullOrWhiteSpace(feedback.SteamAppId) &&
                !string.IsNullOrWhiteSpace(game.SteamAppId) &&
                string.Equals(feedback.SteamAppId, game.SteamAppId, StringComparison.OrdinalIgnoreCase))
                return true;

            var feedbackName = NormalizeMatchText(feedback.Name);
            var gameName = NormalizeMatchText(game.Name);
            var feedbackSource = NormalizeMatchText(feedback.SourcePlugin);
            var gameSource = NormalizeMatchText(game.SourcePlugin);
            return !string.IsNullOrWhiteSpace(feedbackName) &&
                   feedbackName == gameName &&
                   feedbackSource == gameSource;
        }

        private static bool MatchesBlockedSource(IEnumerable<string> blockedSources, EnrichedGame game)
        {
            var source = NormalizeMatchText(game?.SourcePlugin);
            if (string.IsNullOrWhiteSpace(source)) return false;
            return (blockedSources ?? Enumerable.Empty<string>())
                .Select(NormalizeMatchText)
                .Any(blocked => !string.IsNullOrWhiteSpace(blocked) && blocked == source);
        }

        private static bool MatchesBlockedTag(IEnumerable<string> blockedTags, EnrichedGame game)
        {
            var blocked = new HashSet<string>(
                (blockedTags ?? Enumerable.Empty<string>()).Select(NormalizeMatchText).Where(t => !string.IsNullOrWhiteSpace(t)),
                StringComparer.OrdinalIgnoreCase);
            if (blocked.Count == 0) return false;
            return (game?.Tags ?? new List<string>())
                .Select(NormalizeMatchText)
                .Any(tag => blocked.Contains(tag));
        }

        private static bool MatchesBlacklistedGame(BlacklistedGame blocked, EnrichedGame game)
        {
            if (blocked == null || game == null) return false;
            if (blocked.PlayniteId.HasValue && blocked.PlayniteId.Value != Guid.Empty && blocked.PlayniteId.Value == game.PlayniteId)
                return true;
            if (!string.IsNullOrWhiteSpace(blocked.SteamAppId) &&
                !string.IsNullOrWhiteSpace(game.SteamAppId) &&
                string.Equals(blocked.SteamAppId, game.SteamAppId, StringComparison.OrdinalIgnoreCase))
                return true;

            var blockedName = NormalizeGameTitle(blocked.Name);
            var gameName = NormalizeGameTitle(game.Name);
            if (string.IsNullOrWhiteSpace(blockedName) || blockedName != gameName)
                return false;

            var blockedSource = NormalizeMatchText(blocked.SourcePlugin);
            return string.IsNullOrWhiteSpace(blockedSource) || blockedSource == NormalizeMatchText(game.SourcePlugin);
        }

        private static bool MatchesBlacklistedFeedback(BlacklistedGame left, BlacklistedGame right)
        {
            if (left == null || right == null) return false;
            if (left.PlayniteId.HasValue && right.PlayniteId.HasValue &&
                left.PlayniteId.Value != Guid.Empty && right.PlayniteId.Value != Guid.Empty)
                return left.PlayniteId.Value == right.PlayniteId.Value;
            if (!string.IsNullOrWhiteSpace(left.SteamAppId) && !string.IsNullOrWhiteSpace(right.SteamAppId))
                return string.Equals(left.SteamAppId, right.SteamAppId, StringComparison.OrdinalIgnoreCase);
            return NormalizeGameTitle(left.Name) == NormalizeGameTitle(right.Name) &&
                   NormalizeMatchText(left.SourcePlugin) == NormalizeMatchText(right.SourcePlugin);
        }

        private static bool MatchesRejectedFeedback(RejectedGameFeedback left, RejectedGameFeedback right)
        {
            if (left == null || right == null) return false;
            if (left.PlayniteId != Guid.Empty && right.PlayniteId != Guid.Empty)
                return left.PlayniteId == right.PlayniteId;
            if (!string.IsNullOrWhiteSpace(left.SteamAppId) && !string.IsNullOrWhiteSpace(right.SteamAppId))
                return string.Equals(left.SteamAppId, right.SteamAppId, StringComparison.OrdinalIgnoreCase);
            return NormalizeMatchText(left.Name) == NormalizeMatchText(right.Name) &&
                   NormalizeMatchText(left.SourcePlugin) == NormalizeMatchText(right.SourcePlugin);
        }

        private static string NormalizeMatchText(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var normalized = value.ToLowerInvariant();
            normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
            return normalized;
        }

        private static string NormalizeGameTitle(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            return Regex.Replace(value.ToLowerInvariant(), @"[^a-z0-9]+", string.Empty);
        }

        private static List<string> ParseDelimitedList(string value)
        {
            return (value ?? string.Empty)
                .Split(new[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(v => v.Trim())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string FormatDelimitedList(IEnumerable<string> values)
            => string.Join("; ", ParseDelimitedList(string.Join(";", values ?? Enumerable.Empty<string>())));

        private static string NormalizeAssistantVoice(string value)
        {
            if (string.Equals(value, "Anime Guide", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "Playful", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "Waifu", StringComparison.OrdinalIgnoreCase))
                return "Waifu";
            if (string.Equals(value, "Hostile", StringComparison.OrdinalIgnoreCase))
                return "Hostile";
            return "Neutral";
        }

        private static double ClampVolume(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
                return 0.45;
            if (value > 1.0)
                return 1.0;
            return value;
        }
    }
}
