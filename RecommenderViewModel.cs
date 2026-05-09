using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace GameRecommender
{
    internal enum RefreshRequestKind
    {
        ManualRescore,
        ManualMetadata,
        Background
    }

    public class RecommenderViewModel : INotifyPropertyChanged
    {
        private readonly IPlayniteAPI api;
        private readonly GameRecommenderPlugin plugin;
        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly RejectionReasonChoice[] RejectionReasonChoices =
        {
            new RejectionReasonChoice(string.Empty, "No reason provided"),
            new RejectionReasonChoice("already_played_elsewhere", "Already played elsewhere"),
            new RejectionReasonChoice("not_interested", "Not interested"),
            new RejectionReasonChoice("wrong_genre", "Wrong genre"),
            new RejectionReasonChoice("too_multiplayer", "Too multiplayer-focused"),
            new RejectionReasonChoice("too_long", "Too long / too much commitment"),
            new RejectionReasonChoice("quality_concern", "Poor reviews / quality concern"),
            new RejectionReasonChoice("duplicate_wrong_edition", "Duplicate / wrong edition")
        };

        public event PropertyChangedEventHandler PropertyChanged;

        public ICommand RefreshCommand { get; }
        public ICommand RefreshMetadataCommand { get; }
        public ICommand LaunchRecCommand { get; }
        public ICommand LaunchRecentCommand { get; }
        public ICommand GoToLibraryCommand { get; }
        public ICommand OpenSettingsCommand { get; }
        public ICommand ExportCommand { get; }
        public ICommand DetailsCommand { get; }
        public ICommand RejectRecommendationCommand { get; }
        public ICommand RunGenreCommand { get; }
        public ICommand ClearGenreCommand { get; }
        public ICommand FindNotOwnedCommand { get; }
        public ICommand FindDealsCommand { get; }
        public ICommand OpenDealCommand { get; }
        public ICommand RefreshSpotlightCommand { get; }
        public ICommand OpenSpotlightCommand { get; }
        public ICommand ToggleReviewModeCommand { get; }
        public ICommand ReviewPreviousCommand { get; }
        public ICommand ReviewNextCommand { get; }
        public ICommand ReviewPlayCommand { get; }
        public ICommand ReviewGoToLibraryCommand { get; }
        public ICommand ReviewSaveCommand { get; }
        public ICommand ReviewMoreLikeThisCommand { get; }
        public ICommand ReviewLessLikeThisCommand { get; }
        public ICommand ReviewRejectCommand { get; }

        private ObservableCollection<ScoredGame> recommendations = new ObservableCollection<ScoredGame>();
        private static readonly TimeSpan StaleRefreshInterval = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan BackgroundRefreshDebounce = TimeSpan.FromSeconds(10);
        public ObservableCollection<ScoredGame> Recommendations
        {
            get => recommendations;
            set
            {
                recommendations = value ?? new ObservableCollection<ScoredGame>();
                recommendationsVersion++;
                InvalidateFilterCache();
                reviewCompletedIds.Clear();
                OnPropertyChanged();
            }
        }

        private ObservableCollection<ScoredGame> filteredRecommendations = new ObservableCollection<ScoredGame>();
        public ObservableCollection<ScoredGame> FilteredRecommendations
        {
            get => filteredRecommendations;
            set
            {
                filteredRecommendations = value ?? new ObservableCollection<ScoredGame>();
                OnPropertyChanged();
                NotifyRecommendationStateChanged();
                SyncReviewRecommendation();
            }
        }

        private ObservableCollection<ScoredGame> continueList = new ObservableCollection<ScoredGame>();
        public ObservableCollection<ScoredGame> ContinueList { get => continueList; set { continueList = value ?? new ObservableCollection<ScoredGame>(); OnPropertyChanged(); NotifyRecommendationStateChanged(); } }

        private ObservableCollection<Game> recentlyPlayed = new ObservableCollection<Game>();
        public ObservableCollection<Game> RecentlyPlayed { get => recentlyPlayed; set { recentlyPlayed = value; OnPropertyChanged(); } }

        private ObservableCollection<ExternalRecommendation> externalRecommendations = new ObservableCollection<ExternalRecommendation>();
        public ObservableCollection<ExternalRecommendation> ExternalRecommendations { get => externalRecommendations; set { externalRecommendations = value ?? new ObservableCollection<ExternalRecommendation>(); OnPropertyChanged(); NotifyDealsStateChanged(); } }

        private ObservableCollection<ExternalRecommendation> notOwnedRecommendations = new ObservableCollection<ExternalRecommendation>();
        public ObservableCollection<ExternalRecommendation> NotOwnedRecommendations { get => notOwnedRecommendations; set { notOwnedRecommendations = value ?? new ObservableCollection<ExternalRecommendation>(); OnPropertyChanged(); NotifyDealsStateChanged(); } }

        private ObservableCollection<SpotlightItem> spotlights = new ObservableCollection<SpotlightItem>();
        public ObservableCollection<SpotlightItem> Spotlights { get => spotlights; set { spotlights = value; OnPropertyChanged(); } }

        public ObservableCollection<string> RecommendationCategories { get; } = new ObservableCollection<string>
        {
            "All",
            "Best Matches",
            "Fresh Finds",
            "Shooters & Combat",
            "Strategy & Sims",
            "Survival & Crafting",
            "Co-op / Multiplayer",
            "Story & Campaign",
            "RPGs & Tactics",
            "Risky / Mixed"
        };

        private bool isLoading; public bool IsLoading { get => isLoading; set { if (isLoading == value) return; isLoading = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowLoadingView)); NotifyRecommendationStateChanged(); NotifyDealsStateChanged(); UpdateBusyState(); } }
        private bool isAiLoading; public bool IsAiLoading { get => isAiLoading; set { if (isAiLoading == value) return; isAiLoading = value; OnPropertyChanged(); UpdateBusyState(); } }
        private bool isDealsLoading; public bool IsDealsLoading { get => isDealsLoading; set { if (isDealsLoading == value) return; isDealsLoading = value; OnPropertyChanged(); NotifyDealsStateChanged(); UpdateBusyState(); } }
        private bool isSpotlightLoading; public bool IsSpotlightLoading { get => isSpotlightLoading; set { if (isSpotlightLoading == value) return; isSpotlightLoading = value; OnPropertyChanged(); UpdateBusyState(); } }
        private bool isBusy; public bool IsBusy { get => isBusy; private set { if (isBusy == value) return; isBusy = value; OnPropertyChanged(); } }
        private string statusText = "Click refresh to analyse your library"; public string StatusText { get => statusText; set { statusText = value; OnPropertyChanged(); } }
        private string aiStatusText = string.Empty; public string AiStatusText { get => aiStatusText; set { aiStatusText = value; OnPropertyChanged(); } }
        private string assistantText = "Ready to scout your library.";
        public string AssistantText { get => assistantText; set { assistantText = value; OnPropertyChanged(); } }
        private string assistantFrameSource = "pack://application:,,,/GameRecommender;component/Assets/assistant_frames/assistant-0.png";
        public string AssistantFrameSource { get => assistantFrameSource; set { assistantFrameSource = value; OnPropertyChanged(); } }
        private bool assistantVisible;
        public bool AssistantVisible { get => assistantVisible; set { assistantVisible = value; OnPropertyChanged(); } }
        private int totalGames; public int TotalGames { get => totalGames; set { totalGames = value; OnPropertyChanged(); } }
        private int unplayedCount; public int UnplayedCount { get => unplayedCount; set { unplayedCount = value; OnPropertyChanged(); } }
        private string totalPlaytime; public string TotalPlaytime { get => totalPlaytime; set { totalPlaytime = value; OnPropertyChanged(); } }
        private bool aiEnabled; public bool AiEnabled { get => aiEnabled; set { aiEnabled = value; OnPropertyChanged(); } }
        private bool isGenreMode; public bool IsGenreMode { get => isGenreMode; set { isGenreMode = value; OnPropertyChanged(); } }
        private string genreModeTitle = "RECOMMENDED FOR YOU"; public string GenreModeTitle { get => genreModeTitle; set { genreModeTitle = value; OnPropertyChanged(); } }
        private string selectedRecommendationCategory = "All";
        public string SelectedRecommendationCategory
        {
            get => selectedRecommendationCategory;
            set
            {
                var normalized = string.IsNullOrWhiteSpace(value) ? "All" : value;
                if (string.Equals(selectedRecommendationCategory, normalized, StringComparison.Ordinal))
                    return;
                selectedRecommendationCategory = normalized;
                OnPropertyChanged();
                NotifyRecommendationStateChanged();
                if (!IsGenreMode) UpdateCollections(lastFused);
                ReactToCategory(selectedRecommendationCategory);
            }
        }

        private string filterText = string.Empty;
        public string FilterText
        {
            get => filterText;
            set
            {
                var normalized = value ?? string.Empty;
                if (string.Equals(filterText, normalized, StringComparison.Ordinal))
                    return;
                filterText = normalized;
                OnPropertyChanged();
                NotifyRecommendationStateChanged();
                ApplyFilter();
            }
        }

        private string genreText = string.Empty;
        public string GenreText { get => genreText; set { genreText = value; OnPropertyChanged(); NotifyRecommendationStateChanged(); } }

        private string filterSummary = "Showing all recommendation categories";
        public string FilterSummary { get => filterSummary; set { filterSummary = value; OnPropertyChanged(); } }
        public bool ShowLoadingView => IsLoading;
        public bool ShowRecommendationsEmptyState => !IsLoading && FilteredRecommendations.Count == 0 && ContinueList.Count == 0;
        public string RecommendationsEmptyStateText
        {
            get
            {
                if (IsGenreMode && !string.IsNullOrWhiteSpace(GenreText))
                    return $"No owned matches found for {GenreText.Trim()}.";
                if (!string.IsNullOrWhiteSpace(FilterText))
                    return $"No recommendations match {FilterText.Trim()}.";
                if (lastRefresh == DateTime.MinValue)
                    return "No recommendations yet. Refresh to analyse your library.";
                return "No recommendations found. Refresh to try again.";
            }
        }
        public bool ShowDealsEmptyState => !IsLoading && !IsDealsLoading && dealsSearchCompleted && ExternalRecommendations.Count == 0;
        public bool ShowNotOwnedSection => IsDealsLoading || NotOwnedRecommendations.Count > 0 || ExternalRecommendations.Count > 0 || ShowNotOwnedEmptyState;
        public bool ShowNotOwnedEmptyState => !IsLoading && !IsDealsLoading && notOwnedSearchCompleted && NotOwnedRecommendations.Count == 0;

        private bool isReviewModeEnabled;
        public bool IsReviewModeEnabled
        {
            get => isReviewModeEnabled;
            set
            {
                if (isReviewModeEnabled == value)
                    return;
                isReviewModeEnabled = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ReviewModeToggleText));
                SyncReviewRecommendation(resetPosition: true);
            }
        }

        private ScoredGame currentReviewRecommendation;
        public ScoredGame CurrentReviewRecommendation
        {
            get => currentReviewRecommendation;
            private set
            {
                currentReviewRecommendation = value;
                OnPropertyChanged();
                NotifyReviewStateChanged();
            }
        }

        private string reviewQueueSummary = "Review queue is empty";
        public string ReviewQueueSummary { get => reviewQueueSummary; private set { reviewQueueSummary = value; OnPropertyChanged(); } }
        public string ReviewModeToggleText => IsReviewModeEnabled ? "Close review" : "Review";
        public bool ShowReviewPanel => IsReviewModeEnabled && CurrentReviewRecommendation != null;
        public bool ShowReviewEmptyState => IsReviewModeEnabled && CurrentReviewRecommendation == null;

        private DateTime lastRefresh = DateTime.MinValue;
        private DateTime lastBackgroundRefreshStarted = DateTime.MinValue;
        private bool pendingBackgroundRefresh;
        private bool pendingBackgroundEnrichmentRequired;
        private string pendingBackgroundReason = string.Empty;
        private List<EnrichedGame> lastEnrichedGames = new List<EnrichedGame>();
        private TasteProfile lastTasteProfile = new TasteProfile();
        private List<ScoredGame> lastFused = new List<ScoredGame>();
        private readonly DispatcherTimer assistantTimer;
        private int assistantFrame;
        private int refreshGeneration;
        private int activeAiGeneration = -1;
        private int activeSpotlightGeneration = -1;
        private int recommendationsVersion;
        private int filteredRecommendationsVersion = -1;
        private string filteredRecommendationsText = null;
        private bool notOwnedSearchCompleted;
        private bool dealsSearchCompleted;
        private readonly SoundEffectService soundEffects;
        private int lastPreferenceBlockedCount;
        private readonly HashSet<Guid> reviewCompletedIds = new HashSet<Guid>();
        private int reviewIndex;

        public RecommenderViewModel(IPlayniteAPI api, GameRecommenderPlugin plugin)
        {
            this.api = api;
            this.plugin = plugin;
            soundEffects = new SoundEffectService(() => plugin.SettingsVm.GetSettings(), ReportSoundStatus);
            RefreshCommand = new RelayCommand(() => _ = RefreshAsync(RefreshRequestKind.ManualRescore, reason: "manual rescore"));
            RefreshMetadataCommand = new RelayCommand(() => _ = RefreshAsync(RefreshRequestKind.ManualMetadata, enrichmentRequired: true, reason: "manual metadata refresh"));
            LaunchRecCommand = new RelayCommand<ScoredGame>(LaunchRecommendation);
            LaunchRecentCommand = new RelayCommand<Game>(LaunchRecent);
            GoToLibraryCommand = new RelayCommand<ScoredGame>(rec => GoToLibrary(rec, openDetailsOnFailure: true));
            OpenSettingsCommand = new RelayCommand(() => plugin.OpenSettingsView());
            ExportCommand = new RelayCommand(() => ExportToClipboard());
            DetailsCommand = new RelayCommand<ScoredGame>(ShowDetails);
            RejectRecommendationCommand = new RelayCommand<ScoredGame>(RejectRecommendation);
            RunGenreCommand = new RelayCommand(() => RunGenreRecommendation());
            ClearGenreCommand = new RelayCommand(ClearGenreRecommendation);
            FindNotOwnedCommand = new RelayCommand(() => _ = FindNotOwnedAsync());
            FindDealsCommand = new RelayCommand(() => _ = FindDealsAsync());
            OpenDealCommand = new RelayCommand<ExternalRecommendation>(OpenDeal);
            RefreshSpotlightCommand = new RelayCommand(() => _ = RefreshSpotlightAsync(refreshGeneration, manualRequest: true));
            OpenSpotlightCommand = new RelayCommand<SpotlightItem>(OpenSpotlight);
            ToggleReviewModeCommand = new RelayCommand(() => IsReviewModeEnabled = !IsReviewModeEnabled);
            ReviewPreviousCommand = new RelayCommand(MoveReviewPrevious);
            ReviewNextCommand = new RelayCommand(MoveReviewNext);
            ReviewPlayCommand = new RelayCommand(() => LaunchRecommendation(CurrentReviewRecommendation));
            ReviewGoToLibraryCommand = new RelayCommand(() => GoToLibrary(CurrentReviewRecommendation, openDetailsOnFailure: true));
            ReviewSaveCommand = new RelayCommand(() => RecordReviewFeedback("Saved", "Saved for later"));
            ReviewMoreLikeThisCommand = new RelayCommand(() => RecordReviewFeedback("MoreLikeThis", "More like this"));
            ReviewLessLikeThisCommand = new RelayCommand(() => RecordReviewFeedback("LessLikeThis", "Less like this"));
            ReviewRejectCommand = new RelayCommand(RejectCurrentReviewRecommendation);
            AssistantVisible = plugin.SettingsVm.GetSettings().AnimeAssistantEnabled;
            SetAssistantText(AssistantVoiceFormatter.Ready(CurrentAssistantVoice), AssistantVoiceLineKind.Ready);
            assistantTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
            assistantTimer.Tick += (s, e) => AdvanceAssistantFrame();
            assistantTimer.Start();
        }

        public void RefreshIfStale()
        {
            var age = lastRefresh == DateTime.MinValue ? TimeSpan.MaxValue : DateTime.Now - lastRefresh;
            if (age > StaleRefreshInterval)
            {
                QueueBackgroundRefresh(enrichmentRequired: false, reason: "sidebar opened stale");
                return;
            }

            logger.Info($"Refresh skipped: sidebar opened; last refresh was {Math.Round(age.TotalMinutes, 1)} minutes ago");
        }

        public void TriggerBackgroundRefresh(bool enrichmentRequired = false, string reason = "background event")
            => QueueBackgroundRefresh(enrichmentRequired, reason);

        public void Refresh() => _ = RefreshAsync(RefreshRequestKind.ManualRescore, reason: "manual rescore");

        public void OnSettingsChanged()
        {
            AiEnabled = plugin.AiReranker.IsEnabled;
            AiStatusText = AiEnabled ? $"{plugin.AiReranker.ProviderName} re-ranking enabled" : string.Empty;
            AssistantVisible = plugin.SettingsVm.GetSettings().AnimeAssistantEnabled;
            InvalidateFilterCache();
            if (lastFused.Any())
                UpdateCollections(lastFused);
            else
                ApplyFilter();
        }

        private void QueueBackgroundRefresh(bool enrichmentRequired, string reason)
        {
            reason = string.IsNullOrWhiteSpace(reason) ? "background event" : reason.Trim();
            if (IsLoading)
            {
                pendingBackgroundRefresh = true;
                pendingBackgroundEnrichmentRequired |= enrichmentRequired;
                pendingBackgroundReason = MergeRefreshReasons(pendingBackgroundReason, reason);
                logger.Info($"Refresh coalesced: {reason}; refresh already running");
                return;
            }

            var now = DateTime.Now;
            if (!enrichmentRequired &&
                lastBackgroundRefreshStarted != DateTime.MinValue &&
                now - lastBackgroundRefreshStarted < BackgroundRefreshDebounce)
            {
                logger.Info($"Refresh skipped: {reason}; background refresh started {(now - lastBackgroundRefreshStarted).TotalSeconds:0.0}s ago");
                return;
            }

            _ = RefreshAsync(RefreshRequestKind.Background, enrichmentRequired, reason);
        }

        private async Task RefreshAsync(RefreshRequestKind requestKind, bool enrichmentRequired = false, string reason = null)
        {
            var stopwatch = Stopwatch.StartNew();
            var isManual = requestKind != RefreshRequestKind.Background;
            reason = string.IsNullOrWhiteSpace(reason) ? requestKind.ToString() : reason.Trim();
            if (IsLoading)
            {
                if (isManual)
                    StatusText = "Refresh already running...";
                else
                {
                    pendingBackgroundRefresh = true;
                    pendingBackgroundEnrichmentRequired |= enrichmentRequired;
                    pendingBackgroundReason = MergeRefreshReasons(pendingBackgroundReason, reason);
                }
                logger.Info($"Refresh coalesced: {requestKind}; reason={reason}; refresh already running");
                return;
            }

            var generation = isManual ? ++refreshGeneration : refreshGeneration;
            if (!isManual)
                lastBackgroundRefreshStarted = DateTime.Now;
            logger.Info($"Refresh started: kind={requestKind}; reason={reason}; enrichmentRequired={enrichmentRequired}");
            IsLoading = true;
            AiEnabled = plugin.AiReranker.IsEnabled;
            AiStatusText = AiEnabled ? $"{plugin.AiReranker.ProviderName} re-ranking enabled" : string.Empty;
            AssistantVisible = plugin.SettingsVm.GetSettings().AnimeAssistantEnabled;
            if (isManual)
                SetAssistantText(
                    AssistantVoiceFormatter.RefreshStart(
                        CurrentAssistantVoice,
                        requestKind == RefreshRequestKind.ManualMetadata),
                    AssistantVoiceLineKind.Working);

            try
            {
                if (requestKind == RefreshRequestKind.ManualMetadata || enrichmentRequired)
                {
                    StatusText = requestKind == RefreshRequestKind.ManualMetadata
                        ? "Refreshing metadata cache..."
                        : StatusText;
                    plugin.Enrichment.InvalidateCache();
                    logger.Info($"Refresh invalidated enrichment cache: kind={requestKind}; reason={reason}");
                }
                else
                {
                    StatusText = isManual ? "Re-scoring recommendations..." : StatusText;
                }

                var progress = new Progress<string>(msg => StatusText = msg);
                var enriched = await plugin.Enrichment.GetEnrichedGamesAsync(progress);

                var allGames = api.Database.Games.Where(g => !g.Hidden).ToList();
                TotalGames = allGames.Count;
                UnplayedCount = allGames.Count(g => g.Playtime == 0);
                TotalPlaytime = RecommendationEngine.FormatTime(allGames.Sum(g => (long)g.Playtime));

                StatusText = "Building taste profile...";
                var played = enriched.Where(g => g.IsDeepPlayed).ToList();
                var tasteProfile = plugin.WeightedEngine.BuildTasteProfile(played);

                lastEnrichedGames = enriched;
                lastTasteProfile = tasteProfile;

                StatusText = "Computing similarity index...";
                plugin.CosineEngine.BuildIndex(enriched);

                var candidates = enriched
                    .Where(g => g.PlaytimeSeconds < 18000)
                    .Where(g => !RecommendationHeuristics.ShouldSuppressFromRecommendations(g))
                    .ToList();

                StatusText = "Running scoring engines...";
                var wTask = Task.Run(() => plugin.WeightedEngine.Score(candidates, tasteProfile));
                var cTask = Task.Run(() => plugin.CosineEngine.Score(candidates, played));
                var gTask = Task.Run(() => plugin.GraphEngine.Score(candidates, enriched));
                await Task.WhenAll(wTask, cTask, gTask);

                StatusText = "Fusing scores...";
                var topPlayed = played.OrderByDescending(g => g.PlaytimeSeconds).Take(10).ToList();
                var fused = plugin.Fusion.Fuse(wTask.Result, cTask.Result, gTask.Result, candidates, tasteProfile, topPlayed);
                lastFused = fused;

                if (IsGenreMode && !string.IsNullOrWhiteSpace(GenreText))
                    ApplyGenreRecommendation();
                else
                    UpdateCollections(fused, playAssistantVoiceLine: isManual);

                StatusText = requestKind == RefreshRequestKind.ManualMetadata
                    ? $"Metadata refreshed; found {fused.Count} recommendations"
                    : $"Re-scored {fused.Count} recommendations";
                if (isManual)
                    soundEffects.Play(SoundEffectKind.RefreshComplete);
                lastRefresh = DateTime.Now;
                NotifyRecommendationStateChanged();

                if (plugin.AiReranker.IsEnabled && !IsGenreMode)
                {
                    _ = RunAiRerankerAsync(SelectRecommendationCategory(fused).Take(40).ToList(), tasteProfile, generation);
                    _ = RunAiStartedReasonsAsync(ContinueList.ToList(), tasteProfile, generation);
                }
                if (plugin.SettingsVm.GetSettings().SpotlightEnabled)
                    _ = RefreshSpotlightAsync(generation);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Pipeline failed");
                StatusText = $"Error: {ex.Message}";
                if (isManual)
                    soundEffects.Play(SoundEffectKind.Error);
            }
            finally
            {
                IsLoading = false;
                stopwatch.Stop();
                logger.Info($"Refresh finished: kind={requestKind}; reason={reason}; elapsedMs={stopwatch.ElapsedMilliseconds}");
                RunPendingBackgroundRefreshIfNeeded();
            }
        }

        private void RunPendingBackgroundRefreshIfNeeded()
        {
            if (!pendingBackgroundRefresh)
                return;

            var enrichmentRequired = pendingBackgroundEnrichmentRequired;
            var reason = string.IsNullOrWhiteSpace(pendingBackgroundReason)
                ? "coalesced background event"
                : pendingBackgroundReason;
            pendingBackgroundRefresh = false;
            pendingBackgroundEnrichmentRequired = false;
            pendingBackgroundReason = string.Empty;
            QueueBackgroundRefresh(enrichmentRequired, reason);
        }

        private static string MergeRefreshReasons(string existing, string next)
        {
            if (string.IsNullOrWhiteSpace(existing))
                return next ?? string.Empty;
            if (string.IsNullOrWhiteSpace(next) ||
                existing.IndexOf(next, StringComparison.OrdinalIgnoreCase) >= 0)
                return existing;
            return existing + ", " + next;
        }

        private async Task RunAiRerankerAsync(List<ScoredGame> candidates, TasteProfile profile, int generation)
        {
            activeAiGeneration = generation;
            IsAiLoading = true;
            AiStatusText = $"{plugin.AiReranker.ProviderName} is re-ranking...";
            SetAssistantText(
                AssistantVoiceFormatter.AiStart(CurrentAssistantVoice, plugin.AiReranker.ProviderName),
                AssistantVoiceLineKind.Working);
            try
            {
                var reranked = await plugin.AiReranker.ReRankAsync(candidates, profile);
                api.MainView.UIDispatcher.Invoke(() =>
                {
                    if (generation != refreshGeneration)
                        return;
                    ApplyAiRankingToLastFused(reranked);
                    if (!IsGenreMode)
                        UpdateCollections(lastFused);
                    else
                        ApplyFilter();
                    AiStatusText = $"{plugin.AiReranker.ProviderName} re-ranking applied";
                    SetAssistantText(AssistantVoiceFormatter.AiSuccess(CurrentAssistantVoice), AssistantVoiceLineKind.Success, playVoiceLine: false);
                });
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "AI re-ranker failed");
                if (generation != refreshGeneration)
                    return;
                AiStatusText = $"{plugin.AiReranker.ProviderName} failed: {ex.Message}";
                soundEffects.Play(SoundEffectKind.Error);
                SetAssistantText(AssistantVoiceFormatter.AiFailure(CurrentAssistantVoice), AssistantVoiceLineKind.Error);
            }
            finally
            {
                if (activeAiGeneration == generation)
                {
                    IsAiLoading = false;
                    activeAiGeneration = -1;
                }
            }
        }

        private async Task RunAiStartedReasonsAsync(List<ScoredGame> candidates, TasteProfile profile, int generation)
        {
            if (candidates == null || !candidates.Any()) return;
            try
            {
                var items = await plugin.AiReranker.GetStartedGameReasonsAsync(candidates, profile);
                if (items == null || !items.Any()) return;

                api.MainView.UIDispatcher.Invoke(() =>
                {
                    if (generation != refreshGeneration)
                        return;

                    var reasonByName = items
                        .Where(i => !string.IsNullOrWhiteSpace(i.Name) && !string.IsNullOrWhiteSpace(i.Reason))
                        .GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.First().Reason.Trim(), StringComparer.OrdinalIgnoreCase);
                    var intentByName = items
                        .Where(i => !string.IsNullOrWhiteSpace(i.Name) && !string.IsNullOrWhiteSpace(i.Intent))
                        .GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.First().Intent.Trim(), StringComparer.OrdinalIgnoreCase);

                    foreach (var rec in ContinueList)
                    {
                        if (reasonByName.TryGetValue(rec.Game.Name, out var reason))
                        {
                            rec.Reasons.RemoveAll(IsGeneratedStartedReason);
                            rec.Reasons.Insert(0, reason);
                            rec.AiRanked = true;
                        }
                        if (intentByName.TryGetValue(rec.Game.Name, out var intent))
                        {
                            rec.StartedIntent = ParseStartedIntent(intent, rec.StartedIntent);
                            RecommendationDiagnostics.LogSuspiciousStartedIntent(logger, rec, "AI started intent");
                        }
                    }

                    ContinueList = new ObservableCollection<ScoredGame>(ContinueList.Select(EnsureContinuePlayingReason));
                });
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "AI section reasons failed");
            }
        }

        private void UpdateCollections(List<ScoredGame> top, bool playAssistantVoiceLine = false)
        {
            var scoped = SelectRecommendationCategory(top).ToList();
            var recs = scoped
                .Where(g => !g.Game.IsPlayed)
                .Select(EnsureVisibleReason)
                .Take(40)
                .ToList();
            var started = top
                .Where(g => !IsRejected(g) && !IsBlacklisted(g))
                .Select(AssignStartedIntent)
                .Where(g => g.StartedIntent != StartedGameIntent.None)
                .Select(WithStartedReason)
                .OrderByDescending(g => g.FusedScore)
                .Take(8)
                .ToList();
            lastPreferenceBlockedCount = CountBlacklisted(top);
            var recent = api.Database.Games.Where(g => !g.Hidden && g.LastActivity != null)
                .OrderByDescending(g => g.LastActivity).Take(5).ToList();
            var filterSnapshot = FilterText;
            var filtered = BuildFilteredRecommendations(recs, filterSnapshot);

            api.MainView.UIDispatcher.Invoke(() =>
            {
                Recommendations = new ObservableCollection<ScoredGame>(recs);
                ContinueList = new ObservableCollection<ScoredGame>(started);
                RecentlyPlayed = new ObservableCollection<Game>(recent);
                if (string.Equals(NormalizeFilterText(filterSnapshot), NormalizeFilterText(FilterText), StringComparison.Ordinal))
                    SetFilteredRecommendations(filtered, filterSnapshot);
                else
                    ApplyFilter();
                SetAssistantText(
                    recs.Any()
                        ? AssistantVoiceFormatter.SpotlightPick(CurrentAssistantVoice, recs.First().Game.Name)
                        : AssistantVoiceFormatter.NoCleanPicks(CurrentAssistantVoice),
                    recs.Any() ? AssistantVoiceLineKind.Success : AssistantVoiceLineKind.Error,
                    playVoiceLine: playAssistantVoiceLine);
            });
        }

        private static ScoredGame AssignStartedIntent(ScoredGame scored)
        {
            if (scored != null)
            {
                scored.StartedIntent = RecommendationHeuristics.StartedIntentFor(scored);
                RecommendationDiagnostics.LogSuspiciousStartedIntent(logger, scored, "heuristic started intent");
            }
            return scored;
        }

        private void ApplyAiRankingToLastFused(List<ScoredGame> reranked)
        {
            if (reranked == null || !reranked.Any() || !lastFused.Any()) return;

            var rankById = reranked
                .Select((g, index) => new { g.Game.PlayniteId, Rank = index })
                .GroupBy(x => x.PlayniteId)
                .ToDictionary(g => g.Key, g => g.Min(x => x.Rank));

            var originalOrder = lastFused
                .Select((g, index) => new { g.Game.PlayniteId, Index = index })
                .GroupBy(x => x.PlayniteId)
                .ToDictionary(g => g.Key, g => g.Min(x => x.Index));

            lastFused = lastFused
                .OrderBy(g => rankById.TryGetValue(g.Game.PlayniteId, out var rank) ? rank : int.MaxValue)
                .ThenBy(g => originalOrder.TryGetValue(g.Game.PlayniteId, out var index) ? index : int.MaxValue)
                .ThenByDescending(g => g.FusedScore)
                .ToList();
        }

        private IEnumerable<ScoredGame> SelectRecommendationCategory(IEnumerable<ScoredGame> source)
        {
            source = source.Where(s => !IsRejected(s));
            source = source.Where(s => !IsBlacklisted(s));
            var category = SelectedRecommendationCategory;
            if (category == "All")
                return source.Where(s => s.RecommendationCategory != "Risky / Mixed");
            return source.Where(s => RecommendationHeuristics.BelongsToCategory(s, category));
        }

        private static ScoredGame WithWorthReason(ScoredGame scored)
        {
            ScrubGeneratedReasons(scored);
            scored.Reasons.RemoveAll(IsGeneratedWorthReason);
            EnsureVisibleReason(scored);
            return scored;
        }

        private static ScoredGame WithStartedReason(ScoredGame scored)
        {
            RemoveGeneratedStartedReasons(scored);
            EnsureContinuePlayingReason(scored);
            return scored;
        }

        private static ScoredGame EnsureVisibleReason(ScoredGame scored)
        {
            if (scored == null) return scored;
            if (scored.Reasons == null)
                scored.Reasons = new List<string>();
            ScrubGeneratedReasons(scored);
            scored.Reasons = scored.Reasons
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (!scored.Reasons.Any())
                scored.Reasons.Add(BuildFallbackRecommendationReason(scored));
            scored.AiRanked = scored.AiRanked && !string.IsNullOrWhiteSpace(scored.PrimaryReason);
            return scored;
        }

        private static ScoredGame EnsureContinuePlayingReason(ScoredGame scored)
        {
            if (scored == null) return scored;
            if (scored.Reasons == null)
                scored.Reasons = new List<string>();
            scored.Reasons = scored.Reasons
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Select(r => r.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            scored.AiRanked = scored.AiRanked && !string.IsNullOrWhiteSpace(scored.PrimaryReason);
            return scored;
        }

        private static ScoredGame WithRevisitReason(ScoredGame scored)
        {
            ScrubGeneratedReasons(scored);
            var niche = PickRevisitLabel(scored.Game);
            scored.PrimaryTag = RecommendationDiagnostics.IsUsefulPrimaryLabel(niche)
                ? niche
                : "Recommended";
            scored.Reasons.RemoveAll(IsGeneratedRevisitReason);
            EnsureVisibleReason(scored);
            return scored;
        }

        private static StartedGameIntent ParseStartedIntent(string intent, StartedGameIntent fallback)
        {
            if (string.IsNullOrWhiteSpace(intent)) return fallback;
            var normalized = Regex.Replace(intent, @"[^a-zA-Z]", string.Empty);
            if (Enum.TryParse(normalized, true, out StartedGameIntent parsed))
                return parsed;
            if (intent.IndexOf("finish", StringComparison.OrdinalIgnoreCase) >= 0) return StartedGameIntent.Finishable;
            if (intent.IndexOf("co-op", StringComparison.OrdinalIgnoreCase) >= 0 ||
                intent.IndexOf("multiplayer", StringComparison.OrdinalIgnoreCase) >= 0) return StartedGameIntent.CoopMultiplayer;
            if (intent.IndexOf("progress", StringComparison.OrdinalIgnoreCase) >= 0) return StartedGameIntent.LongTermProgression;
            if (intent.IndexOf("sim", StringComparison.OrdinalIgnoreCase) >= 0 ||
                intent.IndexOf("sandbox", StringComparison.OrdinalIgnoreCase) >= 0) return StartedGameIntent.SandboxSim;
            if (intent.IndexOf("session", StringComparison.OrdinalIgnoreCase) >= 0) return StartedGameIntent.SessionGame;
            return fallback;
        }

        private static void ScrubGeneratedReasons(ScoredGame scored)
        {
            if (scored?.Reasons == null) return;
            scored.Reasons.RemoveAll(r =>
                string.IsNullOrWhiteSpace(r) ||
                IsOldMatchSignalReason(r));
        }

        private static void RemoveGeneratedStartedReasons(ScoredGame scored)
        {
            if (scored?.Reasons == null) return;
            scored.Reasons.RemoveAll(r =>
                string.IsNullOrWhiteSpace(r) ||
                IsGeneratedStartedReason(r));
        }

        private static bool IsOldMatchSignalReason(string reason)
        {
            var oldPhrase = "match " + "signal";
            return !string.IsNullOrWhiteSpace(reason) &&
                   reason.IndexOf(oldPhrase, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsGeneratedRevisitReason(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) return false;
            return reason.StartsWith("Return when you want", StringComparison.OrdinalIgnoreCase) ||
                   reason.StartsWith("Best for", StringComparison.OrdinalIgnoreCase) ||
                   reason.StartsWith("Worth revisiting", StringComparison.OrdinalIgnoreCase) ||
                   reason.StartsWith("Good for quick sessions", StringComparison.OrdinalIgnoreCase) ||
                   reason.StartsWith("Revisit for", StringComparison.OrdinalIgnoreCase) ||
                   reason.StartsWith("Good for another session", StringComparison.OrdinalIgnoreCase) ||
                   IsOldMatchSignalReason(reason) ||
                   reason.StartsWith("Endless-style", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsGeneratedStartedReason(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) return false;
            return IsGeneratedRevisitReason(reason) ||
                   IsGeneratedWorthReason(reason) ||
                   reason.StartsWith("Pick this back up", StringComparison.OrdinalIgnoreCase) ||
                   reason.StartsWith("Continue this", StringComparison.OrdinalIgnoreCase) ||
                   reason.StartsWith("Good when you want", StringComparison.OrdinalIgnoreCase) ||
                   reason.StartsWith("Best treated", StringComparison.OrdinalIgnoreCase) ||
                   reason.StartsWith("Return when you want", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsGeneratedWorthReason(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) return false;
            var oldFinishablePhrase = "Started and appears " + "finishable";
            return reason.StartsWith(oldFinishablePhrase, StringComparison.OrdinalIgnoreCase) ||
                   reason.StartsWith("Continue this when you want", StringComparison.OrdinalIgnoreCase) ||
                   reason.StartsWith("Worth finishing", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildFallbackRecommendationReason(ScoredGame scored)
        {
            var tag = scored?.PrimaryTag;
            if (!string.IsNullOrWhiteSpace(tag) && !string.Equals(tag, "Recommended", StringComparison.OrdinalIgnoreCase))
                return $"Owned and unplayed; {tag} is the clearest local match.";
            if (!string.IsNullOrWhiteSpace(scored?.RecommendationCategory) &&
                !string.Equals(scored.RecommendationCategory, "Best Matches", StringComparison.OrdinalIgnoreCase))
                return $"Owned and unplayed {scored.RecommendationCategory.ToLowerInvariant()} pick.";
            return "Owned and unplayed recommendation.";
        }

        private static string PickRevisitLabel(EnrichedGame game)
        {
            if (game == null) return "ongoing play";

            var broad = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Action", "Adventure", "Arcade", "Casual", "Indie", "RPG", "Role-playing",
                "Shooter", "Simulation", "Sports", "Strategy", "Platform", "Fighting",
                "Multiplayer", "Single-player", "Co-op"
            };

            return game.AlgorithmicTags
                .Concat(game.Keywords)
                .Concat(game.Tags)
                .Concat(game.Themes)
                .Concat(game.Genres)
                .FirstOrDefault(v => RecommendationDiagnostics.IsUsefulPrimaryLabel(v) && !broad.Contains(v))
                ?? "ongoing play";
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

        private static bool ContainsAny(string text, params string[] needles)
            => needles.Any(n => text.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0);

        private void RunGenreRecommendation()
        {
            if (string.IsNullOrWhiteSpace(GenreText))
            {
                ClearGenreRecommendation();
                return;
            }

            if (!lastEnrichedGames.Any())
            {
                IsGenreMode = true;
                _ = RefreshAsync(RefreshRequestKind.ManualRescore, reason: "genre search requires profile");
                return;
            }

            ApplyGenreRecommendation();
            StatusText = FilteredRecommendations.Any()
                ? $"Found {FilteredRecommendations.Count} owned recommendations for {GenreText.Trim()}"
                : $"No owned unplayed matches for {GenreText.Trim()}";
        }

        private void ApplyGenreRecommendation()
        {
            var query = GenreText.Trim();
            IsGenreMode = true;
            NotifyRecommendationStateChanged();
            GenreModeTitle = $"OWNED {query.ToUpperInvariant()} PICKS";

            var scored = lastEnrichedGames
                .Where(g => !g.IsPlayed && !IsRejected(g) && !IsBlacklisted(g))
                .Select(g => ScoreForGenre(g, query))
                .Where(s => s.FusedScore > 0)
                .OrderByDescending(s => s.FusedScore)
                .Take(40)
                .ToList();
            lastPreferenceBlockedCount = lastEnrichedGames.Count(g => !g.IsPlayed && IsBlacklisted(g));
            var filterSnapshot = FilterText;
            var filtered = BuildFilteredRecommendations(scored, filterSnapshot);

            api.MainView.UIDispatcher.Invoke(() =>
            {
                Recommendations = new ObservableCollection<ScoredGame>(scored);
                ContinueList = new ObservableCollection<ScoredGame>();
                if (string.Equals(NormalizeFilterText(filterSnapshot), NormalizeFilterText(FilterText), StringComparison.Ordinal))
                    SetFilteredRecommendations(filtered, filterSnapshot);
                else
                    ApplyFilter();
            });
        }

        private void ClearGenreRecommendation()
        {
            IsGenreMode = false;
            GenreModeTitle = "RECOMMENDED FOR YOU";
            GenreText = string.Empty;
            NotifyRecommendationStateChanged();
            UpdateCollections(lastFused);
            StatusText = "Genre mode cleared";
        }

        private ScoredGame ScoreForGenre(EnrichedGame game, string query)
        {
            var q = query.ToLowerInvariant();
            double score = 0;
            var reasons = new List<string>();

            AddMetadataScore(game.Genres, q, 1.4, "genre", ref score, reasons);
            AddMetadataScore(game.Tags, q, 1.1, "tag", ref score, reasons);
            AddMetadataScore(game.Keywords, q, 1.25, "keyword", ref score, reasons);
            AddMetadataScore(game.Themes, q, 1.0, "theme", ref score, reasons);
            AddMetadataScore(game.Features, q, 0.8, "feature", ref score, reasons);
            if (!string.IsNullOrWhiteSpace(game.Description) &&
                game.Description.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                score += 0.35;
                reasons.Add("Description matches your genre search");
            }
            if (game.CommunityScore.HasValue) score += game.CommunityScore.Value / 100.0;
            if (game.CriticScore.HasValue) score += game.CriticScore.Value / 100.0;

            var matchedTag = PickMatchedTag(game, query);
            var primaryTag = RecommendationDiagnostics.IsUsefulPrimaryLabel(matchedTag)
                ? matchedTag
                : "Genre match";
            var scored = new ScoredGame
            {
                Game = game,
                FusedScore = score,
                WeightedScore = score,
                Reasons = reasons.Distinct().DefaultIfEmpty("Owned and unplayed genre match").Take(3).ToList(),
                PrimaryTag = primaryTag,
                QualityLabel = RecommendationHeuristics.QualityLabel(game)
            };
            scored.RecommendationCategory = RecommendationHeuristics.CategoryFor(scored, lastTasteProfile);
            if (scored.RecommendationCategory == "Best Matches" && IsNicheGenreSearchMatch(game, matchedTag))
                scored.RecommendationCategory = "Fresh Finds";
            return scored;
        }

        private static void AddMetadataScore(IEnumerable<string> values, string query, double weight, string label, ref double score, List<string> reasons)
        {
            foreach (var value in values ?? Enumerable.Empty<string>())
            {
                if (value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    query.IndexOf(value.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    score += weight;
                    reasons.Add($"Matches {label}: {value}");
                }
            }
        }

        private static string PickMatchedTag(EnrichedGame game, string query)
        {
            return game.AlgorithmicTags.Concat(game.Keywords).Concat(game.Tags).Concat(game.Genres).Concat(game.Themes)
                .FirstOrDefault(v => RecommendationDiagnostics.IsUsefulPrimaryLabel(v) &&
                                     v.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool IsNicheGenreSearchMatch(EnrichedGame game, string matchedTag)
        {
            if (game == null || string.IsNullOrWhiteSpace(matchedTag)) return false;
            return game.AlgorithmicTags.Contains(matchedTag, StringComparer.OrdinalIgnoreCase) ||
                   game.Keywords.Contains(matchedTag, StringComparer.OrdinalIgnoreCase) ||
                   game.Themes.Contains(matchedTag, StringComparer.OrdinalIgnoreCase);
        }

        private async Task FindNotOwnedAsync()
        {
            if (IsDealsLoading || IsLoading) return;
            IsDealsLoading = true;
            try
            {
                if (!lastEnrichedGames.Any())
                    await RefreshAsync(RefreshRequestKind.Background, reason: "not-owned search requires profile");

                StatusText = "Finding games outside your library...";
                var notOwned = await plugin.Deals.GetNotOwnedRecommendationsAsync(lastEnrichedGames, lastTasteProfile);
                notOwned = await RunExternalAiRerankerAsync(notOwned, "not-owned");
                notOwnedSearchCompleted = true;
                NotOwnedRecommendations = new ObservableCollection<ExternalRecommendation>(notOwned);
                StatusText = !string.IsNullOrWhiteSpace(plugin.Deals.LastDiagnosticsSummary)
                    ? plugin.Deals.LastDiagnosticsSummary
                    : notOwned.Any()
                        ? $"Found {notOwned.Count} games outside your library"
                        : "No matching games outside your library found";
                SetAssistantText(
                    notOwned.Any()
                        ? AssistantVoiceFormatter.DealsFound(CurrentAssistantVoice, notOwned.Count)
                        : AssistantVoiceFormatter.DealsNone(CurrentAssistantVoice),
                    notOwned.Any() ? AssistantVoiceLineKind.Deals : AssistantVoiceLineKind.Error);
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Not-owned recommendations failed");
                StatusText = "Not-owned search failed: " + ex.Message;
                soundEffects.Play(SoundEffectKind.Error);
            }
            finally
            {
                IsDealsLoading = false;
            }
        }

        private async Task FindDealsAsync()
        {
            if (IsDealsLoading || IsLoading) return;
            IsDealsLoading = true;
            try
            {
                if (!lastEnrichedGames.Any())
                    await RefreshAsync(RefreshRequestKind.Background, reason: "deals search requires profile");

                StatusText = "Finding current deals...";
                var deals = plugin.Deals.IsConfigured
                    ? await plugin.Deals.GetRecommendationsAsync(lastEnrichedGames, lastTasteProfile)
                    : throw new InvalidOperationException("Configure at least one deal source in settings first.");
                deals = await RunExternalAiRerankerAsync(deals, "deals");
                dealsSearchCompleted = true;
                ExternalRecommendations = new ObservableCollection<ExternalRecommendation>(deals);
                StatusText = !string.IsNullOrWhiteSpace(plugin.Deals.LastDiagnosticsSummary)
                    ? plugin.Deals.LastDiagnosticsSummary
                    : deals.Any()
                        ? $"Found {deals.Count} relevant current deals"
                        : "No matching current deals found";
                if (deals.Any())
                    soundEffects.Play(SoundEffectKind.DealFound);
                SetAssistantText(
                    deals.Any()
                        ? AssistantVoiceFormatter.DealsFound(CurrentAssistantVoice, deals.Count)
                        : AssistantVoiceFormatter.DealsNone(CurrentAssistantVoice),
                    deals.Any() ? AssistantVoiceLineKind.Deals : AssistantVoiceLineKind.Error);
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Deal recommendations failed");
                StatusText = "Deals failed: " + ex.Message;
                soundEffects.Play(SoundEffectKind.Error);
            }
            finally
            {
                IsDealsLoading = false;
            }
        }

        private async Task<List<ExternalRecommendation>> RunExternalAiRerankerAsync(
            List<ExternalRecommendation> candidates,
            string contextLabel)
        {
            if (candidates == null || !candidates.Any() || plugin?.AiReranker?.IsEnabled != true)
                return candidates ?? new List<ExternalRecommendation>();

            try
            {
                StatusText = $"{plugin.AiReranker.ProviderName} is re-ranking {contextLabel}...";
                var reranked = await plugin.AiReranker.ReRankExternalAsync(
                    candidates,
                    lastTasteProfile,
                    lastEnrichedGames,
                    contextLabel);
                var safe = plugin.Deals.ApplyPostAiSafety(reranked ?? candidates, lastEnrichedGames, out var removed);
                var aiReturned = reranked?.Count ?? candidates.Count;
                var aiOmitted = Math.Max(0, candidates.Count - aiReturned);
                logger.Info($"External AI diagnostics ({contextLabel}): input={candidates.Count}, aiReturned={aiReturned}, aiOmitted={aiOmitted}, postAiRemoved={removed}, final={safe.Count}");
                StatusText = $"{plugin.AiReranker.ProviderName} re-ranking applied to {contextLabel}";
                return safe;
            }
            catch (Exception ex)
            {
                logger.Warn(ex, $"External AI reranking failed for {contextLabel}");
                return candidates;
            }
        }

        private void ShowDetails(ScoredGame rec)
        {
            if (rec == null) return;
            RecommendationDiagnostics.LogSuspiciousPrimaryTag(logger, rec.Game, rec.PrimaryTag);
            RecommendationDiagnostics.LogSuspiciousStartedIntent(logger, rec, "info dialog metadata");
            RecommendationDiagnostics.LogWeakRecommendationReasons(logger, rec, "info dialog");
            soundEffects.Play(SoundEffectKind.InfoOpen);
            var window = new GameInfoWindow(
                rec,
                () => plugin.Enrichment.EnrichMissingInfoAsync(rec.Game),
                LaunchRecommendation,
                r => GoToLibrary(r, openDetailsOnFailure: false),
                TryRejectRecommendation);
            if (Application.Current?.MainWindow != null && Application.Current.MainWindow != window)
                window.Owner = Application.Current.MainWindow;
            window.ShowDialog();
        }

        private void RejectRecommendation(ScoredGame rec)
            => TryRejectRecommendation(rec);

        private bool TryRejectRecommendation(ScoredGame rec)
        {
            if (rec?.Game == null) return false;

            var prompt = ShowRejectPrompt(rec);
            if (prompt == null) return false;

            plugin.SettingsVm.RejectGame(rec, prompt.ReasonCode, prompt.ReasonText);
            RemoveRecommendationFromVisibleLists(rec);
            reviewCompletedIds.Add(rec.Game.PlayniteId);
            StatusText = $"Rejected {rec.Game.Name}; it will stay hidden from recommendations.";
            soundEffects.Play(SoundEffectKind.Reject);
            SetAssistantText(AssistantVoiceFormatter.RecommendationRejected(CurrentAssistantVoice), AssistantVoiceLineKind.Reject);
            SyncReviewRecommendation();
            return true;
        }

        private void MoveReviewPrevious()
        {
            var queue = BuildReviewQueue().ToList();
            if (!queue.Any())
            {
                SyncReviewRecommendation();
                return;
            }

            reviewIndex = reviewIndex <= 0 ? queue.Count - 1 : reviewIndex - 1;
            SetCurrentReviewRecommendation(queue);
        }

        private void MoveReviewNext()
        {
            var queue = BuildReviewQueue().ToList();
            if (!queue.Any())
            {
                SyncReviewRecommendation();
                return;
            }

            reviewIndex = (reviewIndex + 1) % queue.Count;
            SetCurrentReviewRecommendation(queue);
        }

        private void RecordReviewFeedback(string action, string statusAction)
        {
            var rec = CurrentReviewRecommendation;
            if (rec?.Game == null)
                return;

            plugin.SettingsVm.RecordRecommendationFeedback(rec, action);
            reviewCompletedIds.Add(rec.Game.PlayniteId);
            StatusText = $"{statusAction}: {rec.Game.Name}.";
            SyncReviewRecommendation();
        }

        private void RejectCurrentReviewRecommendation()
        {
            var rec = CurrentReviewRecommendation;
            if (TryRejectRecommendation(rec))
                SyncReviewRecommendation();
        }

        private IEnumerable<ScoredGame> BuildReviewQueue()
        {
            return (FilteredRecommendations ?? new ObservableCollection<ScoredGame>())
                .Where(r => r?.Game != null)
                .Where(r => !reviewCompletedIds.Contains(r.Game.PlayniteId))
                .Where(r => !IsRejected(r) && !IsBlacklisted(r));
        }

        private void SyncReviewRecommendation(bool resetPosition = false)
        {
            if (!IsReviewModeEnabled)
            {
                CurrentReviewRecommendation = null;
                ReviewQueueSummary = "Review mode is off";
                return;
            }

            var queue = BuildReviewQueue().ToList();
            if (!queue.Any() && reviewCompletedIds.Any() && FilteredRecommendations.Any())
            {
                reviewCompletedIds.Clear();
                queue = BuildReviewQueue().ToList();
            }

            if (!queue.Any())
            {
                reviewIndex = 0;
                CurrentReviewRecommendation = null;
                ReviewQueueSummary = "No owned recommendations to review";
                return;
            }

            if (resetPosition)
                reviewIndex = 0;
            else if (CurrentReviewRecommendation?.Game != null)
            {
                var currentId = CurrentReviewRecommendation.Game.PlayniteId;
                var currentIndex = queue.FindIndex(r => r.Game.PlayniteId == currentId);
                if (currentIndex >= 0)
                    reviewIndex = currentIndex;
            }

            reviewIndex = Math.Max(0, Math.Min(reviewIndex, queue.Count - 1));
            SetCurrentReviewRecommendation(queue);
        }

        private void SetCurrentReviewRecommendation(IReadOnlyList<ScoredGame> queue)
        {
            if (queue == null || queue.Count == 0)
            {
                CurrentReviewRecommendation = null;
                ReviewQueueSummary = "No owned recommendations to review";
                return;
            }

            reviewIndex = Math.Max(0, Math.Min(reviewIndex, queue.Count - 1));
            CurrentReviewRecommendation = queue[reviewIndex];
            ReviewQueueSummary = $"Reviewing {reviewIndex + 1} of {queue.Count}";
        }

        private void RemoveRecommendationFromVisibleLists(ScoredGame rec)
        {
            if (rec?.Game == null) return;
            var id = rec.Game.PlayniteId;
            Recommendations = new ObservableCollection<ScoredGame>(
                Recommendations.Where(r => r?.Game == null || r.Game.PlayniteId != id));
            FilteredRecommendations = new ObservableCollection<ScoredGame>(
                FilteredRecommendations.Where(r => r?.Game == null || r.Game.PlayniteId != id));
            UpdateFilterSummary();
        }

        private static RejectPromptResult ShowRejectPrompt(ScoredGame rec)
        {
            var reasonBox = new ComboBox
            {
                MinWidth = 260,
                Margin = new Thickness(0, 4, 0, 8),
                SelectedIndex = 0
            };

            foreach (var reason in RejectionReasonChoices)
                reasonBox.Items.Add(reason.Label);

            var notesBox = new TextBox
            {
                MinWidth = 260,
                Margin = new Thickness(0, 4, 0, 12)
            };

            var result = new RejectPromptResult();
            var window = new Window
            {
                Title = "Reject recommendation",
                Width = 360,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new StackPanel
                {
                    Margin = new Thickness(12),
                    Children =
                    {
                        new TextBlock
                        {
                            Text = $"Reject {rec.Game.Name}?",
                            FontWeight = FontWeights.SemiBold,
                            TextWrapping = TextWrapping.Wrap
                        },
                        new TextBlock
                        {
                            Text = "Reason",
                            Margin = new Thickness(0, 10, 0, 0)
                        },
                        reasonBox,
                        new TextBlock { Text = "Notes (optional)" },
                        notesBox
                    }
                }
            };

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var rejectButton = new Button
            {
                Content = "Reject",
                IsDefault = true,
                MinWidth = 72,
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(0, 0, 8, 0)
            };
            var cancelButton = new Button
            {
                Content = "Cancel",
                IsCancel = true,
                MinWidth = 72,
                Padding = new Thickness(8, 3, 8, 3)
            };

            rejectButton.Click += (s, e) =>
            {
                var selected = RejectionReasonChoices[Math.Max(0, reasonBox.SelectedIndex)];
                result.ReasonCode = selected.Code;
                result.ReasonText = notesBox.Text?.Trim() ?? string.Empty;
                window.DialogResult = true;
            };

            buttons.Children.Add(rejectButton);
            buttons.Children.Add(cancelButton);
            ((StackPanel)window.Content).Children.Add(buttons);

            if (Application.Current?.MainWindow != null && Application.Current.MainWindow != window)
                window.Owner = Application.Current.MainWindow;

            return window.ShowDialog() == true ? result : null;
        }

        private async Task RefreshSpotlightAsync(int generation, bool manualRequest = false)
        {
            if (IsSpotlightLoading || IsLoading) return;
            activeSpotlightGeneration = generation;
            IsSpotlightLoading = true;
            try
            {
                if (!lastEnrichedGames.Any()) return;
                if (manualRequest || generation == refreshGeneration)
                    StatusText = "Checking owned Steam game updates...";
                var news = await plugin.News.GetSpotlightsAsync(lastEnrichedGames, lastTasteProfile);
                api.MainView.UIDispatcher.Invoke(() =>
                {
                    if (!manualRequest && generation != refreshGeneration)
                        return;
                    Spotlights = new ObservableCollection<SpotlightItem>(news);
                });
                if (!manualRequest && generation != refreshGeneration)
                    return;
                if (news.Any())
                {
                    var top = news.First();
                    SetAssistantText(
                        AssistantVoiceFormatter.NewsItem(CurrentAssistantVoice, top.Source, top.PublishedText, top.Title),
                        AssistantVoiceLineKind.Success,
                        playVoiceLine: manualRequest);
                    StatusText = $"Loaded {news.Count} current spotlight items";
                }
                else
                {
                    SetAssistantText(AssistantVoiceFormatter.NewsNone(CurrentAssistantVoice), AssistantVoiceLineKind.Error, playVoiceLine: manualRequest);
                    StatusText = "No recent owned Steam updates found";
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Spotlight refresh failed");
                StatusText = "Spotlight failed: " + ex.Message;
                if (manualRequest)
                    soundEffects.Play(SoundEffectKind.Error);
                SetAssistantText(AssistantVoiceFormatter.NewsFailure(CurrentAssistantVoice), AssistantVoiceLineKind.Error, playVoiceLine: manualRequest);
            }
            finally
            {
                if (activeSpotlightGeneration == generation)
                {
                    IsSpotlightLoading = false;
                    activeSpotlightGeneration = -1;
                }
            }
        }

        private string CurrentAssistantVoice => plugin.SettingsVm.GetSettings().AssistantVoice;

        private void SetAssistantText(string text, AssistantVoiceLineKind? voiceLine = null, bool playVoiceLine = true)
        {
            AssistantText = text;
            if (voiceLine.HasValue && playVoiceLine)
                soundEffects.PlayAssistantVoiceLine(
                    voiceLine.Value,
                    CurrentAssistantVoice,
                    plugin.SettingsVm.GetSettings().AnimeAssistantEnabled);
        }

        private void ReportSoundStatus(string message)
        {
            if (!string.IsNullOrWhiteSpace(message))
                StatusText = message;
        }

        private void AdvanceAssistantFrame()
        {
            if (!AssistantVisible) return;
            assistantFrame = (assistantFrame + 1) % 4;
            AssistantFrameSource = $"pack://application:,,,/GameRecommender;component/Assets/assistant_frames/assistant-{assistantFrame}.png";
        }

        private void ReactToCategory(string category)
        {
            if (!AssistantVisible) return;
            assistantFrame = (assistantFrame + 1) % 4;
            AssistantFrameSource = $"pack://application:,,,/GameRecommender;component/Assets/assistant_frames/assistant-{assistantFrame}.png";
            SetAssistantText(AssistantVoiceFormatter.Category(CurrentAssistantVoice, category), AssistantVoiceLineKind.Category);
        }

        private static void OpenSpotlight(SpotlightItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Url)) return;
            UrlOpener.OpenHttpUrl(item.Url);
        }

        private static void OpenDeal(ExternalRecommendation deal)
        {
            if (deal == null || string.IsNullOrWhiteSpace(deal.Url)) return;
            UrlOpener.OpenHttpUrl(deal.Url);
        }

        private void LaunchRecent(Game game)
        {
            if (game == null) return;
            soundEffects.Play(SoundEffectKind.Launch);
            api.StartGame(game.Id);
        }

        private void LaunchRecommendation(ScoredGame rec)
        {
            if (rec?.Game == null) return;
            StatusText = $"Launching {rec.Game.Name}...";
            soundEffects.Play(SoundEffectKind.Launch);
            api.StartGame(rec.Game.PlayniteId);
        }

        private void GoToLibrary(ScoredGame rec, bool openDetailsOnFailure)
        {
            if (rec?.Game == null) return;
            try
            {
                api.MainView.SwitchToLibraryView();
                api.MainView.SelectGame(rec.Game.PlayniteId);
                StatusText = $"Selected {rec.Game.Name} in your Playnite library.";
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Failed to select recommended game in Playnite library");
                StatusText = $"Could not jump to {rec.Game.Name} in the library.";
                if (openDetailsOnFailure)
                    ShowDetails(rec);
            }
        }

        private void ExportToClipboard()
        {
            try
            {
                var allRecs = Recommendations.Concat(ContinueList).ToList();
                var text = ExportService.GenerateAiBlock(lastEnrichedGames, allRecs, lastTasteProfile);
                Clipboard.SetText(text);
                StatusText = "Copied to clipboard; paste into any AI chat";
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Export failed");
                StatusText = "Export failed: " + ex.Message;
            }
        }

        private void ApplyFilter()
        {
            var normalizedFilter = NormalizeFilterText(filterText);
            if (filteredRecommendationsVersion == recommendationsVersion &&
                string.Equals(filteredRecommendationsText, normalizedFilter, StringComparison.Ordinal))
            {
                UpdateFilterSummary();
                return;
            }

            SetFilteredRecommendations(BuildFilteredRecommendations(Recommendations, filterText), filterText);
        }

        private bool IsRejected(ScoredGame scored)
            => IsRejected(scored?.Game);

        private bool IsRejected(EnrichedGame game)
            => game != null && plugin.SettingsVm.IsRejected(game);

        private bool IsBlacklisted(ScoredGame scored)
            => IsBlacklisted(scored?.Game);

        private bool IsBlacklisted(EnrichedGame game)
            => game != null && plugin.SettingsVm.IsBlacklisted(game);

        private List<ScoredGame> BuildFilteredRecommendations(IEnumerable<ScoredGame> source, string filter)
        {
            var normalizedFilter = NormalizeFilterText(filter);
            var visible = (source ?? Enumerable.Empty<ScoredGame>())
                .Where(r => r != null && !IsRejected(r) && !IsBlacklisted(r));

            if (string.IsNullOrWhiteSpace(normalizedFilter))
                return visible.ToList();

            return visible.Where(r => MatchesFilter(r, normalizedFilter)).ToList();
        }

        private static bool MatchesFilter(ScoredGame rec, string filter)
        {
            if (rec?.Game == null) return false;
            return ContainsFilter(rec.Game.Name, filter) ||
                   ContainsFilter(rec.PrimaryTag, filter) ||
                   AnyContainsFilter(rec.Game.Genres, filter) ||
                   AnyContainsFilter(rec.Game.Tags, filter) ||
                   AnyContainsFilter(rec.Game.Themes, filter) ||
                   AnyContainsFilter(rec.Game.Keywords, filter) ||
                   AnyContainsFilter(rec.Game.AlgorithmicTags, filter) ||
                   ContainsFilter(rec.RecommendationCategory, filter) ||
                   AnyContainsFilter(rec.Reasons, filter);
        }

        private void SetFilteredRecommendations(IEnumerable<ScoredGame> filtered, string filter)
        {
            FilteredRecommendations = new ObservableCollection<ScoredGame>(filtered ?? Enumerable.Empty<ScoredGame>());
            filteredRecommendationsVersion = recommendationsVersion;
            filteredRecommendationsText = NormalizeFilterText(filter);
            UpdateFilterSummary();
        }

        private void InvalidateFilterCache()
        {
            filteredRecommendationsVersion = -1;
            filteredRecommendationsText = null;
        }

        private void UpdateBusyState()
            => IsBusy = IsLoading || IsAiLoading || IsDealsLoading || IsSpotlightLoading;

        private void NotifyRecommendationStateChanged()
        {
            OnPropertyChanged(nameof(ShowRecommendationsEmptyState));
            OnPropertyChanged(nameof(RecommendationsEmptyStateText));
        }

        private void NotifyReviewStateChanged()
        {
            OnPropertyChanged(nameof(ShowReviewPanel));
            OnPropertyChanged(nameof(ShowReviewEmptyState));
        }

        private void NotifyDealsStateChanged()
        {
            OnPropertyChanged(nameof(ShowDealsEmptyState));
            OnPropertyChanged(nameof(ShowNotOwnedSection));
            OnPropertyChanged(nameof(ShowNotOwnedEmptyState));
        }

        private static string NormalizeFilterText(string value)
            => (value ?? string.Empty).Trim().ToLowerInvariant();

        private static bool ContainsFilter(string value, string filter)
            => !string.IsNullOrWhiteSpace(value) &&
               value.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool AnyContainsFilter(IEnumerable<string> values, string filter)
            => (values ?? Enumerable.Empty<string>()).Any(v => ContainsFilter(v, filter));

        private class RejectPromptResult
        {
            public string ReasonCode { get; set; } = string.Empty;
            public string ReasonText { get; set; } = string.Empty;
        }

        private class RejectionReasonChoice
        {
            public RejectionReasonChoice(string code, string label)
            {
                Code = code;
                Label = label;
            }

            public string Code { get; }
            public string Label { get; }
        }

        private void UpdateFilterSummary()
        {
            var pieces = new List<string>();
            pieces.Add(IsGenreMode && !string.IsNullOrWhiteSpace(GenreText)
                ? $"Genre mode: {GenreText.Trim()}"
                : $"Category: {SelectedRecommendationCategory}");
            if (!string.IsNullOrWhiteSpace(FilterText))
                pieces.Add($"Search: {FilterText.Trim()}");
            pieces.Add($"Showing {FilteredRecommendations.Count} of {Recommendations.Count}");
            if (lastPreferenceBlockedCount > 0)
                pieces.Add($"{lastPreferenceBlockedCount} hidden by preferences");
            FilterSummary = string.Join(" · ", pieces);
        }

        private int CountBlacklisted(IEnumerable<ScoredGame> source)
            => (source ?? Enumerable.Empty<ScoredGame>()).Count(IsBlacklisted);

        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
