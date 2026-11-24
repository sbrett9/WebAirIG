using Godot;
using WebAirIG.Common.Config;

namespace WebAirIG.IG.Entities;

/// <summary>
/// Procedural stand-in models used until real assets are supplied. Built in Godot entity-local axes
/// (+X right, +Y up, -Z forward), with the origin roughly at the center of gravity.
/// </summary>
public static class PlaceholderModels
{
    public static Node3D Build(PlaceholderDef p)
    {
        var root = new Node3D { Name = $"Placeholder_{p.Kind}" };
        double L = Mathf.Max(0.2, p.LengthM), S = Mathf.Max(0.2, p.SpanM), H = Mathf.Max(0.1, p.HeightM);
        switch ((p.Kind ?? "jet").ToLowerInvariant())
        {
            case "airliner": Airliner(root, L, S, H); break;
            case "uav": Uav(root, L, S, H); break;
            case "quad": Quad(root, L, S, H); break;
            case "helo": Helo(root, L, S, H); break;
            case "missile": Store(root, L, S, radius: 0.03 * L, midWings: true); break;
            case "bomb": Store(root, L, S, radius: 0.065 * L, midWings: false); break;
            default: Jet(root, L, S, H); break;
        }
        return root;
    }

    private static void Airliner(Node3D root, double L, double S, double H)
    {
        float r = (float)(0.042 * L);
        Fuselage(root, r, (float)L);
        Wing(root, (float)S, (float)(0.14 * L), 0.9f, z: (float)(0.02 * L), y: -r * 0.45f, sweepDeg: 31);
        Box(root, "HStab", new Vector3((float)(0.33 * S), 0.5f, (float)(0.075 * L)), new Vector3(0, 0.4f, (float)(0.42 * L)));
        Box(root, "VFin", new Vector3(0.6f, (float)(0.2 * L), (float)(0.1 * L)), new Vector3(0, r + (float)(0.1 * L), (float)(0.43 * L)));
        foreach (float side in new[] { -1f, 1f })
            Nacelle(root, 1.7f, 6.5f, new Vector3(side * 9.7f, -2.6f, -4.0f));
    }

    private static void Jet(Node3D root, double L, double S, double H)
    {
        float r = (float)(0.05 * L);
        Fuselage(root, r, (float)L);
        Wing(root, (float)S, (float)(0.15 * L), 0.35f, z: (float)(0.05 * L), y: -r * 0.4f, sweepDeg: 22);
        Box(root, "VFin", new Vector3(0.25f, (float)(0.16 * L), (float)(0.1 * L)), new Vector3(0, r + (float)(0.08 * L), (float)(0.42 * L)));
        Box(root, "HStab", new Vector3((float)(0.38 * S), 0.18f, (float)(0.07 * L)), new Vector3(0, r + (float)(0.16 * L), (float)(0.45 * L)));
        foreach (float side in new[] { -1f, 1f })
            Nacelle(root, 0.55f, 2.6f, new Vector3(side * 1.7f, 0.9f, 5.0f));
    }

    private static void Uav(Node3D root, double L, double S, double H)
    {
        float r = (float)(0.045 * L);
        Fuselage(root, r, (float)L);
        Wing(root, (float)S, (float)(0.06 * S), 0.18f, z: 0f, y: r * 0.6f, sweepDeg: 0);
        foreach (float side in new[] { -1f, 1f })
        {
            var tail = Box(root, "VTail", new Vector3((float)(0.15 * S), 0.08f, (float)(0.08 * L)), new Vector3(side * (float)(0.05 * S), r, (float)(0.42 * L)));
            tail.RotationDegrees = new Vector3(0, 0, side * 40);
        }
        var prop = Cylinder(root, "Prop", (float)(0.12 * L), 0.05f, new Vector3(0, 0, (float)(0.47 * L)));
        prop.RotationDegrees = new Vector3(90, 0, 0);
    }

    private static void Quad(Node3D root, double L, double S, double H)
    {
        float body = (float)(0.3 * L);
        Box(root, "Body", new Vector3(body, (float)(0.35 * H), body), Vector3.Zero);
        float arm = (float)(0.47 * L);
        foreach (var (x, z) in new[] { (-1f, -1f), (1f, -1f), (-1f, 1f), (1f, 1f) })
        {
            var a = Box(root, "Arm", new Vector3(arm * 1.414f, 0.03f, 0.04f), new Vector3(x * arm * 0.5f, 0, z * arm * 0.5f));
            a.RotationDegrees = new Vector3(0, x * z > 0 ? -45 : 45, 0);
            Cylinder(root, "Rotor", (float)(0.17 * L), 0.01f, new Vector3(x * arm * 0.85f, (float)(0.2 * H), z * arm * 0.85f));
            Cylinder(root, "Motor", 0.04f, 0.06f, new Vector3(x * arm * 0.85f, (float)(0.1 * H), z * arm * 0.85f));
        }
    }

    private static void Helo(Node3D root, double L, double S, double H)
    {
        float r = (float)(0.1 * L);
        var cabin = new MeshInstance3D { Name = "Cabin", Mesh = new CapsuleMesh { Radius = r, Height = (float)(0.5 * L) } };
        cabin.RotationDegrees = new Vector3(90, 0, 0);
        cabin.Position = new Vector3(0, 0, (float)(-0.12 * L));
        root.AddChild(cabin);
        var boom = Cylinder(root, "Boom", r * 0.22f, (float)(0.5 * L), new Vector3(0, r * 0.3f, (float)(0.3 * L)));
        boom.RotationDegrees = new Vector3(90, 0, 0);
        Cylinder(root, "MainRotor", (float)(S / 2), 0.04f, new Vector3(0, r + 0.6f, (float)(-0.1 * L)));
        var tr = Cylinder(root, "TailRotor", (float)(0.09 * L), 0.04f, new Vector3(0.3f, r * 0.5f, (float)(0.55 * L)));
        tr.RotationDegrees = new Vector3(0, 0, 90);
        Box(root, "Skids", new Vector3(r * 1.8f, 0.08f, (float)(0.4 * L)), new Vector3(0, -r - 0.3f, (float)(-0.1 * L)));
    }

    /// <summary>Missile or bomb: a capsule body with cruciform tail fins (span S) and, for missiles, smaller mid-body wings.</summary>
    private static void Store(Node3D root, double L, double S, double radius, bool midWings)
    {
        float r = (float)radius;
        Fuselage(root, r, (float)L);
        float fin = (float)(0.12 * L);
        foreach (float rollDeg in new[] { 0f, 90f })
        {
            var tail = Box(root, "TailFins", new Vector3((float)S, 0.02f + r * 0.1f, fin), new Vector3(0, 0, (float)(0.5 * L) - fin * 0.6f));
            tail.RotationDegrees = new Vector3(0, 0, rollDeg);
            if (!midWings) continue;
            var wing = Box(root, "MidWings", new Vector3((float)(0.6 * S), 0.02f + r * 0.1f, fin * 0.8f), new Vector3(0, 0, (float)(0.05 * L)));
            wing.RotationDegrees = new Vector3(0, 0, rollDeg);
        }
    }

    private static void Fuselage(Node3D root, float radius, float length)
    {
        var mi = new MeshInstance3D { Name = "Fuselage", Mesh = new CapsuleMesh { Radius = radius, Height = length, RadialSegments = 24, Rings = 8 } };
        mi.RotationDegrees = new Vector3(90, 0, 0);
        root.AddChild(mi);
    }

    private static void Wing(Node3D root, float span, float chord, float thickness, float z, float y, float sweepDeg)
    {
        foreach (float side in new[] { -1f, 1f })
        {
            var half = new MeshInstance3D { Name = side < 0 ? "WingL" : "WingR", Mesh = new BoxMesh { Size = new Vector3(span / 2, thickness, chord) } };
            float sweep = Mathf.DegToRad(sweepDeg);
            half.Position = new Vector3(side * span / 4, y, z + span / 4 * Mathf.Tan(sweep));
            // Rotation about +Y takes +X toward -Z (forward), so sweep the tips aft with the opposite sign.
            half.RotationDegrees = new Vector3(0, -side * sweepDeg, 0);
            root.AddChild(half);
        }
    }

    private static void Nacelle(Node3D root, float radius, float length, Vector3 pos)
    {
        var n = Cylinder(root, "Nacelle", radius, length, pos);
        n.RotationDegrees = new Vector3(90, 0, 0);
    }

    private static MeshInstance3D Box(Node3D root, string name, Vector3 size, Vector3 pos)
    {
        var mi = new MeshInstance3D { Name = name, Mesh = new BoxMesh { Size = size }, Position = pos };
        root.AddChild(mi);
        return mi;
    }

    private static MeshInstance3D Cylinder(Node3D root, string name, float radius, float height, Vector3 pos)
    {
        var mi = new MeshInstance3D { Name = name, Mesh = new CylinderMesh { TopRadius = radius, BottomRadius = radius, Height = height, RadialSegments = 20 }, Position = pos };
        root.AddChild(mi);
        return mi;
    }
}
