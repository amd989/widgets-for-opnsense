using System.Reflection;
using System.Text.Json.Nodes;

namespace OPNsenseWidgets.Provider;

/// <summary>
/// Serves web widget files embedded from Widgets/&lt;Name&gt;/Web/ for requests to <see cref="Origin"/>.
/// URL https://opnsense-widget.test/DhcpLeases/index.html maps to Widgets/DhcpLeases/Web/index.html.
/// The origin does not exist on the network; the Widget Board routes it to the provider through
/// the manifest WebRequestFilter and IWidgetResourceProvider.
/// </summary>
public static class WebContent
{
    public const string Origin = "https://opnsense-widget.test";

    private static readonly Assembly Assembly = typeof(WebContent).Assembly;

    // Lower-cased "folder/file" -> manifest resource name.
    private static readonly Dictionary<string, string> Resources = Assembly.GetManifestResourceNames()
        .Select(name => (name, match: System.Text.RegularExpressions.Regex.Match(name, @"^OPNsenseWidgets\.Widgets\.(\w+)\.Web\.(.+)$")))
        .Where(x => x.match.Success)
        .ToDictionary(x => $"{x.match.Groups[1].Value}/{x.match.Groups[2].Value}".ToLowerInvariant(), x => x.name);

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

        var key = parsed.AbsolutePath.Trim('/').ToLowerInvariant();
        if (!Resources.TryGetValue(key, out var resourceName))
            return false;

        using var stream = Assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            return false;

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        content = buffer.ToArray();
        contentType = Path.GetExtension(key) switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".json" => "application/json",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".woff2" => "font/woff2",
            _ => "application/octet-stream"
        };
        return true;
    }
}
