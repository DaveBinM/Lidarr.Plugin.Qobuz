using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.ImportLists.Qobuz
{
    public class QobuzFavouritesSettings : IImportListSettings
    {
        public QobuzFavouritesSettings()
        {
            BaseUrl = "https://www.qobuz.com";
        }

        public string BaseUrl { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult();
        }
    }
}
