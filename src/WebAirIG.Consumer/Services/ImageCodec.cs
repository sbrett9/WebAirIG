using System.Runtime.InteropServices;
using SkiaSharp;

namespace WebAirIG.Consumer.Services;

/// <summary>SkiaSharp helpers for BGRA buffers: encoding and fast area-averaged downscaling.</summary>
public static class ImageCodec
{
    public static byte[] Encode(byte[] bgra, int width, int height, SKEncodedImageFormat format, int quality)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            using var pixmap = new SKPixmap(info, handle.AddrOfPinnedObject(), width * 4);
            using var data = pixmap.Encode(format, quality) ?? throw new InvalidOperationException("Image encode failed");
            return data.ToArray();
        }
        finally { handle.Free(); }
    }

    /// <summary>
    /// Box-filter downscale of a BGRA image into <paramref name="dst"/> (BGRA, dstStride bytes per row) at (offsetX, offsetY).
    /// </summary>
    public static unsafe void Downscale(byte[] src, int srcW, int srcH, byte* dst, int dstStride, int offsetX, int offsetY, int dstW, int dstH)
    {
        double fx = (double)srcW / dstW, fy = (double)srcH / dstH;
        nint dstBase = (nint)dst;
        Parallel.For(0, dstH, y =>
        {
            int sy0 = (int)(y * fy), sy1 = Math.Min(srcH, Math.Max(sy0 + 1, (int)Math.Ceiling((y + 1) * fy)));
            byte* row = (byte*)dstBase + (y + offsetY) * dstStride + offsetX * 4;
            for (int x = 0; x < dstW; x++)
            {
                int sx0 = (int)(x * fx), sx1 = Math.Min(srcW, Math.Max(sx0 + 1, (int)Math.Ceiling((x + 1) * fx)));
                int b = 0, g = 0, r = 0, n = 0;
                for (int sy = sy0; sy < sy1; sy++)
                {
                    int o = (sy * srcW + sx0) * 4;
                    for (int sx = sx0; sx < sx1; sx++, o += 4)
                    {
                        b += src[o]; g += src[o + 1]; r += src[o + 2]; n++;
                    }
                }
                row[x * 4] = (byte)(b / n);
                row[x * 4 + 1] = (byte)(g / n);
                row[x * 4 + 2] = (byte)(r / n);
                row[x * 4 + 3] = 255;
            }
        });
    }
}
