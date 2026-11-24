using Godot;
using WebAirIG.Common.Truth;

namespace WebAirIG.IG.Ui;

/// <summary>Draws truth bounding boxes over a KeepAspectCentered TextureRect.</summary>
public sealed partial class TruthOverlay : Control
{
    public int ImageWidth = 2048;
    public int ImageHeight = 2048;
    public FrameTruth? Truth;

    public override void _Draw()
    {
        if (Truth is null) return;
        var size = Size;
        float scale = Mathf.Min(size.X / ImageWidth, size.Y / ImageHeight);
        var offset = new Vector2((size.X - ImageWidth * scale) / 2, (size.Y - ImageHeight * scale) / 2);
        foreach (var o in Truth.Objects)
        {
            if (o.Bbox.Length < 4) continue;
            var p1 = offset + new Vector2((float)o.Bbox[0], (float)o.Bbox[1]) * scale;
            var p2 = offset + new Vector2((float)o.Bbox[2], (float)o.Bbox[3]) * scale;
            var rect = new Rect2(p1, (p2 - p1).Max(new Vector2(2, 2)));
            var color = o.IsOwnship ? new Color(1, 1, 1, 0.6f) : new Color(0.2f, 1f, 0.2f, 0.95f);
            DrawRect(rect, color, false, 1.5f);
        }
    }
}
