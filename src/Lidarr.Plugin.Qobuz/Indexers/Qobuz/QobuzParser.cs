using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Qobuz.API;
using QobuzApiSharp.Models.Content;

namespace NzbDrone.Core.Indexers.Qobuz
{
    public class QobuzParser : IParseIndexerResponse
    {
        public QobuzIndexerSettings Settings { get; set; }
        public Logger Logger { get; set; }

        public IList<ReleaseInfo> ParseResponse(IndexerResponse response)
        {
            var torrentInfos = new List<ReleaseInfo>();
            var content = new HttpResponse<SearchResult>(response.HttpResponse).Content;

            Logger?.Debug("Qobuz raw search response: {0}", content);

            var jsonResponse = JObject.Parse(content).ToObject<SearchResult>();
            var releases = jsonResponse.Albums.Items.Select(result => ProcessAlbumResult(result)).ToArray();

            foreach (var task in releases)
            {
                torrentInfos.AddRange(task);
            }

            return torrentInfos
                .OrderByDescending(o => o.Size)
                .ToArray();
        }

        private IEnumerable<ReleaseInfo> ProcessAlbumResult(Album result)
        {
            // determine available audio qualities
            List<AudioQuality> qualityList = new() { AudioQuality.MP3320, AudioQuality.FLACLossless };

            if ((result.Hires ?? false) && (result.HiresStreamable ?? false))
            {
                qualityList.Add(AudioQuality.FLACHiRes24Bit192Khz);
                qualityList.Add(AudioQuality.FLACHiRes24Bit96kHz);
            }

            return qualityList.Select(q => ToReleaseInfo(result, q));
        }

        private static ReleaseInfo ToReleaseInfo(Album x, AudioQuality bitrate)
        {
            var publishDate = DateTime.UtcNow;
            var year = 0;
            if (x.ReleaseDateOriginal != null)
            {
                publishDate = x.ReleaseDateOriginal.Value.DateTime;
                year = publishDate.Year;
            }

            var url = x.Url;

            var result = new ReleaseInfo
            {
                Guid = $"Qobuz-{x.Id}-{bitrate}",
                Artist = x.Artist.Name,
                Album = x.CompleteTitle,
                DownloadUrl = url,
                InfoUrl = url,
                PublishDate = publishDate,
                DownloadProtocol = nameof(QobuzDownloadProtocol)
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
            // FLAC Lossless is always CD quality (16-bit/44.1kHz). Hi-Res uses actual album specs.
            const double flacCompressionFactor = 0.7;
            double bitsPerSecond = bitrate switch
            {
                AudioQuality.MP3320 => 320_000,
                AudioQuality.FLACLossless => 16.0 * 44_100 * 2,
                AudioQuality.FLACHiRes24Bit96kHz or AudioQuality.FLACHiRes24Bit192Khz =>
                    (x.MaximumBitDepth ?? 24) * ((x.MaximumSamplingRate ?? 96) * 1000) * (x.MaximumChannelCount ?? 2),
                _ => 320_000
            };
            double compressionFactor = bitrate == AudioQuality.MP3320 ? 1.0 : flacCompressionFactor;
            result.Size = (long)(x.Duration.Value * bitsPerSecond / 8 * compressionFactor);

            result.Title = $"{x.Artist.Name} - {x.CompleteTitle}";

            if (year > 0)
            {
                result.Title += $" ({year})";
            }

            if (!string.IsNullOrEmpty(x.ReleaseType))
            {
                result.Title += $" [{x.ReleaseType}]";
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
