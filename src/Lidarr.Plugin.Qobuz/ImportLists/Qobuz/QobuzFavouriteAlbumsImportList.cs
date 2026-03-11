using System;
using System.Collections.Generic;
using FluentValidation.Results;
using NLog;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Qobuz.API;

namespace NzbDrone.Core.ImportLists.Qobuz
{
    public class QobuzFavouriteAlbumsImportList : ImportListBase<QobuzFavouritesSettings>
    {
        private const int PageSize = 500;

        public override string Name => "Qobuz Favourite Albums";
        public override ImportListType ListType => ImportListType.Other;
        public override TimeSpan MinRefreshInterval => TimeSpan.FromHours(12);

        public QobuzFavouriteAlbumsImportList(IImportListStatusService importListStatusService,
                                              IConfigService configService,
                                              IParsingService parsingService,
                                              Logger logger)
            : base(importListStatusService, configService, parsingService, logger)
        {
        }

        public override IList<ImportListItemInfo> Fetch()
        {
            var items = new List<ImportListItemInfo>();

            try
            {
                var offset = 0;
                int total;
                do
                {
                    var favourites = QobuzAPI.Instance?.Client?.GetUserFavorites(null, type: "albums", limit: PageSize, offset: offset);
                    if (favourites?.Albums?.Items == null)
                        break;

                    foreach (var album in favourites.Albums.Items)
                    {
                        var artistName = album.Artist?.Name;
                        if (string.IsNullOrWhiteSpace(artistName))
                            continue;

                        items.Add(new ImportListItemInfo { Artist = artistName });
                    }

                    total = favourites.Albums.Total ?? 0;
                    offset += favourites.Albums.Items.Count;
                }
                while (offset < total);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to fetch Qobuz favourite albums");
            }

            return CleanupListItems(items);
        }

        protected override void Test(List<ValidationFailure> failures)
        {
            try
            {
                var favourites = QobuzAPI.Instance?.Client?.GetUserFavorites(null, type: "albums", limit: 1);
                if (favourites == null)
                    failures.Add(new ValidationFailure(string.Empty, "Failed to connect to Qobuz. Ensure the download client or indexer is configured and authenticated."));
            }
            catch (Exception ex)
            {
                failures.Add(new ValidationFailure(string.Empty, $"Failed to fetch Qobuz favourite albums: {ex.Message}"));
            }
        }
    }
}
