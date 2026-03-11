using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.ImportLists.Qobuz
{
    public class QobuzPlaylistSettingsValidator : AbstractValidator<QobuzPlaylistSettings>
    {
        public QobuzPlaylistSettingsValidator()
        {
            RuleFor(c => c.PlaylistIds).NotEmpty();
        }
    }

    public class QobuzPlaylistSettings : IImportListSettings
    {
        private static readonly QobuzPlaylistSettingsValidator Validator = new();

        public QobuzPlaylistSettings()
        {
            BaseUrl = "https://www.qobuz.com";
        }

        public string BaseUrl { get; set; }

        [FieldDefinition(0, Label = "Playlist IDs", HelpText = "Comma-separated list of Qobuz playlist IDs to import artists from")]
        public string PlaylistIds { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
