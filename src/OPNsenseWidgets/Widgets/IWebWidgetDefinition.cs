namespace OPNsenseWidgets.Widgets;

/// <summary>
/// A widget that renders as an HTML page on hosts that support web content.
/// The Adaptive Card from <see cref="IWidgetDefinition.GetTemplate"/> stays as the fallback body.
/// </summary>
public interface IWebWidgetDefinition : IWidgetDefinition
{
    /// <summary>Page path under the packaged origin, e.g. "DhcpLeases/index.html".</summary>
    string WebPath { get; }

    /// <summary>Data pushed to the page as the "payload" of a "data" message.</summary>
    object BuildWebData(object? data);
}
