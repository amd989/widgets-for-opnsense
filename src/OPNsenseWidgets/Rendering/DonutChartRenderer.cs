using SkiaSharp;

namespace OPNsenseWidgets.Rendering;

public static class DonutChartRenderer
{
    public static readonly SKColor[] Palette =
    [
        new SKColor(251, 146,  60),  // orange
        new SKColor( 74, 222, 128),  // green
        new SKColor(248, 113, 113),  // red
        new SKColor(167, 139, 250),  // purple
        new SKColor(180, 128,  85),  // brown
        new SKColor(244, 114, 182),  // pink
        new SKColor(148, 163, 184),  // slate
        new SKColor( 96, 165, 250),  // blue
        new SKColor(251, 191,  36),  // amber
        new SKColor( 45, 212, 160),  // teal
    ];

    /// <summary>
    /// Renders a donut chart PNG as a data URI. Slices rendered clockwise from 12 o'clock
    /// in the order supplied — so the palette index matches the legend order.
    /// </summary>
    public static string RenderDonut(
        IReadOnlyList<double> values,
        int size = 200,
        float ringThickness = 42f,
        float gapDegrees = 2f)
    {
        if (values.Count == 0) return "";
        var total = values.Sum();
        if (total <= 0) return "";

        using var surface = SKSurface.Create(
            new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        var outerRadius = size / 2f - 2f;
        var innerRadius = outerRadius - ringThickness;
        if (innerRadius < 4f) innerRadius = 4f;

        var center = new SKPoint(size / 2f, size / 2f);
        var midRadius = (outerRadius + innerRadius) / 2f;
        var rect = new SKRect(
            center.X - midRadius, center.Y - midRadius,
            center.X + midRadius, center.Y + midRadius);

        var startAngle = -90f; // start at 12 o'clock

        // Only apply gaps when there are ≥2 visible slices
        var visibleSlices = values.Count(v => v > 0);
        var effectiveGap = visibleSlices > 1 ? gapDegrees : 0f;

        using var paint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = ringThickness,
            StrokeCap = SKStrokeCap.Butt
        };

        for (var i = 0; i < values.Count; i++)
        {
            var v = values[i];
            if (v <= 0) continue;

            var sweep = (float)(v / total * 360.0);
            var drawSweep = Math.Max(0.01f, sweep - effectiveGap);

            paint.Color = Palette[i % Palette.Length];

            using var path = new SKPath();
            path.AddArc(rect, startAngle + effectiveGap / 2f, drawSweep);
            canvas.DrawPath(path, paint);

            startAngle += sweep;
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return $"data:image/png;base64,{Convert.ToBase64String(data.ToArray())}";
    }

    /// <summary>
    /// Renders the legend with a colored swatch + label per series.
    /// <paramref name="columns"/> &gt; 1 lays items out in multiple columns, reducing height.
    /// </summary>
    public static string RenderLegend(
        IReadOnlyList<string> labels,
        int width = 240,
        int rowHeight = 22,
        float textSize = 12f,
        int columns = 1)
    {
        if (labels.Count == 0) return "";

        columns = Math.Max(1, columns);
        var rows = (int)Math.Ceiling(labels.Count / (double)columns);
        var height = rowHeight * rows;
        var colWidth = width / columns;

        using var surface = SKSurface.Create(
            new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        using var font = new SKFont(SKTypeface.Default, textSize);
        using var textPaint = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(220, 220, 220)
        };

        const float swatchW = 14f;
        const float swatchH = 10f;
        const float swatchRadius = 2f;
        const float gap = 6f;
        const float padL = 4f;

        var textOffX = padL + swatchW + gap;
        var maxTextW = colWidth - textOffX - 4f;

        for (var i = 0; i < labels.Count; i++)
        {
            var col = i / rows;
            var row = i % rows;
            var originX = col * colWidth;
            var midY = rowHeight * (row + 0.5f);

            using var swatchPaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
                Color = Palette[i % Palette.Length]
            };
            var swatch = new SKRect(
                originX + padL, midY - swatchH / 2f,
                originX + padL + swatchW, midY + swatchH / 2f);
            canvas.DrawRoundRect(swatch, swatchRadius, swatchRadius, swatchPaint);

            var label = TruncateToWidth(labels[i], font, maxTextW);
            canvas.DrawText(label, originX + textOffX, midY + textSize / 2f - 2f,
                SKTextAlign.Left, font, textPaint);
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return $"data:image/png;base64,{Convert.ToBase64String(data.ToArray())}";
    }

    private static string TruncateToWidth(string text, SKFont font, float maxWidth)
    {
        if (font.MeasureText(text) <= maxWidth) return text;
        var ellipsis = "\u2026";
        var ellipsisW = font.MeasureText(ellipsis);
        // Binary-trim to fit
        var lo = 0;
        var hi = text.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            var candidate = text[..mid];
            if (font.MeasureText(candidate) + ellipsisW <= maxWidth)
                lo = mid;
            else
                hi = mid - 1;
        }
        return lo > 0 ? text[..lo].TrimEnd() + ellipsis : ellipsis;
    }
}
