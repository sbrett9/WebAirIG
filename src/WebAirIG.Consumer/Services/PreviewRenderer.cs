using Microsoft.Extensions.Options;
using SkiaSharp;
using WebAirIG.Consumer.Options;
using WebAirIG.Consumer.Services.Detection;

namespace WebAirIG.Consumer.Services;

/// <summary>
/// Renders browser previews: a single camera or a 2x2 mosaic, downscaled with an area filter, with truth boxes
/// (green, ownship white) and detections (orange, with track ID and score) burned in so they always line up.
/// </summary>
public sealed class PreviewRenderer
{
    private readonly ICameraPipelineService _pipelines;
    private readonly IDetectorService _detector;
    public PreviewOptions Settings { get; }

    public PreviewRenderer(ICameraPipelineService pipelines, IDetectorService detector, IOptions<ConsumerOptions> options)
    {
        _pipelines = pipelines;
        _detector = detector;
        Settings = options.Value.Preview;
    }

    /// <param name="cameraId">Camera to show, or -1 for a 2x2 mosaic of the first four cameras.</param>
    public unsafe byte[] RenderJpeg(int cameraId, int size, bool showTruth, bool showDetections)
    {
        size = Math.Clamp(size, 128, 2048);
        var info = new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var bitmap = new SKBitmap(info);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(16, 22, 28));

        var cams = cameraId >= 0 ? [cameraId] : _pipelines.CameraIds.Take(4).ToArray();
        int tiles = cameraId >= 0 ? 1 : 2;
        int tile = size / tiles;
        using var font = new SKFont(SKTypeface.Default, Math.Max(11, tile / 40f));
        using var textPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };

        if (cams.Length == 0)
        {
            canvas.DrawText("Waiting for video...", 20, 40, SKTextAlign.Left, font, textPaint);
        }

        for (int i = 0; i < cams.Length; i++)
        {
            int ox = (i % tiles) * tile, oy = (i / tiles) * tile;
            var frame = _pipelines.AcquireLatest(cams[i]);
            if (frame is null)
            {
                canvas.DrawText($"camera {cams[i]}: no decoded frames yet", ox + 10, oy + 30, SKTextAlign.Left, font, textPaint);
                continue;
            }
            try
            {
                canvas.Flush();
                ImageCodec.Downscale(frame.Bgra, frame.Width, frame.Height, (byte*)bitmap.GetPixels(), bitmap.RowBytes, ox, oy, tile, tile);
                float sx = (float)tile / frame.Width, sy = (float)tile / frame.Height;

                if (showTruth && frame.Truth is { } truth)
                {
                    using var p = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
                    foreach (var o in truth.Objects)
                    {
                        if (o.Bbox.Length != 4) continue;
                        p.Color = o.IsOwnship ? new SKColor(255, 255, 255, 140) : new SKColor(40, 255, 40);
                        canvas.DrawRect(Pad(ox + (float)o.Bbox[0] * sx, oy + (float)o.Bbox[1] * sy, ox + (float)o.Bbox[2] * sx, oy + (float)o.Bbox[3] * sy, 2), p);
                    }
                }
                if (showDetections && _detector.Latest(frame.CameraId) is { } det)
                {
                    using var p = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = new SKColor(255, 140, 0), IsAntialias = true };
                    using var tp = new SKPaint { Color = new SKColor(255, 170, 40), IsAntialias = true };
                    foreach (var t in det.Detections)
                    {
                        var d = t.Detection;
                        var r = Pad(ox + d.X1 * sx, oy + d.Y1 * sy, ox + d.X2 * sx, oy + d.Y2 * sy, 4);
                        canvas.DrawRect(r, p);
                        canvas.DrawText($"#{t.TrackId} {d.ClassName} {d.Score:F2}", r.Left, r.Top - 3, SKTextAlign.Left, font, tp);
                    }
                }
                string name = frame.Truth?.Camera.Name ?? $"cam{frame.CameraId}";
                canvas.DrawText($"{name}  frame {frame.FrameNumber}  t={frame.SimTime:F1}s", ox + 8, oy + font.Size + 6, SKTextAlign.Left, font, textPaint);
            }
            finally { frame.Release(); }
        }
        canvas.Flush();
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(Settings.JpegQuality, 30, 100));
        return data.ToArray();
    }

    /// <summary>Keeps tiny far-away targets visible by enforcing a minimum drawn box size.</summary>
    private static SKRect Pad(float x1, float y1, float x2, float y2, float min)
    {
        float cx = (x1 + x2) / 2, cy = (y1 + y2) / 2;
        float hw = Math.Max((x2 - x1) / 2, min), hh = Math.Max((y2 - y1) / 2, min);
        return new SKRect(cx - hw, cy - hh, cx + hw, cy + hh);
    }
}

public static class PreviewEndpoints
{
    private const string Boundary = "webairigframe";

    /// <summary>
    /// GET /preview/stream?cam=-1&amp;size=1024&amp;truth=true&amp;det=true : MJPEG (multipart/x-mixed-replace) for an &lt;img&gt; tag.
    /// GET /preview/snapshot.jpg?... : one JPEG.
    /// </summary>
    public static void MapPreviewEndpoints(this WebApplication app)
    {
        app.MapGet("/preview/stream", async (HttpContext ctx, PreviewRenderer renderer, int cam = -1, int size = 1024, bool truth = true, bool det = true) =>
        {
            ctx.Response.ContentType = $"multipart/x-mixed-replace; boundary={Boundary}";
            ctx.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            var ct = ctx.RequestAborted;
            var delay = TimeSpan.FromSeconds(1.0 / Math.Clamp(renderer.Settings.MaxFps, 1, 30));
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var jpeg = await Task.Run(() => renderer.RenderJpeg(cam, size, truth, det), ct);
                    var header = System.Text.Encoding.ASCII.GetBytes($"--{Boundary}\r\nContent-Type: image/jpeg\r\nContent-Length: {jpeg.Length}\r\n\r\n");
                    await ctx.Response.Body.WriteAsync(header, ct);
                    await ctx.Response.Body.WriteAsync(jpeg, ct);
                    await ctx.Response.Body.WriteAsync("\r\n"u8.ToArray(), ct);
                    await ctx.Response.Body.FlushAsync(ct);
                    await Task.Delay(delay, ct);
                }
            }
            catch (OperationCanceledException) { }
        });

        app.MapGet("/preview/snapshot.jpg", (PreviewRenderer renderer, int cam = -1, int size = 1024, bool truth = true, bool det = true) =>
            Results.File(renderer.RenderJpeg(cam, size, truth, det), "image/jpeg"));
    }
}
