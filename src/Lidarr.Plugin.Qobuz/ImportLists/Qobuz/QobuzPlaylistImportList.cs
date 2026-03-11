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

            if (string.IsNullOrWhiteSpace(Settings.PlaylistIds))
                return items;

            var playlistIds = Settings.PlaylistIds
                .Split(',')
                .Select(id => id.Trim())
                .Where(id => !string.IsNullOrEmpty(id));

            foreach (var playlistId in playlistIds)
            {
                try
                {
                    var offset = 0;
                    int total;
                    do
                    {
                        var playlist = QobuzAPI.Instance?.Client?.GetPlaylist(playlistId, extra: "tracks", limit: PageSize, offset: offset);
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
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Failed to fetch Qobuz playlist {0}", playlistId);
                }
            }

            return CleanupListItems(items);
        }

        protected override void Test(List<ValidationFailure> failures)
        {
            if (string.IsNullOrWhiteSpace(Settings.PlaylistIds))
            {
                failures.Add(new ValidationFailure("PlaylistIds", "At least one playlist ID is required"));
                return;
            }

            var firstId = Settings.PlaylistIds.Split(',').First().Trim();
            try
            {
                var playlist = QobuzAPI.Instance?.Client?.GetPlaylist(firstId, extra: "tracks", limit: 1);
                if (playlist == null)
                    failures.Add(new ValidationFailure("PlaylistIds", $"Could not find Qobuz playlist with ID: {firstId}"));
            }
            catch (Exception ex)
            {
                failures.Add(new ValidationFailure("PlaylistIds", $"Failed to fetch Qobuz playlist {firstId}: {ex.Message}"));
            }
        }
    }
}
