using System.Reflection;

namespace OPNsenseWidgets.Hosting;

public static class TemplateHelper
{
    private static readonly Assembly Assembly = typeof(TemplateHelper).Assembly;
    private static readonly Dictionary<string, string> Cache = [];

    public static string LoadSharedTemplate(string fileName)
    {
        return LoadTemplate($"OPNsenseWidgets.Templates.{fileName}");
    }

    public static string LoadWidgetTemplate(string widgetFolder, string fileName)
    {
        return LoadTemplate($"OPNsenseWidgets.Widgets.{widgetFolder}.Templates.{fileName}");
    }

    private static string LoadTemplate(string resourceName)
    {
        if (Cache.TryGetValue(resourceName, out var cached))
            return cached;

        using var stream = Assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Embedded resource not found: {resourceName}");

        using var reader = new StreamReader(stream);
        var content = reader.ReadToEnd();
        Cache[resourceName] = content;
        return content;
    }
}
