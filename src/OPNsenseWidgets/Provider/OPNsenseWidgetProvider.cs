using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Windows.Widgets.Providers;
using OPNsenseWidgets.Configuration;
using OPNsenseWidgets.Hosting;
using OPNsenseWidgets.Widgets;
using Windows.Storage.Streams;
using WidgetInfo = OPNsenseWidgets.Hosting.WidgetInfo;

namespace OPNsenseWidgets.Provider;

/// <summary>
/// The Widget Board side of the widgets: board callbacks, Adaptive Card templates and web content.
/// Widget state and behavior live in <see cref="WidgetController"/>.
/// </summary>
public sealed class OPNsenseWidgetProvider : IWidgetProvider, IWidgetProvider2, IWidgetResourceProvider, IWidgetProviderMessage, IWidgetHost
{
    private readonly WidgetController _controller;
    private readonly ISettingsManager _settingsManager;
    private readonly ICredentialStore _credentialStore;
    private readonly ILogger<OPNsenseWidgetProvider> _logger;

    public OPNsenseWidgetProvider(
        WidgetController controller,
        ISettingsManager settingsManager,
        ICredentialStore credentialStore,
        ILogger<OPNsenseWidgetProvider> logger)
    {
        _controller = controller;
        _settingsManager = settingsManager;
        _credentialStore = credentialStore;
        _logger = logger;

        _controller.AttachHost(this);
        _logger.LogInformation("Provider constructed");
    }

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

            _controller.Add(info);
            Update(info);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CreateWidget failed");
        }
    }

    public void DeleteWidget(string widgetId, string customState)
    {
        _logger.LogInformation("DeleteWidget: id={WidgetId}", widgetId);
        _controller.Remove(widgetId);
    }

    public void OnActionInvoked(WidgetActionInvokedArgs actionInvokedArgs)
    {
        _logger.LogInformation("OnActionInvoked: verb={Verb} id={WidgetId}", actionInvokedArgs.Verb, actionInvokedArgs.WidgetContext.Id);
        try
        {
            var info = GetOrAddWidget(actionInvokedArgs.WidgetContext);
            _controller.HandleAction(info, actionInvokedArgs.Verb, actionInvokedArgs.Data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OnActionInvoked failed");
        }
    }

    public void OnWidgetContextChanged(WidgetContextChangedArgs contextChangedArgs)
    {
        _logger.LogInformation("OnWidgetContextChanged: id={WidgetId} size={Size}", contextChangedArgs.WidgetContext.Id, contextChangedArgs.WidgetContext.Size);
        try
        {
            var info = GetOrAddWidget(contextChangedArgs.WidgetContext);

            info.CurrentSize = contextChangedArgs.WidgetContext.Size.ToString().ToLowerInvariant();
            Update(info);
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
            Update(info);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Activate failed");
        }
    }

    public void Deactivate(string widgetId)
    {
        _logger.LogInformation("Deactivate: id={WidgetId}", widgetId);
        if (!_controller.TryGet(widgetId, out var info))
            return;
        info.IsActive = false;

        // The board's Customize dialog closing (X, Save then X, or the board closing) shows up as a
        // Deactivate. Leave settings so the widget comes back with its data on the next Activate.
        // The grace period skips a Deactivate the board may send while the dialog is still opening.
        if (info.InCustomization && info.CustomizingInDialog &&
            DateTime.Now - info.CustomizationStartedAt > TimeSpan.FromSeconds(2))
        {
            _logger.LogInformation("Deactivate: customize dialog closed for {WidgetId}", widgetId);
            WidgetController.EndCustomization(info);
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
            WidgetController.ClearTestResult(info);
            Update(info);
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
        var info = _controller.GetOrAdd(context.Id, () => new WidgetInfo
        {
            WidgetId = context.Id,
            DefinitionId = context.DefinitionId,
            IsActive = context.IsActive,
            CurrentSize = context.Size.ToString().ToLowerInvariant()
        }, out var added);

        if (added)
            _logger.LogInformation("GetOrAddWidget: adopted id={WidgetId} defId={DefinitionId}", context.Id, context.DefinitionId);
        return info;
    }

    public void RecoverRunningWidgets()
    {
        _logger.LogInformation("RecoverRunningWidgets called");
        var widgetManager = WidgetManager.GetDefault();
        var existingWidgets = widgetManager.GetWidgetInfos();

        _logger.LogInformation("RecoverRunningWidgets: found {Count} existing widgets", existingWidgets?.Length ?? 0);

        if (existingWidgets is null || existingWidgets.Length == 0)
            return;

        foreach (var existing in existingWidgets)
        {
            var context = existing.WidgetContext;
            _controller.GetOrAdd(context.Id, () => new WidgetInfo
            {
                WidgetId = context.Id,
                DefinitionId = context.DefinitionId,
                IsActive = context.IsActive,
                CustomState = existing.CustomState ?? string.Empty
            }, out var added);

            if (added)
                _logger.LogInformation("RecoverRunningWidgets: recovering id={WidgetId} defId={DefinitionId}", context.Id, context.DefinitionId);
        }
    }

    // ─── IWidgetHost ───

    public void Update(WidgetInfo info)
    {
        _logger.LogDebug("UpdateWidget: id={WidgetId} defId={DefinitionId} size={Size}", info.WidgetId, info.DefinitionId, info.CurrentSize);
        try
        {
            if (!_controller.Definitions.TryGetValue(info.DefinitionId, out var definition))
            {
                _logger.LogWarning("UpdateWidget: no definition found for {DefinitionId}", info.DefinitionId);
                return;
            }

            var view = _controller.GetView(info);
            var (template, data) = BuildCard(info, definition, view.State);

            // A widget with web content gets a page for every state, not only its data view: the board
            // fails to load plain cards (settings, errors) for these widgets. The card body stays as the
            // fallback for hosts without web content.
            var isWeb = definition is IWebWidgetDefinition;
            if (isWeb && view.WebPath is not null)
                template = WebContent.WithWebUrl(template, WebContent.UrlFor(view.WebPath));

            _logger.LogDebug("UpdateWidget: view={View} template length={TemplateLength} data length={DataLength}", view.PageView, template.Length, data.Length);

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
            info.WebView = view.PageView;

            _logger.LogDebug("UpdateWidget: calling WidgetManager.UpdateWidget");
            WidgetManager.GetDefault().UpdateWidget(updateOptions);
            _logger.LogDebug("UpdateWidget: WidgetManager.UpdateWidget succeeded");

            // Same template and a live page: it did not reload, so it still needs the new data.
            // (PushWebData is a no-op unless the page already said "ready".)
            if (isWeb)
                _controller.PushWebData(info);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UpdateWidget failed");
        }
    }

    /// <summary>The Adaptive Card for a state: the whole widget on hosts without web content.</summary>
    private (string Template, string Data) BuildCard(WidgetInfo info, IWidgetDefinition definition, WidgetState state)
    {
        switch (state)
        {
            case WidgetState.Settings:
                var settings = _settingsManager.Load();
                var credentials = _credentialStore.LoadCredentials();
                return (TemplateHelper.LoadSharedTemplate("ConfigurationTemplate.json"), JsonSerializer.Serialize(new
                {
                    hostUrl = settings.HostUrl,
                    apiKey = credentials?.ApiKey ?? "",
                    apiSecret = credentials?.ApiSecret ?? "",
                    allowSelfSigned = settings.AllowSelfSignedCertificates,
                    refreshInterval = settings.RefreshIntervalSeconds,
                    testResult = info.TestResult ?? "",
                    hasTestResult = !string.IsNullOrEmpty(info.TestResult)
                }));
            case WidgetState.Unconfigured:
                return (TemplateHelper.LoadSharedTemplate("UnconfiguredTemplate.json"), "{}");
            case WidgetState.Error:
                return (TemplateHelper.LoadSharedTemplate("ErrorTemplate.json"),
                    JsonSerializer.Serialize(new { errorMessage = info.LastError }));
            default:
                return (definition.GetTemplate(info.CurrentSize), definition.BuildDataJson(info.CachedData, info.CurrentSize));
        }
    }

    // Always async: never call back into the Widget service on the thread of one of its callbacks.
    // Calling SendMessageToContent from inside a board callback (Activate etc.) while the page was
    // still being created deadlocked the Widget service.
    public void SendToPage(WidgetInfo info, string message)
    {
        var widgetId = info.WidgetId;
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

    // ─── Web content (Windows App SDK 1.6.3+) ───

    // Every request is logged so the debug log shows whether the page itself, or only its
    // subresources, reach the provider.
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
        _logger.LogInformation("OnMessageReceived: id={WidgetId} message={Message}", args.WidgetContext.Id, args.Message);
        var info = GetOrAddWidget(args.WidgetContext);
        _controller.OnPageMessage(info, args.Message);
    }
}
