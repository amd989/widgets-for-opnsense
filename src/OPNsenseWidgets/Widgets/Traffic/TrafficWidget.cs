using System.Text.Json;
using OPNsenseWidgets.Api;
using OPNsenseWidgets.Api.Models;
using OPNsenseWidgets.Provider;
using OPNsenseWidgets.Rendering;

namespace OPNsenseWidgets.Widgets.Traffic;

public sealed class TrafficWidget : IWidgetDefinition
{
    public string DefinitionId => "Traffic_Widget";
    public string DisplayName => "Traffic";

    private const int MaxHistory = 30;

    private readonly List<TrafficSample> _history = [];
    private long _prevBytesIn;
    private long _prevBytesOut;
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

        // Auto-detect WAN: reuse previously found device, then match name containing "WAN", then first
        InterfaceStats? wan = null;

        if (!string.IsNullOrEmpty(_wanDeviceName) && stats.TryGetValue(_wanDeviceName, out wan))
        {
            // Reuse previously detected interface
        }
        else
        {
            wan = stats.Values.FirstOrDefault(s =>
                      s.Name.Contains("WAN", StringComparison.OrdinalIgnoreCase))
                  ?? stats.Values.First();
            _wanDeviceName = wan.DeviceName;
        }

        var now = DateTime.Now;
        double inBps = 0, outBps = 0;

        if (_prevTimestamp != default)
        {
            var elapsed = (now - _prevTimestamp).TotalSeconds;
            if (elapsed > 0)
            {
                var deltaIn = wan.BytesReceived - _prevBytesIn;
                var deltaOut = wan.BytesTransmitted - _prevBytesOut;

                // Handle counter reset (reboot, overflow)
                if (deltaIn < 0) deltaIn = wan.BytesReceived;
                if (deltaOut < 0) deltaOut = wan.BytesTransmitted;

                inBps = deltaIn * 8.0 / elapsed;
                outBps = deltaOut * 8.0 / elapsed;

                _history.Add(new TrafficSample(now, inBps, outBps));
                if (_history.Count > MaxHistory)
                    _history.RemoveAt(0);
            }
        }

        _prevBytesIn = wan.BytesReceived;
        _prevBytesOut = wan.BytesTransmitted;
        _prevTimestamp = now;

        return new TrafficData
        {
            History = [.. _history],
            InterfaceName = wan.Name,
            CurrentInBps = inBps,
            CurrentOutBps = outBps,
            TotalBytesIn = wan.BytesReceived,
            TotalBytesOut = wan.BytesTransmitted,
            HasData = _history.Count > 0
        };
    }

    public string BuildDataJson(object? data, string size)
    {
        var traffic = data as TrafficData;
        var now = DateTime.Now;

        if (traffic is null || !traffic.HasData)
        {
            return JsonSerializer.Serialize(new
            {
                sampleCount = 0,
                downloadSpeed = "\u2014",
                uploadSpeed = "\u2014",
                interfaceName = traffic?.InterfaceName ?? "\u2014",
                lastRefresh = now.ToString("h:mm tt"),
                totalIn = "\u2014",
                totalOut = "\u2014",
                chartImage = ""
            });
        }

        var chartHeight = size switch
        {
            "small" => 32,
            "large" => 140,
            _ => 80
        };

        var chartImage = traffic.History.Count >= 2
            ? TrafficChartRenderer.Render(
                traffic.History.Select(s => s.InBitsPerSecond).ToArray(),
                traffic.History.Select(s => s.OutBitsPerSecond).ToArray(),
                width: 560,
                height: chartHeight)
            : "";

        var result = new
        {
            sampleCount = traffic.History.Count,
            downloadSpeed = FormatSpeed(traffic.CurrentInBps),
            uploadSpeed = FormatSpeed(traffic.CurrentOutBps),
            interfaceName = traffic.InterfaceName,
            totalIn = FormatBytes(traffic.TotalBytesIn),
            totalOut = FormatBytes(traffic.TotalBytesOut),
            lastRefresh = now.ToString("h:mm tt"),
            chartImage
        };

        return JsonSerializer.Serialize(result);
    }

    private static string FormatSpeed(double bitsPerSecond)
    {
        return bitsPerSecond switch
        {
            >= 1_000_000_000 => $"{bitsPerSecond / 1_000_000_000:F2} Gbps",
            >= 1_000_000 => $"{bitsPerSecond / 1_000_000:F1} Mbps",
            >= 1_000 => $"{bitsPerSecond / 1_000:F0} Kbps",
            _ => $"{bitsPerSecond:F0} bps"
        };
    }

    private static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            >= 1_073_741_824L => $"{(double)bytes / 1_073_741_824:F1} GB",
            >= 1_048_576L => $"{(double)bytes / 1_048_576:F1} MB",
            >= 1024L => $"{(double)bytes / 1024:F0} KB",
            _ => $"{bytes} B"
        };
    }
}
