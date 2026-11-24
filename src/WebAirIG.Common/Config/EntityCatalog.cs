namespace WebAirIG.Common.Config;

/// <summary>config/entity_catalog.json — entity types known to the IG (models, IR signature) and host (DIS, speeds).</summary>
public sealed class EntityCatalog
{
    public List<EntityTypeDef> Types { get; set; } = [];

    public EntityTypeDef? Find(int typeId) => Types.FirstOrDefault(t => t.TypeId == typeId);
}

public sealed class EntityTypeDef
{
    /// <summary>CIGI short entity type (sent in Entity Control).</summary>
    public int TypeId { get; set; }
    public string Name { get; set; } = "";

    /// <summary>Path to .glb/.gltf/.fbx, relative to the catalog file. Empty = procedural placeholder.</summary>
    public string Model { get; set; } = "";
    public double ModelScale { get; set; } = 1.0;
    /// <summary>Euler rotation (deg, Godot X/Y/Z order) applied to the imported model so it faces Godot -Z with +Y up.</summary>
    public double[] ModelRotationDeg { get; set; } = [0, 0, 0];
    /// <summary>Translation (m, body FRD) applied after rotation/scale so the entity origin lands where you want it.</summary>
    public double[] ModelOffsetFrdM { get; set; } = [0, 0, 0];

    public PlaceholderDef Placeholder { get; set; } = new();
    public IrSignatureDef Ir { get; set; } = new();

    /// <summary>YOLO class index written to truth/labels.</summary>
    public int ClassId { get; set; }
    public string ClassName { get; set; } = "object";

    public DisEntityTypeDef Dis { get; set; } = new();
    public double DefaultSpeedMps { get; set; } = 100;
}

public sealed class PlaceholderDef
{
    /// <summary>"airliner", "jet", "uav", "quad", "helo".</summary>
    public string Kind { get; set; } = "jet";
    public double LengthM { get; set; } = 15;
    public double SpanM { get; set; } = 12;
    public double HeightM { get; set; } = 4;
}

public sealed class IrSignatureDef
{
    public double SkinTemp { get; set; } = 0.45;
    public double SolarGain { get; set; } = 0.08;
    /// <summary>How strongly the original albedo texture modulates the IR skin (keeps paint/markings visible).</summary>
    public double DetailStrength { get; set; } = 0.06;
    public List<HotspotDef> Hotspots { get; set; } = [];
}

public sealed class HotspotDef
{
    public string Name { get; set; } = "";
    /// <summary>Body FRD meters relative to the entity origin.</summary>
    public double[] PositionFrdM { get; set; } = [0, 0, 0];
    public double RadiusM { get; set; } = 1.5;
    public double Temp { get; set; } = 1.0;
}

public sealed class DisEntityTypeDef
{
    public byte Kind { get; set; } = 1;       // platform
    public byte Domain { get; set; } = 2;     // air
    public ushort Country { get; set; } = 225; // USA
    public byte Category { get; set; }
    public byte Subcategory { get; set; }
    public byte Specific { get; set; }
    public byte Extra { get; set; }
}
