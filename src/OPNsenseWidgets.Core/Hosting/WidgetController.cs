using System.Text.Json;
using Microsoft.Extensions.Logging;
using OPNsenseWidgets.Api;
using OPNsenseWidgets.Configuration;
using OPNsenseWidgets.Widgets;

namespace OPNsenseWidgets.Hosting;

/// <summary>
/// Widget state and behavior shared by every OS: which view to show, page messages, actions
/// (settings, test connection, key import) and refreshes. The OS-specific <see cref="IWidgetHost"/>
/// shows the result.
/// </summary>
public sealed class WidgetController
{
    private readonly Dictionary<string, WidgetInfo> _widgets = [];
    private readonly Dictionary<string, IWidgetDefinition> _definitions = [];
    private readonly object _lock = new();
    private readonly ISettingsManager _settingsManager;
    private readonly ICredentialStore _credentialStore;
    private readonly IOPNsenseApiClientFactory _apiClientFactory;
    private readonly IPlatformPaths _paths;
    private readonly ILogger<WidgetController> _logger;
    private IWidgetHost? _host;

    /// <summary>Set while no widgets are tracked. The Windows provider exits on it.</summary>
    public ManualResetEventSlim EmptyEvent { get; } = new(false);

    public WidgetController(
        IEnumerable<IWidgetDefinition> widgetDefinitions,
        ISettingsManager settingsManager,
        ICredentialStore credentialStore,
        IOPNsenseApiClientFactory apiClientFactory,
        IPlatformPaths paths,
        ILogger<WidgetController> logger)
    {
        _settingsManager = settingsManager;
        _credentialStore = credentialStore;
        _apiClientFactory = apiClientFactory;
        _paths = paths;
        _logger = logger;

        foreach (var definition in widgetDefinitions)
            _definitions[definition.DefinitionId] = definition;
    }

    private IWidgetHost Host => _host ?? throw new InvalidOperationException("No widget host attached.");

    public void AttachHost(IWidgetHost host) => _host = host;

    public IReadOnlyDictionary<string, IWidgetDefinition> Definitions => _definitions;

    public IReadOnlyDictionary<string, WidgetInfo> Widgets
    {
        get { lock (_lock) return new Dictionary<string, WidgetInfo>(_widgets); }
    }

    // ─── Tracking ───

    /// <summary>Starts tracking a widget, replacing any earlier entry with the same id.</summary>
    public void Add(WidgetInfo info)
    {
        lock (_lock)
        {
            _widgets[info.WidgetId] = info;
            EmptyEvent.Reset();
        }
    }

    /// <summary>Returns the tracked widget, or tracks the one <paramref name="create"/> makes.</summary>
    public WidgetInfo GetOrAdd(string widgetId, Func<WidgetInfo> create, out bool added)
    {
        lock (_lock)
        {
            if (_widgets.TryGetValue(widgetId, out var existing))
            {
                added = false;
                return existing;
            }

            var info = create();
            _widgets[widgetId] = info;
            EmptyEvent.Reset();
            added = true;
            return info;
        }
    }

    public bool TryGet(string widgetId, out WidgetInfo info)
    {
        lock (_lock)
            return _widgets.TryGetValue(widgetId, out info!);
    }

    public void Remove(string widgetId)
    {
        lock (_lock)
        {
            _widgets.Remove(widgetId);
            if (_widgets.Count == 0)
                EmptyEvent.Set();
        }
    }

    // ─── Views ───

    /// <summary>Which state the widget is in. Missing settings win over a stale error.</summary>
    public WidgetView GetView(WidgetInfo info)
    {
        if (info.InCustomization)
            return new WidgetView(WidgetState.Settings, WebAssets.SettingsPath);

        if (!_settingsManager.Load().IsConfigured || _credentialStore.LoadCredentials() is null)
            return new WidgetView(WidgetState.Unconfigured, WebAssets.StatusPath);

        if (info.LastError is not null)
            return new WidgetView(WidgetState.Error, WebAssets.StatusPath);

        var webPath = _definitions.TryGetValue(info.DefinitionId, out var definition)
            ? (definition as IWebWidgetDefinition)?.WebPath
            : null;
        return new WidgetView(WidgetState.Data, webPath);
    }

    /// <summary>
    /// Sends the current view's payload to the widget's page. A no-op until the page said it is ready.
    /// </summary>
    public void PushWebData(WidgetInfo info)
    {
        if (!info.WebReady || !_definitions.TryGetValue(info.DefinitionId, out var definition) ||
            definition is not IWebWidgetDefinition web)
            return;

        string message;
        try
        {
            object? payload = info.WebView switch
            {
                "settings" => BuildSettingsPayload(info),
                "status" => BuildStatusPayload(info),
                _ => info.CachedData is null ? null : web.BuildWebData(info.CachedData)
            };
            if (payload is null)
                return;

            message = JsonSerializer.Serialize(new
            {
                type = "data",
                view = info.WebView,
                size = info.CurrentSize,
                payload
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PushWebData: serialize failed for {WidgetId}", info.WidgetId);
            return;
        }

        Host.SendToPage(info, message);
    }

    /// <summary>Saved settings for settings.html. The secret itself never goes to the page.</summary>
    private object BuildSettingsPayload(WidgetInfo info)
    {
        var settings = _settingsManager.Load();
        var credentials = _credentialStore.LoadCredentials();
        return new
        {
            hostUrl = settings.HostUrl,
            apiKey = credentials?.ApiKey ?? "",
            hasSecret = !string.IsNullOrEmpty(credentials?.ApiSecret),
            allowSelfSigned = settings.AllowSelfSignedCertificates,
            refreshInterval = settings.RefreshIntervalSeconds,
            testResult = info.TestResult,
            testOk = info.TestOk,
            inDialog = info.CustomizingInDialog
        };
    }

    /// <summary>Same order as <see cref="GetView"/>: missing settings win over a stale error.</summary>
    private object BuildStatusPayload(WidgetInfo info)
    {
        var configured = _settingsManager.Load().IsConfigured && _credentialStore.LoadCredentials() is not null;
        return !configured || info.LastError is null
            ? new { kind = "unconfigured", message = (string?)null }
            : new { kind = "error", message = (string?)info.LastError };
    }

    // ─── Page messages ───

    /// <summary>
    /// Handles a message from a widget page: {type:"ready"}, {type:"refresh"} or {type:"action", verb, data}.
    /// </summary>
    public void OnPageMessage(WidgetInfo info, string message)
    {
        string? type = null, verb = null, data = null;
        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("type", out var t)) type = t.GetString();
                if (root.TryGetProperty("verb", out var v)) verb = v.GetString();
                if (root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object) data = d.GetRawText();
            }
        }
        catch (JsonException)
        {
            // Not JSON; ignore.
        }

        if (!_definitions.TryGetValue(info.DefinitionId, out var definition) || definition is not IWebWidgetDefinition)
            return;

        switch (type)
        {
            case "ready":
                info.WebReady = true;
                if (info.WebView != "data" || info.CachedData is not null)
                    PushWebData(info);
                else if (info.LastRefreshAttempt is null || DateTime.Now - info.LastRefreshAttempt.Value > TimeSpan.FromSeconds(5))
                    _ = RefreshAsync(info); // the page retries "ready" every 3 s; don't stack fetches
                break;
            case "refresh":
                _ = RefreshAsync(info);
                break;
            case "action" when !string.IsNullOrEmpty(verb):
                // Off the caller's thread: on Windows this is a board callback, and actions update the
                // widget and may send to the page.
                _ = Task.Run(() =>
                {
                    try { HandleAction(info, verb, data ?? "{}"); }
                    catch (Exception ex) { _logger.LogError(ex, "HandleAction failed for {Verb}", verb); }
                });
                break;
        }
    }

    // ─── Actions ───

    /// <summary>Card buttons (Action.Execute) and web page buttons ({type:"action"}) share these verbs.</summary>
    public void HandleAction(WidgetInfo info, string verb, string data)
    {
        switch (verb)
        {
            case "refresh":
                _ = RefreshAsync(info);
                break;
            case "openSettings":
                // Settings inside the widget itself (status page button), not the board's dialog.
                info.InCustomization = true;
                info.CustomizingInDialog = false;
                ClearTestResult(info);
                Host.Update(info);
                break;
            case "saveConfig":
                SaveConfiguration(data);
                if (info.InCustomization && info.CustomizingInDialog)
                {
                    // The dialog stays open whatever we send, so confirm the save on the settings page.
                    _ = SaveInDialogAsync(info);
                    break;
                }
                EndCustomization(info);
                _ = RefreshAsync(info);
                break;
            case "cancelConfig":
                EndCustomization(info);
                Host.Update(info);
                break;
            case "testConnection":
                SaveConfiguration(data);
                _ = TestConnectionAsync(info);
                break;
            case "importKeyFile":
                ImportKeyFile(info);
                break;
            default:
                _logger.LogWarning("HandleAction: unknown verb {Verb}", verb);
                break;
        }
    }

    public static void ClearTestResult(WidgetInfo info)
    {
        info.TestResult = null;
        info.TestOk = null;
    }

    public static void EndCustomization(WidgetInfo info)
    {
        info.InCustomization = false;
        info.CustomizingInDialog = false;
        ClearTestResult(info);
    }

    /// <summary>Save from the Customize dialog: fetch with the new settings and report on the settings page.</summary>
    private async Task SaveInDialogAsync(WidgetInfo info)
    {
        if (!_definitions.TryGetValue(info.DefinitionId, out var definition))
            return;

        using var client = _apiClientFactory.Create();
        if (client is null)
        {
            info.TestResult = "Saved. Add the URL, key and secret to connect.";
            info.TestOk = false;
            Host.Update(info);
            return;
        }

        info.LastRefreshAttempt = DateTime.Now;
        try
        {
            info.CachedData = await definition.FetchDataAsync(client);
            info.LastError = null;
            info.LastRefresh = DateTime.Now;
            info.TestResult = "Saved. Close this window to see the widget.";
            info.TestOk = true;
        }
        catch (Exception ex)
        {
            info.LastError = ex.Message;
            info.TestResult = $"Saved, but the connection failed: {ex.Message}";
            info.TestOk = false;
            _logger.LogError(ex, "SaveInDialogAsync: fetch failed");
        }

        Host.Update(info);
    }

    private void SaveConfiguration(string formData)
    {
        try
        {
            using var doc = JsonDocument.Parse(formData);
            var root = doc.RootElement;

            var hostUrl = root.TryGetProperty("hostUrl", out var h) ? h.GetString() ?? "" : "";
            var apiKey = root.TryGetProperty("apiKey", out var k) ? k.GetString() ?? "" : "";
            var apiSecret = root.TryGetProperty("apiSecret", out var s) ? s.GetString() ?? "" : "";
            var allowSelfSigned = !root.TryGetProperty("allowSelfSigned", out var a) || a.GetString() == "true";
            var refreshInterval = root.TryGetProperty("refreshInterval", out var r) ? int.TryParse(r.GetString(), out var ri) ? ri : 30 : 30;

            refreshInterval = Math.Clamp(refreshInterval, 10, 300);

            // The settings page never receives the saved secret, so an empty field means "keep it".
            if (string.IsNullOrWhiteSpace(apiSecret))
                apiSecret = _credentialStore.LoadCredentials()?.ApiSecret ?? "";

            if (!string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(apiSecret))
                _credentialStore.SaveCredentials(apiKey, apiSecret);

            var settings = _settingsManager.Load();
            settings.HostUrl = hostUrl;
            settings.AllowSelfSignedCertificates = allowSelfSigned;
            settings.RefreshIntervalSeconds = refreshInterval;
            _settingsManager.Save(settings);

            _logger.LogInformation("SaveConfiguration: saved host={HostUrl} allowSelfSigned={AllowSelfSigned} interval={Interval}", hostUrl, allowSelfSigned, refreshInterval);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SaveConfiguration failed");
        }
    }

    public async Task RefreshAsync(WidgetInfo info)
    {
        _logger.LogDebug("RefreshAsync: id={WidgetId}", info.WidgetId);
        if (!_definitions.TryGetValue(info.DefinitionId, out var definition))
            return;

        info.LastRefreshAttempt = DateTime.Now;
        using var client = _apiClientFactory.Create();
        if (client is null)
        {
            info.LastError = null;
            info.CachedData = null;
            Host.Update(info);
            return;
        }

        var wasError = info.LastError is not null;
        try
        {
            var data = await definition.FetchDataAsync(client);
            info.CachedData = data;
            info.LastError = null;
            info.LastRefresh = DateTime.Now;
            _logger.LogDebug("RefreshAsync: fetch succeeded");
        }
        catch (Exception ex)
        {
            info.LastError = ex.Message;
            _logger.LogError(ex, "RefreshAsync failed");
        }

        // A live web page only needs the data; re-sending the template could reload it.
        if (definition is IWebWidgetDefinition && info.WebReady && info.WebView == "data" &&
            !wasError && info.LastError is null && !info.InCustomization)
        {
            PushWebData(info);
            return;
        }

        Host.Update(info);
    }

    private async Task TestConnectionAsync(WidgetInfo info)
    {
        _logger.LogInformation("TestConnectionAsync called");

        using var client = _apiClientFactory.Create();
        if (client is null)
        {
            info.TestResult = "Enter URL and credentials first.";
            info.TestOk = false;
            Host.Update(info);
            return;
        }

        try
        {
            await client.GetLeasesAsync(page: 1, rowCount: 1);
            info.LastError = null;
            info.TestResult = "Connection successful!";
            info.TestOk = true;

            if (_definitions.TryGetValue(info.DefinitionId, out var definition))
            {
                var data = await definition.FetchDataAsync(client);
                info.CachedData = data;
                info.LastRefresh = DateTime.Now;
            }

            _logger.LogInformation("TestConnectionAsync: connection OK");
        }
        catch (Exception ex)
        {
            info.TestResult = $"Failed: {ex.Message}";
            info.TestOk = false;
            _logger.LogError(ex, "TestConnectionAsync failed");
        }

        Host.Update(info);
    }

    private void ImportKeyFile(WidgetInfo info)
    {
        _logger.LogInformation("ImportKeyFile called");
        try
        {
            var downloadsPath = _paths.DownloadsFolder;
            _logger.LogInformation("ImportKeyFile: scanning {DownloadsPath}", downloadsPath);

            var keyFiles = Directory.Exists(downloadsPath)
                ? Directory.GetFiles(downloadsPath, "*_apikey.txt")
                : [];
            if (keyFiles.Length == 0)
            {
                info.TestResult = "No *_apikey.txt found in Downloads.";
                info.TestOk = false;
                Host.Update(info);
                return;
            }

            // Pick the most recently modified file
            var newest = keyFiles.OrderByDescending(File.GetLastWriteTime).First();
            _logger.LogInformation("ImportKeyFile: parsing {FileName}", newest);

            string? apiKey = null, apiSecret = null;
            foreach (var line in File.ReadAllLines(newest))
            {
                if (line.StartsWith("key=", StringComparison.OrdinalIgnoreCase))
                    apiKey = line[4..].Trim();
                else if (line.StartsWith("secret=", StringComparison.OrdinalIgnoreCase))
                    apiSecret = line[7..].Trim();
            }

            if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiSecret))
            {
                info.TestResult = "Key file found but missing key or secret.";
                info.TestOk = false;
                Host.Update(info);
                return;
            }

            _credentialStore.SaveCredentials(apiKey, apiSecret);
            info.TestResult = $"Imported from {Path.GetFileName(newest)}";
            info.TestOk = true;
            _logger.LogInformation("ImportKeyFile: imported from {FileName}", Path.GetFileName(newest));
        }
        catch (Exception ex)
        {
            info.TestResult = $"Import failed: {ex.Message}";
            info.TestOk = false;
            _logger.LogError(ex, "ImportKeyFile failed");
        }

        Host.Update(info);
    }
}
