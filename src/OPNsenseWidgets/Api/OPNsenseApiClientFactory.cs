using Microsoft.Extensions.Logging;
using OPNsenseWidgets.Configuration;

namespace OPNsenseWidgets.Api;

public sealed class OPNsenseApiClientFactory : IOPNsenseApiClientFactory
{
    private readonly ISettingsManager _settingsManager;
    private readonly ICredentialStore _credentialStore;
    private readonly ILogger<OPNsenseApiClient> _logger;

    public OPNsenseApiClientFactory(ISettingsManager settingsManager, ICredentialStore credentialStore, ILogger<OPNsenseApiClient> logger)
    {
        _settingsManager = settingsManager;
        _credentialStore = credentialStore;
        _logger = logger;
    }

    public OPNsenseApiClient? Create()
    {
        var settings = _settingsManager.Load();
        var credentials = _credentialStore.LoadCredentials();

        if (!settings.IsConfigured || credentials is null)
            return null;

        return new OPNsenseApiClient(
            settings.HostUrl,
            credentials.Value.ApiKey,
            credentials.Value.ApiSecret,
            settings.AllowSelfSignedCertificates,
            _logger);
    }
}
