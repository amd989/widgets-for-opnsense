using SkiaSharp;

namespace OPNsenseWidgets.Rendering;

public record DeviceCardData(string Hostname, string Meta, string Time, string? Mac);

public static class DeviceCardRenderer
{
    public static string RenderCard(DeviceCardData d, int logicalWidth, bool showMac = false)
    {
        const int scale = 3;
        var width = logicalWidth * scale;
        const int padH = 10 * scale, padV = 7 * scale;
        const float radius = 6f * scale;
        const float hostTextSize = 13f * scale;
        const float subTextSize = 11f * scale;
        const float macTextSize = 10f * scale;
        const int lineGap = 2 * scale;

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
