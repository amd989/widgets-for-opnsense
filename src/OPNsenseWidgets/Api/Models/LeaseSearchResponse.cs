using System.Text.Json.Serialization;

namespace OPNsenseWidgets.Api.Models;

public sealed class LeaseSearchResponse
{
    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("rowCount")]
    public int RowCount { get; set; }

    [JsonPropertyName("current")]
    public int Current { get; set; }

    [JsonPropertyName("rows")]
    public List<DhcpLease> Rows { get; set; } = [];
}
