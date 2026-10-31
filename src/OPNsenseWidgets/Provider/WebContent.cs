using System.Text.Json.Nodes;
using OPNsenseWidgets.Hosting;

namespace OPNsenseWidgets.Provider;

/// <summary>
/// Serves <see cref="WebAssets"/> to the Widget Board for requests to <see cref="Origin"/>.
/// URL https://opnsense-widget.test/DhcpLeases/Web/index.html maps to Widgets/DhcpLeases/Web/index.html.
/// The origin does not exist on the network; the Widget Board routes it to the provider through
/// the manifest WebRequestFilter and IWidgetResourceProvider.
/// </summary>
public static class WebContent
{
    public const string Origin = "https://opnsense-widget.test";

    public static string UrlFor(string path) => $"{Origin}/{path.TrimStart('/')}";

    /// <summary>Adds metadata.webUrl to an Adaptive Card template, keeping its body as the fallback.</summary>
    public static string WithWebUrl(string template, string webUrl)
    {
        var card = JsonNode.Parse(template)!.AsObject();
        var metadata = card["metadata"] as JsonObject ?? new JsonObject();
        metadata["webUrl"] = webUrl;
        card["metadata"] = metadata;
        return card.ToJsonString();
    }

    public static bool TryGet(string uri, out byte[] content, out string contentType)
    {
        content = [];
        contentType = string.Empty;

        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            return false;
        if (!string.Equals(parsed.GetLeftPart(UriPartial.Authority), Origin, StringComparison.OrdinalIgnoreCase))
            return false;

        return WebAssets.TryGet(parsed.AbsolutePath, out content, out contentType);
    }
}
