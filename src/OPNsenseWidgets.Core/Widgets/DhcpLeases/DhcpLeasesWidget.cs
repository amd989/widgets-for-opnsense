using System.Text.Json;
using OPNsenseWidgets.Api;
using OPNsenseWidgets.Api.Models;
using OPNsenseWidgets.Hosting;
using OPNsenseWidgets.Rendering;

namespace OPNsenseWidgets.Widgets.DhcpLeases;

public sealed class DhcpLeasesWidget : IWebWidgetDefinition
{
    public string DefinitionId => "DhcpLeases_Widget";
    public string DisplayName => "DHCP Leases";
    public string WebPath => "DhcpLeases/Web/index.html";

    /// <summary>A device counts as new for this long after it first appeared.</summary>
    public static readonly TimeSpan NewWindow = TimeSpan.FromHours(24);
    /// <summary>A lease renewed within this window means the device is online.</summary>
    public static readonly TimeSpan ActiveWindow = TimeSpan.FromHours(1);

    private readonly FirstSeenStore _firstSeenStore;

    // KEA reservations change rarely; refresh them once a minute.
    private static readonly TimeSpan ReservationsTtl = TimeSpan.FromSeconds(60);
    private (HashSet<string> Macs, HashSet<string> Ips) _reservations = ([], []);
    private DateTime _reservationsFetchedAt = DateTime.MinValue;
    private IReadOnlyDictionary<string, DateTimeOffset?> _firstSeen = new Dictionary<string, DateTimeOffset?>();

    public DhcpLeasesWidget(FirstSeenStore firstSeenStore)
    {
        _firstSeenStore = firstSeenStore;
    }

    /// <summary>When the device first appeared; null if unknown or present before tracking started.</summary>
    private DateTimeOffset? FirstSeenOf(DhcpLease lease) =>
        !string.IsNullOrWhiteSpace(lease.EffectiveMac) &&
        _firstSeen.TryGetValue(FirstSeenStore.Normalize(lease.EffectiveMac), out var seen) ? seen : null;

    private bool IsNew(DhcpLease lease, DateTimeOffset now) =>
        FirstSeenOf(lease) is { } seen && now - seen < NewWindow;

    /// <summary>
    /// Lease data for the HTML page (Web/index.html), newest arrivals first. The page derives
    /// status, relative times and the 24h arrivals chart itself so they stay current between refreshes.
    /// firstSeen is null for devices present before tracking started (trackingSince).
    /// </summary>
    public object BuildWebData(object? data)
    {
        var leases = data as List<DhcpLease> ?? [];
        return new
        {
            updatedAt = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
            trackingSince = _firstSeenStore.TrackingSince.ToUnixTimeMilliseconds(),
            newWindowMs = (long)NewWindow.TotalMilliseconds,
            activeWindowMs = (long)ActiveWindow.TotalMilliseconds,
            total = leases.Count,
            leases = leases.Select(l => new
            {
                host = string.IsNullOrWhiteSpace(l.Hostname) ? null : l.Hostname,
                ip = l.Address,
                vendor = TruncateVendor(l.EffectiveManufacturer),
                mac = l.EffectiveMac,
                start = l.EffectiveLeaseStart == DateTime.MinValue
                    ? (long?)null
                    : new DateTimeOffset(l.EffectiveLeaseStart).ToUnixTimeMilliseconds(),
                firstSeen = FirstSeenOf(l)?.ToUnixTimeMilliseconds(),
                reserved = l.Reserved
            })
        };
    }

    private const int MaxPages = 5;

    private static readonly Dictionary<string, int> CardsPerPage = new()
    {
        ["small"] = 1,
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
        _firstSeen = _firstSeenStore.Observe(response.Rows.Select(l => l.EffectiveMac));

        if (DateTime.Now - _reservationsFetchedAt > ReservationsTtl)
        {
            _reservations = await client.GetReservationsAsync(ct);
            _reservationsFetchedAt = DateTime.Now;
        }
        foreach (var lease in response.Rows)
        {
            lease.Reserved = lease.ReservedByApi ||
                (!string.IsNullOrWhiteSpace(lease.EffectiveMac) && _reservations.Macs.Contains(FirstSeenStore.Normalize(lease.EffectiveMac))) ||
                _reservations.Ips.Contains(lease.Address);
        }

        // Newest arrivals first; devices that predate tracking follow, most recently renewed first.
        return response.Rows
            .OrderByDescending(l => FirstSeenOf(l) ?? DateTimeOffset.MinValue)
            .ThenByDescending(l => l.EffectiveLeaseStart)
            .ToList();
    }

    /// <summary>
    /// Card time: how long ago the device joined. Devices that predate tracking show the tracking
    /// age with a "+" ("3d+" = at least 3 days), or nothing during the first hour of tracking.
    /// </summary>
    private string JoinedLabel(DhcpLease lease, DateTimeOffset now)
    {
        if (FirstSeenOf(lease) is { } seen)
            return FormatRelativeTime(seen.LocalDateTime, now.LocalDateTime);
        var tracking = now - _firstSeenStore.TrackingSince;
        return tracking.TotalHours < 1 ? "" : FormatRelativeTime(_firstSeenStore.TrackingSince.LocalDateTime, now.LocalDateTime) + "+";
    }

    public string BuildDataJson(object? data, string size)
    {
        var leases = data as List<DhcpLease> ?? [];
        var cardsPerPage = CardsPerPage.GetValueOrDefault(size, 6);
        var now = DateTime.Now;
        var maxCards = cardsPerPage * MaxPages;
        var displayLeases = leases.Take(maxCards).ToList();

        var nowOffset = DateTimeOffset.Now;
        var newCount = leases.Count(l => IsNew(l, nowOffset));
        var activeCount = leases.Count(l => now - l.EffectiveLeaseStart < ActiveWindow);
        var reservedCount = leases.Count(l => l.Reserved);

        var showMac = size == "large";
        var columns = size == "small" ? 1 : 2;
        var cardLogicalWidth = size == "small" ? 280 : 160;

        var cardImages = displayLeases.Select(l =>
        {
            var vendor = TruncateVendor(l.EffectiveManufacturer);
            var hasHostname = !string.IsNullOrWhiteSpace(l.Hostname);
            var metaParts = new List<string>();
            if (hasHostname) metaParts.Add(l.Address);
            if (!string.IsNullOrEmpty(vendor)) metaParts.Add(vendor);
            if (l.Reserved) metaParts.Add("rsv");

            var status = l.Reserved ? "reserved"
                : IsNew(l, nowOffset) ? "new"
                : now - l.EffectiveLeaseStart < ActiveWindow ? "active"
                : "stale";

            var cardData = new DeviceCardData(
                Hostname: hasHostname ? l.Hostname : l.Address,
                Meta: string.Join(" \u00b7 ", metaParts),
                Time: JoinedLabel(l, nowOffset),
                Mac: showMac ? l.EffectiveMac : null,
                Status: status
            );

            // Small: regular card, no MAC line. A taller card makes the carousel overflow
            // and the Widget Board then hides it entirely. The side inset keeps the
            // carousel arrows off the text.
            return new { img = DeviceCardRenderer.RenderCard(cardData, cardLogicalWidth, showMac, insetX: size == "small" ? 22 : 0) };
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
            sparkline = BuildArrivalsSparkline(leases, nowOffset)
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

    /// <summary>Devices first seen per hour over the last 24h; buckets[23] is the current hour.</summary>
    private string BuildArrivalsSparkline(List<DhcpLease> leases, DateTimeOffset now)
    {
        var buckets = new int[24];
        foreach (var lease in leases)
        {
            if (FirstSeenOf(lease) is not { } seen) continue;
            var hoursAgo = (int)Math.Floor((now - seen).TotalHours);
            if (hoursAgo is >= 0 and < 24)
                buckets[23 - hoursAgo]++;
        }

        return SparklineRenderer.Render(buckets);
    }
}
