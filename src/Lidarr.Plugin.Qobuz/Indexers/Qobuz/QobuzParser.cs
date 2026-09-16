using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Qobuz.API;
using QobuzApiSharp.Models.Content;

namespace NzbDrone.Core.Indexers.Qobuz
{
    public class QobuzParser : IParseIndexerResponse
    {
        private const string MainArtistRole = "main-artist";

        public QobuzIndexerSettings Settings { get; set; }
        public Logger Logger { get; set; }

        public IList<ReleaseInfo> ParseResponse(IndexerResponse response)
        {
            var torrentInfos = new List<ReleaseInfo>();
            var content = new HttpResponse<SearchResult>(response.HttpResponse).Content;

            // Logger?.Debug("Qobuz raw search response: {0}", content);

            var jsonResponse = JObject.Parse(content).ToObject<SearchResult>();
            var allAlbums = jsonResponse.Albums.Items;

            var request = response.Request as QobuzIndexerRequest;
            var context = request?.Context;

            // Release-type lookups are one API call plus a throttling sleep each, so they only run for
            // the first page of a query and only for albums by the artist we searched for. A broad
            // query used to spend ~10 s per page on albums Lidarr rejects as the wrong artist anyway,
            // which is what pushed wide searches past Huntarr's 90 s limit.
            var releaseTypes = request == null || request.Offset == 0
                ? FetchReleaseTypes(SelectForReleaseTypeLookup(allAlbums, context))
                : new Dictionary<string, string>();

            var releases = allAlbums.Select(result => ProcessAlbumResult(result, releaseTypes)).ToArray();

            foreach (var task in releases)
            {
                torrentInfos.AddRange(task);
            }

            // Tell the request generator that this search is answered, so its gated fallback query
            // doesn't run. Nothing is dropped here: IsFullPage counts what we return, so paging must
            // see the full page.
            if (context is { MatchFound: false } && allAlbums.Any(a => IsSearchedAlbum(a, context)))
            {
                context.MatchFound = true;
            }

            return torrentInfos
                .OrderBy(o => QualityPriority(o))
                .ThenBy(o => o.Size)
                .ToArray();
        }

        private static int QualityPriority(ReleaseInfo r) => r.Container switch
        {
            "Lossless"    => 0,
            "24bit 96kHz" => 1,
            "24bit 192kHz" => 2,
            _              => 3  // MP3
        };

        // The album the search was actually for: same primary artist, same core title.
        private static bool IsSearchedAlbum(Album album, QobuzSearchContext context)
        {
            if (string.IsNullOrEmpty(context.CoreTitle) || string.IsNullOrEmpty(context.ArtistCleanName))
            {
                return false;
            }

            return IsPrimaryArtist(album, context.ArtistCleanName) &&
                   QobuzRequestGenerator.CoreKey(album.CompleteTitle) == context.CoreTitle;
        }

        private static bool IsPrimaryArtist(Album album, string artistCleanName)
            => album.Artist?.Name != null && album.Artist.Name.CleanArtistName() == artistCleanName;

        private static List<Album> SelectForReleaseTypeLookup(List<Album> albums, QobuzSearchContext context)
        {
            if (string.IsNullOrEmpty(context?.ArtistCleanName))
            {
                return albums.Take(10).ToList();
            }

            return albums
                .Where(a => IsPrimaryArtist(a, context.ArtistCleanName) ||
                            MainArtistsOf(a).Any(n => n.CleanArtistName() == context.ArtistCleanName))
                .Take(10)
                .ToList();
        }

        private static IEnumerable<string> MainArtistsOf(Album album)
        {
            if (album.Artists == null)
            {
                return Enumerable.Empty<string>();
            }

            return album.Artists
                .Where(a => a?.Name != null && a.Roles != null &&
                            a.Roles.Any(r => string.Equals(r, MainArtistRole, StringComparison.OrdinalIgnoreCase)))
                .Select(a => a.Name);
        }

        private Dictionary<string, string> FetchReleaseTypes(List<Album> albums)
        {
            var result = new Dictionary<string, string>();
            var rng = new Random();
            foreach (var a in albums)
            {
                try
                {
                    var detail = QobuzAPI.Instance?.Client?.GetAlbum(a.Id, true);
                    if (detail?.ReleaseType != null)
                        result[a.Id] = detail.ReleaseType;
                }
                catch (Exception ex)
                {
                    Logger?.Warn("Failed to fetch Qobuz album detail for {0}: {1}", a.Id, ex.Message);
                }
                Task.Delay(rng.Next(300, 800)).Wait();
            }
            return result;
        }

        private IEnumerable<ReleaseInfo> ProcessAlbumResult(Album result, Dictionary<string, string> releaseTypes)
        {
            // determine available audio qualities
            List<AudioQuality> qualityList = new() { AudioQuality.MP3320, AudioQuality.FLACLossless };

            if ((result.Hires ?? false) && (result.HiresStreamable ?? false))
            {
                qualityList.Add(AudioQuality.FLACHiRes24Bit96kHz);
                if ((result.MaximumSamplingRate ?? 0) > 96)
                    qualityList.Add(AudioQuality.FLACHiRes24Bit192Khz);
            }

            releaseTypes.TryGetValue(result.Id, out var releaseType);
            return qualityList.Select(q => ToReleaseInfo(result, q, releaseType));
        }

        // Qobuz returns album URLs with its default "fr-fr" locale; rewrite it to the signed-in
        // account's locale (e.g. "au-en") so Lidarr's info link opens the right storefront. The
        // download only needs the album ID parsed out of this URL, so this is purely cosmetic.
        private static string LocalizeUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
                return url;

            var user = QobuzAPI.Instance?.Login?.User;
            if (string.IsNullOrEmpty(user?.CountryCode) || string.IsNullOrEmpty(user?.LanguageCode))
                return url;

            var locale = $"{user.CountryCode}-{user.LanguageCode}".ToLowerInvariant();
            return Regex.Replace(url, @"(qobuz\.com/)[a-z]{2}-[a-z]{2}/", $"$1{locale}/", RegexOptions.IgnoreCase);
        }

        private static ReleaseInfo ToReleaseInfo(Album x, AudioQuality bitrate, string releaseType)
        {
            var publishDate = DateTime.UtcNow;
            var year = 0;
            if (x.ReleaseDateOriginal != null)
            {
                publishDate = x.ReleaseDateOriginal.Value.DateTime;
                year = publishDate.Year;
            }

            var url = LocalizeUrl(x.Url);

            var result = new QobuzReleaseInfo
            {
                Guid = $"Qobuz-{x.Id}-{bitrate}",
                Artist = x.Artist.Name,
                Album = x.CompleteTitle,
                DownloadUrl = url,
                InfoUrl = url,
                PublishDate = publishDate,
                DownloadProtocol = nameof(QobuzDownloadProtocol),
                MainArtists = MainArtistsOf(x).ToList()
            };

            string format;
            switch (bitrate)
            {
                case AudioQuality.MP3320:
                    result.Codec = "MP3";
                    result.Container = "320";
                    format = "MP3 320kbps";
                    break;
                case AudioQuality.FLACLossless:
                    result.Codec = "FLAC";
                    result.Container = "Lossless";
                    format = "FLAC Lossless";
                    break;
                case AudioQuality.FLACHiRes24Bit96kHz:
                    result.Codec = "FLAC";
                    result.Container = "24bit 96kHz";
                    format = "FLAC 24bit 96kHz";
                    break;
                case AudioQuality.FLACHiRes24Bit192Khz:
                    result.Codec = "FLAC";
                    result.Container = "24bit 192kHz";
                    format = "FLAC 24bit 192kHz";
                    break;
                default:
                    throw new NotImplementedException();
            }

            // Size estimates: raw PCM bitrate / 8 for bytes, with FLAC compression factor applied.
            // FLAC Lossless is always CD quality (16-bit/44.1kHz). Hi-Res uses actual album specs,
            // capped at the tier's maximum sampling rate.
            const double flacCompressionFactor = 0.7;
            double bitsPerSecond = bitrate switch
            {
                AudioQuality.MP3320 => 320_000,
                AudioQuality.FLACLossless => 16.0 * 44_100 * 2,
                AudioQuality.FLACHiRes24Bit96kHz =>
                    (x.MaximumBitDepth ?? 24) * (Math.Min(x.MaximumSamplingRate ?? 96, 96) * 1000) * (x.MaximumChannelCount ?? 2),
                AudioQuality.FLACHiRes24Bit192Khz =>
                    (x.MaximumBitDepth ?? 24) * ((x.MaximumSamplingRate ?? 192) * 1000) * (x.MaximumChannelCount ?? 2),
                _ => 320_000
            };
            double compressionFactor = bitrate == AudioQuality.MP3320 ? 1.0 : flacCompressionFactor;
            result.Size = (long)(x.Duration.Value * bitsPerSecond / 8 * compressionFactor);

            result.Title = $"{x.Artist.Name} - {x.CompleteTitle}";

            if (year > 0)
            {
                result.Title += $" ({year})";
            }

            if (!string.IsNullOrEmpty(releaseType))
            {
                result.Title += $" [{releaseType}]";
            }

            if (x.ParentalWarning.GetValueOrDefault())
            {
                result.Title += " [Explicit]";
            }

            result.Title += $" [{format}] [WEB]";

            return result;
        }
    }
}
