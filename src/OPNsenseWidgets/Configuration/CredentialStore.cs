using AdysTech.CredentialManager;

namespace OPNsenseWidgets.Configuration;

public static class CredentialStore
{
    private const string TargetName = "OPNsenseWidgets/ApiCredentials";

    public static void SaveCredentials(string apiKey, string apiSecret)
    {
        var credential = new System.Net.NetworkCredential(apiKey, apiSecret);
        CredentialManager.SaveCredentials(TargetName, credential);
    }

    public static (string ApiKey, string ApiSecret)? LoadCredentials()
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

    public static void DeleteCredentials()
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
