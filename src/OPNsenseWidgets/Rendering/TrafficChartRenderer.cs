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

        for (var i = 0; i < count; i++)
        {
            var x = padL + (float)i / (count - 1) * plotW;
            var ratio = (float)(values[i] / maxVal);
            var y = padT + plotH - ratio * plotH;

            if (i == 0)
            {
                linePath.MoveTo(x, y);
                fillPath.MoveTo(x, bottom);
                fillPath.LineTo(x, y);
            }
            else
            {
                linePath.LineTo(x, y);
                fillPath.LineTo(x, y);
            }
        }

        // Close fill area back to baseline
        var lastX = padL + (float)(count - 1) / (count - 1) * plotW;
        fillPath.LineTo(lastX, bottom);
        fillPath.Close();

        canvas.DrawPath(fillPath, fillPaint);
        canvas.DrawPath(linePath, linePaint);
    }
}
