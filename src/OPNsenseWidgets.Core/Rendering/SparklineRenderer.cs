using SkiaSharp;

namespace OPNsenseWidgets.Rendering;

public static class SparklineRenderer
{
    /// <summary>
    /// Renders a sparkline bar chart as a PNG data URI from hourly bucket counts.
    /// Buckets should be ordered oldest-first (index 0 = 24h ago, last = now).
    /// </summary>
    public static string Render(int[] buckets, int width = 280, int height = 24)
    {
        var max = buckets.Max();
        if (max == 0) max = 1;

        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        var barCount = buckets.Length;
        var gap = 2f;
        var barWidth = (width - (barCount - 1) * gap) / barCount;
        var cornerRadius = 1.5f;

        using var paint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };

        for (var i = 0; i < barCount; i++)
        {
            var ratio = (float)buckets[i] / max;
            var barHeight = buckets[i] == 0
                ? 2f
                : Math.Max(3f, ratio * (height - 2));

            var x = i * (barWidth + gap);
            var y = height - barHeight;

            // Inactive bars are barely visible; active bars scale from dim to bright
            var alpha = buckets[i] == 0
                ? (byte)20
                : (byte)(60 + ratio * 195);

            paint.Color = new SKColor(45, 212, 160, alpha);

            var rect = new SKRect(x, y, x + barWidth, y + barHeight);
            canvas.DrawRoundRect(rect, cornerRadius, cornerRadius, paint);
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return $"data:image/png;base64,{Convert.ToBase64String(data.ToArray())}";
    }
}
