using SkiaSharp;

namespace OPNsenseWidgets.Rendering;

public record DeviceCardData(string Hostname, string Meta, string Time, string? Mac, string Status = "active");

public static class DeviceCardRenderer
{
    private static readonly Dictionary<string, SKColor> AccentColors = new()
    {
        ["new"] = new SKColor(91, 163, 255),       // blue
        ["active"] = new SKColor(45, 212, 160),     // green
        ["reserved"] = new SKColor(155, 135, 245),  // purple
        ["stale"] = new SKColor(255, 255, 255, 31), // subtle gray
    };

    public static string RenderCard(DeviceCardData d, int logicalWidth, bool showMac = false, bool compact = false)
    {
        const int scale = 3;
        var width = logicalWidth * scale;
        var padH = (compact ? 8 : 10) * scale;
        var padV = (compact ? 6 : 10) * scale;
        const float radius = 6f * scale;
        var hostTextSize = (compact ? 12f : 15f) * scale;
        var subTextSize = (compact ? 10f : 13f) * scale;
        var macTextSize = (compact ? 9f : 11f) * scale;
        var lineGap = (compact ? 2 : 4) * scale;

        var hasMeta = !string.IsNullOrEmpty(d.Meta);
        var hasMac = showMac && !string.IsNullOrEmpty(d.Mac);

        var height = padV * 2 + (int)hostTextSize;
        if (hasMeta) height += (int)subTextSize + lineGap;
        if (hasMac) height += (int)macTextSize + lineGap;

        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        using var bg = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Color = new SKColor(255, 255, 255, 22)
        };
        canvas.DrawRoundRect(new SKRect(0, 0, width, height), radius, radius, bg);

        // Status accent bar (left edge)
        const float barWidth = 2.5f * scale;
        const float barInset = 6f * scale;
        const float barRadius = 1.5f * scale;
        var accentColor = AccentColors.GetValueOrDefault(d.Status, AccentColors["stale"]);
        using var barPaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Color = accentColor
        };
        var barRect = new SKRect(0, barInset, barWidth, height - barInset);
        canvas.DrawRoundRect(barRect, barRadius, barRadius, barPaint);

        using var hostPaint = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(255, 255, 255, 230),
            TextSize = hostTextSize,
            Typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold)
        };
        using var subPaint = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(255, 255, 255, 120),
            TextSize = subTextSize,
            Typeface = SKTypeface.FromFamilyName("Segoe UI")
        };
        using var macPaint = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(255, 255, 255, 90),
            TextSize = macTextSize,
            Typeface = SKTypeface.FromFamilyName("Consolas")
        };

        float tx = padH;
        float ty = padV + hostTextSize;

        var timeW = subPaint.MeasureText(d.Time);
        var maxHostW = width - padH * 2 - timeW - 6 * scale;
        canvas.DrawText(Truncate(d.Hostname, hostPaint, maxHostW), tx, ty, hostPaint);
        canvas.DrawText(d.Time, width - padH - timeW, ty, subPaint);

        if (hasMeta)
        {
            ty += subTextSize + lineGap;
            canvas.DrawText(Truncate(d.Meta, subPaint, width - padH * 2), tx, ty, subPaint);
        }

        if (hasMac)
        {
            ty += macTextSize + lineGap;
            canvas.DrawText(d.Mac!, tx, ty, macPaint);
        }

        using var img = surface.Snapshot();
        using var png = img.Encode(SKEncodedImageFormat.Png, 90);
        return $"data:image/png;base64,{Convert.ToBase64String(png.ToArray())}";
    }

    private static string Truncate(string text, SKPaint paint, float maxW)
    {
        if (paint.MeasureText(text) <= maxW) return text;
        for (var n = text.Length - 1; n > 0; n--)
            if (paint.MeasureText(text[..n] + "\u2026") <= maxW)
                return text[..n] + "\u2026";
        return "\u2026";
    }
}
