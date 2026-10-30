using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OPNsenseWidgets.Widgets.DhcpLeases;

/// <summary>
/// Remembers when each MAC address first showed up in the DHCP leases, persisted to
/// ~/.opnsense-widgets/dhcp-first-seen.json (same folder as settings, so it survives reinstalls).
///
/// Lease start times can't answer "what just joined?": KEA reports the last renewal, so nearly
/// every device looks like it connected within the hour. Devices already present the first time
/// tracking runs are recorded with no first-seen time ("known before tracking started") so they
/// don't all show up as new.
/// </summary>
public sealed class FirstSeenStore
{
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".opnsense-widgets",
        "dhcp-first-seen.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ILogger<FirstSeenStore> _logger;
    private readonly object _lock = new();
    private StoreFile? _store;

    public FirstSeenStore(ILogger<FirstSeenStore> logger)
    {
        _logger = logger;
    }

    /// <summary>When tracking started; devices with a null first-seen time were already present then.</summary>
    public DateTimeOffset TrackingSince
    {
        get { lock (_lock) return Load().TrackingSince; }
    }

    /// <summary>
    /// Records any MAC addresses not seen before and returns a snapshot of first-seen times
    /// (keys are lower-case MACs; a null value means "present before tracking started").
    /// </summary>
    public IReadOnlyDictionary<string, DateTimeOffset?> Observe(IEnumerable<string> macs)
    {
        var now = DateTimeOffset.Now;
        var normalized = macs
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(Normalize)
            .Distinct()
            .ToList();

        lock (_lock)
        {
            var store = Load();

            // First successful fetch ever: everything present now predates tracking.
            var seeding = !store.Persisted;
            if (seeding && normalized.Count == 0)
                return new Dictionary<string, DateTimeOffset?>(store.Devices);
            if (seeding)
                store.TrackingSince = now;

            var added = 0;
            foreach (var mac in normalized)
            {
                if (store.Devices.ContainsKey(mac))
                    continue;
                store.Devices[mac] = seeding ? null : now;
                added++;
            }

            if (added > 0 || seeding)
            {
                Save(store);
                _logger.LogInformation("FirstSeenStore: {Added} {Kind} device(s), {Total} known",
                    added, seeding ? "existing" : "new", store.Devices.Count);
            }

            return new Dictionary<string, DateTimeOffset?>(store.Devices);
        }
    }

    public static string Normalize(string mac) => mac.Trim().ToLowerInvariant().Replace('-', ':');

    private StoreFile Load()
    {
        if (_store is not null)
            return _store;

        try
        {
            if (File.Exists(_path))
            {
                var loaded = JsonSerializer.Deserialize<StoreFile>(File.ReadAllText(_path), JsonOptions);
                if (loaded is not null)
                {
                    loaded.Persisted = true;
                    return _store = loaded;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FirstSeenStore: could not read {Path}, starting fresh", _path);
        }

        return _store = new StoreFile { TrackingSince = DateTimeOffset.Now };
    }

    private void Save(StoreFile store)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(store, JsonOptions));
            File.Move(temp, _path, overwrite: true);
            store.Persisted = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FirstSeenStore: could not write {Path}", _path);
        }
    }

    private sealed class StoreFile
    {
        public DateTimeOffset TrackingSince { get; set; }
        public Dictionary<string, DateTimeOffset?> Devices { get; set; } = [];

        [System.Text.Json.Serialization.JsonIgnore]
        public bool Persisted { get; set; }
    }
}
