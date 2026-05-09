using System;
using System.IO;
using Playnite.SDK;
using Playnite.SDK.Data;

namespace GameRecommender
{
    /// <summary>
    /// Simple JSON key-value cache with time-to-live.
    /// Files are stored in the plugin's data folder so they survive Playnite restarts.
    /// One file per cache key — simple, debuggable, no SQLite dependency.
    /// </summary>
    public class DiskCache
    {
        private readonly string cacheDir;
        private readonly object cacheLock = new object();
        private static readonly ILogger logger = LogManager.GetLogger();

        public DiskCache(string pluginDataPath)
        {
            cacheDir = Path.Combine(pluginDataPath, "cache");
            Directory.CreateDirectory(cacheDir);
        }

        private string KeyToPath(string key)
        {
            // Sanitise key for use as filename
            var safe = key
                .Replace(":", "_")
                .Replace("/", "_")
                .Replace("\\", "_")
                .Replace("?", "_")
                .Replace("*", "_")
                .Replace("\"", "_")
                .Replace("<", "_")
                .Replace(">", "_")
                .Replace("|", "_");
            return Path.Combine(cacheDir, safe + ".json");
        }

        private class CacheEntry<T>
        {
            public DateTime ExpiresAt { get; set; }
            public T Value { get; set; }
        }

        public bool TryGet<T>(string key, out T value)
        {
            value = default;
            try
            {
                lock (cacheLock)
                {
                    var path = KeyToPath(key);
                    if (!File.Exists(path)) return false;

                    var json = File.ReadAllText(path);
                    var entry = Serialization.FromJson<CacheEntry<T>>(json);
                    if (entry == null || DateTime.UtcNow > entry.ExpiresAt)
                    {
                        TryDelete(path);
                        return false;
                    }
                    value = entry.Value;
                    return true;
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, $"Cache read failed for key: {key}");
                return false;
            }
        }

        public void Set<T>(string key, T value, TimeSpan ttl)
        {
            try
            {
                lock (cacheLock)
                {
                    var entry = new CacheEntry<T>
                    {
                        ExpiresAt = DateTime.UtcNow.Add(ttl),
                        Value = value
                    };
                    var json = Serialization.ToJson(entry);
                    var path = KeyToPath(key);
                    var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    File.WriteAllText(tempPath, json);
                    if (File.Exists(path))
                        File.Replace(tempPath, path, null);
                    else
                        File.Move(tempPath, path);
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, $"Cache write failed for key: {key}");
            }
        }

        public void Invalidate(string key)
        {
            try
            {
                lock (cacheLock)
                {
                    var path = KeyToPath(key);
                    TryDelete(path);
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, $"Cache invalidate failed for key: {key}");
            }
        }

        public void InvalidateByPrefix(string keyPrefix)
        {
            try
            {
                lock (cacheLock)
                {
                    var safePrefix = Path.GetFileNameWithoutExtension(KeyToPath(keyPrefix));
                    foreach (var file in Directory.GetFiles(cacheDir, safePrefix + "*.json"))
                        TryDelete(file);
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, $"Cache prefix invalidate failed for key prefix: {keyPrefix}");
            }
        }

        public void PurgeExpired()
        {
            try
            {
                lock (cacheLock)
                {
                    foreach (var file in Directory.GetFiles(cacheDir, "*.json"))
                    {
                        try
                        {
                            var json = File.ReadAllText(file);
                            // Quick check without full deserialise
                            if (json.Contains("\"ExpiresAt\""))
                            {
                                // Deserialise as dynamic to check expiry
                                var raw = Serialization.FromJson<CacheEntry<object>>(json);
                                if (raw != null && DateTime.UtcNow > raw.ExpiresAt)
                                    TryDelete(file);
                            }
                        }
                        catch { /* skip individual corrupt files */ }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Cache purge failed");
            }
        }

        private static void TryDelete(string path)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                File.Delete(path);
        }
    }
}
