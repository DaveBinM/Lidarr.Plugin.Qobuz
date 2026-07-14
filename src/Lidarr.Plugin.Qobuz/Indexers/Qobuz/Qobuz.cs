using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download.Clients.Qobuz;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Qobuz.API;

namespace NzbDrone.Core.Indexers.Qobuz
{
    public class Qobuz : HttpIndexerBase<QobuzIndexerSettings>
    {
        public override string Name => "Qobuz";
        public override string Protocol => nameof(QobuzDownloadProtocol);
        public override bool SupportsRss => false;
        public override bool SupportsSearch => true;
        public override int PageSize => 100;
        public override TimeSpan RateLimit => new TimeSpan(0);

        private readonly IQobuzProxy _qobuzProxy;

        public Qobuz(IQobuzProxy qobuzProxy,
            IHttpClient httpClient,
            IIndexerStatusService indexerStatusService,
            IConfigService configService,
            IParsingService parsingService,
            Logger logger)
            : base(httpClient, indexerStatusService, configService, parsingService, logger)
        {
            _qobuzProxy = qobuzProxy;
        }

        public override IIndexerRequestGenerator GetRequestGenerator()
        {
            bool ep = !string.IsNullOrEmpty(Settings.Email) && !string.IsNullOrEmpty(Settings.MD5Password);
            bool it = !string.IsNullOrEmpty(Settings.UserID) && !string.IsNullOrEmpty(Settings.UserAuthToken);
            if (ep || it)
            {
                bool forceRecreate = (Settings.AppID != QobuzAPI.Instance?.Client?.AppId) || (Settings.AppSecret != QobuzAPI.Instance?.Client?.AppSecret)
                                  || (Settings.Email != QobuzAPI.Instance?.Login?.User?.Email) || (Settings.MD5Password != QobuzAPI.Instance?.LastPassword)
                                  || (Settings.UserID != QobuzAPI.Instance?.Login?.User?.Id.ToString()) || (Settings.UserAuthToken != QobuzAPI.Instance?.Login?.AuthToken);

                QobuzAPI.Initialize(_logger, Settings.AppID, Settings.AppSecret, forceRecreate);
                QobuzAPI.Instance.PickSignInFromSettings(Settings, _logger);
            }
            else
                return null;

            return new QobuzRequestGenerator()
            {
                Settings = Settings,
                Logger = _logger
            };
        }

        public override IParseIndexerResponse GetParser()
        {
            return new QobuzParser()
            {
                Settings = Settings,
                Logger = _logger
            };
        }

        // Qobuz's /album/search tacks "Various Artists" compilations onto many specific-artist searches.
        // They're irrelevant to such a search, and they are the sole trigger for an interactive-search 500:
        // when the library holds two distinct "Various Artists" entries, Lidarr's ArtistRepository.FindByName
        // throws MultipleArtistsFoundException on them, aborting the whole search. Drop them here unless the
        // search itself is for Various Artists. Plain string check, no artist lookup; a search with no VA
        // result is returned unchanged.
        public override async Task<IList<ReleaseInfo>> Fetch(AlbumSearchCriteria searchCriteria)
        {
            return SkipVariousArtists(await base.Fetch(searchCriteria), searchCriteria.Artist?.Name);
        }

        public override async Task<IList<ReleaseInfo>> Fetch(ArtistSearchCriteria searchCriteria)
        {
            return SkipVariousArtists(await base.Fetch(searchCriteria), searchCriteria.Artist?.Name);
        }

        private IList<ReleaseInfo> SkipVariousArtists(IList<ReleaseInfo> releases, string searchedArtist)
        {
            if (releases.Count == 0 || IsVariousArtists(searchedArtist))
            {
                return releases;
            }

            var kept = releases.Where(r => !IsVariousArtists(r.Artist)).ToList();
            if (kept.Count != releases.Count)
            {
                _logger.Debug("Qobuz: skipped {0} 'Various Artists' result(s) for search '{1}'", releases.Count - kept.Count, searchedArtist);
            }

            return kept;
        }

        private static bool IsVariousArtists(string artist)
            => !string.IsNullOrWhiteSpace(artist)
               && (artist.Trim().Equals("Various Artists", StringComparison.OrdinalIgnoreCase)
                   || artist.Trim().Equals("VA", StringComparison.OrdinalIgnoreCase));
    }
}
