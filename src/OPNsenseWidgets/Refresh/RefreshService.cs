using OPNsenseWidgets.Configuration;
using OPNsenseWidgets.Provider;

namespace OPNsenseWidgets.Refresh;

public sealed class RefreshService : IDisposable
{
    private readonly OPNsenseWidgetProvider _provider;
    private readonly ISettingsManager _settingsManager;
    private Timer? _timer;
    private readonly CancellationTokenSource _cts = new();

    // Cache settings so we don't hit disk on every 1-second tick
    private WidgetSettings _cachedSettings = new();
    private DateTime _settingsLoadedAt = DateTime.MinValue;
    private static readonly TimeSpan SettingsTtl = TimeSpan.FromSeconds(30);

    public RefreshService(OPNsenseWidgetProvider provider, ISettingsManager settingsManager)
    {
        _provider = provider;
        _settingsManager = settingsManager;
    }

    public void Start()
    {
        ReloadSettings();
        // Base timer runs every second — the minimum refresh interval any widget needs
        _timer = new Timer(OnTick, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    public void Stop()
    {
        _cts.Cancel();
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private void OnTick(object? state)
    {
        if (_cts.IsCancellationRequested)
            return;

        // Refresh cached settings periodically
        if (DateTime.Now - _settingsLoadedAt > SettingsTtl)
            ReloadSettings();

        var globalInterval = Math.Clamp(_cachedSettings.RefreshIntervalSeconds, 10, 300);
        var now = DateTime.Now;

        var activeWidgets = _provider.RunningWidgets
            .Where(kv => kv.Value.IsActive && !kv.Value.InCustomization)
            .Select(kv => kv.Value)
            .ToList();

        foreach (var widget in activeWidgets)
        {
            // Determine this widget's desired interval
            var desiredSeconds = 0;
            if (_provider.Definitions.TryGetValue(widget.DefinitionId, out var def))
                desiredSeconds = def.RefreshIntervalSeconds;
            if (desiredSeconds <= 0)
                desiredSeconds = globalInterval;

            var elapsed = widget.LastRefresh.HasValue
                ? (now - widget.LastRefresh.Value).TotalSeconds
                : double.MaxValue;

            if (elapsed >= desiredSeconds)
                _ = _provider.RefreshWidgetAsync(widget);
        }
    }

    private void ReloadSettings()
    {
        _cachedSettings = _settingsManager.Load();
        _settingsLoadedAt = DateTime.Now;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _timer?.Dispose();
        _cts.Dispose();
    }
}
