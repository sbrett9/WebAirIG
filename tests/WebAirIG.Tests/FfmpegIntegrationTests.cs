using System.Collections.Concurrent;
using WebAirIG.Common.Transport;
using WebAirIG.Common.Video;
using Xunit.Abstractions;

namespace WebAirIG.Tests;

/// <summary>
/// End-to-end NVENC encode -> Annex-B split -> NVDEC decode through the real ffmpeg wrappers. Verifies the core
/// assumption of the transport: one access unit per input frame, decoded 1:1 and in order, so metadata (truth)
/// stays paired with the right image. Each frame encodes its index as the brightness of a large patch.
/// Skipped (passes trivially) when ffmpeg is not installed.
/// </summary>
public class FfmpegIntegrationTests(ITestOutputHelper output)
{
    private const int W = 2048, H = 2048, Frames = 75;

    [Theory]
    [InlineData(VideoCodec.H264)]
    [InlineData(VideoCodec.Hevc)]
    public void Nvenc_Roundtrip_Preserves_Frame_Pairing(VideoCodec codec)
    {
        var ffmpeg = FfmpegLocator.Find(null);
        if (ffmpeg is null)
        {
            output.WriteLine("ffmpeg not found - skipping");
            return;
        }

        var aus = new ConcurrentQueue<(int Index, byte[] Au, bool Key)>();
        var log = new ConcurrentQueue<string>();
        using (var enc = new FfmpegEncoder<int>(new EncoderSettings { FfmpegPath = ffmpeg, Width = W, Height = H, Fps = 30, Codec = codec, Gop = 30, QueueCapacity = Frames }))
        {
            enc.Log += log.Enqueue;
            enc.AccessUnitEncoded += (i, au, key) => aus.Enqueue((i, au, key));
            enc.Start();
            for (int i = 0; i < Frames; i++) Assert.True(enc.TrySubmit(MakeFrame(i), i));
            // Dispose closes stdin, flushes the encoder and drains the last access unit.
        }
        foreach (var l in log) output.WriteLine(l);

        var list = aus.ToList();
        Assert.Equal(Frames, list.Count);
        Assert.Equal(Enumerable.Range(0, Frames), list.Select(a => a.Index));
        Assert.True(list[0].Key, "first access unit must be a keyframe");
        Assert.True(list[30].Key, "GOP 30 => frame 30 is a keyframe");
        output.WriteLine($"{codec}: {Frames} AUs, keyframe {list[0].Au.Length / 1024} KB, P-frame {list[5].Au.Length / 1024} KB");

        var decoded = new ConcurrentQueue<(int Index, int Brightness)>();
        using (var dec = new FfmpegDecoder<int>(new DecoderSettings { FfmpegPath = ffmpeg, Codec = codec, Width = W, Height = H, HwAccel = "cuda", QueueCapacity = Frames }))
        {
            dec.Log += l => output.WriteLine(l);
            dec.FrameDecoded += (i, bgra) =>
            {
                // Sample the center of the index patch (BGRA, green channel).
                int o = ((H / 2) * W + W / 2) * 4;
                decoded.Enqueue((i, bgra[o + 1]));
                dec.Pool.Return(bgra);
            };
            dec.Start();
            foreach (var (i, au, _) in list) Assert.True(dec.TrySubmit(au, i));
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (decoded.Count < Frames && DateTime.UtcNow < deadline) Thread.Sleep(20);
        }

        var d = decoded.ToList();
        Assert.Equal(Frames, d.Count);
        for (int k = 0; k < d.Count; k++)
        {
            Assert.Equal(k, d[k].Index);
            Assert.InRange(d[k].Brightness, Expected(k) - 12, Expected(k) + 12);
        }
    }

    private static int Expected(int i) => 20 + (i * 3) % 220;

    private static byte[] MakeFrame(int i)
    {
        var f = new byte[W * H * 4];
        byte v = (byte)Expected(i);
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            int o = (y * W + x) * 4;
            bool patch = Math.Abs(x - W / 2) < 256 && Math.Abs(y - H / 2) < 256;
            byte bg = (byte)((x / 64 + y / 64 + i) % 2 == 0 ? 60 : 90);
            f[o] = patch ? v : bg;       // R
            f[o + 1] = patch ? v : bg;   // G
            f[o + 2] = patch ? v : bg;   // B
            f[o + 3] = 255;
        }
        return f;
    }
}
