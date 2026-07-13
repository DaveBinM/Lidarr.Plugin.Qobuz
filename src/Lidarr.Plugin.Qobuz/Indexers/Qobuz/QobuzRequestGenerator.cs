using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Plugin.Qobuz.API;

namespace NzbDrone.Core.Indexers.Qobuz
{
    public class QobuzRequestGenerator : IIndexerRequestGenerator
    {
        private const int PageSize = 100;
        private const int MaxPages = 15;

        // Tier-2 fallback cleaning (see GetSearchRequests). Qobuz's /album/search is token-AND, so a
        // bracketed group or a trailing edition/soundtrack qualifier that Qobuz doesn't carry in its
        // own title drops the result to zero. These strip that noise back to the core title.
        private static readonly Regex BracketedGroups = new Regex(@"\s*[\(\[][^\)\]]*[\)\]]", RegexOptions.Compiled);
        private static readonly Regex TrailingQualifier = new Regex(
            @"\s*[:\-–—]?\s*\b(?:" +
            @"(?:original\s+)?(?:motion\s+picture\s+)?(?:sound\s?tracks?|score)" +
            @"|OST" +
            @"|(?:\d+(?:st|nd|rd|th)?\s+)?anniversary\s+edition" +
            @"|special\s+edition" +
            @"|deluxe|expanded|remaster\w*|bonus\s+track\w*|EP|single" +
            @")\b.*$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        public QobuzIndexerSettings Settings { get; set; }
        public Logger Logger { get; set; }

        public virtual IndexerPageableRequestChain GetRecentRequests()
        {
            // this is a lazy implementation, just here so that lidarr has something to test against when saving settings
            var pageableRequests = new IndexerPageableRequestChain();
            pageableRequests.Add(GetRequests("never gonna give you up"));

            return pageableRequests;
        }

        public IndexerPageableRequestChain GetSearchRequests(AlbumSearchCriteria searchCriteria)
        {
            var chain = new IndexerPageableRequestChain();

            // Tier 1: the raw artist + album query. This is what already works for the vast majority
            // of searches, so it stays first and unchanged (HttpIndexerBase advances to the next tier
            // only when the current one returns nothing).
            var tier1 = $"{searchCriteria.ArtistQuery} {searchCriteria.AlbumQuery}";
            chain.AddTier(GetRequests(tier1));

            // Tier 2 (fallback, reached only when Tier 1 returns nothing): clean punctuation/accents
            // and strip trailing edition/soundtrack qualifiers so the core title survives Qobuz's
            // token-AND matching (e.g. MB "Batman: Original Motion Picture Score" -> "Batman", which
            // Qobuz lists as "Batman (Original Motion Picture Soundtrack)"). Added only when it
            // actually differs from Tier 1, to avoid issuing a redundant identical request.
            if (!string.IsNullOrWhiteSpace(searchCriteria.AlbumTitle) &&
                !string.IsNullOrWhiteSpace(searchCriteria.ArtistQuery))
            {
                var artist = searchCriteria.CleanArtistQuery.Replace('+', ' ');
                var album = SearchCriteriaBase.GetQueryTitle(StripQualifiers(searchCriteria.AlbumTitle)).Replace('+', ' ');
                var tier2 = $"{artist} {album}";

                if (!string.Equals(tier2, tier1, StringComparison.OrdinalIgnoreCase))
                {
                    chain.AddTier(GetRequests(tier2));
                }
            }

            return chain;
        }

        // Reduces a MusicBrainz album title to its core so it survives Qobuz's token-AND search:
        // drops bracketed groups and any trailing edition/soundtrack/remaster qualifier. Returns the
        // original title if stripping would leave nothing (e.g. an album literally named "Deluxe").
        private static string StripQualifiers(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return title;
            }

            var stripped = BracketedGroups.Replace(title, string.Empty);
            stripped = TrailingQualifier.Replace(stripped, string.Empty);
            stripped = Regex.Replace(stripped, @"\s{2,}", " ").Trim(' ', ':', '-', '–', '—');

            return string.IsNullOrWhiteSpace(stripped) ? title : stripped;
        }

        public IndexerPageableRequestChain GetSearchRequests(ArtistSearchCriteria searchCriteria)
        {
            var chain = new IndexerPageableRequestChain();

            chain.AddTier(GetRequests(searchCriteria.ArtistQuery));

            return chain;
        }

        private IEnumerable<IndexerRequest> GetRequests(string searchParameters)
        {
            // make sure we are logged in and have valid credentials
            // if we don't it should throw an error
            if (!QobuzAPI.Instance.Client.IsAppSecretValid())
            {
                QobuzAPI.Instance.PickSignInFromSettings(Settings, Logger);
            }

            if (QobuzAPI.Instance.Login == null)
            {
                throw new Exception("Qobuz login failed, please check your credentials in the indexer settings.");
            }

            for (var page = 0; page < MaxPages; page++)
            {
                var data = new Dictionary<string, string>()
                {
                    ["query"] = searchParameters,
                    ["limit"] = $"{PageSize}",
                    ["offset"] = $"{page * PageSize}",
                };

                var url = QobuzAPI.Instance!.GetAPIUrl("/album/search", data);
                var req = new IndexerRequest(url, HttpAccept.Json);
                req.HttpRequest.Method = System.Net.Http.HttpMethod.Get;
                req.HttpRequest.Headers.Add("X-App-ID", $"{QobuzAPI.Instance.Client.AppId}");
                req.HttpRequest.Headers.Add("X-User-Auth-Token", $"{QobuzAPI.Instance.Login.AuthToken}");
                yield return req;
            }
        }
    }
}
