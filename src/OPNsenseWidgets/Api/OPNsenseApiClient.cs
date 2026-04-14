using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OPNsenseWidgets.Api.Models;

namespace OPNsenseWidgets.Api;

public sealed class OPNsenseApiClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;

    private static readonly string[] LeaseEndpoints =
    [
        "/api/dhcpv4/leases/searchLease",
        "/api/kea/leases4/search"
    ];

    public OPNsenseApiClient(string baseUrl, string apiKey, string apiSecret, bool allowSelfSigned = true)
    {
        _baseUrl = baseUrl.TrimEnd('/');

        var handler = new HttpClientHandler();
        if (allowSelfSigned)
        {
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{apiKey}:{apiSecret}"));
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", credentials);
    }

    public async Task<LeaseSearchResponse> GetLeasesAsync(
        int page = 1,
        int rowCount = 50,
        CancellationToken ct = default)
    {
        Exception? lastException = null;

        foreach (var endpoint in LeaseEndpoints)
        {
            var url = $"{_baseUrl}{endpoint}?current={page}&rowCount={rowCount}";

            try
            {
                var response = await _httpClient.GetAsync(url, ct);

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    lastException = new HttpRequestException($"Endpoint not found: {endpoint}");
                    continue; // Try next endpoint (ISC -> KEA fallback)
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized ||
                    response.StatusCode == HttpStatusCode.Forbidden)
                {
                    throw new OPNsenseAuthException(
                        "Authentication failed. Check your API key and secret.");
                }

                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(ct);
                Provider.OPNsenseWidgetProvider.Log($"GetLeasesAsync: endpoint={endpoint} status={response.StatusCode} bodyLength={json.Length}");
                Provider.OPNsenseWidgetProvider.Log($"GetLeasesAsync: body preview={json[..Math.Min(500, json.Length)]}");
                var result = JsonSerializer.Deserialize<LeaseSearchResponse>(json);
                Provider.OPNsenseWidgetProvider.Log($"GetLeasesAsync: deserialized total={result?.Total} rowCount={result?.RowCount} rows={result?.Rows?.Count}");

                // If this endpoint returned empty rows, try the next one (ISC disabled → try KEA)
                if ((result?.Rows?.Count ?? 0) == 0)
                {
                    Provider.OPNsenseWidgetProvider.Log($"GetLeasesAsync: 0 rows from {endpoint}, trying next");
                    lastException = new HttpRequestException($"No leases from {endpoint}");
                    continue;
                }

                return result ?? new LeaseSearchResponse();
            }
            catch (OPNsenseAuthException)
            {
                throw;
            }
            catch (HttpRequestException ex) when (
                ex.InnerException is System.Security.Authentication.AuthenticationException)
            {
                throw new OPNsenseCertificateException(
                    "SSL certificate error. Enable 'Allow self-signed certificates' in settings.", ex);
            }
            catch (HttpRequestException ex) when (
                ex.InnerException is System.Net.Sockets.SocketException)
            {
                throw new OPNsenseConnectionException(
                    $"Cannot reach {_baseUrl}. Check the URL and ensure OPNsense is running.", ex);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new OPNsenseConnectionException(
                    "Request timed out. OPNsense may be busy or unreachable.", ex);
            }
            catch (HttpRequestException ex)
            {
                lastException = ex;
                continue;
            }
        }

        throw new OPNsenseConnectionException(
            "DHCP lease API not available. Check your OPNsense version and ensure DHCP is configured.",
            lastException);
    }

    public async Task<Dictionary<string, InterfaceStats>> GetInterfaceStatisticsAsync(
        CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api/diagnostics/interface/getInterfaceStatistics";

        try
        {
            var response = await _httpClient.GetAsync(url, ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized ||
                response.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new OPNsenseAuthException(
                    "Authentication failed. Check your API key and secret.");
            }

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            Provider.OPNsenseWidgetProvider.Log(
                $"GetInterfaceStatisticsAsync: bodyLength={json.Length}");
            Provider.OPNsenseWidgetProvider.Log(
                $"GetInterfaceStatisticsAsync: body preview={json[..Math.Min(800, json.Length)]}");

            using var doc = JsonDocument.Parse(json);

            // OPNsense response shape: "statistics" is an object where each key is
            // "[FriendlyName] (device) / address" and value holds counters per-address.
            // One device may have multiple entries (MAC row + per-IP rows). The MAC row
            // carries the aggregate interface counters — dedupe by device, pick max.
            var raw = new List<(string OuterKey, InterfaceStats Stats)>();

            if (doc.RootElement.TryGetProperty("statistics", out var stats) &&
                stats.ValueKind == JsonValueKind.Object)
            {
                foreach (var iface in stats.EnumerateObject())
                {
                    var deviceName = GetJsonString(iface.Value, "name");
                    if (string.IsNullOrEmpty(deviceName))
                        deviceName = iface.Name;

                    if (IsSystemInterface(deviceName))
                        continue;

                    var friendly = ExtractFriendlyFromKey(iface.Name);
                    raw.Add((iface.Name, ExtractStats(iface.Value, deviceName, friendly)));
                }
            }

            var result = raw
                .GroupBy(r => r.Stats.DeviceName)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderByDescending(r =>
                            r.Stats.BytesReceived + r.Stats.BytesTransmitted)
                         .First().Stats);

            foreach (var kv in result)
            {
                Provider.OPNsenseWidgetProvider.Log(
                    $"  iface={kv.Key} name='{kv.Value.Name}' in={kv.Value.BytesReceived} out={kv.Value.BytesTransmitted}");
            }

            return result;
        }
        catch (OPNsenseAuthException) { throw; }
        catch (HttpRequestException ex) when (
            ex.InnerException is System.Security.Authentication.AuthenticationException)
        {
            throw new OPNsenseCertificateException(
                "SSL certificate error. Enable 'Allow self-signed certificates' in settings.", ex);
        }
        catch (HttpRequestException ex) when (
            ex.InnerException is System.Net.Sockets.SocketException)
        {
            throw new OPNsenseConnectionException(
                $"Cannot reach {_baseUrl}. Check the URL and ensure OPNsense is running.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new OPNsenseConnectionException(
                "Request timed out. OPNsense may be busy or unreachable.", ex);
        }
    }

    private static InterfaceStats ExtractStats(JsonElement obj, string deviceName, string friendly)
    {
        if (string.IsNullOrEmpty(friendly))
            friendly = FirstNonEmptyString(obj, "descr", "description");
        if (string.IsNullOrEmpty(friendly))
        {
            var n = GetJsonString(obj, "name");
            if (!string.IsNullOrEmpty(n) && !n.Equals(deviceName, StringComparison.OrdinalIgnoreCase))
                friendly = n;
        }
        if (string.IsNullOrEmpty(friendly))
            friendly = deviceName;

        return new InterfaceStats
        {
            DeviceName = deviceName,
            Name = friendly,
            BytesReceived = FirstLong(obj,
                "received-bytes", "bytes received", "bytes-received", "bytes_received",
                "bytes in", "bytes-in", "bytes_in", "rx_bytes"),
            BytesTransmitted = FirstLong(obj,
                "sent-bytes", "bytes transmitted", "bytes-transmitted", "bytes_transmitted",
                "bytes out", "bytes-out", "bytes_out", "tx_bytes"),
            PacketsReceived = FirstLong(obj,
                "received-packets", "input packets", "packets received", "packets-received",
                "packets_received", "rx_packets"),
            PacketsTransmitted = FirstLong(obj,
                "sent-packets", "output packets", "packets transmitted", "packets-transmitted",
                "packets_transmitted", "tx_packets")
        };
    }

    private static readonly string[] SystemInterfacePrefixes =
        ["enc", "pfsync", "pflog", "lo", "faith", "tap", "tun", "ipsec", "gif", "gre", "stf", "bridge"];

    private static bool IsSystemInterface(string deviceName)
    {
        foreach (var prefix in SystemInterfacePrefixes)
            if (deviceName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static string ExtractFriendlyFromKey(string outerKey)
    {
        // Format: "[WAN] (vmx0) / 00:0c:29:a3:6d:9e" — pull "WAN" from brackets.
        var start = outerKey.IndexOf('[');
        var end = outerKey.IndexOf(']');
        if (start >= 0 && end > start + 1)
            return outerKey.Substring(start + 1, end - start - 1).Trim();
        return "";
    }

    private static string FirstNonEmptyString(JsonElement obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            var val = GetJsonString(obj, key);
            if (!string.IsNullOrWhiteSpace(val)) return val;
        }
        return "";
    }

    private static long FirstLong(JsonElement obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (obj.TryGetProperty(key, out var val))
            {
                var n = ReadLong(val);
                if (n > 0) return n;
            }
        }
        return 0;
    }

    private static long ReadLong(JsonElement val) => val.ValueKind switch
    {
        JsonValueKind.Number when val.TryGetInt64(out var n) => n,
        JsonValueKind.String when long.TryParse(val.GetString(), out var n) => n,
        _ => 0
    };

    private static string GetJsonString(JsonElement obj, string property)
    {
        if (obj.TryGetProperty(property, out var val))
            return val.ValueKind == JsonValueKind.String ? val.GetString() ?? "" : val.ToString();
        return "";
    }

    private static long GetJsonLong(JsonElement obj, string property)
    {
        if (!obj.TryGetProperty(property, out var val)) return 0;
        return ReadLong(val);
    }

    public void Dispose() => _httpClient.Dispose();
}

public class OPNsenseAuthException(string message, Exception? inner = null)
    : Exception(message, inner);

public class OPNsenseConnectionException(string message, Exception? inner = null)
    : Exception(message, inner);

public class OPNsenseCertificateException(string message, Exception? inner = null)
    : Exception(message, inner);
