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

    private const int MaxPages = 5;
    private readonly List<(DateTime Time, int Count)> _history = [];

    private static readonly Dictionary<string, int> CardsPerPage = new()
    {
        ["small"] = 3,
        ["medium"] = 6,
        ["large"] = 12
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
        var cardsPerPage = CardsPerPage.GetValueOrDefault(size, 6);
        var now = DateTime.Now;
        var maxCards = cardsPerPage * MaxPages;
        var displayLeases = leases.Take(maxCards).ToList();

        var newCount = leases.Count(l => (now - l.EffectiveLeaseStart).TotalMinutes < 5);
        var activeCount = leases.Count(l => (now - l.EffectiveLeaseStart).TotalHours < 1);
        var reservedCount = leases.Count(l => l.IsReserved == "1");

        var showMac = size == "large";
        var columns = size == "small" ? 1 : 2;
        var cardLogicalWidth = columns == 1 ? 150 : 160;

        var cardImages = displayLeases.Select(l =>
        {
            var vendor = TruncateVendor(l.EffectiveManufacturer);
            var hasHostname = !string.IsNullOrWhiteSpace(l.Hostname);
            var metaParts = new List<string>();
            if (hasHostname) metaParts.Add(l.Address);
            if (!string.IsNullOrEmpty(vendor)) metaParts.Add(vendor);
            if (l.IsReserved == "1") metaParts.Add("rsv");

            var cardData = new DeviceCardData(
                Hostname: hasHostname ? l.Hostname : l.Address,
                Meta: string.Join(" \u00b7 ", metaParts),
                Time: FormatRelativeTime(l.EffectiveLeaseStart, now),
                Mac: showMac ? l.EffectiveMac : null
            );

            return new { img = DeviceCardRenderer.RenderCard(cardData, cardLogicalWidth, showMac) };
        }).ToList();

        var pages = new List<object>();
        for (var i = 0; i < cardImages.Count; i += cardsPerPage)
        {
            var pageCards = cardImages.Skip(i).Take(cardsPerPage).ToList();
            if (columns == 1)
            {
                pages.Add(new
                {
                    col1 = pageCards.Cast<object>().ToList(),
                    col2 = new List<object>()
                });
            }
            else
            {
                pages.Add(new
                {
                    col1 = pageCards.Where((_, j) => j % 2 == 0).Cast<object>().ToList(),
                    col2 = pageCards.Where((_, j) => j % 2 == 1).Cast<object>().ToList()
                });
            }
        }

        var result = new
        {
            totalLeases = leases.Count,
            displayedLeases = displayLeases.Count,
            pageCount = pages.Count,
            newCount,
            activeCount,
            reservedCount,
            lastRefresh = now.ToString("h:mm tt"),
            pages,
            sparkline = BuildSparkline(leases.Count, now)
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

    private string BuildSparkline(int currentCount, DateTime now)
    {
        _history.Add((now, currentCount));
        _history.RemoveAll(h => (now - h.Time).TotalHours > 25);

        // buckets[0] = 24h ago, buckets[23] = now
        var buckets = new int[24];
        foreach (var (time, count) in _history)
        {
            var age = now - time;
            if (age.TotalHours >= 24) continue;
            var slot = 23 - (int)age.TotalHours;
            buckets[slot] = Math.Max(buckets[slot], count);
        }

        return SparklineRenderer.Render(buckets);
    }
}
