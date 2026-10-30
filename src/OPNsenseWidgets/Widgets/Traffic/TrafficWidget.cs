using System.Text.Json;
using OPNsenseWidgets.Api;
using OPNsenseWidgets.Api.Models;
using OPNsenseWidgets.Provider;
using OPNsenseWidgets.Rendering;

namespace OPNsenseWidgets.Widgets.Traffic;

public sealed class TrafficWidget : IWebWidgetDefinition
{
    public string DefinitionId => "Traffic_Widget";
    public string DisplayName => "Traffic";
    public int RefreshIntervalSeconds => 1;
    public string WebPath => "Traffic/Web/index.html";

    // One sample per second; the web page charts the last 60 s.
    private const int MaxHistory = 60;
    private const double MaxSampleGapSeconds = 5;

    /// <summary>
    /// Per-interface history and totals for the HTML page (Web/index.html), WAN first.
    /// Samples are [unixMs, inBps, outBps].
    /// </summary>
    public object BuildWebData(object? data)
    {
        var traffic = data as TrafficData;
        var updatedAt = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        if (traffic is null)
            return new { updatedAt, interfaces = Array.Empty<object>() };

        string NameOf(string key) => traffic.InterfaceNames.GetValueOrDefault(key, key);

        var interfaces = traffic.InterfaceNames.Keys
            .Union(traffic.AllHistory.Keys)
            .OrderByDescending(k => NameOf(k) == traffic.InterfaceName)
            .ThenByDescending(k => NameOf(k).Contains("WAN", StringComparison.OrdinalIgnoreCase))
            .ThenBy(k => NameOf(k), StringComparer.OrdinalIgnoreCase)
            .Select(k =>
            {
                var totals = traffic.Totals.GetValueOrDefault(k);
                var samples = traffic.AllHistory.GetValueOrDefault(k) ?? [];
                return new
                {
                    id = k,
                    name = NameOf(k),
                    isWan = NameOf(k) == traffic.InterfaceName,
                    totalIn = totals.BytesIn,
                    totalOut = totals.BytesOut,
                    samples = samples.Select(s => new[]
                    {
                        new DateTimeOffset(s.Timestamp).ToUnixTimeMilliseconds(),
                        Math.Round(s.InBitsPerSecond),
                        Math.Round(s.OutBitsPerSecond)
                    })
                };
            });

        return new { updatedAt, interfaces };
    }

    private readonly Dictionary<string, List<TrafficSample>> _allHistory = new();
    private readonly Dictionary<string, (long BytesIn, long BytesOut)> _prevBytes = new();
    private readonly Dictionary<string, string> _interfaceNames = new();
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private DateTime _prevTimestamp;
    private string _wanDeviceName = "";

    public string GetTemplate(string size)
    {
        var fileName = size switch
        {
            "small" => "SmallTemplate.json",
            "large" => "LargeTemplate.json",
            _ => "MediumTemplate.json"
        };
        return TemplateHelper.LoadWidgetTemplate("Traffic", fileName);
    }

    public async Task<object?> FetchDataAsync(OPNsenseApiClient client, CancellationToken ct = default)
    {
        var stats = await client.GetInterfaceStatisticsAsync(ct);

        if (stats.Count == 0)
            return new TrafficData
            {
                History = [],
                InterfaceName = "\u2014",
                HasData = false
            };

        await _stateLock.WaitAsync(ct);
        try
        {
        // Update friendly name cache
        foreach (var kv in stats)
            _interfaceNames[kv.Key] = kv.Value.Name;

        var now = DateTime.Now;

        if (_prevTimestamp != default)
        {
            // Polling stops while the board is closed. The first reading after that would average
            // the whole gap into one misleading point, so only use it as the new baseline.
            var elapsed = (now - _prevTimestamp).TotalSeconds;
            if (elapsed > 0 && elapsed <= MaxSampleGapSeconds)
            {
                foreach (var kv in stats)
                {
                    _prevBytes.TryGetValue(kv.Key, out var prev);
                    var deltaIn  = kv.Value.BytesReceived    - prev.BytesIn;
                    var deltaOut = kv.Value.BytesTransmitted - prev.BytesOut;
                    if (deltaIn  < 0) deltaIn  = kv.Value.BytesReceived;
                    if (deltaOut < 0) deltaOut = kv.Value.BytesTransmitted;

                    double ifaceInBps  = deltaIn  * 8.0 / elapsed;
                    double ifaceOutBps = deltaOut * 8.0 / elapsed;

                    if (!_allHistory.TryGetValue(kv.Key, out var hist))
                    {
                        hist = [];
                        _allHistory[kv.Key] = hist;
                    }
                    hist.Add(new TrafficSample(now, ifaceInBps, ifaceOutBps));
                    if (hist.Count > MaxHistory) hist.RemoveAt(0);
                }
            }
        }

        foreach (var kv in stats)
            _prevBytes[kv.Key] = (kv.Value.BytesReceived, kv.Value.BytesTransmitted);
        _prevTimestamp = now;

        // Auto-detect WAN for current speed display and medium/small charts
        InterfaceStats? wan = null;
        if (!string.IsNullOrEmpty(_wanDeviceName) && stats.TryGetValue(_wanDeviceName, out wan)) { }
        else
        {
            wan = stats.Values.FirstOrDefault(s =>
                      s.Name.Contains("WAN", StringComparison.OrdinalIgnoreCase))
                  ?? stats.Values.First();
            _wanDeviceName = wan.DeviceName;
        }

        double inBps = 0, outBps = 0;
        if (_allHistory.TryGetValue(_wanDeviceName, out var wanHist) && wanHist.Count > 0)
        {
            inBps  = wanHist[^1].InBitsPerSecond;
            outBps = wanHist[^1].OutBitsPerSecond;
        }

        return new TrafficData
        {
            History       = wanHist is not null ? [.. wanHist] : [],
            InterfaceName = wan.Name,
            CurrentInBps  = inBps,
            CurrentOutBps = outBps,
            TotalBytesIn  = wan.BytesReceived,
            TotalBytesOut = wan.BytesTransmitted,
            HasData       = _allHistory.Values.Any(h => h.Count > 0),
            AllHistory    = _allHistory.ToDictionary(kv => kv.Key, kv => kv.Value.ToList()),
            InterfaceNames = new Dictionary<string, string>(_interfaceNames),
            Totals        = stats.ToDictionary(kv => kv.Key, kv => (kv.Value.BytesReceived, kv.Value.BytesTransmitted))
        };
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public string BuildDataJson(object? data, string size)
    {
        var traffic = data as TrafficData;
        var now = DateTime.Now;

        if (traffic is null || !traffic.HasData)
        {
            return JsonSerializer.Serialize(new
            {
                sampleCount   = 0,
                downloadSpeed = "\u2014",
                uploadSpeed   = "\u2014",
                interfaceName = traffic?.InterfaceName ?? "\u2014",
                lastRefresh   = now.ToString("h:mm tt"),
                totalIn       = "\u2014",
                totalOut      = "\u2014",
                chartImage    = "",
                downloadChart = "",
                uploadChart   = ""
            });
        }

        var sampleCount = traffic.History.Count;

        if (size == "large")
        {
            // Sort: WAN-named interfaces first, then alphabetical
            var orderedKeys = traffic.AllHistory.Keys
                .OrderByDescending(k =>
                    traffic.InterfaceNames.GetValueOrDefault(k, k)
                           .Contains("WAN", StringComparison.OrdinalIgnoreCase))
                .ThenBy(k => traffic.InterfaceNames.GetValueOrDefault(k, k))
                .ToList();

            var downloadSeries = orderedKeys
                .Where(k => traffic.AllHistory[k].Count >= 2)
                .Select(k => (
                    Label:  traffic.InterfaceNames.GetValueOrDefault(k, k),
                    Values: traffic.AllHistory[k].Select(s => s.InBitsPerSecond).ToArray()))
                .ToList();

            var uploadSeries = orderedKeys
                .Where(k => traffic.AllHistory[k].Count >= 2)
                .Select(k => (
                    Label:  traffic.InterfaceNames.GetValueOrDefault(k, k),
                    Values: traffic.AllHistory[k].Select(s => s.OutBitsPerSecond).ToArray()))
                .ToList();

            var downloadChart = downloadSeries.Count > 0
                ? TrafficChartRenderer.RenderMultiSeries(downloadSeries, width: 450, height: 230)
                : "";
            var uploadChart = uploadSeries.Count > 0
                ? TrafficChartRenderer.RenderMultiSeries(uploadSeries, width: 450, height: 230)
                : "";

            var legendLabels = orderedKeys
                .Select(k => traffic.InterfaceNames.GetValueOrDefault(k, k))
                .ToList();
            var legendImage = legendLabels.Count > 1
                ? TrafficChartRenderer.RenderLegend(legendLabels, width: 280)
                : "";

            return JsonSerializer.Serialize(new
            {
                sampleCount,
                downloadSpeed = FormatSpeed(traffic.CurrentInBps),
                uploadSpeed   = FormatSpeed(traffic.CurrentOutBps),
                interfaceName = traffic.InterfaceName,
                totalIn       = FormatBytes(traffic.TotalBytesIn),
                totalOut      = FormatBytes(traffic.TotalBytesOut),
                lastRefresh   = now.ToString("h:mm tt"),
                chartImage    = "",
                downloadChart,
                uploadChart,
                legendImage
            });
        }

        if (size == "small")
        {
            var chartImage = traffic.History.Count >= 2
                ? TrafficChartRenderer.Render(
                    traffic.History.Select(s => s.InBitsPerSecond).ToArray(),
                    traffic.History.Select(s => s.OutBitsPerSecond).ToArray(),
                    width: 450, height: 112)
                : "";

            return JsonSerializer.Serialize(new
            {
                sampleCount,
                downloadSpeed = FormatSpeed(traffic.CurrentInBps),
                uploadSpeed   = FormatSpeed(traffic.CurrentOutBps),
                interfaceName = traffic.InterfaceName,
                lastRefresh   = now.ToString("h:mm tt"),
                chartImage,
                lanChart      = "",
                lanSampleCount = 0,
                lanInterfaceName = ""
            });
        }

        // medium — WAN chart + LAN chart
        var wanChart = traffic.History.Count >= 2
            ? TrafficChartRenderer.Render(
                traffic.History.Select(s => s.InBitsPerSecond).ToArray(),
                traffic.History.Select(s => s.OutBitsPerSecond).ToArray(),
                width: 450, height: 140)
            : "";

        // Find LAN: prefer name containing "LAN", else first non-WAN interface
        var wanKey = traffic.AllHistory.Keys.FirstOrDefault(k =>
            traffic.InterfaceNames.GetValueOrDefault(k, k) == traffic.InterfaceName);

        var lanKey = traffic.AllHistory.Keys
            .Where(k => k != wanKey)
            .OrderByDescending(k => traffic.InterfaceNames.GetValueOrDefault(k, k)
                                           .Contains("LAN", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();

        var lanHistory = lanKey is not null
            ? traffic.AllHistory.GetValueOrDefault(lanKey)
            : null;
        var lanInterfaceName = lanKey is not null
            ? traffic.InterfaceNames.GetValueOrDefault(lanKey, lanKey)
            : "";
        var lanChart = lanHistory?.Count >= 2
            ? TrafficChartRenderer.Render(
                lanHistory.Select(s => s.InBitsPerSecond).ToArray(),
                lanHistory.Select(s => s.OutBitsPerSecond).ToArray(),
                width: 450, height: 140)
            : "";
        var lanSampleCount = lanHistory?.Count ?? 0;

        return JsonSerializer.Serialize(new
        {
            sampleCount,
            downloadSpeed    = FormatSpeed(traffic.CurrentInBps),
            uploadSpeed      = FormatSpeed(traffic.CurrentOutBps),
            interfaceName    = traffic.InterfaceName,
            lastRefresh      = now.ToString("h:mm tt"),
            chartImage       = wanChart,
            lanChart,
            lanSampleCount,
            lanInterfaceName
        });
    }

    private static string FormatSpeed(double bitsPerSecond)
    {
        return bitsPerSecond switch
        {
            >= 1_000_000_000 => $"{bitsPerSecond / 1_000_000_000:F2} Gbps",
            >= 1_000_000     => $"{bitsPerSecond / 1_000_000:F1} Mbps",
            >= 1_000         => $"{bitsPerSecond / 1_000:F0} Kbps",
            _                => $"{bitsPerSecond:F0} bps"
        };
    }

    private static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            >= 1_099_511_627_776L => $"{(double)bytes / 1_099_511_627_776:F2} TB",
            >= 1_073_741_824L => $"{(double)bytes / 1_073_741_824:F1} GB",
            >= 1_048_576L     => $"{(double)bytes / 1_048_576:F1} MB",
            >= 1024L          => $"{(double)bytes / 1024:F0} KB",
            _                 => $"{bytes} B"
        };
    }
}
