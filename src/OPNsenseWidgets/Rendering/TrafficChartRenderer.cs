using SkiaSharp;

namespace OPNsenseWidgets.Rendering;

public static class TrafficChartRenderer
{
    /// <summary>
    /// Renders a dual-line area chart (download + upload) as a PNG data URI.
    /// Samples should be ordered oldest-first.
    /// </summary>
    public static string Render(
        double[] inValues,
        double[] outValues,
        int width = 560,
        int height = 100)
    {
        if (inValues.Length < 2) return "";

        var maxVal = Math.Max(
            inValues.Max(),
            outValues.Max());
        if (maxVal == 0) maxVal = 1;

        using var surface = SKSurface.Create(
            new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        const float pad = 4f;
        var plotW = width - pad * 2;
        var plotH = height - pad * 2;
        var count = inValues.Length;

        // Download (green) — drawn first so upload overlaps on top
        DrawSeries(canvas, inValues, maxVal, pad, pad, plotW, plotH, count,
            new SKColor(45, 212, 160));

        // Upload (blue)
        DrawSeries(canvas, outValues, maxVal, pad, pad, plotW, plotH, count,
            new SKColor(96, 165, 250));

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return $"data:image/png;base64,{Convert.ToBase64String(data.ToArray())}";
    }

    /// <summary>
    /// Renders a horizontal legend strip — colored dot + label for each interface.
    /// Uses the same palette order as RenderMultiSeries.
    /// </summary>
    public static string RenderLegend(IReadOnlyList<string> labels, int width = 560)
    {
        if (labels.Count == 0) return "";

        const int height   = 22;
        const int dotW     = 16;
        const int dotH     = 4;
        const float textSz = 13f;
        const int gap      = 6;   // dot → text
        const int itemGap  = 16;  // between items

        using var surface = SKSurface.Create(
            new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        using var font = new SKFont(SKTypeface.Default, textSz);

        // Measure items to center the row
        var itemWidths = new float[labels.Count];
        float totalW = 0;
        for (var i = 0; i < labels.Count; i++)
        {
            itemWidths[i] = dotW + gap + font.MeasureText(labels[i]);
            totalW += itemWidths[i] + (i > 0 ? itemGap : 0);
        }

        float x = Math.Max(4f, (width - totalW) / 2f);
        float midY = height / 2f;

        using var textPaint = new SKPaint { IsAntialias = true };

        for (var i = 0; i < labels.Count; i++)
        {
            var color = Palette[i % Palette.Length];

            using var linePaint = new SKPaint
            {
                IsAntialias = true,
                Style       = SKPaintStyle.Fill,
                Color       = color
            };

            // Colored line segment
            var lineRect = new SKRect(x, midY - dotH / 2f, x + dotW, midY + dotH / 2f);
            canvas.DrawRoundRect(lineRect, 2, 2, linePaint);

            // Label
            textPaint.Color = new SKColor(210, 210, 210);
            canvas.DrawText(labels[i], x + dotW + gap, midY + textSz / 2f - 2f,
                SKTextAlign.Left, font, textPaint);

            x += itemWidths[i] + itemGap;
        }

        using var image = surface.Snapshot();
        using var data  = image.Encode(SKEncodedImageFormat.Png, 90);
        return $"data:image/png;base64,{Convert.ToBase64String(data.ToArray())}";
    }

    private static readonly SKColor[] Palette =
    [
        new SKColor(45,  212, 160),  // teal
        new SKColor(96,  165, 250),  // blue
        new SKColor(251, 191,  36),  // amber
        new SKColor(248, 113, 113),  // red
        new SKColor(167, 139, 250),  // purple
    ];

    /// <summary>
    /// Renders multiple single-direction series (e.g. all-interface download) on one chart.
    /// </summary>
    public static string RenderMultiSeries(
        IReadOnlyList<(string Label, double[] Values)> series,
        int width = 560,
        int height = 90)
    {
        if (series.Count == 0) return "";
        var maxLen = series.Max(s => s.Values.Length);
        if (maxLen < 2) return "";

        var maxVal = series.SelectMany(s => s.Values).DefaultIfEmpty(1).Max();
        if (maxVal == 0) maxVal = 1;

        using var surface = SKSurface.Create(
            new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        const float pad = 4f;
        var plotW = width - pad * 2;
        var plotH = height - pad * 2;

        for (var i = 0; i < series.Count; i++)
        {
            var values = series[i].Values;
            if (values.Length < 2) continue;
            DrawSeries(canvas, values, maxVal, pad, pad, plotW, plotH, values.Length,
                Palette[i % Palette.Length]);
        }

        using var image = surface.Snapshot();
        using var data  = image.Encode(SKEncodedImageFormat.Png, 90);
        return $"data:image/png;base64,{Convert.ToBase64String(data.ToArray())}";
    }

    private static void DrawSeries(
        SKCanvas canvas,
        double[] values,
        double maxVal,
        float padL, float padT, float plotW, float plotH,
        int count,
        SKColor color)
    {
        using var linePaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            Color = color,
            StrokeWidth = 2f,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round
        };

        using var fillPaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Color = color.WithAlpha(35)
        };

        using var linePath = new SKPath();
        using var fillPath = new SKPath();

        var bottom = padT + plotH;

        // Precompute point coords
        var pts = new SKPoint[count];
        for (var i = 0; i < count; i++)
        {
            var x = padL + (float)i / (count - 1) * plotW;
            var ratio = (float)(values[i] / maxVal);
            var y = padT + plotH - ratio * plotH;
            pts[i] = new SKPoint(x, y);
        }

        // Catmull-Rom → cubic bezier (tension 0.5 for smooth but not overshoot-prone)
        linePath.MoveTo(pts[0]);
        fillPath.MoveTo(pts[0].X, bottom);
        fillPath.LineTo(pts[0]);

        for (var i = 0; i < count - 1; i++)
        {
            var p0 = i == 0 ? pts[i] : pts[i - 1];
            var p1 = pts[i];
            var p2 = pts[i + 1];
            var p3 = i + 2 < count ? pts[i + 2] : pts[i + 1];

            var cp1 = new SKPoint(p1.X + (p2.X - p0.X) / 6f, p1.Y + (p2.Y - p0.Y) / 6f);
            var cp2 = new SKPoint(p2.X - (p3.X - p1.X) / 6f, p2.Y - (p3.Y - p1.Y) / 6f);

            linePath.CubicTo(cp1, cp2, p2);
            fillPath.CubicTo(cp1, cp2, p2);
        }

        // Close fill area back to baseline
        fillPath.LineTo(pts[count - 1].X, bottom);
        fillPath.Close();

        canvas.DrawPath(fillPath, fillPaint);
        canvas.DrawPath(linePath, linePaint);
    }
}
