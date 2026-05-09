using System;
using System.Diagnostics;
using Playnite.SDK;

namespace GameRecommender
{
    internal static class UrlOpener
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        public static bool OpenHttpUrl(string url)
        {
            if (!TryNormalizeHttpUrl(url, out var normalized))
                return false;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = normalized,
                    UseShellExecute = true
                });
                return true;
            }
            catch (Exception ex)
            {
                logger.Warn(ex, $"Failed to open URL: {normalized}");
                return false;
            }
        }

        public static bool TryNormalizeHttpUrl(string url, out string normalized)
        {
            normalized = string.Empty;
            if (string.IsNullOrWhiteSpace(url))
                return false;
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
                return false;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                return false;

            normalized = uri.AbsoluteUri;
            return true;
        }
    }
}
