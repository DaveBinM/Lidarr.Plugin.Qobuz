using System;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download.Clients.Qobuz;
using NzbDrone.Core.Parser;
using NzbDrone.Plugin.Qobuz.API;

namespace NzbDrone.Core.Indexers.Qobuz
{
    public class Qobuz : HttpIndexerBase<QobuzIndexerSettings>
    {
        public override string Name => "Qobuz";
        public override string Protocol => nameof(QobuzDownloadProtocol);
        public override bool SupportsRss => false;
        public override bool SupportsSearch => true;
        public override int PageSize => 100;
        public override TimeSpan RateLimit => new TimeSpan(0);

        private readonly IQobuzProxy _qobuzProxy;

        public Qobuz(IQobuzProxy qobuzProxy,
            IHttpClient httpClient,
            IIndexerStatusService indexerStatusService,
            IConfigService configService,
            IParsingService parsingService,
            Logger logger)
            : base(httpClient, indexerStatusService, configService, parsingService, logger)
        {
            _qobuzProxy = qobuzProxy;
        }

        public override IIndexerRequestGenerator GetRequestGenerator()
        {
            bool ep = !string.IsNullOrEmpty(Settings.Email) && !string.IsNullOrEmpty(Settings.MD5Password);
            bool it = !string.IsNullOrEmpty(Settings.UserID) && !string.IsNullOrEmpty(Settings.UserAuthToken);
            if (ep || it)
            {
                bool forceRecreate = (Settings.AppID != QobuzAPI.Instance?.Client?.AppId) || (Settings.AppSecret != QobuzAPI.Instance?.Client?.AppSecret)
                                  || (Settings.Email != QobuzAPI.Instance?.Login?.User?.Email) || (Settings.MD5Password != QobuzAPI.Instance?.LastPassword)
                                  || (Settings.UserID != QobuzAPI.Instance?.Login?.User?.Id.ToString()) || (Settings.UserAuthToken != QobuzAPI.Instance?.Login?.AuthToken);

                QobuzAPI.Initialize(_logger, Settings.AppID, Settings.AppSecret, forceRecreate);
                QobuzAPI.Instance.PickSignInFromSettings(Settings, _logger);
            }
            else
                return null;

            return new QobuzRequestGenerator()
            {
                Settings = Settings,
                Logger = _logger
            };
        }

        public override IParseIndexerResponse GetParser()
        {
            return new QobuzParser()
            {
                Settings = Settings,
                Logger = _logger
            };
        }
    }
}
