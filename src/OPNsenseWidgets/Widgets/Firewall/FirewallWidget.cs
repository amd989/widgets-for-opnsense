using System.Text.Json;
using OPNsenseWidgets.Api;
using OPNsenseWidgets.Api.Models;
using OPNsenseWidgets.Provider;
using OPNsenseWidgets.Rendering;

namespace OPNsenseWidgets.Widgets.Firewall;

public sealed class FirewallWidget : IWidgetDefinition
{
    public string DefinitionId => "Firewall_Widget";
    public string DisplayName => "Firewall";
    public int RefreshIntervalSeconds => 1;

    private static readonly Dictionary<string, int> TopNPerSize = new()
    {
        ["small"] = 3,
        ["medium"] = 5,
        ["large"] = 10
    };

    // Device names returned by GetInterfaceStatisticsAsync (same set Traffic widget uses), cached for 60 s.
    private HashSet<string> _trackedDevices = [];
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
                _devicesFetchedAt = DateTime.Now;
                OPNsenseWidgetProvider.Log(
                    $"FirewallWidget: tracked devices={string.Join(",", _trackedDevices)}");
            }
            catch (Exception ex)
            {
                OPNsenseWidgetProvider.Log($"FirewallWidget: interface fetch failed: {ex.Message}");
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

        return rules
            .OrderByDescending(r => r.Evaluations)
            .ThenByDescending(r => r.Packets)
            .ToList();
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
        var sliceLabels = primary.Select(r => r.EffectiveLabel).ToList();

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
            "small"  => (90,  18f, 0,   22, 12f, 1),
            "large"  => (200, 42f, 320, 22, 12f, 2),
            _        => (130, 30f, 290, 20, 11f, 1),  // medium: stacked, full-width legend
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
            ruleCount = rules.Count
        });
    }
}
