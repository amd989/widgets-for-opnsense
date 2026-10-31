using AdysTech.CredentialManager;
using OPNsenseWidgets.Configuration;

namespace OPNsenseWidgets.Platform;

public sealed class WindowsCredentialStore : ICredentialStore
{
    private const string TargetName = "OPNsenseWidgets/ApiCredentials";

    public void SaveCredentials(string apiKey, string apiSecret)
    {
        var credential = new System.Net.NetworkCredential(apiKey, apiSecret);
        CredentialManager.SaveCredentials(TargetName, credential);
    }

    public (string ApiKey, string ApiSecret)? LoadCredentials()
    {
        try
        {
            var credential = CredentialManager.GetCredentials(TargetName);
            if (credential is null)
                return null;

            return (credential.UserName, credential.Password);
        }
        catch
        {
            return null;
        }
    }

    public void DeleteCredentials()
    {
        try
        {
            CredentialManager.RemoveCredentials(TargetName);
        }
        catch
        {
            // Ignore if credentials don't exist
        }
    }
}
