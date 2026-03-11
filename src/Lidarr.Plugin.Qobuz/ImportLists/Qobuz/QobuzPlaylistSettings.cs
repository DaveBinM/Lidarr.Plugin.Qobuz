using System;
using System.Collections.Generic;
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
            PlaylistIds = Array.Empty<string>();
        }

        public string BaseUrl { get; set; }

        [FieldDefinition(0, Label = "Playlist IDs", Type = FieldType.Tag, HelpText = "One or more Qobuz playlist IDs to import artists from")]
        public IEnumerable<string> PlaylistIds { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
