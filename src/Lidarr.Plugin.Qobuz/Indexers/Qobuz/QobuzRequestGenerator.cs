using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Plugin.Qobuz.API;

namespace NzbDrone.Core.Indexers.Qobuz
{
    // One query in a search's plan, in the order HttpIndexerBase will run it.
    public class QobuzQuery
    {
        public QobuzQuery(int tier, string query, bool gated)
        {
            Tier = tier;
            Query = query;
            Gated = gated;
        }

        public int Tier { get; }
        public string Query { get; }

        // A gated query is skipped when an earlier query in the same tier already returned the album
        // we were searching for.
        public bool Gated { get; }

        public override string ToString() => $"tier {Tier}{(Gated ? " (gated)" : string.Empty)}: {Query}";
    }

    public class QobuzRequestGenerator : IIndexerRequestGenerator
    {
        private const int PageSize = 100;
        private const int MaxPages = 15;

        // Fallback-query cleaning (see BuildAlbumQueries). Qobuz's /album/search is token-AND, so a
        // bracketed group or a trailing edition/soundtrack qualifier that Qobuz doesn't carry in its
        // own title drops the result to zero. These strip that noise back to the core title.
        private static readonly Regex BracketedGroups = new Regex(@"\s*[\(\[][^\)\]]*[\)\]]", RegexOptions.Compiled);

        // A colon/dash subtitle that names a soundtrack or an alternate version is dropped wholesale.
        // MB's "The Hack: Original Television Soundtrack" is just "The Hack" on Qobuz, and Qobuz keeps
        // "Remixes"/"Live"/"Deluxe" style qualifiers in a separate `version` field that its search does
        // not index at all — so "Guilty as Charged: The Remixes" can only be found as "Guilty as Charged".
        private static readonly Regex SubtitleQualifier = new Regex(
            @"\s*[:\-–—]\s[^:]*\b(?:sound\s?tracks?|score|OST|music\s+from" +
            @"|re[-‐]?mix(?:es|ed)?|rework(?:s|ed)?|mix(?:es)?|edits?|version|live|acoustic|instrumentals?" +
            @"|remaster\w*|anniversary|deluxe|expanded|edition|reissue|demos?|mono|stereo)\b.*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex TrailingQualifier = new Regex(
            @"\s*[:\-–—]?\s*\b(?:" +
            @"(?:original\s+)?(?:motion\s+picture\s+)?(?:sound\s?tracks?|score)" +
            @"|OST" +
            @"|(?:\d+(?:st|nd|rd|th)?\s+)?anniversary\s+edition" +
            @"|special\s+edition" +
            @"|deluxe|expanded|remaster\w*|bonus\s+track\w*|EP|single" +
            @"|(?:the\s+)?re[-‐]?mix(?:es|ed)?|rework(?:s|ed)?" +
            @")\b.*$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        // Dotted acronyms: Qobuz matches "S.H.I.E.L.D." and "SHIELD" for each other, but not the
        // "S H I E L D" that GetQueryTitle's punctuation stripping would otherwise produce.
        private static readonly Regex AcronymDots = new Regex(@"(?<!\w)(?:\p{L}\.){2,}\p{L}?(?!\w)", RegexOptions.Compiled);

        // A trailing ": subtitle" that Qobuz keeps in its unsearchable `version` field rather than in
        // the title ("Imagine: The Evolution Documentary" is listed as "Imagine").
        private static readonly Regex TrailingSubtitle = new Regex(@"^(.*\S)\s*:\s+\S.*$", RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex Apostrophes = new Regex(@"['`´‘’]", RegexOptions.Compiled);
        private static readonly Regex NonAlphanumeric = new Regex(@"[^a-z0-9]+", RegexOptions.Compiled);
        private static readonly Regex RepeatedWhitespace = new Regex(@"\s{2,}", RegexOptions.Compiled);

        public QobuzIndexerSettings Settings { get; set; }
        public Logger Logger { get; set; }

        // Set by Qobuz.GetRequestGenerator, which signs in before the chain is built.
        public QobuzAPI Api { get; set; }

        public virtual IndexerPageableRequestChain GetRecentRequests()
        {
            // this is a lazy implementation, just here so that lidarr has something to test against when saving settings
            var pageableRequests = new IndexerPageableRequestChain();
            pageableRequests.Add(GetRequests("never gonna give you up", null, false));

            return pageableRequests;
        }

        public IndexerPageableRequestChain GetSearchRequests(AlbumSearchCriteria searchCriteria)
        {
            var context = new QobuzSearchContext
            {
                ArtistName = searchCriteria.Artist?.Name,
                ArtistCleanName = searchCriteria.Artist?.CleanName,
                CoreTitle = CoreKey(searchCriteria.AlbumTitle)
            };

            var chain = new IndexerPageableRequestChain();
            var currentTier = 0;

            foreach (var query in BuildAlbumQueries(searchCriteria.ArtistQuery, searchCriteria.AlbumQuery, searchCriteria.AlbumTitle))
            {
                // Everything in one tier runs together; HttpIndexerBase only advances to the next tier
                // when the current one returned nothing at all.
                if (query.Tier != currentTier)
                {
                    chain.AddTier(GetRequests(query.Query, context, query.Gated));
                    currentTier = query.Tier;
                }
                else
                {
                    chain.Add(GetRequests(query.Query, context, query.Gated));
                }
            }

            return chain;
        }

        public IndexerPageableRequestChain GetSearchRequests(ArtistSearchCriteria searchCriteria)
        {
            var context = new QobuzSearchContext
            {
                ArtistName = searchCriteria.Artist?.Name,
                ArtistCleanName = searchCriteria.Artist?.CleanName
            };

            var chain = new IndexerPageableRequestChain();

            chain.AddTier(GetRequests(searchCriteria.ArtistQuery, context, false));

            return chain;
        }

        // The whole query plan for an album search. Pure and static so it can be checked without a
        // Qobuz session.
        public static List<QobuzQuery> BuildAlbumQueries(string artistQuery, string albumQuery, string albumTitle)
        {
            var queries = new List<QobuzQuery>();

            void TryAdd(int tier, string query, bool gated)
            {
                if (string.IsNullOrWhiteSpace(query) ||
                    queries.Any(q => string.Equals(q.Query, query, StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }

                queries.Add(new QobuzQuery(tier, query, gated));
            }

            // Tier 1 / query 1: the raw artist + album query. This is what already works for the vast
            // majority of searches, so it stays first and unchanged.
            TryAdd(1, $"{artistQuery} {albumQuery}".Trim(), false);

            if (string.IsNullOrWhiteSpace(albumTitle) || string.IsNullOrWhiteSpace(artistQuery))
            {
                return queries;
            }

            var artist = CleanForTokenSearch(SafeQueryTitle(CollapseAcronyms(artistQuery)));
            if (string.IsNullOrWhiteSpace(artist))
            {
                return queries;
            }

            // Tier 1 / query 2, gated: clean punctuation and accents and strip edition/version
            // qualifiers so the core title survives Qobuz's token-AND matching (MB "Batman: Original
            // Motion Picture Score" -> "Batman", which Qobuz lists as "Batman (Original Motion Picture
            // Soundtrack)"). It shares tier 1 with the raw query because a tier-1 hit is often the
            // wrong edition or an unrelated artist, which used to mask this query entirely; the gate
            // keeps it free for the searches that the raw query already answers.
            TryAdd(1, Compose(artist, CleanAlbumForQuery(albumTitle, false)), true);

            // Tier 2, reached only when tier 1 returned nothing at all.
            // Drop a trailing ": subtitle" as well — Qobuz keeps those in its unsearchable `version`
            // field ("Bumps Per Minute: 18 Studies for Dodgems" is listed as "Bumps Per Minute").
            TryAdd(2, Compose(artist, CleanAlbumForQuery(albumTitle, true)), false);

            // Same tier: MB split-release titles like "A / B", which Qobuz usually carries as separate
            // releases. Lidarr's matching of a half against the combined MB album is best-effort, but
            // returning the halves beats a guaranteed zero.
            if (albumTitle.Contains(" / "))
            {
                foreach (var part in albumTitle.Split(new[] { " / " }, StringSplitOptions.RemoveEmptyEntries))
                {
                    TryAdd(2, Compose(artist, CleanAlbumForQuery(part, false)), false);
                }
            }

            return queries;
        }

        // The normalised core of an album title, used to decide whether a result is the album we were
        // actually searching for (see QobuzParser). Qualifier-stripped so that MB's
        // "Vespertine (Deluxe Edition)" and Qobuz's "Vespertine" compare equal.
        public static string CoreKey(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            var normalized = Apostrophes.Replace(StripQualifiers(title), string.Empty).RemoveAccent().ToLowerInvariant();
            normalized = NonAlphanumeric.Replace(normalized, " ").Trim();

            return normalized.Length == 0 ? null : normalized;
        }

        private static string Compose(string artist, string album)
            => string.IsNullOrWhiteSpace(album) ? null : $"{artist} {album}";

        private static string CleanAlbumForQuery(string title, bool dropSubtitle)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            var album = CollapseAcronyms(StripQualifiers(title));

            if (dropSubtitle)
            {
                album = DropTrailingSubtitle(album);
            }

            return CleanForTokenSearch(SafeQueryTitle(album));
        }

        // GetQueryTitle throws on a blank title, and can't be reached with one here, but the fallback
        // queries feed it derived strings rather than the criteria's own.
        private static string SafeQueryTitle(string title)
            => string.IsNullOrWhiteSpace(title) ? null : SearchCriteriaBase.GetQueryTitle(title);

        // Final cleanup of a GetQueryTitle result before sending it to Qobuz: '+' back to spaces
        // (GetAPIUrl would encode a literal '+' as %2B), and apostrophes to spaces — Qobuz does not
        // unify apostrophe variants attached to digits (MB "Birthday Blizzard ’26" is "‘26" on Qobuz,
        // matched only by a bare "26"), while titles with real apostrophes still match without them.
        private static string CleanForTokenSearch(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return query;
            }

            var cleaned = query.Replace('+', ' ').Replace('\'', ' ');
            return RepeatedWhitespace.Replace(cleaned, " ").Trim();
        }

        // "S.H.I.E.L.D." -> "SHIELD", so it survives GetQueryTitle's punctuation stripping as one token.
        private static string CollapseAcronyms(string text)
            => string.IsNullOrWhiteSpace(text) ? text : AcronymDots.Replace(text, m => m.Value.Replace(".", string.Empty));

        private static string DropTrailingSubtitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return title;
            }

            var match = TrailingSubtitle.Match(title);
            if (!match.Success)
            {
                return title;
            }

            // Only worth it if something searchable is left.
            var head = match.Groups[1].Value.Trim();
            return head.Any(char.IsLetterOrDigit) ? head : title;
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
            stripped = SubtitleQualifier.Replace(stripped, string.Empty);
            stripped = TrailingQualifier.Replace(stripped, string.Empty);
            stripped = RepeatedWhitespace.Replace(stripped, " ").Trim(' ', ':', '-', '–', '—');

            return string.IsNullOrWhiteSpace(stripped) ? title : stripped;
        }

        private IEnumerable<IndexerRequest> GetRequests(string searchParameters, QobuzSearchContext context, bool gated)
        {
            // Lazily evaluated, when HttpIndexerBase actually reaches this query: an earlier query in
            // the same tier may already have returned the album we were looking for.
            if (gated && context is { MatchFound: true })
            {
                Logger?.Debug("Qobuz: skipping fallback query '{0}', the album was already found", searchParameters);
                yield break;
            }

            var api = Api ?? QobuzAPI.Instance;

            if (api?.Login == null)
            {
                throw new Exception("Qobuz login failed, please check your credentials in the indexer settings.");
            }

            for (var page = 0; page < MaxPages; page++)
            {
                var offset = page * PageSize;

                var data = new Dictionary<string, string>()
                {
                    ["query"] = searchParameters,
                    ["limit"] = $"{PageSize}",
                    ["offset"] = $"{offset}",
                };

                var req = new QobuzIndexerRequest(api.GetAPIUrl("/album/search", data), context, offset);
                req.HttpRequest.Method = System.Net.Http.HttpMethod.Get;
                req.HttpRequest.Headers.Add("X-App-ID", $"{api.Client.AppId}");
                req.HttpRequest.Headers.Add("X-User-Auth-Token", $"{api.Login.AuthToken}");
                yield return req;
            }
        }
    }
}
