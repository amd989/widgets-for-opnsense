using OPNsenseWidgets.Api;

namespace OPNsenseWidgets.Widgets;

public interface IWidgetDefinition
{
    string DefinitionId { get; }
    string DisplayName { get; }
    string GetTemplate(string size);
    Task<object?> FetchDataAsync(OPNsenseApiClient client, CancellationToken ct = default);
    string BuildDataJson(object? data, string size);
}
