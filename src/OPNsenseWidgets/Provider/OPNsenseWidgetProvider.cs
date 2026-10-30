using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Windows.Widgets.Providers;
using OPNsenseWidgets.Api;
using OPNsenseWidgets.Configuration;
using OPNsenseWidgets.Widgets;
using Windows.Storage.Streams;

namespace OPNsenseWidgets.Provider;

public sealed class OPNsenseWidgetProvider : IWidgetProvider, IWidgetProvider2, IWidgetResourceProvider, IWidgetProviderMessage
{
    private readonly Dictionary<string, WidgetInfo> _runningWidgets = [];
    private readonly Dictionary<string, IWidgetDefinition> _definitions = [];
    private readonly object _lock = new();
    private readonly ISettingsManager _settingsManager;
    private readonly ICredentialStore _credentialStore;
    private readonly IOPNsenseApiClientFactory _apiClientFactory;
    private readonly ILogger<OPNsenseWidgetProvider> _logger;

    public ManualResetEventSlim EmptyEvent { get; } = new(false);

    public OPNsenseWidgetProvider(
        IEnumerable<IWidgetDefinition> widgetDefinitions,
        ISettingsManager settingsManager,
        ICredentialStore credentialStore,
        IOPNsenseApiClientFactory apiClientFactory,
        ILogger<OPNsenseWidgetProvider> logger)
    {
        _settingsManager = settingsManager;
        _credentialStore = credentialStore;
        _apiClientFactory = apiClientFactory;
        _logger = logger;

        _logger.LogInformation("Provider constructed");
        foreach (var definition in widgetDefinitions)
            RegisterDefinition(definition);
    }

    public void RegisterDefinition(IWidgetDefinition definition)
    {
        _definitions[definition.DefinitionId] = definition;
    }

    public IReadOnlyDictionary<string, WidgetInfo> RunningWidgets
    {
        get { lock (_lock) return new Dictionary<string, WidgetInfo>(_runningWidgets); }
    }

    public IReadOnlyDictionary<string, IWidgetDefinition> Definitions => _definitions;

    public void CreateWidget(WidgetContext widgetContext)
    {
        _logger.LogInformation("CreateWidget: id={WidgetId} defId={DefinitionId}", widgetContext.Id, widgetContext.DefinitionId);
        try
        {
            var info = new WidgetInfo
            {
                WidgetId = widgetContext.Id,
                DefinitionId = widgetContext.DefinitionId,
                IsActive = true
            };

            lock (_lock)
            {
                _runningWidgets[widgetContext.Id] = info;
                EmptyEvent.Reset();
            }

            UpdateWidget(info);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CreateWidget failed");
        }
    }

    public void DeleteWidget(string widgetId, string customState)
    {
        _logger.LogInformation("DeleteWidget: id={WidgetId}", widgetId);
        lock (_lock)
        {
            _runningWidgets.Remove(widgetId);
            if (_runningWidgets.Count == 0)
                EmptyEvent.Set();
        }
    }

    public void OnActionInvoked(WidgetActionInvokedArgs actionInvokedArgs)
    {
        _logger.LogInformation("OnActionInvoked: verb={Verb} id={WidgetId}", actionInvokedArgs.Verb, actionInvokedArgs.WidgetContext.Id);
        try
        {
            var info = GetOrAddWidget(actionInvokedArgs.WidgetContext);
            HandleAction(info, actionInvokedArgs.Verb, actionInvokedArgs.Data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OnActionInvoked failed");
        }
    }

    /// <summary>Card buttons (Action.Execute) and web page buttons ({type:"action"}) share these verbs.</summary>
    private void HandleAction(WidgetInfo info, string verb, string data)
    {
        switch (verb)
        {
            case "refresh":
                _ = RefreshWidgetAsync(info);
                break;
            case "openSettings":
                // Settings inside the widget itself (status page button), not the board's dialog.
                info.InCustomization = true;
                info.CustomizingInDialog = false;
                ClearTestResult(info);
                UpdateWidget(info);
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
                _ = RefreshWidgetAsync(info);
                break;
            case "cancelConfig":
                EndCustomization(info);
                UpdateWidget(info);
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

    private static void ClearTestResult(WidgetInfo info)
    {
        info.TestResult = null;
        info.TestOk = null;
    }

    private static void EndCustomization(WidgetInfo info)
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
            UpdateWidget(info);
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

        UpdateWidget(info);
    }

    public void OnWidgetContextChanged(WidgetContextChangedArgs contextChangedArgs)
    {
        _logger.LogInformation("OnWidgetContextChanged: id={WidgetId} size={Size}", contextChangedArgs.WidgetContext.Id, contextChangedArgs.WidgetContext.Size);
        try
        {
            var info = GetOrAddWidget(contextChangedArgs.WidgetContext);

            info.CurrentSize = contextChangedArgs.WidgetContext.Size.ToString().ToLowerInvariant();
            UpdateWidget(info);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OnWidgetContextChanged failed");
        }
    }

    public void Activate(WidgetContext widgetContext)
    {
        _logger.LogInformation("Activate: id={WidgetId} size={Size}", widgetContext.Id, widgetContext.Size);
        try
        {
            var info = GetOrAddWidget(widgetContext);
            info.IsActive = true;
            info.CurrentSize = widgetContext.Size.ToString().ToLowerInvariant();
            UpdateWidget(info);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Activate failed");
        }
    }

    public void Deactivate(string widgetId)
    {
        _logger.LogInformation("Deactivate: id={WidgetId}", widgetId);
        lock (_lock)
        {
            if (!_runningWidgets.TryGetValue(widgetId, out var info))
                return;
            info.IsActive = false;

            // The board's Customize dialog closing (X, Save then X, or the board closing) shows up as a
            // Deactivate. Leave settings so the widget comes back with its data on the next Activate.
            // The grace period skips a Deactivate the board may send while the dialog is still opening.
            if (info.InCustomization && info.CustomizingInDialog &&
                DateTime.Now - info.CustomizationStartedAt > TimeSpan.FromSeconds(2))
            {
                _logger.LogInformation("Deactivate: customize dialog closed for {WidgetId}", widgetId);
                EndCustomization(info);
            }
        }
    }

    public void OnCustomizationRequested(WidgetCustomizationRequestedArgs customizationRequestedArgs)
    {
        _logger.LogInformation("OnCustomizationRequested: id={WidgetId}", customizationRequestedArgs.WidgetContext.Id);
        try
        {
            var info = GetOrAddWidget(customizationRequestedArgs.WidgetContext);

            // The board shows customization in its own dialog (resized to large), not in the widget.
            info.InCustomization = true;
            info.CustomizingInDialog = true;
            info.CustomizationStartedAt = DateTime.Now;
            ClearTestResult(info);
            UpdateWidget(info);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OnCustomizationRequested failed");
        }
    }

    /// <summary>
    /// Returns the tracked widget, adopting it if this process has not seen it yet. After a provider
    /// restart the board can call in before RecoverRunningWidgets finishes (or if it hangs), and
    /// ignoring those calls left widgets without data.
    /// </summary>
    private WidgetInfo GetOrAddWidget(WidgetContext context)
    {
        lock (_lock)
        {
            if (_runningWidgets.TryGetValue(context.Id, out var existing))
                return existing;

            var info = new WidgetInfo
            {
                WidgetId = context.Id,
                DefinitionId = context.DefinitionId,
                IsActive = context.IsActive,
                CurrentSize = context.Size.ToString().ToLowerInvariant()
            };
            _runningWidgets[context.Id] = info;
            EmptyEvent.Reset();
            _logger.LogInformation("GetOrAddWidget: adopted id={WidgetId} defId={DefinitionId}", context.Id, context.DefinitionId);
            return info;
        }
    }

    public void RecoverRunningWidgets()
    {
        _logger.LogInformation("RecoverRunningWidgets called");
        var widgetManager = WidgetManager.GetDefault();
        var existingWidgets = widgetManager.GetWidgetInfos();

        _logger.LogInformation("RecoverRunningWidgets: found {Count} existing widgets", existingWidgets?.Length ?? 0);

        if (existingWidgets is null || existingWidgets.Length == 0)
            return;

        lock (_lock)
        {
            foreach (var existing in existingWidgets)
            {
                if (!_runningWidgets.ContainsKey(existing.WidgetContext.Id))
                {
                    _logger.LogInformation("RecoverRunningWidgets: recovering id={WidgetId} defId={DefinitionId}", existing.WidgetContext.Id, existing.WidgetContext.DefinitionId);
                    var info = new WidgetInfo
                    {
                        WidgetId = existing.WidgetContext.Id,
                        DefinitionId = existing.WidgetContext.DefinitionId,
                        IsActive = existing.WidgetContext.IsActive,
                        CustomState = existing.CustomState ?? string.Empty
                    };
                    _runningWidgets[existing.WidgetContext.Id] = info;
                }
            }
        }
    }

    public void UpdateWidget(WidgetInfo info)
    {
        _logger.LogDebug("UpdateWidget: id={WidgetId} defId={DefinitionId} size={Size}", info.WidgetId, info.DefinitionId, info.CurrentSize);
        try
        {
            if (!_definitions.TryGetValue(info.DefinitionId, out var definition))
            {
                _logger.LogWarning("UpdateWidget: no definition found for {DefinitionId}", info.DefinitionId);
                return;
            }

            var settings = _settingsManager.Load();
            var credentials = _credentialStore.LoadCredentials();

            string template;
            string data;
            string view;
            string? webPath;

            if (info.InCustomization)
            {
                _logger.LogDebug("UpdateWidget: using ConfigurationTemplate");
                template = TemplateHelper.LoadSharedTemplate("ConfigurationTemplate.json");
                data = JsonSerializer.Serialize(new
                {
                    hostUrl = settings.HostUrl,
                    apiKey = credentials?.ApiKey ?? "",
                    apiSecret = credentials?.ApiSecret ?? "",
                    allowSelfSigned = settings.AllowSelfSignedCertificates,
                    refreshInterval = settings.RefreshIntervalSeconds,
                    testResult = info.TestResult ?? "",
                    hasTestResult = !string.IsNullOrEmpty(info.TestResult)
                });
                view = "settings";
                webPath = WebContent.SettingsPath;
            }
            else if (!settings.IsConfigured || credentials is null)
            {
                _logger.LogDebug("UpdateWidget: using UnconfiguredTemplate");
                template = TemplateHelper.LoadSharedTemplate("UnconfiguredTemplate.json");
                data = "{}";
                view = "status";
                webPath = WebContent.StatusPath;
            }
            else if (info.LastError is not null)
            {
                _logger.LogDebug("UpdateWidget: using ErrorTemplate, error={Error}", info.LastError);
                template = TemplateHelper.LoadSharedTemplate("ErrorTemplate.json");
                data = JsonSerializer.Serialize(new { errorMessage = info.LastError });
                view = "status";
                webPath = WebContent.StatusPath;
            }
            else
            {
                _logger.LogDebug("UpdateWidget: using data template for size={Size}", info.CurrentSize);
                template = definition.GetTemplate(info.CurrentSize);
                data = definition.BuildDataJson(info.CachedData, info.CurrentSize);
                view = "data";
                webPath = (definition as IWebWidgetDefinition)?.WebPath;
            }

            // A widget with web content gets a page for every state, not only its data view: the board
            // fails to load plain cards (settings, errors) for these widgets. The card body stays as the
            // fallback for hosts without web content.
            var isWeb = definition is IWebWidgetDefinition;
            if (isWeb && webPath is not null)
                template = WebContent.WithWebUrl(template, WebContent.UrlFor(webPath));

            _logger.LogDebug("UpdateWidget: view={View} template length={TemplateLength} data length={DataLength}", view, template.Length, data.Length);

            var updateOptions = new WidgetUpdateRequestOptions(info.WidgetId)
            {
                Template = template,
                Data = data,
                CustomState = info.CustomState
            };

            // A different template may reload the page, which then posts "ready" again.
            // Reset before sending so a fast "ready" is not overwritten.
            if (!isWeb || template != info.LastTemplate)
                info.WebReady = false;
            info.LastTemplate = template;
            info.WebView = view;

            _logger.LogDebug("UpdateWidget: calling WidgetManager.UpdateWidget");
            WidgetManager.GetDefault().UpdateWidget(updateOptions);
            _logger.LogDebug("UpdateWidget: WidgetManager.UpdateWidget succeeded");

            // Same template and a live page: it did not reload, so it still needs the new data.
            // (PushWebData is a no-op unless the page already said "ready".)
            if (isWeb)
                PushWebData(info);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UpdateWidget failed");
        }
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

    public async Task RefreshWidgetAsync(WidgetInfo info)
    {
        _logger.LogDebug("RefreshWidgetAsync: id={WidgetId}", info.WidgetId);
        if (!_definitions.TryGetValue(info.DefinitionId, out var definition))
            return;

        using var client = _apiClientFactory.Create();
        if (client is null)
        {
            info.LastError = null;
            info.CachedData = null;
            UpdateWidget(info);
            return;
        }

        info.LastRefreshAttempt = DateTime.Now;
        var wasError = info.LastError is not null;
        try
        {
            var data = await definition.FetchDataAsync(client);
            info.CachedData = data;
            info.LastError = null;
            info.LastRefresh = DateTime.Now;
            _logger.LogDebug("RefreshWidgetAsync: fetch succeeded");
        }
        catch (Exception ex)
        {
            info.LastError = ex.Message;
            _logger.LogError(ex, "RefreshWidgetAsync failed");
        }

        // A live web page only needs the data; re-sending the template could reload it.
        if (definition is IWebWidgetDefinition && info.WebReady && info.WebView == "data" &&
            !wasError && info.LastError is null && !info.InCustomization)
        {
            PushWebData(info);
            return;
        }

        UpdateWidget(info);
    }

    /// <summary>
    /// Sends the current view's payload to the widget's page. Only when the page said it is ready, and
    /// always off the calling thread: calling SendMessageToContent from inside a board callback (Activate
    /// etc.) while the page is still being created deadlocked the Widget service.
    /// </summary>
    private void PushWebData(WidgetInfo info)
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

        SendToContent(info.WidgetId, message);
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

    /// <summary>Same order as UpdateWidget: missing settings win over a stale error.</summary>
    private object BuildStatusPayload(WidgetInfo info)
    {
        var configured = _settingsManager.Load().IsConfigured && _credentialStore.LoadCredentials() is not null;
        return !configured || info.LastError is null
            ? new { kind = "unconfigured", message = (string?)null }
            : new { kind = "error", message = (string?)info.LastError };
    }

    // Web content (Windows App SDK 1.6.3+). Every request is logged so the debug log shows
    // whether the page itself, or only its subresources, reach the provider.
    public void OnResourceRequested(WidgetResourceRequestedArgs args)
    {
        var uri = args.Request.Uri;
        _logger.LogInformation("OnResourceRequested: id={WidgetId} method={Method} uri={Uri}",
            args.WidgetContext.Id, args.Request.Method, uri);

        if (!WebContent.TryGet(uri, out var content, out var contentType))
        {
            _logger.LogInformation("OnResourceRequested: no packaged file for {Uri}, letting the system fetch it", uri);
            return;
        }

        // The host reads the stream from another process, so use a native WinRT stream
        // (InMemoryRandomAccessStream) rather than a managed MemoryStream wrapper.
        var deferral = args.GetDeferral();
        _ = Task.Run(async () =>
        {
            try
            {
                var stream = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(content);
                    await writer.StoreAsync();
                    await writer.FlushAsync();
                    writer.DetachStream();
                }
                stream.Seek(0);

                var response = new WidgetResourceResponse(RandomAccessStreamReference.CreateFromStream(stream), "OK", 200);
                response.Headers.Add("Content-Type", contentType);
                args.Response = response;
                _logger.LogInformation("OnResourceRequested: served {Bytes} bytes as {ContentType}", content.Length, contentType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OnResourceRequested failed");
            }
            finally
            {
                deferral.Complete();
            }
        });
    }

    public void OnMessageReceived(WidgetMessageReceivedArgs args)
    {
        var widgetId = args.WidgetContext.Id;
        _logger.LogInformation("OnMessageReceived: id={WidgetId} message={Message}", widgetId, args.Message);

        var info = GetOrAddWidget(args.WidgetContext);

        string? type = null, verb = null, data = null;
        try
        {
            using var doc = JsonDocument.Parse(args.Message);
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
                    _ = RefreshWidgetAsync(info); // the page retries "ready" every 3 s; don't stack fetches
                break;
            case "refresh":
                _ = RefreshWidgetAsync(info);
                break;
            case "action" when !string.IsNullOrEmpty(verb):
                // Off the callback thread, like SendToContent: actions update the widget and may send to the page.
                _ = Task.Run(() =>
                {
                    try { HandleAction(info, verb, data ?? "{}"); }
                    catch (Exception ex) { _logger.LogError(ex, "HandleAction failed for {Verb}", verb); }
                });
                break;
        }
    }

    // Always async: never call back into the Widget service on the thread of one of its callbacks.
    private void SendToContent(string widgetId, string message)
    {
        _ = Task.Run(() =>
        {
            try
            {
                WidgetManager.GetDefault().SendMessageToContent(widgetId, message);
                _logger.LogDebug("SendMessageToContent: id={WidgetId} bytes={Bytes}", widgetId, message.Length);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SendMessageToContent failed for {WidgetId}", widgetId);
            }
        });
    }

    private async Task TestConnectionAsync(WidgetInfo info)
    {
        _logger.LogInformation("TestConnectionAsync called");

        using var client = _apiClientFactory.Create();
        if (client is null)
        {
            info.TestResult = "Enter URL and credentials first.";
            info.TestOk = false;
            UpdateWidget(info);
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

        UpdateWidget(info);
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(
        ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    private static string GetDownloadsPath()
    {
        var folderId = Guid.Parse("374DE290-123F-4565-9164-39C4925E467B"); // FOLDERID_Downloads
        SHGetKnownFolderPath(ref folderId, 0, IntPtr.Zero, out var pathPtr);
        var path = Marshal.PtrToStringUni(pathPtr)!;
        Marshal.FreeCoTaskMem(pathPtr);
        return path;
    }

    private void ImportKeyFile(WidgetInfo info)
    {
        _logger.LogInformation("ImportKeyFile called");
        try
        {
            var downloadsPath = GetDownloadsPath();
            _logger.LogInformation("ImportKeyFile: scanning {DownloadsPath}", downloadsPath);

            var keyFiles = Directory.GetFiles(downloadsPath, "*_apikey.txt");
            if (keyFiles.Length == 0)
            {
                info.TestResult = "No *_apikey.txt found in Downloads.";
                info.TestOk = false;
                UpdateWidget(info);
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
                UpdateWidget(info);
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

        UpdateWidget(info);
    }
}
