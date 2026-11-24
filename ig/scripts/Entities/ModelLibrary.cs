using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using WebAirIG.Common.Config;
using WebAirIG.Common.Geo;
using WebAirIG.IG.Core;

namespace WebAirIG.IG.Entities;

/// <summary>A loaded entity type: prototype node tree (with IR materials) plus truth sample points.</summary>
public sealed class ModelTemplate
{
    public required EntityTypeDef Type { get; init; }
    public required Node3D Prototype { get; init; }
    /// <summary>Model vertices (subsampled) in body FRD meters, used to compute tight truth bounding boxes.</summary>
    public required Vec3d[] SamplePointsFrd { get; init; }
    public required string Source { get; init; }

    public Node3D Instantiate() => (Node3D)Prototype.Duplicate();
}

/// <summary>
/// Entity loader. Loads .glb/.gltf/.fbx files at runtime (no Godot import step), applies the catalog's orientation
/// fix-up, swaps every surface material for the pseudo-IR shader, and extracts sample points for truth boxes.
/// Falls back to a procedural placeholder when no model file is configured or it fails to load.
/// </summary>
public sealed class ModelLibrary
{
    private readonly EntityCatalog _catalog;
    private readonly string _catalogDir;
    private readonly Shader _irShader;
    private readonly int _maxSamplePoints;
    private readonly Dictionary<int, ModelTemplate> _cache = new();

    public ModelLibrary(EntityCatalog catalog, string catalogDir, Shader irShader, int maxSamplePoints)
    {
        _catalog = catalog;
        _catalogDir = catalogDir;
        _irShader = irShader;
        _maxSamplePoints = Math.Max(16, maxSamplePoints);
    }

    public ModelTemplate Get(int typeId)
    {
        if (_cache.TryGetValue(typeId, out var t)) return t;
        var def = _catalog.Find(typeId) ?? new EntityTypeDef
        {
            TypeId = typeId,
            Name = $"unknown{typeId}",
            ClassName = "unknown",
            ClassId = 99,
            Placeholder = new PlaceholderDef { Kind = "jet", LengthM = 15, SpanM = 12, HeightM = 4 },
        };
        t = Build(def);
        _cache[typeId] = t;
        var p = t.SamplePointsFrd;
        double len = p.Max(v => v.X) - p.Min(v => v.X), span = p.Max(v => v.Y) - p.Min(v => v.Y), height = p.Max(v => v.Z) - p.Min(v => v.Z);
        IgLog.Info($"Entity type {typeId} '{def.Name}': {t.Source}, {p.Length} truth sample points, " +
                   $"extent fwd {len:F1} m x span {span:F1} m x height {height:F1} m (nose at +{p.Max(v => v.X):F1} m)");
        if (def.Placeholder.Kind is "airliner" or "jet" or "uav" && !string.IsNullOrWhiteSpace(def.Model) && span > 0 && len < 0.5 * span)
            IgLog.Warn($"Type {typeId}: model is much wider than long - check modelRotationDeg (nose must point to Godot -Z)");
        return t;
    }

    private ModelTemplate Build(EntityTypeDef def)
    {
        Node3D? model = null;
        string source = "placeholder";
        if (!string.IsNullOrWhiteSpace(def.Model))
        {
            var path = JsonConfig.Resolve(_catalogDir, def.Model);
            model = LoadModelFile(path);
            source = model is null ? $"placeholder (failed to load {path})" : path;
        }

        var root = new Node3D { Name = $"Type{def.TypeId}_{def.Name}" };
        if (model is not null)
        {
            // Fix-up node: rotate/scale the asset into Godot entity axes, then offset (given in body FRD).
            var fix = new Node3D
            {
                Name = "ModelFix",
                RotationDegrees = new Vector3((float)At(def.ModelRotationDeg, 0), (float)At(def.ModelRotationDeg, 1), (float)At(def.ModelRotationDeg, 2)),
                Scale = Vector3.One * (float)def.ModelScale,
                Position = GodotConv.FrdToGodotLocal(def.ModelOffsetFrdM),
            };
            fix.AddChild(model);
            root.AddChild(fix);
        }
        else
        {
            root.AddChild(PlaceholderModels.Build(def.Placeholder));
        }

        ConvertImporterMeshes(root);
        var meshes = new List<(MeshInstance3D Mesh, Transform3D ToEntity)>();
        CollectMeshes(root, root, meshes);
        ApplyIrMaterials(def, meshes);
        var samples = ExtractSamplePoints(meshes);
        return new ModelTemplate { Type = def, Prototype = root, SamplePointsFrd = samples, Source = source };
    }

    private static Node3D? LoadModelFile(string path)
    {
        if (!File.Exists(path))
        {
            IgLog.Warn($"Model file not found: {path} (local-only assets in models/local are not in the repository); using the placeholder");
            return null;
        }
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".obj") return ObjLoader.Load(path);
            if (ext == ".blend")
            {
                IgLog.Error($"{path}: .blend files cannot be loaded at runtime. Convert to .glb with tools/models/convert_blend.ps1 (needs Blender).");
                return null;
            }
            GltfDocument doc = ext == ".fbx" ? new FbxDocument() : new GltfDocument();
            GltfState state = ext == ".fbx" ? new FbxState() : new GltfState();
            var err = doc.AppendFromFile(path, state);
            if (err != Error.Ok)
            {
                IgLog.Error($"Failed to parse {path}: {err}");
                return null;
            }
            var scene = doc.GenerateScene(state);
            if (scene is Node3D n3) return n3;
            var wrapper = new Node3D { Name = "Model" };
            if (scene is not null) wrapper.AddChild(scene);
            return wrapper;
        }
        catch (Exception ex)
        {
            IgLog.Error($"Exception loading {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Some import paths produce ImporterMeshInstance3D; swap them for renderable MeshInstance3D.</summary>
    private static void ConvertImporterMeshes(Node node)
    {
        foreach (var child in node.GetChildren().ToList())
        {
            if (child is ImporterMeshInstance3D imi && imi.Mesh is not null)
            {
                var mi = new MeshInstance3D { Name = imi.Name, Mesh = imi.Mesh.GetMesh(), Transform = imi.Transform };
                foreach (var grandChild in imi.GetChildren().ToList())
                {
                    imi.RemoveChild(grandChild);
                    mi.AddChild(grandChild);
                }
                node.AddChild(mi);
                node.MoveChild(mi, imi.GetIndex());
                node.RemoveChild(imi);
                imi.Free();
                ConvertImporterMeshes(mi);
            }
            else ConvertImporterMeshes(child);
        }
    }

    private static void CollectMeshes(Node node, Node3D root, List<(MeshInstance3D, Transform3D)> output)
    {
        if (node is MeshInstance3D mi && mi.Mesh is not null) output.Add((mi, RelativeTransform(mi, root)));
        foreach (var child in node.GetChildren()) CollectMeshes(child, root, output);
    }

    private static Transform3D RelativeTransform(Node3D node, Node3D root)
    {
        var t = Transform3D.Identity;
        Node? n = node;
        while (n is not null && n != root)
        {
            if (n is Node3D n3) t = n3.Transform * t;
            n = n.GetParent();
        }
        return t;
    }

    private void ApplyIrMaterials(EntityTypeDef def, List<(MeshInstance3D Mesh, Transform3D ToEntity)> meshes)
    {
        var ir = def.Ir;
        var hotspots = ir.Hotspots.Take(8).ToList();
        foreach (var (mesh, toEntity) in meshes)
        {
            mesh.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            var toMesh = toEntity.AffineInverse();
            float scale = (toMesh.Basis.Scale.X + toMesh.Basis.Scale.Y + toMesh.Basis.Scale.Z) / 3f;
            var hotPos = new Vector4[8];
            var hotTemp = new float[8];
            for (int i = 0; i < hotspots.Count; i++)
            {
                var local = toMesh * GodotConv.FrdToGodotLocal(hotspots[i].PositionFrdM);
                hotPos[i] = new Vector4(local.X, local.Y, local.Z, (float)hotspots[i].RadiusM * scale);
                hotTemp[i] = (float)hotspots[i].Temp;
            }

            int surfaces = mesh.Mesh.GetSurfaceCount();
            for (int s = 0; s < surfaces; s++)
            {
                var mat = new ShaderMaterial { Shader = _irShader };
                mat.SetShaderParameter("skin_temp", (float)ir.SkinTemp);
                mat.SetShaderParameter("solar_gain", (float)ir.SolarGain);
                mat.SetShaderParameter("detail_strength", (float)ir.DetailStrength);
                mat.SetShaderParameter("hot_count", hotspots.Count);
                mat.SetShaderParameter("hot_pos_radius", hotPos);
                mat.SetShaderParameter("hot_temp", hotTemp);
                if (mesh.GetActiveMaterial(s) is BaseMaterial3D bm && bm.AlbedoTexture is not null)
                {
                    mat.SetShaderParameter("detail_tex", bm.AlbedoTexture);
                    mat.SetShaderParameter("has_detail", true);
                }
                mesh.SetSurfaceOverrideMaterial(s, mat);
            }
        }
    }

    private Vec3d[] ExtractSamplePoints(List<(MeshInstance3D Mesh, Transform3D ToEntity)> meshes)
    {
        var all = new List<Vector3>();
        foreach (var (mesh, toEntity) in meshes)
        {
            for (int s = 0; s < mesh.Mesh.GetSurfaceCount(); s++)
            {
                var arrays = mesh.Mesh.SurfaceGetArrays(s);
                if (arrays.Count <= (int)Mesh.ArrayType.Vertex) continue;
                var verts = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                foreach (var v in verts) all.Add(toEntity * v);
            }
        }
        if (all.Count == 0) return [Vec3d.Zero];

        // Always keep the extreme points along each axis, then a uniform stride over the rest.
        var picked = new HashSet<int>
        {
            IndexOfMax(all, v => v.X), IndexOfMax(all, v => -v.X),
            IndexOfMax(all, v => v.Y), IndexOfMax(all, v => -v.Y),
            IndexOfMax(all, v => v.Z), IndexOfMax(all, v => -v.Z),
        };
        int stride = Math.Max(1, all.Count / _maxSamplePoints);
        for (int i = 0; i < all.Count && picked.Count < _maxSamplePoints + 6; i += stride) picked.Add(i);
        return picked.Select(i => GodotConv.GodotLocalToFrd(all[i])).ToArray();
    }

    /// <summary>Frees the prototype node trees (they are never added to the scene tree).</summary>
    public void FreeAll()
    {
        foreach (var t in _cache.Values)
            if (GodotObject.IsInstanceValid(t.Prototype)) t.Prototype.Free();
        _cache.Clear();
    }

    private static int IndexOfMax(List<Vector3> pts, Func<Vector3, float> key)
    {
        int best = 0;
        float bestV = float.MinValue;
        for (int i = 0; i < pts.Count; i++)
        {
            float k = key(pts[i]);
            if (k > bestV) { bestV = k; best = i; }
        }
        return best;
    }

    private static double At(double[] a, int i) => a is not null && a.Length > i ? a[i] : 0.0;
}
