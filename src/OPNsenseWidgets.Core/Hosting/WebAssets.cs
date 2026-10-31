using System.Reflection;
using System.Text.RegularExpressions;

namespace OPNsenseWidgets.Hosting;

/// <summary>
/// Web widget files embedded from Widgets/&lt;Name&gt;/Web/. Paths mirror the source tree
/// ("DhcpLeases/Web/index.html", "Shared/Web/widget.css") so relative links work in every host
/// and when a page is opened from disk for preview.
/// </summary>
public static class WebAssets
{
    /// <summary>Connection settings, shown while a widget is customized.</summary>
    public const string SettingsPath = "Shared/Web/settings.html";
    /// <summary>"Not set up" and "connection error" states.</summary>
    public const string StatusPath = "Shared/Web/status.html";

    private static readonly Assembly Assembly = typeof(WebAssets).Assembly;

    // Lower-cased "folder/web/file" -> manifest resource name.
    private static readonly Dictionary<string, string> Resources = Assembly.GetManifestResourceNames()
        .Select(name => (name, match: Regex.Match(name, @"^OPNsenseWidgets\.Widgets\.(\w+)\.Web\.(.+)$")))
        .Where(x => x.match.Success)
        .ToDictionary(x => $"{x.match.Groups[1].Value}/web/{x.match.Groups[2].Value}".ToLowerInvariant(), x => x.name);

    /// <summary>Looks up a file by its path ("DhcpLeases/Web/index.html"), case-insensitively.</summary>
    public static bool TryGet(string path, out byte[] content, out string contentType)
    {
        content = [];
        contentType = string.Empty;

        var key = path.Trim('/').ToLowerInvariant();
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
