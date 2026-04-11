using System.Text.Json.Serialization;

namespace OPNsenseWidgets.Configuration;

public sealed class WidgetSettings
{
    [JsonPropertyName("hostUrl")]
    public string HostUrl { get; set; } = string.Empty;

    [JsonPropertyName("refreshIntervalSeconds")]
    public int RefreshIntervalSeconds { get; set; } = 30;

    [JsonPropertyName("allowSelfSignedCertificates")]
    public bool AllowSelfSignedCertificates { get; set; } = true;

    [JsonPropertyName("dhcpBackend")]
    public string DhcpBackend { get; set; } = "auto";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(HostUrl);
}
