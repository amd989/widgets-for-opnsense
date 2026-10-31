namespace OPNsenseWidgets.Api;

public interface IOPNsenseApiClientFactory
{
    /// <summary>
    /// Creates an API client using the current settings and credentials.
    /// Returns null if not configured or credentials are missing.
    /// </summary>
    OPNsenseApiClient? Create();
}
