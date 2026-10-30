using System.Text.Json;
using Microsoft.Extensions.Logging;
using OPNsenseWidgets.Api;
using OPNsenseWidgets.Api.Models;
using OPNsenseWidgets.Provider;
using OPNsenseWidgets.Rendering;

namespace OPNsenseWidgets.Widgets.Firewall;

public sealed class FirewallWidget : IWebWidgetDefinition
{
    public string WebPath => "Firewall/Web/index.html";

    // Enough rules for the large view's lists; the rest only count toward the totals.
    private const int MaxWebRules = 40;
    private const int MaxRateHistory = 60;          // one point per second
    private const double MaxRateGapSeconds = 5;

    // Live evaluation rates, derived from consecutive fetches (pf counters are cumulative).
    // Kept here rather than in the page so they survive the page reloading on every board open.
    private readonly object _rateLock = new();
    private Dictionary<string, long> _prevEvaluations = [];
    private long _prevTotal = -1;
    private DateTime _prevAt;
    private Dictionary<string, double> _rates = [];
    private double? _totalRate;
    private readonly List<(DateTime At, double Rate)> _rateHistory = [];

    private void UpdateRates(List<FirewallRuleStat> rules)
    {
        var now = DateTime.Now;
        var byLabel = rules
            .GroupBy(r => r.ShownLabel)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Evaluations));
        var total = byLabel.Values.Sum();

        lock (_rateLock)
        {
            var elapsed = (now - _prevAt).TotalSeconds;
            if (_prevTotal >= 0 && total < _prevTotal)
            {
                // pf counters were reset: nothing to compare against.
                _rates = [];
                _totalRate = null;
            }
            else if (_prevTotal >= 0 && elapsed > 0 && elapsed <= MaxRateGapSeconds)
            {
                _rates = byLabel
                    .Where(kv => _prevEvaluations.TryGetValue(kv.Key, out var old) && kv.Value >= old)
                    .ToDictionary(kv => kv.Key, kv => (kv.Value - _prevEvaluations[kv.Key]) / elapsed);
                _totalRate = (total - _prevTotal) / elapsed;
                _rateHistory.Add((now, _totalRate.Value));
                if (_rateHistory.Count > MaxRateHistory)
                    _rateHistory.RemoveAt(0);
            }
            // Otherwise (first fetch, or polling paused while the board was closed) keep the last
            // known rates for one more second instead of averaging the whole gap.

            _prevEvaluations = byLabel;
            _prevTotal = total;
            _prevAt = now;
        }
    }

    /// <summary>
    /// Rule counters for the HTML page (Web/index.html), merged by label and sorted by evaluations,
    /// with live per-second rates (null until two fetches have been compared) and 60 s of total rate
    /// history as [unixMs, evalsPerSecond].
    /// </summary>
    public object BuildWebData(object? data)
    {
        var rules = data as List<FirewallRuleStat> ?? [];

        Dictionary<string, double> rates;
        double? totalRate;
        List<double[]> history;
        lock (_rateLock)
        {
            rates = _rates;
            totalRate = _totalRate;
            history = _rateHistory
                .Select(h => new[] { new DateTimeOffset(h.At).ToUnixTimeMilliseconds(), Math.Round(h.Rate, 1) })
                .ToList();
        }

        // Rules that read the same (e.g. the IPv4 and IPv6 variants of one rule) are merged;
        // "rule" keeps the raw pf text for the tooltip.
        var merged = rules
            .GroupBy(r => r.ShownLabel)
            .Select(g => new
            {
                label = g.Key,
                rule = string.Join("\n", g.Select(r => r.EffectiveLabel).Distinct().Take(3)),
                evaluations = g.Sum(r => r.Evaluations),
                packets = g.Sum(r => r.Packets),
                bytes = g.Sum(r => r.Bytes),
                states = g.Sum(r => r.States),
                rate = rates.TryGetValue(g.Key, out var ruleRate) ? Math.Round(ruleRate, 1) : (double?)null
            })
            .OrderByDescending(r => r.evaluations)
            .ThenByDescending(r => r.packets)
            .ToList();

        return new
        {
            updatedAt = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
            ruleCount = merged.Count,
            rate = totalRate is null ? (double?)null : Math.Round(totalRate.Value, 1),
            rateHistory = history,
            totals = new
            {
                evaluations = merged.Sum(r => r.evaluations),
                packets = merged.Sum(r => r.packets),
                bytes = merged.Sum(r => r.bytes),
                states = merged.Sum(r => r.states)
            },
            rules = merged.Take(MaxWebRules)
        };
    }

    private readonly ILogger<FirewallWidget> _logger;

    public FirewallWidget(ILogger<FirewallWidget> logger)
    {
        _logger = logger;
    }

    public string DefinitionId => "Firewall_Widget";
    public string DisplayName => "Firewall";
    public int RefreshIntervalSeconds => 1;

    private static readonly Dictionary<string, int> TopNPerSize = new()
    {
        ["small"] = 4,
        ["medium"] = 5,
        ["large"] = 10
    };

    // Device names returned by GetInterfaceStatisticsAsync (same set Traffic widget uses), cached for 60 s.
    private HashSet<string> _trackedDevices = [];
    // Device -> OPNsense interface name (vmx0 -> WAN), for readable rule labels.
    private Dictionary<string, string> _interfaceNames = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _devicesFetchedAt = DateTime.MinValue;

    // UUID → description map from the rule list API, cached for 60 s.
    private Dictionary<string, string> _ruleDescriptions = [];
    private DateTime _descriptionsFetchedAt = DateTime.MinValue;

    private static readonly TimeSpan InterfaceCacheTtl = TimeSpan.FromSeconds(60);

    public string GetTemplate(string size)
    {
        var fileName = size switch
        {
            "small" => "SmallTemplate.json",
            "large" => "LargeTemplate.json",
            _ => "MediumTemplate.json"
        };
        return TemplateHelper.LoadWidgetTemplate("Firewall", fileName);
    }

    public async Task<object?> FetchDataAsync(OPNsenseApiClient client, CancellationToken ct = default)
    {
        // Refresh tracked-device set periodically (same interfaces the Traffic widget shows).
        if (DateTime.Now - _devicesFetchedAt > InterfaceCacheTtl)
        {
            try
            {
                var ifaces = await client.GetInterfaceStatisticsAsync(ct);
                _trackedDevices = ifaces.Keys
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                _interfaceNames = ifaces.ToDictionary(kv => kv.Key, kv => kv.Value.Name, StringComparer.OrdinalIgnoreCase);
                _devicesFetchedAt = DateTime.Now;
                _logger.LogDebug("FirewallWidget: tracked devices={TrackedDevices}", string.Join(",", _trackedDevices));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FirewallWidget: interface fetch failed");
            }
        }

        // Refresh UUID → description map periodically.
        if (DateTime.Now - _descriptionsFetchedAt > InterfaceCacheTtl)
        {
            _ruleDescriptions = await client.GetFirewallRuleDescriptionsAsync(ct);
            _descriptionsFetchedAt = DateTime.Now;
        }

        var allRules = await client.GetFirewallRuleStatsAsync(ct);

        // Resolve UUID labels to human-readable descriptions; drop rules whose label is
        // an unresolvable UUID (internal/anonymous rules OPNsense dashboard also hides).
        var resolved = new List<FirewallRuleStat>();
        foreach (var r in allRules)
        {
            if (!string.IsNullOrWhiteSpace(r.Label) &&
                OPNsenseApiClient.LooksLikeUuid(r.Label))
            {
                var normUuid = OPNsenseApiClient.NormaliseUuid(r.Label);
                if (_ruleDescriptions.TryGetValue(normUuid, out var humanDescr))
                    r.Description = humanDescr;
                else
                    continue; // UUID with no matching rule → skip
            }
            resolved.Add(r);
        }

        // Keep only rules that touch a tracked interface (or are global).
        var rules = _trackedDevices.Count > 0
            ? resolved.Where(r => RuleAppliesToTrackedInterface(r.Description, _trackedDevices)).ToList()
            : resolved;

        var sorted = rules
            .OrderByDescending(r => r.Evaluations)
            .ThenByDescending(r => r.Packets)
            .ToList();
        foreach (var rule in sorted)
            rule.DisplayLabel = PfRuleLabel.Pretty(rule.EffectiveLabel, _interfaceNames);
        UpdateRates(sorted);
        return sorted;
    }

    /// <summary>
    /// Returns true if a pf rule applies to at least one tracked interface.
    /// Handles:
    ///   "… on vmx0 …"   → keep if vmx0 is tracked
    ///   "… on ! vmx2 …" → keep if the negated device is NOT tracked
    ///                      (the rule applies to everything else, including tracked devices)
    ///   no "on" clause  → global rule, keep
    /// </summary>
    private static bool RuleAppliesToTrackedInterface(string description, HashSet<string> tracked)
    {
        var tokens = description.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < tokens.Length - 1; i++)
        {
            if (!tokens[i].Equals("on", StringComparison.OrdinalIgnoreCase))
                continue;

            var next = tokens[i + 1];
            if (next == "!")
            {
                // "on ! <device>" — excludes one device; applies to all others.
                // Keep unless the excluded device is the only tracked one
                // (i.e. the rule would hit nothing we care about).
                if (i + 2 < tokens.Length)
                {
                    var excluded = tokens[i + 2];
                    // Rule hits all non-excluded devices; keep if any tracked device is not excluded.
                    return tracked.Any(d => !d.Equals(excluded, StringComparison.OrdinalIgnoreCase));
                }
                return true;
            }

            // "on <device>" — rule is for exactly one interface.
            return tracked.Contains(next);
        }

        // No "on" clause → global.
        return true;
    }

    public string BuildDataJson(object? data, string size)
    {
        var rules = data as List<FirewallRuleStat> ?? [];
        var now = DateTime.Now;
        var topN = TopNPerSize.GetValueOrDefault(size, 7);

        if (rules.Count == 0)
        {
            return JsonSerializer.Serialize(new
            {
                hasData = false,
                lastRefresh = now.ToString("h:mm:ss tt"),
                donutImage = "",
                legendImage = "",
                topRules = Array.Empty<object>(),
                totalEvaluations = "\u2014",
                totalPackets = "\u2014",
                ruleCount = 0
            });
        }

        var grandEvaluations = rules.Sum(r => r.Evaluations);
        var grandPackets = rules.Sum(r => r.Packets);

        // Top N by evaluations; fold the rest into "Other"
        var ordered = rules
            .OrderByDescending(r => r.Evaluations)
            .ThenByDescending(r => r.Packets)
            .ToList();

        var primary = ordered.Take(topN).ToList();
        var rest = ordered.Skip(topN).ToList();

        var sliceValues = primary.Select(r => (double)r.Evaluations).ToList();
        var sliceLabels = primary.Select(r => r.ShownLabel).ToList();

        if (rest.Count > 0)
        {
            var otherSum = rest.Sum(r => r.Evaluations);
            if (otherSum > 0)
            {
                sliceValues.Add(otherSum);
                sliceLabels.Add($"Other ({rest.Count})");
            }
        }

        // If every visible slice is zero (fresh pf reset), just show a flat "no activity"
        if (sliceValues.Sum() <= 0)
        {
            return JsonSerializer.Serialize(new
            {
                hasData = false,
                lastRefresh = now.ToString("h:mm:ss tt"),
                donutImage = "",
                legendImage = "",
                topRules = Array.Empty<object>(),
                totalEvaluations = grandEvaluations.ToString("N0"),
                totalPackets = grandPackets.ToString("N0"),
                ruleCount = rules.Count
            });
        }

        var (donutSize, ringThickness, legendWidth, legendRowH, legendTextSize, legendColumns) = size switch
        {
            "small"  => (108, 24f, 0,   22, 12f, 1),
            "large"  => (225, 46f, 320, 25, 12f, 2),
            _        => (110, 26f, 290, 18, 11f, 1),  // medium: stacked, full-width legend
        };

        var donutImage = DonutChartRenderer.RenderDonut(
            sliceValues, size: donutSize, ringThickness: ringThickness);

        var legendImage = legendWidth > 0
            ? DonutChartRenderer.RenderLegend(
                sliceLabels, width: legendWidth, rowHeight: legendRowH,
                textSize: legendTextSize, columns: legendColumns)
            : "";

        // Textual top list (used by small + as fallback rows)
        var topRules = sliceLabels.Zip(sliceValues, (label, v) => new
        {
            label,
            share = grandEvaluations > 0
                ? $"{v / grandEvaluations * 100:F0}%"
                : "0%",
            evaluations = ((long)v).ToString("N0")
        }).ToArray();

        return JsonSerializer.Serialize(new
        {
            hasData = true,
            lastRefresh = now.ToString("h:mm:ss tt"),
            donutImage,
            legendImage,
            topRules,
            totalEvaluations = grandEvaluations.ToString("N0"),
            totalPackets = grandPackets.ToString("N0"),
            evaluationsShort = FormatCompact(grandEvaluations),
            packetsShort = FormatCompact(grandPackets),
            ruleCount = rules.Count
        });
    }

    // 966269802 -> "966.3M" so footers fit on one line
    private static string FormatCompact(long value) => value switch
    {
        >= 1_000_000_000_000 => $"{value / 1e12:F1}T",
        >= 1_000_000_000     => $"{value / 1e9:F1}B",
        >= 1_000_000         => $"{value / 1e6:F1}M",
        >= 1_000             => $"{value / 1e3:F1}K",
        _                    => value.ToString()
    };
}
