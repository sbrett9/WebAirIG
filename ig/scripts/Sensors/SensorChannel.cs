using Godot;
using WebAirIG.Common.Config;
using WebAirIG.Common.Sensors;
using WebAirIG.Common.Truth;
using WebAirIG.IG.Cigi;
using WebAirIG.IG.Core;

namespace WebAirIG.IG.Sensors;

/// <summary>
/// One gimbaled pseudo-MWIR camera. Render chain:
///   SceneViewport (HDR, linear apparent temperature, MSAA) -> PostViewport (sensor_post.gdshader, RGBA8) -> readback.
/// The scene viewport is a child of the post viewport so it is rendered first each frame. Both share the main World3D.
/// </summary>
public sealed class SensorChannel
{
    public CameraDef Def { get; }
    public SubViewport PostViewport { get; }
    public SubViewport SceneViewport { get; }
    public Camera3D Camera { get; }
    private readonly ShaderMaterial _post;

    public CameraPose Pose { get; private set; }
    public PinholeIntrinsics Intrinsics { get; private set; }
    public double AzDeg { get; private set; }
    public double ElDeg { get; private set; }
    public bool HasPose { get; private set; }
    public IgViewState View { get; }
    public FrameTruth? LatestTruth { get; set; }
    public int ReadbacksInFlight;

    public SensorChannel(Node parent, CameraDef def, IgViewState view, Shader postShader, int msaa)
    {
        Def = def;
        View = view;
        var size = new Vector2I(def.Width, def.Height);

        PostViewport = new SubViewport
        {
            Name = $"Post_{def.Name}",
            Size = size,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            TransparentBg = false,
        };
        parent.AddChild(PostViewport);

        SceneViewport = new SubViewport
        {
            Name = $"Scene_{def.Name}",
            Size = size,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            UseHdr2D = true,
            Msaa3D = msaa switch { >= 8 => Viewport.Msaa.Msaa8X, >= 4 => Viewport.Msaa.Msaa4X, >= 2 => Viewport.Msaa.Msaa2X, _ => Viewport.Msaa.Disabled },
            PositionalShadowAtlasSize = 0,
        };
        PostViewport.AddChild(SceneViewport);

        Camera = new Camera3D
        {
            Name = $"Camera_{def.Name}",
            KeepAspect = Camera3D.KeepAspectEnum.Height,
            Fov = (float)def.FovDeg,
            Near = (float)def.NearM,
            Far = (float)def.FarM,
            Current = true,
        };
        SceneViewport.AddChild(Camera);

        _post = new ShaderMaterial { Shader = postShader };
        _post.SetShaderParameter("scene_tex", SceneViewport.GetTexture());
        _post.SetShaderParameter("texel", new Vector2(1f / def.Width, 1f / def.Height));
        var rect = new ColorRect { Name = "PostRect", Material = _post, Position = Vector2.Zero, Size = new Vector2(def.Width, def.Height) };
        PostViewport.AddChild(rect);
    }

    /// <summary>Positions the camera from the host-commanded attach entity, mount, gimbal and FOV.</summary>
    public void Update(IgState state, ulong frame)
    {
        if (!state.Entities.TryGetValue(View.EntityId, out var platform) || !platform.HasPosition)
        {
            HasPose = false;
            return;
        }
        platform.Parts.TryGetValue(CameraDef.GimbalPartBaseId + Def.Id, out var gimbal);
        (AzDeg, ElDeg) = GimbalMath.Clamp(Def.Limits, gimbal.Yaw, gimbal.Pitch);

        Pose = CameraPose.Compute(new PlatformPose(platform.PositionNed, platform.NedFromBody), View.Mount, AzDeg, ElDeg);
        Intrinsics = PinholeIntrinsics.FromVerticalFov(Def.Width, Def.Height, View.VfovDeg);
        HasPose = true;

        Camera.Transform = GodotConv.FromNedPose(Pose.PositionNed, Pose.NedFromCam);
        Camera.Fov = (float)View.VfovDeg;
        Camera.Near = (float)View.NearM;
        Camera.Far = (float)View.FarM;

        _post.SetShaderParameter("sensor_on", View.SensorOn);
        _post.SetShaderParameter("black_hot", View.BlackHot);
        _post.SetShaderParameter("gain", (float)View.Gain);
        _post.SetShaderParameter("level", (float)View.Level);
        _post.SetShaderParameter("noise_sigma", (float)View.Noise);
        _post.SetShaderParameter("frame_seed", (float)(frame % 1024));
    }
}
