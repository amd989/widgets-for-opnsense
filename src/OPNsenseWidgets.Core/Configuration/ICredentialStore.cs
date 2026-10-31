namespace OPNsenseWidgets.Configuration;

public interface ICredentialStore
{
    void SaveCredentials(string apiKey, string apiSecret);
    (string ApiKey, string ApiSecret)? LoadCredentials();
    void DeleteCredentials();
}
