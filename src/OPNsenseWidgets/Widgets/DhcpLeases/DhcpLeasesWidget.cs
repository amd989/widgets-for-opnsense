using System.Text.Json;
using OPNsenseWidgets.Api;
using OPNsenseWidgets.Api.Models;
using OPNsenseWidgets.Provider;
using OPNsenseWidgets.Rendering;

namespace OPNsenseWidgets.Widgets.DhcpLeases;

public sealed class DhcpLeasesWidget : IWidgetDefinition
{
    public string DefinitionId => "DhcpLeases_Widget";
    public string DisplayName => "DHCP Leases";

    private static readonly Dictionary<string, int> MaxRows = new()
    {
        ["small"] = 3,
        ["medium"] = 6,
        ["large"] = 10
    };

    public string GetTemplate(string size)
    {
        var fileName = size switch
        {
            "small" => "SmallTemplate.json",
            "large" => "LargeTemplate.json",
            _ => "MediumTemplate.json"
        };
        return TemplateHelper.LoadWidgetTemplate("DhcpLeases", fileName);
    }

    public async Task<object?> FetchDataAsync(OPNsenseApiClient client, CancellationToken ct = default)
    {
        var response = await client.GetLeasesAsync(page: 1, rowCount: 100, ct: ct);
        return response.Rows
            .OrderByDescending(l => l.EffectiveLeaseStart)
            .ToList();
    }

    public string BuildDataJson(object? data, string size)
    {
        var leases = data as List<DhcpLease> ?? [];
        var maxRows = MaxRows.GetValueOrDefault(size, 6);
        var now = DateTime.Now;
        var displayLeases = leases.Take(maxRows).ToList();

        var newCount = leases.Count(l => (now - l.EffectiveLeaseStart).TotalMinutes < 5);
        var activeCount = leases.Count(l => (now - l.EffectiveLeaseStart).TotalHours < 1);
        var reservedCount = leases.Count(l => l.IsReserved == "1");

        var mapped = displayLeases.Select(l =>
        {
            var age = now - l.EffectiveLeaseStart;
            var status = l.IsReserved == "1" ? "reserved"
                : age.TotalMinutes < 5 ? "new"
                : age.TotalHours < 1 ? "active"
                : "stale";

            var vendor = TruncateVendor(l.EffectiveManufacturer);
            var hasHostname = !string.IsNullOrWhiteSpace(l.Hostname);
            var metaParts = new List<string>();
            if (hasHostname) metaParts.Add(l.Address);
            if (!string.IsNullOrEmpty(vendor)) metaParts.Add(vendor);
            if (l.IsReserved == "1") metaParts.Add("rsv");

            return new
            {
                hostname = hasHostname ? l.Hostname : l.Address,
                address = l.Address,
                mac = l.EffectiveMac,
                vendor,
                meta = string.Join(" \u00b7 ", metaParts),
                time = FormatRelativeTime(l.EffectiveLeaseStart, now),
                status,
                isNew = status == "new",
                isReserved = l.IsReserved == "1"
            };
        }).ToList();

        // Split into two columns for medium/large card grid
        var col1 = mapped.Where((_, i) => i % 2 == 0).ToList();
        var col2 = mapped.Where((_, i) => i % 2 == 1).ToList();

        var result = new
        {
            totalLeases = leases.Count,
            displayedLeases = displayLeases.Count,
            newCount,
            activeCount,
            reservedCount,
            lastRefresh = now.ToString("h:mm tt"),
            leases = mapped,
            col1,
            col2,
            sparkline = BuildSparkline(leases, now)
        };

        return JsonSerializer.Serialize(result);
    }

    private static string FormatRelativeTime(DateTime dt, DateTime now)
    {
        if (dt == DateTime.MinValue)
            return "\u2014";

        var age = now - dt;
        if (age.TotalSeconds < 60) return "now";
        if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes}m";
        if (age.TotalHours < 24) return $"{(int)age.TotalHours}h";
        return $"{(int)age.TotalDays}d";
    }

    private static string TruncateVendor(string vendor)
    {
        if (string.IsNullOrWhiteSpace(vendor))
            return "";

        var comma = vendor.IndexOf(',');
        if (comma > 0) return vendor[..comma].Trim();

        var parts = vendor.Split(' ');
        if (parts.Length > 1 && parts[0].Length >= 3)
            return parts[0];

        return vendor;
    }

    private static string BuildSparkline(List<DhcpLease> leases, DateTime now)
    {
        var buckets = new int[24];

        foreach (var lease in leases)
        {
            var start = lease.EffectiveLeaseStart;
            if (start == DateTime.MinValue) continue;

            var age = now - start;
            if (age.TotalHours >= 0 && age.TotalHours < 24)
            {
                var bucket = (int)age.TotalHours;
                if (bucket < 24) buckets[bucket]++;
            }
        }

        // Reverse so index 0 = 24h ago, index 23 = now
        Array.Reverse(buckets);

        return SparklineRenderer.Render(buckets);
    }
}
