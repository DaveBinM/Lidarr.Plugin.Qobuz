using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download.Clients.Qobuz;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Qobuz.API;
using ReleaseTitleParser = NzbDrone.Core.Parser.Parser;

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
        private readonly IArtistService _artistService;

        public Qobuz(IQobuzProxy qobuzProxy,
            IHttpClient httpClient,
            IIndexerStatusService indexerStatusService,
            IConfigService configService,
            IParsingService parsingService,
            IArtistService artistService,
            Logger logger)
            : base(httpClient, indexerStatusService, configService, parsingService, logger)
        {
            _qobuzProxy = qobuzProxy;
            _artistService = artistService;
        }

        public override IIndexerRequestGenerator GetRequestGenerator()
        {
            var ep = !string.IsNullOrEmpty(Settings.Email) && !string.IsNullOrEmpty(Settings.MD5Password);
            var it = !string.IsNullOrEmpty(Settings.UserID) && !string.IsNullOrEmpty(Settings.UserAuthToken);

            if (!ep && !it)
            {
                return null;
            }

            return new QobuzRequestGenerator()
            {
                Settings = Settings,
                Logger = _logger,

                // Reuses the signed-in session unless the settings changed or it stopped working, so
                // concurrent searches can't swap the static instance out from under each other.
                Api = QobuzAPI.EnsureSignedIn(Settings, _logger)
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

        public override async Task<IList<ReleaseInfo>> Fetch(AlbumSearchCriteria searchCriteria)
        {
            var releases = SkipVariousArtists(await base.Fetch(searchCriteria), searchCriteria.Artist?.Name);
            return ResolveAmbiguousArtists(releases, searchCriteria.Artist);
        }

        public override async Task<IList<ReleaseInfo>> Fetch(ArtistSearchCriteria searchCriteria)
        {
            var releases = SkipVariousArtists(await base.Fetch(searchCriteria), searchCriteria.Artist?.Name);
            return ResolveAmbiguousArtists(releases, searchCriteria.Artist);
        }

        // Qobuz's /album/search tacks "Various Artists" compilations onto many specific-artist searches.
        // They're irrelevant to such a search, and they're the most common trigger of the whole-search
        // 500 that ResolveAmbiguousArtists handles below (this library holds two distinct "Various
        // Artists" entries). Drop them here unless the search itself is for Various Artists. Plain
        // string check, no artist lookup; a search with no VA result is returned unchanged.
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

        // Lidarr resolves every returned release's parsed artist name through ArtistRepository
        // .FindByName, which throws MultipleArtistsFoundException when two library artists share a
        // CleanName (this library has two "Justice", two "Jungle", ...). That exception surfaces as an
        // NRE and fails the *entire* interactive search with a 500, losing every result — including
        // the album that was actually wanted. Qobuz legitimately returns such names (a remix credited
        // to RAYE on a David Guetta search), so rather than lose the search: retitle the release under
        // the searched artist when Qobuz credits them as a main artist, and drop it otherwise.
        //
        // Runs after paging, so dropping a release here can't end pagination early (IsFullPage counts
        // what the parser returned). Downloads are unaffected: DownloadItem uses DownloadUrl only.
        private IList<ReleaseInfo> ResolveAmbiguousArtists(IList<ReleaseInfo> releases, Artist searched)
        {
            if (releases.Count == 0 || string.IsNullOrWhiteSpace(searched?.CleanName))
            {
                return releases;
            }

            // Mirrors FindByName's throw condition. GetAllArtists is cached for 30s.
            var duplicates = _artistService.GetAllArtists()
                .Where(a => a.CleanName != null)
                .GroupBy(a => a.CleanName)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToHashSet();

            if (duplicates.Count == 0)
            {
                return releases;
            }

            var kept = new List<ReleaseInfo>(releases.Count);
            var retitled = 0;
            var dropped = 0;

            foreach (var release in releases)
            {
                // The same parse Lidarr's DownloadDecisionMaker will do.
                var clean = ReleaseTitleParser.ParseAlbumTitle(release.Title)?.ArtistName?.CleanArtistName();

                // An unparseable title never reaches FindByName, and the searched artist is resolved
                // from the search criteria rather than looked up, so both are safe as-is.
                if (clean == null || clean == searched.CleanName || !duplicates.Contains(clean))
                {
                    kept.Add(release);
                    continue;
                }

                if (TryRetitleAs(release, searched))
                {
                    retitled++;
                    kept.Add(release);
                    continue;
                }

                dropped++;
            }

            if (retitled > 0 || dropped > 0)
            {
                _logger.Debug("Qobuz: {0} result(s) retitled and {1} dropped for '{2}': their credited artist matches more than one library artist",
                    retitled, dropped, searched.Name);
            }

            return kept;
        }

        // "RAYE - Stay (Don't Go Away) ... [Lossless] [WEB]" -> "David Guetta - Stay (Don't Go Away) ..."
        // Only for releases where Qobuz credits the searched artist as a main artist, and only when the
        // rewritten title actually parses back to them.
        private static bool TryRetitleAs(ReleaseInfo release, Artist searched)
        {
            if (release is not QobuzReleaseInfo qobuzRelease ||
                string.IsNullOrEmpty(release.Artist) ||
                release.Title?.StartsWith(release.Artist, StringComparison.Ordinal) != true ||
                !qobuzRelease.MainArtists.Any(name => name.CleanArtistName() == searched.CleanName))
            {
                return false;
            }

            var title = searched.Name + release.Title[release.Artist.Length..];
            if (ReleaseTitleParser.ParseAlbumTitle(title)?.ArtistName?.CleanArtistName() != searched.CleanName)
            {
                return false;
            }

            release.Title = title;
            release.Artist = searched.Name;
            return true;
        }

        private static bool IsVariousArtists(string artist)
            => !string.IsNullOrWhiteSpace(artist)
               && (artist.Trim().Equals("Various Artists", StringComparison.OrdinalIgnoreCase)
                   || artist.Trim().Equals("VA", StringComparison.OrdinalIgnoreCase));
    }
}
