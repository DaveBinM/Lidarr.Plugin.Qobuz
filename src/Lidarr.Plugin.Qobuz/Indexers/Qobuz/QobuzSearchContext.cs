using System.Collections.Generic;
using NzbDrone.Common.Http;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Indexers.Qobuz
{
    // One per search, shared by every request the chain issues for it. QobuzParser sets MatchFound
    // while parsing an earlier query's response, and the gated fallback queries check it before they
    // issue anything: HttpIndexerBase runs a tier's request enumerables strictly in order, so a
    // search that has already found what it was looking for doesn't pay for the fallbacks.
    public class QobuzSearchContext
    {
        public string ArtistName { get; set; }
        public string ArtistCleanName { get; set; }

        // Normalised core of the wanted album title; null for an artist search.
        public string CoreTitle { get; set; }

        public bool MatchFound { get; set; }
    }

    // Carries the search context and the page offset through to the parser: HttpIndexerBase.FetchPage
    // hands this same instance to IParseIndexerResponse as IndexerResponse.Request.
    public class QobuzIndexerRequest : IndexerRequest
    {
        public QobuzIndexerRequest(string url, QobuzSearchContext context, int offset)
            : base(url, HttpAccept.Json)
        {
            Context = context;
            Offset = offset;
        }

        public QobuzSearchContext Context { get; }

        public int Offset { get; }
    }

    // Keeps Qobuz's full main-artist credit list on the release so Qobuz.Fetch can tell a genuinely
    // wrong-artist result from one where the searched artist is a credited main artist but not the
    // album's primary one (a remix released under the remixer's name, say).
    public class QobuzReleaseInfo : ReleaseInfo
    {
        public List<string> MainArtists { get; set; } = new List<string>();
    }
}
