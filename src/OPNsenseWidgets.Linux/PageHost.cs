using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OPNsenseWidgets.Hosting;

namespace OPNsenseWidgets.Linux;

/// <summary>
/// <see cref="IWidgetHost"/> for desktop widgets that load the pages from this daemon (KDE Plasma).
/// Each widget instance keeps one WebSocket open from its page. The messages are the same as on the
/// Windows Widget Board; switching views (data, settings, status) sends {type:"navigate", path}
/// because the page cannot be swapped from outside like the board does.
/// </summary>
public sealed class PageHost : IWidgetHost
{
    private readonly ConcurrentDictionary<string, PageConnection> _pages = new();
    private readonly WidgetController _controller;
    private readonly ILogger<PageHost> _logger;

    public PageHost(WidgetController controller, ILogger<PageHost> logger)
    {
        _controller = controller;
        _logger = logger;
        _controller.AttachHost(this);
    }

    /// <summary>The widget, created on first contact. Null for an unknown definition.</summary>
    public WidgetInfo? GetWidget(string widgetId, string definitionId)
    {
        if (!_controller.Definitions.ContainsKey(definitionId))
            return null;

        var info = _controller.GetOrAdd(widgetId, () => new WidgetInfo
        {
            WidgetId = widgetId,
            DefinitionId = definitionId
        }, out var added);

        if (added)
            _logger.LogInformation("New widget {WidgetId} ({DefinitionId})", widgetId, definitionId);
        return info;
    }

    /// <summary>Page path for the widget's current view, e.g. "DhcpLeases/Web/index.html".</summary>
    public string? CurrentPath(WidgetInfo info) => _controller.GetView(info).WebPath;

    public void Update(WidgetInfo info)
    {
        var view = _controller.GetView(info);
        info.WebView = view.PageView;

        if (!_pages.TryGetValue(info.WidgetId, out var page) || view.WebPath is null)
            return;

        if (!SamePath(page.Path, view.WebPath))
        {
            // The page reconnects from the new URL and says "ready" again.
            info.WebReady = false;
            page.Send(Navigate(view.WebPath));
            return;
        }

        _controller.PushWebData(info);
    }

    public void SendToPage(WidgetInfo info, string message)
    {
        if (_pages.TryGetValue(info.WidgetId, out var page))
            page.Send(message);
    }

    /// <summary>Runs one page's WebSocket until it closes.</summary>
    public async Task RunAsync(WebSocket socket, WidgetInfo info, string pagePath, CancellationToken ct)
    {
        var page = new PageConnection(socket, pagePath, _logger);
        if (_pages.TryGetValue(info.WidgetId, out var previous))
            previous.Abort(); // a reload or a second view of the same widget: the newest page wins
        _pages[info.WidgetId] = page;

        info.IsActive = true;
        info.WebReady = false;
        _logger.LogInformation("Page connected: {WidgetId} {Path}", info.WidgetId, pagePath);

        // A page opened from an old URL (settings saved elsewhere, say) goes to the current view.
        var view = _controller.GetView(info);
        info.WebView = view.PageView;
        if (view.WebPath is not null && !SamePath(pagePath, view.WebPath))
            page.Send(Navigate(view.WebPath));

        try
        {
            var buffer = new byte[16 * 1024];
            using var message = new MemoryStream();
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                message.Write(buffer, 0, result.Count);
                if (message.Length > 256 * 1024)
                    break; // pages only send small JSON messages
                if (!result.EndOfMessage)
                    continue;

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                    _logger.LogDebug("Page message {WidgetId}: {Message}", info.WidgetId, text);
                    _controller.OnPageMessage(info, text);
                }
                message.SetLength(0);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            // Page closed or daemon stopping.
        }
        finally
        {
            if (_pages.TryGetValue(info.WidgetId, out var current) && current == page)
            {
                _pages.TryRemove(info.WidgetId, out _);
                info.IsActive = false;
                info.WebReady = false;
            }
            await page.CloseAsync();
            _logger.LogInformation("Page disconnected: {WidgetId}", info.WidgetId);
        }
    }

    private static string Navigate(string webPath) =>
        JsonSerializer.Serialize(new { type = "navigate", path = "/" + webPath.TrimStart('/') });

    private static bool SamePath(string a, string b) =>
        string.Equals(a.Trim('/'), b.Trim('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>One page's socket. Sends are queued so they never overlap.</summary>
    private sealed class PageConnection(WebSocket socket, string path, ILogger logger)
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public string Path { get; } = path;

        public void Send(string message)
        {
            _ = Task.Run(async () =>
            {
                await _sendLock.WaitAsync();
                try
                {
                    if (socket.State == WebSocketState.Open)
                        await socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Send to page failed");
                }
                finally
                {
                    _sendLock.Release();
                }
            });
        }

        public void Abort() => socket.Abort();

        public async Task CloseAsync()
        {
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
            catch (Exception)
            {
                // Already gone.
            }
        }
    }
}
