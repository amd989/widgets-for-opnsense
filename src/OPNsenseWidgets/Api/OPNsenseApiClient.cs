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

            using var doc = JsonDocument.Parse(json);
            var result = new Dictionary<string, InterfaceStats>();

            if (doc.RootElement.TryGetProperty("statistics", out var stats))
            {
                foreach (var iface in stats.EnumerateObject())
                {
                    var obj = iface.Value;
                    result[iface.Name] = new InterfaceStats
                    {
                        DeviceName = iface.Name,
                        Name = GetJsonString(obj, "name"),
                        BytesReceived = GetJsonLong(obj, "bytes received"),
                        BytesTransmitted = GetJsonLong(obj, "bytes transmitted"),
                        PacketsReceived = GetJsonLong(obj, "input packets"),
                        PacketsTransmitted = GetJsonLong(obj, "output packets")
                    };
                }
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

    private static string GetJsonString(JsonElement obj, string property)
    {
        if (obj.TryGetProperty(property, out var val))
            return val.ValueKind == JsonValueKind.String ? val.GetString() ?? "" : val.ToString();
        return "";
    }

    private static long GetJsonLong(JsonElement obj, string property)
    {
        if (!obj.TryGetProperty(property, out var val)) return 0;
        return val.ValueKind switch
        {
            JsonValueKind.Number => val.GetInt64(),
            JsonValueKind.String when long.TryParse(val.GetString(), out var n) => n,
            _ => 0
        };
    }

    public void Dispose() => _httpClient.Dispose();
}

public class OPNsenseAuthException(string message, Exception? inner = null)
    : Exception(message, inner);

public class OPNsenseConnectionException(string message, Exception? inner = null)
    : Exception(message, inner);

public class OPNsenseCertificateException(string message, Exception? inner = null)
    : Exception(message, inner);
