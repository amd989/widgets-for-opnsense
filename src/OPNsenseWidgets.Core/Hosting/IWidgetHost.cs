namespace OPNsenseWidgets.Hosting;

/// <summary>
/// The OS side of a widget: the Windows Widget Board, or the Linux daemon's WebSocket pages.
/// <see cref="WidgetController"/> owns the state and calls back here to show it.
/// </summary>
public interface IWidgetHost
{
    /// <summary>
    /// Shows the widget's current view (<see cref="WidgetController.GetView"/>). Called after any change that
    /// may switch views, such as settings saved or a fetch failing. Must set <see cref="WidgetInfo.WebView"/>.
    /// </summary>
    void Update(WidgetInfo info);

    /// <summary>Delivers a message to the widget's page. Only called once the page reported ready.</summary>
    void SendToPage(WidgetInfo info, string message);
}

/// <summary>Folders that differ per OS.</summary>
public interface IPlatformPaths
{
    /// <summary>Where the browser saves OPNsense API key files.</summary>
    string DownloadsFolder { get; }
}

public enum WidgetState
{
    /// <summary>Connection settings are being edited.</summary>
    Settings,
    /// <summary>No host URL or credentials yet.</summary>
    Unconfigured,
    /// <summary>The last fetch failed.</summary>
    Error,
    /// <summary>The widget itself.</summary>
    Data
}

/// <summary>What a widget should show, and which page shows it.</summary>
public readonly record struct WidgetView(WidgetState State, string? WebPath)
{
    /// <summary>The "view" field of page messages: "data", "settings" or "status".</summary>
    public string PageView => State switch
    {
        WidgetState.Settings => "settings",
        WidgetState.Data => "data",
        _ => "status"
    };
}
