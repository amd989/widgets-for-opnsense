using OPNsenseWidgets.Api;

namespace OPNsenseWidgets.Widgets;

public interface IWidgetDefinition
{
    string DefinitionId { get; }
    string DisplayName { get; }
    /// <summary>
    /// Per-widget refresh interval in seconds. 0 means use the global setting.
    /// </summary>
    int RefreshIntervalSeconds => 0;
    string GetTemplate(string size);
    Task<object?> FetchDataAsync(OPNsenseApiClient client, CancellationToken ct = default);
    string BuildDataJson(object? data, string size);
}
