using System.Text.Json;
using WebAirIG.Common.Config;

namespace WebAirIG.Common.Truth;

/// <summary>
/// Ground truth for one camera for one rendered frame ("truth sidecar"). Emitted by the IG, carried inside each
/// video frame message (so it is perfectly frame-synchronous) and optionally logged to JSONL on disk.
/// Pixel coordinates: origin at the top-left corner of the image, x right, y down, continuous (pixel centers at +0.5).
/// </summary>
public sealed class FrameTruth
{
    public const string SchemaId = "webairig.truth/1";

    public string Schema { get; set; } = SchemaId;
    public uint SessionId { get; set; }
    public ulong Frame { get; set; }
    public double SimTime { get; set; }
    public DateTime UtcTime { get; set; }
    public CameraTruth Camera { get; set; } = new();
    public EntityTruth? Ownship { get; set; }
    public List<ObjectTruth> Objects { get; set; } = [];

    public byte[] ToUtf8Json() => JsonSerializer.SerializeToUtf8Bytes(this, JsonConfig.Compact);
    public static FrameTruth? FromUtf8Json(ReadOnlySpan<byte> json) =>
        json.IsEmpty ? null : JsonSerializer.Deserialize<FrameTruth>(json, JsonConfig.Compact);
}

public sealed class CameraTruth
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public double HfovDeg { get; set; }
    public double VfovDeg { get; set; }
    public double Fx { get; set; }
    public double Fy { get; set; }
    public double Cx { get; set; }
    public double Cy { get; set; }
    public double GimbalAzDeg { get; set; }
    public double GimbalElDeg { get; set; }
    public double Lat { get; set; }
    public double Lon { get; set; }
    public double Alt { get; set; }
    /// <summary>Camera position in the world LTP, meters [east, north, up].</summary>
    public double[] Enu { get; set; } = [];
    /// <summary>R_localNed_from_cam (row-major 3x3). Camera axes: x = boresight, y = image right, z = image down.</summary>
    public double[] RNedFromCam { get; set; } = [];
    /// <summary>Boresight yaw/pitch/roll (deg) relative to local NED (aerospace ZYX). Degenerate near nadir; prefer RNedFromCam.</summary>
    public double YawDeg { get; set; }
    public double PitchDeg { get; set; }
    public double RollDeg { get; set; }
    /// <summary>Ground intersection of the boresight [lat, lon], null if it does not hit the ground.</summary>
    public double[]? BoresightGround { get; set; }
    /// <summary>Ground footprint corners TL, TR, BR, BL as [lat, lon] pairs.</summary>
    public double[][]? Footprint { get; set; }
}

public class EntityTruth
{
    public int EntityId { get; set; }
    public int TypeId { get; set; }
    public string TypeName { get; set; } = "";
    public double Lat { get; set; }
    public double Lon { get; set; }
    public double Alt { get; set; }
    public double YawDeg { get; set; }
    public double PitchDeg { get; set; }
    public double RollDeg { get; set; }
    /// <summary>Velocity in local NED, m/s.</summary>
    public double[] VelNed { get; set; } = [0, 0, 0];
}

public sealed class ObjectTruth : EntityTruth
{
    public int ClassId { get; set; }
    public string ClassName { get; set; } = "";
    public bool IsOwnship { get; set; }
    /// <summary>Tight box of projected model vertices clipped to the image: [x1, y1, x2, y2].</summary>
    public double[] Bbox { get; set; } = [];
    /// <summary>Same box before clipping (may extend outside the image).</summary>
    public double[] BboxUnclipped { get; set; } = [];
    /// <summary>True if the object extends beyond the image edges or partly behind the camera.</summary>
    public bool Truncated { get; set; }
    /// <summary>Fraction of sampled model points that land inside the image.</summary>
    public double VisibleFraction { get; set; }
    /// <summary>Projection of the entity origin [u, v] (may be outside the image).</summary>
    public double[] CenterPx { get; set; } = [];
    public double RangeM { get; set; }
    /// <summary>Line-of-sight angles of the entity origin relative to the boresight, degrees (x right, y down).</summary>
    public double OffBoresightXDeg { get; set; }
    public double OffBoresightYDeg { get; set; }
}
