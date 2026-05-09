using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Media;
using Playnite.SDK;

namespace GameRecommender
{
    internal enum SoundEffectKind
    {
        RefreshComplete,
        Error,
        InfoOpen,
        Launch,
        Reject,
        DealFound
    }

    internal enum AssistantVoiceLineKind
    {
        Ready,
        Working,
        Success,
        Error,
        Reject,
        Deals,
        Category
    }

    internal class SoundEffectService
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly TimeSpan RoutineAssistantVoiceLineCooldown = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan ErrorAssistantVoiceLineCooldown = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan ActionAssistantVoiceLineCooldown = TimeSpan.FromSeconds(2);
        private readonly Func<RecommenderSettings> settingsProvider;
        private readonly Action<string> statusReporter;
        private readonly Dictionary<string, MediaPlayer> players = new Dictionary<string, MediaPlayer>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<AssistantVoiceLineKind, DateTime> lastAssistantVoiceLineUtc =
            new Dictionary<AssistantVoiceLineKind, DateTime>();

        public SoundEffectService(Func<RecommenderSettings> settingsProvider, Action<string> statusReporter)
        {
            this.settingsProvider = settingsProvider;
            this.statusReporter = statusReporter;
        }

        public void Play(SoundEffectKind kind)
        {
            var settings = settingsProvider?.Invoke();
            if (settings?.SoundEffectsEnabled != true)
                return;

            try
            {
                var player = GetPlayer(kind);
                player.Volume = ClampVolume(settings.SoundEffectsVolume);
                player.Stop();
                player.Position = TimeSpan.Zero;
                player.Play();
            }
            catch (Exception ex)
            {
                logger.Warn(ex, $"Sound effect failed: {kind}");
                statusReporter?.Invoke($"Sound effect failed: {kind}");
            }
        }

        public void PlayAssistantVoiceLine(AssistantVoiceLineKind kind, string voice, bool assistantEnabled)
        {
            var settings = settingsProvider?.Invoke();
            if (settings?.SoundEffectsEnabled != true || !assistantEnabled || !IsWaifuVoice(voice))
                return;

            try
            {
                if (IsAssistantVoiceLineOnCooldown(kind))
                    return;

                var player = GetOptionalPlayer($"assistant:{kind}", FileNamesForAssistantLine(kind));
                if (player == null)
                    return;

                player.Volume = ClampVolume(settings.SoundEffectsVolume);
                player.Stop();
                player.Position = TimeSpan.Zero;
                player.Play();
                lastAssistantVoiceLineUtc[kind] = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                logger.Warn(ex, $"Assistant voice line failed: {kind}");
            }
        }

        private MediaPlayer GetPlayer(SoundEffectKind kind)
        {
            return GetRequiredPlayer($"effect:{kind}", FileNameFor(kind), $"Sound effect playback failed: {kind}");
        }

        private MediaPlayer GetRequiredPlayer(string cacheKey, string fileName, string failureMessage)
        {
            if (players.TryGetValue(cacheKey, out var existing))
                return existing;

            var path = ResolveSoundPath(fileName, out var checkedPaths);

            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"Sound asset not found: {fileName}. Checked: {string.Join("; ", checkedPaths)}",
                    path ?? fileName);

            var player = new MediaPlayer();
            player.MediaFailed += (s, e) =>
            {
                logger.Warn(e.ErrorException, failureMessage);
                statusReporter?.Invoke(failureMessage);
            };
            player.Open(new Uri(path, UriKind.Absolute));
            players[cacheKey] = player;
            return player;
        }

        private MediaPlayer GetOptionalPlayer(string cacheKey, IEnumerable<string> fileNames)
        {
            if (players.TryGetValue(cacheKey, out var existing))
                return existing;

            var path = ResolveFirstSoundPath(fileNames, out var resolvedFileName);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return null;

            var player = new MediaPlayer();
            player.MediaFailed += (s, e) =>
                logger.Warn(e.ErrorException, $"Assistant voice line playback failed: {resolvedFileName}");
            player.Open(new Uri(path, UriKind.Absolute));
            players[cacheKey] = player;
            return player;
        }

        private static string ResolveFirstSoundPath(IEnumerable<string> fileNames, out string resolvedFileName)
        {
            resolvedFileName = null;
            foreach (var fileName in fileNames ?? Enumerable.Empty<string>())
            {
                var path = ResolveSoundPath(fileName, out _);
                if (File.Exists(path))
                {
                    resolvedFileName = fileName;
                    return path;
                }
            }

            return null;
        }

        private static string ResolveSoundPath(string fileName, out List<string> checkedPaths)
        {
            checkedPaths = new List<string>();
            foreach (var root in CandidateAssetRoots())
            {
                if (string.IsNullOrWhiteSpace(root))
                    continue;

                var candidate = Path.Combine(root, "Assets", "sounds", fileName);
                checkedPaths.Add(candidate);
                if (File.Exists(candidate))
                    return candidate;
            }

            return checkedPaths.FirstOrDefault();
        }

        private static IEnumerable<string> CandidateAssetRoots()
        {
            var assemblyLocation = typeof(SoundEffectService).Assembly.Location;
            if (!string.IsNullOrWhiteSpace(assemblyLocation))
                yield return Path.GetDirectoryName(assemblyLocation);

            yield return AppDomain.CurrentDomain.BaseDirectory;
            yield return Directory.GetCurrentDirectory();
        }

        private static string FileNameFor(SoundEffectKind kind)
        {
            switch (kind)
            {
                case SoundEffectKind.RefreshComplete:
                    return "refresh_complete.wav";
                case SoundEffectKind.Error:
                    return "error.wav";
                case SoundEffectKind.InfoOpen:
                    return "info_open.wav";
                case SoundEffectKind.Launch:
                    return "launch.wav";
                case SoundEffectKind.Reject:
                    return "reject.wav";
                case SoundEffectKind.DealFound:
                    return "deal_found.wav";
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
            }
        }

        private bool IsAssistantVoiceLineOnCooldown(AssistantVoiceLineKind kind)
        {
            if (!lastAssistantVoiceLineUtc.TryGetValue(kind, out var lastPlayedUtc))
                return false;

            return DateTime.UtcNow - lastPlayedUtc < CooldownForAssistantLine(kind);
        }

        private static TimeSpan CooldownForAssistantLine(AssistantVoiceLineKind kind)
        {
            switch (kind)
            {
                case AssistantVoiceLineKind.Error:
                    return ErrorAssistantVoiceLineCooldown;
                case AssistantVoiceLineKind.Reject:
                case AssistantVoiceLineKind.Category:
                    return ActionAssistantVoiceLineCooldown;
                default:
                    return RoutineAssistantVoiceLineCooldown;
            }
        }

        private static IEnumerable<string> FileNamesForAssistantLine(AssistantVoiceLineKind kind)
        {
            string baseName;
            switch (kind)
            {
                case AssistantVoiceLineKind.Ready:
                    baseName = "waifu_ready";
                    break;
                case AssistantVoiceLineKind.Working:
                    baseName = "waifu_working";
                    break;
                case AssistantVoiceLineKind.Success:
                    baseName = "waifu_success";
                    break;
                case AssistantVoiceLineKind.Error:
                    baseName = "waifu_error";
                    break;
                case AssistantVoiceLineKind.Reject:
                    baseName = "waifu_reject";
                    break;
                case AssistantVoiceLineKind.Deals:
                    baseName = "waifu_deals";
                    break;
                case AssistantVoiceLineKind.Category:
                    baseName = "waifu_category";
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
            }

            yield return Path.Combine("assistant", baseName + ".wav");
            yield return Path.Combine("assistant", baseName + ".mp3");

            if (kind == AssistantVoiceLineKind.Error)
            {
                yield return Path.Combine("assistant", "waifu_failure.wav");
                yield return Path.Combine("assistant", "waifu_failure.mp3");
            }
        }

        private static bool IsWaifuVoice(string voice)
        {
            return string.Equals(voice, "Waifu", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(voice, "Anime Guide", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(voice, "Playful", StringComparison.OrdinalIgnoreCase);
        }

        private static double ClampVolume(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return 0.45;
            if (value < 0)
                return 0;
            if (value > 1)
                return 1;
            return value;
        }
    }
}
