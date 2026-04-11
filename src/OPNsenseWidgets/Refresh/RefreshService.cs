using OPNsenseWidgets.Configuration;
using OPNsenseWidgets.Provider;

namespace OPNsenseWidgets.Refresh;

public sealed class RefreshService : IDisposable
{
    private readonly OPNsenseWidgetProvider _provider;
    private Timer? _timer;
    private readonly CancellationTokenSource _cts = new();

    public RefreshService(OPNsenseWidgetProvider provider)
    {
        _provider = provider;
    }

    public void Start()
    {
        var settings = SettingsManager.Load();
        var interval = TimeSpan.FromSeconds(Math.Clamp(settings.RefreshIntervalSeconds, 10, 300));

        _timer = new Timer(OnTick, null, TimeSpan.Zero, interval);
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

        var activeWidgets = _provider.RunningWidgets
            .Where(kv => kv.Value.IsActive && !kv.Value.InCustomization)
            .Select(kv => kv.Value)
            .ToList();

        if (activeWidgets.Count == 0)
            return;

        // Reload settings in case the refresh interval changed
        var settings = SettingsManager.Load();
        var newInterval = TimeSpan.FromSeconds(Math.Clamp(settings.RefreshIntervalSeconds, 10, 300));
        _timer?.Change(newInterval, newInterval);

        foreach (var widget in activeWidgets)
        {
            _ = _provider.RefreshWidgetAsync(widget);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _timer?.Dispose();
        _cts.Dispose();
    }
}
