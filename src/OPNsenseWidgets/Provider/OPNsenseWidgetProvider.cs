using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Windows.Widgets.Providers;
using OPNsenseWidgets.Api;
using OPNsenseWidgets.Configuration;
using OPNsenseWidgets.Widgets;
using OPNsenseWidgets.Widgets.DhcpLeases;
using OPNsenseWidgets.Widgets.Firewall;
using OPNsenseWidgets.Widgets.Traffic;

namespace OPNsenseWidgets.Provider;

public sealed class OPNsenseWidgetProvider : IWidgetProvider, IWidgetProvider2
{
    private readonly Dictionary<string, WidgetInfo> _runningWidgets = [];
    private readonly Dictionary<string, IWidgetDefinition> _definitions = [];
    private readonly object _lock = new();

    // Desktop is always writable for full-trust packaged apps
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        "opnsense-widget-debug.log");

    public ManualResetEventSlim EmptyEvent { get; } = new(false);

    public OPNsenseWidgetProvider()
    {
        Log("Provider constructed");
        RegisterDefinition(new DhcpLeasesWidget());
        RegisterDefinition(new TrafficWidget());
        RegisterDefinition(new FirewallWidget());
    }

    internal static void Log(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
        // Console output appears in VS Output window when debugging
        Console.Error.WriteLine($"[OPNsense] {line}");
        try
        {
            File.AppendAllText(LogPath, line + Environment.NewLine);
        }
        catch { }
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
        Log($"CreateWidget: id={widgetContext.Id} defId={widgetContext.DefinitionId}");
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
            Log($"CreateWidget EXCEPTION: {ex}");
        }
    }

    public void DeleteWidget(string widgetId, string customState)
    {
        Log($"DeleteWidget: id={widgetId}");
        lock (_lock)
        {
            _runningWidgets.Remove(widgetId);
            if (_runningWidgets.Count == 0)
                EmptyEvent.Set();
        }
    }

    public void OnActionInvoked(WidgetActionInvokedArgs actionInvokedArgs)
    {
        Log($"OnActionInvoked: verb={actionInvokedArgs.Verb} id={actionInvokedArgs.WidgetContext.Id}");
        try
        {
            var widgetId = actionInvokedArgs.WidgetContext.Id;
            WidgetInfo? info;
            lock (_lock)
            {
                if (!_runningWidgets.TryGetValue(widgetId, out info))
                    return;
            }

            switch (actionInvokedArgs.Verb)
            {
                case "refresh":
                    _ = RefreshWidgetAsync(info);
                    break;
                case "saveConfig":
                    SaveConfiguration(actionInvokedArgs.Data);
                    info.InCustomization = false;
                    info.TestResult = null;
                    _ = RefreshWidgetAsync(info);
                    break;
                case "cancelConfig":
                    info.InCustomization = false;
                    info.TestResult = null;
                    UpdateWidget(info);
                    break;
                case "testConnection":
                    SaveConfiguration(actionInvokedArgs.Data);
                    _ = TestConnectionAsync(info);
                    break;
                case "importKeyFile":
                    ImportKeyFile(info);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log($"OnActionInvoked EXCEPTION: {ex}");
        }
    }

    public void OnWidgetContextChanged(WidgetContextChangedArgs contextChangedArgs)
    {
        Log($"OnWidgetContextChanged: id={contextChangedArgs.WidgetContext.Id} size={contextChangedArgs.WidgetContext.Size}");
        try
        {
            var widgetId = contextChangedArgs.WidgetContext.Id;
            WidgetInfo? info;
            lock (_lock)
            {
                if (!_runningWidgets.TryGetValue(widgetId, out info))
                    return;
            }

            info.CurrentSize = contextChangedArgs.WidgetContext.Size.ToString().ToLowerInvariant();
            UpdateWidget(info);
        }
        catch (Exception ex)
        {
            Log($"OnWidgetContextChanged EXCEPTION: {ex}");
        }
    }

    public void Activate(WidgetContext widgetContext)
    {
        Log($"Activate: id={widgetContext.Id} size={widgetContext.Size}");
        try
        {
            WidgetInfo? info;
            lock (_lock)
            {
                if (!_runningWidgets.TryGetValue(widgetContext.Id, out info))
                    return;
            }

            info.IsActive = true;
            info.CurrentSize = widgetContext.Size.ToString().ToLowerInvariant();
            UpdateWidget(info);
        }
        catch (Exception ex)
        {
            Log($"Activate EXCEPTION: {ex}");
        }
    }

    public void Deactivate(string widgetId)
    {
        Log($"Deactivate: id={widgetId}");
        lock (_lock)
        {
            if (_runningWidgets.TryGetValue(widgetId, out var info))
                info.IsActive = false;
        }
    }

    public void OnCustomizationRequested(WidgetCustomizationRequestedArgs customizationRequestedArgs)
    {
        Log($"OnCustomizationRequested: id={customizationRequestedArgs.WidgetContext.Id}");
        try
        {
            var widgetId = customizationRequestedArgs.WidgetContext.Id;
            WidgetInfo? info;
            lock (_lock)
            {
                if (!_runningWidgets.TryGetValue(widgetId, out info))
                    return;
            }

            info.InCustomization = true;
            UpdateWidgetWithConfigTemplate(info);
        }
        catch (Exception ex)
        {
            Log($"OnCustomizationRequested EXCEPTION: {ex}");
        }
    }

    public void RecoverRunningWidgets()
    {
        Log("RecoverRunningWidgets called");
        var widgetManager = WidgetManager.GetDefault();
        var existingWidgets = widgetManager.GetWidgetInfos();

        Log($"RecoverRunningWidgets: found {existingWidgets?.Length ?? 0} existing widgets");

        if (existingWidgets is null || existingWidgets.Length == 0)
            return;

        lock (_lock)
        {
            foreach (var existing in existingWidgets)
            {
                if (!_runningWidgets.ContainsKey(existing.WidgetContext.Id))
                {
                    Log($"RecoverRunningWidgets: recovering id={existing.WidgetContext.Id} defId={existing.WidgetContext.DefinitionId}");
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
        Log($"UpdateWidget: id={info.WidgetId} defId={info.DefinitionId} size={info.CurrentSize}");
        try
        {
            var settings = SettingsManager.Load();
            var credentials = CredentialStore.LoadCredentials();

            string template;
            string data;

            if (info.InCustomization)
            {
                UpdateWidgetWithConfigTemplate(info);
                return;
            }
            else if (!settings.IsConfigured || credentials is null)
            {
                Log("UpdateWidget: using UnconfiguredTemplate");
                template = TemplateHelper.LoadSharedTemplate("UnconfiguredTemplate.json");
                data = "{}";
            }
            else if (info.LastError is not null)
            {
                Log($"UpdateWidget: using ErrorTemplate, error={info.LastError}");
                template = TemplateHelper.LoadSharedTemplate("ErrorTemplate.json");
                data = JsonSerializer.Serialize(new { errorMessage = info.LastError });
            }
            else if (_definitions.TryGetValue(info.DefinitionId, out var definition))
            {
                Log($"UpdateWidget: using data template for size={info.CurrentSize}");
                template = definition.GetTemplate(info.CurrentSize);
                data = definition.BuildDataJson(info.CachedData, info.CurrentSize);
            }
            else
            {
                Log($"UpdateWidget: no definition found for {info.DefinitionId}");
                return;
            }

            Log($"UpdateWidget: template length={template.Length} data length={data.Length}");

            var updateOptions = new WidgetUpdateRequestOptions(info.WidgetId)
            {
                Template = template,
                Data = data,
                CustomState = info.CustomState
            };

            Log("UpdateWidget: calling WidgetManager.UpdateWidget");
            WidgetManager.GetDefault().UpdateWidget(updateOptions);
            Log("UpdateWidget: WidgetManager.UpdateWidget succeeded");
        }
        catch (Exception ex)
        {
            Log($"UpdateWidget EXCEPTION: {ex}");
        }
    }

    private void UpdateWidgetWithConfigTemplate(WidgetInfo info)
    {
        Log($"UpdateWidgetWithConfigTemplate: id={info.WidgetId}");
        try
        {
            var settings = SettingsManager.Load();
            var credentials = CredentialStore.LoadCredentials();

            var template = TemplateHelper.LoadSharedTemplate("ConfigurationTemplate.json");
            var data = JsonSerializer.Serialize(new
            {
                hostUrl = settings.HostUrl,
                apiKey = credentials?.ApiKey ?? "",
                apiSecret = credentials?.ApiSecret ?? "",
                allowSelfSigned = settings.AllowSelfSignedCertificates,
                refreshInterval = settings.RefreshIntervalSeconds,
                testResult = info.TestResult ?? "",
                hasTestResult = !string.IsNullOrEmpty(info.TestResult)
            });

            var updateOptions = new WidgetUpdateRequestOptions(info.WidgetId)
            {
                Template = template,
                Data = data,
                CustomState = info.CustomState
            };

            WidgetManager.GetDefault().UpdateWidget(updateOptions);
            Log("UpdateWidgetWithConfigTemplate: succeeded");
        }
        catch (Exception ex)
        {
            Log($"UpdateWidgetWithConfigTemplate EXCEPTION: {ex}");
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

            if (!string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(apiSecret))
                CredentialStore.SaveCredentials(apiKey, apiSecret);

            var settings = SettingsManager.Load();
            settings.HostUrl = hostUrl;
            settings.AllowSelfSignedCertificates = allowSelfSigned;
            settings.RefreshIntervalSeconds = refreshInterval;
            SettingsManager.Save(settings);

            Log($"SaveConfiguration: saved host={hostUrl} allowSelfSigned={allowSelfSigned} interval={refreshInterval}");
        }
        catch (Exception ex)
        {
            Log($"SaveConfiguration EXCEPTION: {ex}");
        }
    }

    public async Task RefreshWidgetAsync(WidgetInfo info)
    {
        Log($"RefreshWidgetAsync: id={info.WidgetId}");
        if (!_definitions.TryGetValue(info.DefinitionId, out var definition))
            return;

        var settings = SettingsManager.Load();
        var credentials = CredentialStore.LoadCredentials();

        if (!settings.IsConfigured || credentials is null)
        {
            info.LastError = null;
            info.CachedData = null;
            UpdateWidget(info);
            return;
        }

        try
        {
            using var client = new OPNsenseApiClient(
                settings.HostUrl,
                credentials.Value.ApiKey,
                credentials.Value.ApiSecret,
                settings.AllowSelfSignedCertificates);

            var data = await definition.FetchDataAsync(client);
            info.CachedData = data;
            info.LastError = null;
            info.LastRefresh = DateTime.Now;
            Log($"RefreshWidgetAsync: fetch succeeded");
        }
        catch (Exception ex)
        {
            info.LastError = ex.Message;
            Log($"RefreshWidgetAsync EXCEPTION: {ex}");
        }

        UpdateWidget(info);
    }

    private async Task TestConnectionAsync(WidgetInfo info)
    {
        Log("TestConnectionAsync called");
        var settings = SettingsManager.Load();
        var credentials = CredentialStore.LoadCredentials();

        if (!settings.IsConfigured || credentials is null)
        {
            info.TestResult = "Enter URL and credentials first.";
            UpdateWidgetWithConfigTemplate(info);
            return;
        }

        try
        {
            using var client = new OPNsenseApiClient(
                settings.HostUrl,
                credentials.Value.ApiKey,
                credentials.Value.ApiSecret,
                settings.AllowSelfSignedCertificates);

            await client.GetLeasesAsync(page: 1, rowCount: 1);
            info.LastError = null;
            info.TestResult = "Connection successful!";

            if (_definitions.TryGetValue(info.DefinitionId, out var definition))
            {
                var data = await definition.FetchDataAsync(client);
                info.CachedData = data;
                info.LastRefresh = DateTime.Now;
            }

            Log("TestConnectionAsync: connection OK");
        }
        catch (Exception ex)
        {
            info.TestResult = $"Failed: {ex.Message}";
            Log($"TestConnectionAsync EXCEPTION: {ex}");
        }

        UpdateWidgetWithConfigTemplate(info);
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
        Log("ImportKeyFile called");
        try
        {
            var downloadsPath = GetDownloadsPath();
            Log($"ImportKeyFile: scanning {downloadsPath}");

            var keyFiles = Directory.GetFiles(downloadsPath, "*_apikey.txt");
            if (keyFiles.Length == 0)
            {
                info.TestResult = "No *_apikey.txt found in Downloads.";
                UpdateWidgetWithConfigTemplate(info);
                return;
            }

            // Pick the most recently modified file
            var newest = keyFiles.OrderByDescending(File.GetLastWriteTime).First();
            Log($"ImportKeyFile: parsing {newest}");

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
                UpdateWidgetWithConfigTemplate(info);
                return;
            }

            CredentialStore.SaveCredentials(apiKey, apiSecret);
            info.TestResult = $"Imported from {Path.GetFileName(newest)}";
            Log($"ImportKeyFile: imported from {Path.GetFileName(newest)}");
        }
        catch (Exception ex)
        {
            info.TestResult = $"Import failed: {ex.Message}";
            Log($"ImportKeyFile EXCEPTION: {ex}");
        }

        UpdateWidgetWithConfigTemplate(info);
    }
}
