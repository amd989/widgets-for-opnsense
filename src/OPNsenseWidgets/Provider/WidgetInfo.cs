namespace OPNsenseWidgets.Provider;

public sealed class WidgetInfo
{
    public required string WidgetId { get; init; }
    public required string DefinitionId { get; init; }
    public bool IsActive { get; set; }
    public bool InCustomization { get; set; }
    /// <summary>
    /// Customization runs in the board's Customize dialog (not in the widget itself). The provider
    /// cannot close that dialog, so Save stays on the settings page, and the dialog closing
    /// (a Deactivate) ends customization.
    /// </summary>
    public bool CustomizingInDialog { get; set; }
    public DateTime CustomizationStartedAt { get; set; }
    public string CustomState { get; set; } = string.Empty;
    public object? CachedData { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastRefresh { get; set; }
    public DateTime? LastRefreshAttempt { get; set; }
    public string CurrentSize { get; set; } = "medium";
    public string? TestResult { get; set; }
    /// <summary>Whether <see cref="TestResult"/> is a success (true), a failure (false) or just information (null).</summary>
    public bool? TestOk { get; set; }
    /// <summary>Which web page the widget shows: "data" (the widget itself), "settings" or "status".</summary>
    public string WebView { get; set; } = "data";
    /// <summary>Last template sent to the board, used to tell whether the web page will reload.</summary>
    public string? LastTemplate { get; set; }
    /// <summary>True once the web page posted "ready" for the current template; refreshes then push data by message.</summary>
    public bool WebReady { get; set; }
}
