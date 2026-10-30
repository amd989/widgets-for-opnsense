using System.Text.Json.Serialization;

namespace OPNsenseWidgets.Api.Models;

public sealed class DhcpLease
{
    // Shared fields
    [JsonPropertyName("address")]
    public string Address { get; set; } = string.Empty;

    [JsonPropertyName("hostname")]
    public string Hostname { get; set; } = string.Empty;

    [JsonPropertyName("if")]
    public string Interface { get; set; } = string.Empty;

    [JsonPropertyName("if_descr")]
    public string InterfaceDescription { get; set; } = string.Empty;

    // ISC fields
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("mac")]
    public string Mac { get; set; } = string.Empty;

    [JsonPropertyName("starts")]
    public string Starts { get; set; } = string.Empty;

    [JsonPropertyName("ends")]
    public string Ends { get; set; } = string.Empty;

    [JsonPropertyName("descr")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("man")]
    public string Manufacturer { get; set; } = string.Empty;

    // KEA fields
    [JsonPropertyName("hwaddr")]
    public string HwAddr { get; set; } = string.Empty;

    [JsonPropertyName("mac_info")]
    public string MacInfo { get; set; } = string.Empty;

    [JsonPropertyName("expire")]
    public string Expire { get; set; } = string.Empty;

    [JsonPropertyName("valid_lifetime")]
    public string ValidLifetime { get; set; } = string.Empty;

    [JsonPropertyName("if_name")]
    public string InterfaceName { get; set; } = string.Empty;

    [JsonPropertyName("is_reserved")]
    public string IsReserved { get; set; } = string.Empty;

    /// <summary>
    /// Has a fixed address: the API's own flag, an ISC static mapping, or a KEA reservation
    /// (set by DhcpLeasesWidget from the reservations API, since KEA's lease list doesn't flag them).
    /// </summary>
    [JsonIgnore]
    public bool Reserved { get; set; }

    [JsonIgnore]
    public bool ReservedByApi =>
        IsReserved is "1" or "true" or "True" ||
        Type.Equals("static", StringComparison.OrdinalIgnoreCase);

    // Normalized accessors — prefer KEA fields if ISC fields are empty
    [JsonIgnore]
    public string EffectiveMac => !string.IsNullOrEmpty(Mac) ? Mac : HwAddr;

    [JsonIgnore]
    public string EffectiveManufacturer => !string.IsNullOrEmpty(Manufacturer) ? Manufacturer : MacInfo;

    [JsonIgnore]
    public DateTime EffectiveLeaseStart
    {
        get
        {
            // ISC: has a starts field with date string
            if (!string.IsNullOrEmpty(Starts))
            {
                string[] formats = [
                    "yyyy-MM-dd HH:mm:ss",
                    "yyyy/MM/dd HH:mm:ss",
                    "MM/dd/yyyy HH:mm:ss"
                ];
                if (DateTime.TryParseExact(Starts, formats,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var dt))
                    return dt;
                if (DateTime.TryParse(Starts, out dt))
                    return dt;
            }

            // KEA: compute start from expire - valid_lifetime
            if (!string.IsNullOrEmpty(Expire) &&
                long.TryParse(Expire, out var expireEpoch) &&
                long.TryParse(ValidLifetime, out var lifetime))
            {
                return DateTimeOffset.FromUnixTimeSeconds(expireEpoch - lifetime).LocalDateTime;
            }

            // KEA: expire only
            if (!string.IsNullOrEmpty(Expire) && long.TryParse(Expire, out expireEpoch))
            {
                return DateTimeOffset.FromUnixTimeSeconds(expireEpoch).LocalDateTime;
            }

            return DateTime.MinValue;
        }
    }
}
