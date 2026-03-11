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
    public class QobuzFavouriteArtistsImportList : ImportListBase<QobuzFavouritesSettings>
    {
        private const int PageSize = 500;

        public override string Name => "Qobuz Favourite Artists";
        public override ImportListType ListType => ImportListType.Other;
        public override TimeSpan MinRefreshInterval => TimeSpan.FromHours(12);

        public QobuzFavouriteArtistsImportList(IImportListStatusService importListStatusService,
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
                    var favourites = QobuzAPI.Instance?.Client?.GetUserFavorites(null, type: "artists", limit: PageSize, offset: offset);
                    if (favourites?.Artists?.Items == null)
                        break;

                    foreach (var artist in favourites.Artists.Items)
                    {
                        if (string.IsNullOrWhiteSpace(artist.Name))
                            continue;

                        items.Add(new ImportListItemInfo { Artist = artist.Name });
                    }

                    total = favourites.Artists.Total ?? 0;
                    offset += favourites.Artists.Items.Count;
                }
                while (offset < total);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to fetch Qobuz favourite artists");
            }

            return CleanupListItems(items);
        }

        protected override void Test(List<ValidationFailure> failures)
        {
            try
            {
                var favourites = QobuzAPI.Instance?.Client?.GetUserFavorites(null, type: "artists", limit: 1);
                if (favourites == null)
                    failures.Add(new ValidationFailure(string.Empty, "Failed to connect to Qobuz. Ensure the download client or indexer is configured and authenticated."));
            }
            catch (Exception ex)
            {
                failures.Add(new ValidationFailure(string.Empty, $"Failed to fetch Qobuz favourite artists: {ex.Message}"));
            }
        }
    }
}
