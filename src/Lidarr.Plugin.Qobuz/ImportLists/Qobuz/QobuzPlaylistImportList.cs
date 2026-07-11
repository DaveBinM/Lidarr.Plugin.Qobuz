using System;
using System.Collections.Generic;
using System.Linq;
using FluentValidation.Results;
using NLog;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Qobuz.API;
using QobuzApiSharp.Exceptions;

namespace NzbDrone.Core.ImportLists.Qobuz
{
    public class QobuzPlaylistImportList : ImportListBase<QobuzPlaylistSettings>
    {
        private const int PageSize = 500;

        public override string Name => "Qobuz Playlist";
        public override ImportListType ListType => ImportListType.Other;
        public override TimeSpan MinRefreshInterval => TimeSpan.FromHours(12);

        public QobuzPlaylistImportList(IImportListStatusService importListStatusService,
                                       IConfigService configService,
                                       IParsingService parsingService,
                                       Logger logger)
            : base(importListStatusService, configService, parsingService, logger)
        {
        }

        public override IList<ImportListItemInfo> Fetch()
        {
            var items = new List<ImportListItemInfo>();

            if (Settings.PlaylistIds == null || !Settings.PlaylistIds.Any())
                return items;

            foreach (var playlistId in Settings.PlaylistIds)
            {
                try
                {
                    var offset = 0;
                    int total;
                    do
                    {
                        var playlist = QobuzAPI.Instance?.Client?.GetPlaylist(playlistId, withAuth: true, extra: "tracks", limit: PageSize, offset: offset);
                        if (playlist?.Tracks?.Items == null)
                        {
                            if (offset == 0)
                                _logger.Warn("Qobuz playlist {0} returned no tracks", playlistId);
                            break;
                        }

                        foreach (var track in playlist.Tracks.Items)
                        {
                            var artistName = track.Album?.Artist?.Name ?? track.Performer?.Name;
                            if (string.IsNullOrWhiteSpace(artistName))
                                continue;

                            items.Add(new ImportListItemInfo { Artist = artistName });
                        }

                        total = playlist.Tracks.Total ?? 0;
                        offset += playlist.Tracks.Items.Count;
                    }
                    while (offset < total);
                }
                catch (ApiErrorResponseException ex)
                {
                    // One unavailable playlist (deleted, private, etc.) must not stop the others.
                    _logger.Warn("Skipping Qobuz playlist {0}: API error {1} ({2}).", playlistId, ex.ResponseStatusCode, ex.ResponseReason);
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Failed to fetch Qobuz playlist {0}", playlistId);
                }
            }

            return CleanupListItems(items);
        }

        protected override void Test(List<ValidationFailure> failures)
        {
            if (Settings.PlaylistIds == null || !Settings.PlaylistIds.Any())
            {
                failures.Add(new ValidationFailure("PlaylistIds", "At least one playlist ID is required"));
                return;
            }

            var reachable = 0;
            var unreachable = new List<string>();

            foreach (var playlistId in Settings.PlaylistIds)
            {
                try
                {
                    var playlist = QobuzAPI.Instance?.Client?.GetPlaylist(playlistId, withAuth: true, extra: "tracks", limit: 1);
                    if (playlist != null)
                        reachable++;
                    else
                        unreachable.Add(playlistId);
                }
                catch (Exception ex)
                {
                    _logger.Warn("Qobuz playlist {0} is not reachable: {1}", playlistId, ex.Message);
                    unreachable.Add(playlistId);
                }
            }

            // Only fail validation when not a single playlist is reachable (a genuine auth/systemic
            // break); a few missing playlists must not disable an otherwise-working list.
            if (reachable == 0)
                failures.Add(new ValidationFailure("PlaylistIds", $"None of the configured Qobuz playlists could be fetched: {string.Join(", ", unreachable)}"));
            else if (unreachable.Any())
                _logger.Warn("Some Qobuz playlists could not be fetched and will be skipped: {0}", string.Join(", ", unreachable));
        }
    }
}
