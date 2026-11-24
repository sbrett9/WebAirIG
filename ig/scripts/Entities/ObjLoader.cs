using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Godot;
using WebAirIG.IG.Core;

namespace WebAirIG.IG.Entities;

/// <summary>
/// Minimal runtime Wavefront OBJ + MTL loader. Godot has no runtime OBJ API, and many free aircraft models ship as
/// OBJ, so this covers positions, UVs, polygon faces (fan-triangulated), per-material diffuse textures (map_Kd) and
/// colors (Kd). Normals are always recomputed (smoothed per shared position index) because many exports omit them.
/// OBJ is right-handed Y-up like Godot; triangle winding is flipped to Godot's clockwise front faces.
/// </summary>
public static class ObjLoader
{
    private sealed class MaterialDef
    {
        public Color Diffuse = Colors.White;
        public string? DiffuseMap;
    }

    private sealed class Surface
    {
        public readonly List<int> Positions = new();
        public readonly List<int> Uvs = new();
    }

    public static Node3D? Load(string path)
    {
        var ci = CultureInfo.InvariantCulture;
        var dir = Path.GetDirectoryName(path) ?? ".";
        var positions = new List<Vector3>();
        var uvs = new List<Vector2>();
        var surfaces = new Dictionary<string, Surface>();
        var materials = new Dictionary<string, MaterialDef>();
        string current = "default";

        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var t = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            switch (t[0])
            {
                case "v" when t.Length >= 4:
                    positions.Add(new Vector3(F(t[1]), F(t[2]), F(t[3])));
                    break;
                case "vt" when t.Length >= 3:
                    uvs.Add(new Vector2(F(t[1]), 1f - F(t[2])));
                    break;
                case "usemtl" when t.Length >= 2:
                    current = t[1];
                    break;
                case "mtllib" when t.Length >= 2:
                    var mtlPath = Path.Combine(dir, line[(line.IndexOf(' ') + 1)..].Trim());
                    if (File.Exists(mtlPath)) LoadMtl(mtlPath, materials);
                    else IgLog.Warn($"OBJ: material library not found: {mtlPath}");
                    break;
                case "f" when t.Length >= 4:
                    if (!surfaces.TryGetValue(current, out var s)) surfaces[current] = s = new Surface();
                    var pi = new int[t.Length - 1];
                    var ti = new int[t.Length - 1];
                    for (int i = 1; i < t.Length; i++)
                    {
                        var parts = t[i].Split('/');
                        pi[i - 1] = Index(parts[0], positions.Count);
                        ti[i - 1] = parts.Length > 1 && parts[1].Length > 0 ? Index(parts[1], uvs.Count) : -1;
                    }
                    for (int k = 1; k + 1 < pi.Length; k++)
                    {
                        // OBJ is counter-clockwise; Godot front faces are clockwise: emit (0, k+1, k).
                        s.Positions.Add(pi[0]); s.Uvs.Add(ti[0]);
                        s.Positions.Add(pi[k + 1]); s.Uvs.Add(ti[k + 1]);
                        s.Positions.Add(pi[k]); s.Uvs.Add(ti[k]);
                    }
                    break;
            }
        }
        if (positions.Count == 0 || surfaces.Count == 0)
        {
            IgLog.Error($"OBJ: no geometry in {path}");
            return null;
        }

        // Smooth normals: accumulate area-weighted face normals (computed in OBJ's CCW order) per position index.
        var accum = new Vector3[positions.Count];
        foreach (var s in surfaces.Values)
        {
            for (int i = 0; i < s.Positions.Count; i += 3)
            {
                // Stored order is (a, c, b) for an OBJ triangle (a, b, c).
                int a = s.Positions[i], c = s.Positions[i + 1], b = s.Positions[i + 2];
                var n = (positions[b] - positions[a]).Cross(positions[c] - positions[a]);
                accum[a] += n; accum[b] += n; accum[c] += n;
            }
        }

        var mesh = new ArrayMesh();
        var textureCache = new Dictionary<string, Texture2D?>();
        foreach (var (name, s) in surfaces)
        {
            int count = s.Positions.Count;
            var verts = new Vector3[count];
            var norms = new Vector3[count];
            var tex = new Vector2[count];
            for (int i = 0; i < count; i++)
            {
                int p = s.Positions[i];
                verts[i] = positions[p];
                norms[i] = accum[p].LengthSquared() > 0 ? accum[p].Normalized() : Vector3.Up;
                int u = s.Uvs[i];
                tex[i] = u >= 0 && u < uvs.Count ? uvs[u] : Vector2.Zero;
            }
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = verts;
            arrays[(int)Mesh.ArrayType.Normal] = norms;
            arrays[(int)Mesh.ArrayType.TexUV] = tex;
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

            var mat = new StandardMaterial3D();
            if (materials.TryGetValue(name, out var def))
            {
                mat.AlbedoColor = def.Diffuse;
                if (def.DiffuseMap is { } map)
                {
                    if (!textureCache.TryGetValue(map, out var texture))
                    {
                        texture = LoadTexture(map);
                        textureCache[map] = texture;
                    }
                    if (texture is not null) mat.AlbedoTexture = texture;
                }
            }
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, mat);
        }

        var root = new Node3D { Name = Path.GetFileNameWithoutExtension(path) };
        root.AddChild(new MeshInstance3D { Name = "ObjMesh", Mesh = mesh });
        IgLog.Info($"OBJ: {Path.GetFileName(path)}: {positions.Count} vertices, {surfaces.Count} materials, {textureCache.Count} textures");
        return root;

        static float F(string s) => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
        static int Index(string s, int count)
        {
            int i = int.Parse(s, CultureInfo.InvariantCulture);
            return i > 0 ? i - 1 : count + i; // negative indices are relative to the end
        }
    }

    private static void LoadMtl(string path, Dictionary<string, MaterialDef> materials)
    {
        var dir = Path.GetDirectoryName(path) ?? ".";
        MaterialDef? cur = null;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var t = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            switch (t[0])
            {
                case "newmtl" when t.Length >= 2:
                    cur = new MaterialDef();
                    materials[t[1]] = cur;
                    break;
                case "Kd" when cur is not null && t.Length >= 4:
                    cur.Diffuse = new Color(P(t[1]), P(t[2]), P(t[3]));
                    break;
                case "map_Kd" when cur is not null && t.Length >= 2:
                    // Options (e.g. "-s 1 1 1") may precede the file name; the path is the last token.
                    cur.DiffuseMap = Path.Combine(dir, t[^1]);
                    break;
            }
        }

        static float P(string s) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 1f;
    }

    private static Texture2D? LoadTexture(string path)
    {
        if (!File.Exists(path))
        {
            IgLog.Warn($"OBJ: texture not found: {path}");
            return null;
        }
        var img = Image.LoadFromFile(path);
        if (img is null || img.IsEmpty()) return null;
        img.GenerateMipmaps();
        return ImageTexture.CreateFromImage(img);
    }
}
