using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using X4Data;
using X4Data.Model;
using X4Data.Textures;
using Num = System.Numerics;

namespace GalaxyEditor3D;

/// <summary>
/// Turns X4 data (X4Data library) into Godot nodes.
/// Space: X4 is left-handed (x right, y up, z forward), Godot right-handed (-z forward): every point (x, y, z) -> (x, y, -z).
/// Mirroring flips the triangle winding, so each triangle's index order is reversed (Godot: clockwise = front).
/// </summary>
public sealed class X4ToGodot
{
    readonly GameData _g;
    readonly Dictionary<string, Material> _materials = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Texture2D?> _textures = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Notes { get; } = new();
    /// <summary>Use the game's "-small" textures (faster loading, less memory).</summary>
    public bool SmallTextures { get; set; }

    public X4ToGodot(GameData g) => _g = g;

    /// <summary>Builds a macro / component as a Node3D with one MeshInstance3D per part.</summary>
    public Node3D BuildObject(string name, int lod = 0)
    {
        var root = new Node3D { Name = name };
        foreach (var p in Assembly.Build(_g, name, lod, Notes))
        {
            var mesh = BuildMesh(p);
            if (mesh == null) continue;
            root.AddChild(new MeshInstance3D { Name = p.PartName, Mesh = mesh, Transform = ToGodot(p.Transform) });
        }
        return root;
    }

    /// <summary>One surface per xmf material range; vertices compacted per surface.</summary>
    ArrayMesh? BuildMesh(Assembly.PlacedPart p)
    {
        var x = p.Mesh;
        if (x.Positions.Length == 0 || x.Indices.Length == 0) return null;
        var mesh = new ArrayMesh();
        var ranges = x.Materials.Count > 0 ? x.Materials : new() { (0, x.Indices.Length, "") };
        for (int r = 0; r < ranges.Count; r++)
        {
            var (first, count, _) = ranges[r];
            if (count < 3) continue;
            var remap = new Dictionary<int, int>();
            var verts = new List<Vector3>(); var norms = new List<Vector3>(); var uvs = new List<Vector2>(); var cols = new List<Color>();
            var idx = new int[count - count % 3];
            for (int i = 0; i < idx.Length; i += 3)
                for (int k = 0; k < 3; k++)
                {
                    int src = x.Indices[first + i + (2 - k)];          // reversed order: mirrored space
                    if (!remap.TryGetValue(src, out var dst))
                    {
                        dst = verts.Count; remap[src] = dst;
                        var v = x.Positions[src]; verts.Add(new Vector3(v.X, v.Y, -v.Z));
                        if (x.Normals != null) { var n = x.Normals[src]; norms.Add(new Vector3(n.X, n.Y, -n.Z)); }
                        if (x.Uvs != null) { var u = x.Uvs[src]; uvs.Add(new Vector2(u.X, u.Y)); }
                        if (x.Colors != null) cols.Add(Color.Color8(x.Colors[src * 4], x.Colors[src * 4 + 1], x.Colors[src * 4 + 2], 255));   // X4 keeps 0 in the alpha byte: not transparency
                    }
                    idx[i + k] = dst;
                }
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
            if (norms.Count == verts.Count) arrays[(int)Mesh.ArrayType.Normal] = norms.ToArray();
            if (uvs.Count == verts.Count) arrays[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();
            if (cols.Count == verts.Count) arrays[(int)Mesh.ArrayType.Color] = cols.ToArray();
            arrays[(int)Mesh.ArrayType.Index] = idx;
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, GetMaterial(r < p.MaterialNames.Length ? p.MaterialNames[r] : ""));
        }
        return mesh.GetSurfaceCount() > 0 ? mesh : null;
    }

    /// <summary>X4 row-vector matrix -> Godot transform in mirrored space (S * M * S, S = mirror z).</summary>
    public static Transform3D ToGodot(Num.Matrix4x4 m)
    {
        var s = Num.Matrix4x4.CreateScale(1, 1, -1);
        var g = s * m * s;
        // row vectors: x axis image = row 1, y = row 2, z = row 3, origin = row 4
        var basis = new Basis(new Vector3(g.M11, g.M12, g.M13), new Vector3(g.M21, g.M22, g.M23), new Vector3(g.M31, g.M32, g.M33));
        return new Transform3D(basis, new Vector3(g.M41, g.M42, g.M43));
    }

    /// <summary>
    /// X4 material -> StandardMaterial3D. Close, not exact: diffuse texture, metal / smooth numbers, additive /
    /// alpha effects unshaded. Not yet used: normal, smooth, metal, dirt and detail maps (X4's own shaders).
    /// </summary>
    Material GetMaterial(string name)
    {
        if (_materials.TryGetValue(name, out var cached)) return cached;
        var m = new StandardMaterial3D { AlbedoColor = new Color(0.55f, 0.57f, 0.6f), Roughness = 0.6f };
        var x = name == "" ? null : _g.Materials.Get(name);
        if (x == null) { if (name != "") Notes.Add($"material '{name}' not in the library"); }
        else
        {
            var diffuse = x.Map("diffuse_map") ?? x.Map("diffuse") ?? x.Map("color_map");
            var tex = diffuse != null ? GetTexture(diffuse) : null;
            if (tex != null) { m.AlbedoTexture = tex; m.AlbedoColor = Colors.White; }
            m.Metallic = Math.Clamp(x.Float("Metallness", 0.3f), 0f, 1f) * 0.25f;   // low: no sky to reflect yet, full metal looks like chrome
            m.Roughness = Math.Clamp(1f - 0.5f * x.Float("Smoothness", 0.5f), 0.2f, 1f);
            bool additive = x.BlendMode is "ADDITIVE" or "SCREEN";
            bool alpha = x.BlendMode.StartsWith("ALPHA");             // ALPHA, ALPHA8, ALPHA_TEST ...
            // sky, stars, nebulae, effects: no lighting; painted by vertex colours (white if a mesh has none)
            bool unlit = x.Shader.Contains("unlit") || x.Shader.Contains("star") || x.Shader.Contains("effect");
            if (x.BlendMode == "PLANET_ATMOSPHERE")
            {
                // X4 computes light scattering; a thin additive blue shell comes close enough
                m.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded; m.AlbedoTexture = null;
                m.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; m.BlendMode = BaseMaterial3D.BlendModeEnum.Add;
                m.AlbedoColor = new Color(0.35f, 0.55f, 1f, 0.18f); m.CullMode = BaseMaterial3D.CullModeEnum.Front;
            }
            else if (unlit || additive || alpha)
            {
                m.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
                m.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
                if (unlit) { m.VertexColorUseAsAlbedo = true; m.VertexColorIsSrgb = true; }
                if (additive || alpha || tex == null)
                {
                    m.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
                    m.BlendMode = additive || tex == null ? BaseMaterial3D.BlendModeEnum.Add : BaseMaterial3D.BlendModeEnum.Mix;
                }
                if (tex == null && !unlit) m.AlbedoColor = new Color(0.25f, 0.55f, 1f, 0.35f);   // effect without texture (gate horizon): blue glow
            }
        }
        _materials[name] = m;
        return m;
    }

    Texture2D? GetTexture(string path)
    {
        if (_textures.TryGetValue(path, out var t)) return t;
        Texture2D? result = null;
        try
        {
            var dds = Dds.Load(_g.Vfs, path, SmallTextures);
            if (dds == null) Notes.Add($"texture '{path}' not found");
            else result = ToTexture(dds);
        }
        catch (Exception e) { Notes.Add($"texture '{path}': {e.Message}"); }
        _textures[path] = result;
        return result;
    }

    static readonly Dictionary<string, (Image.Format f, int blockBytes)> Formats = new()
    {
        ["Dxt1"] = (Image.Format.Dxt1, 8), ["Dxt3"] = (Image.Format.Dxt3, 16), ["Dxt5"] = (Image.Format.Dxt5, 16),
        ["RgtcR"] = (Image.Format.RgtcR, 8), ["RgtcRg"] = (Image.Format.RgtcRg, 16), ["BptcRgba"] = (Image.Format.BptcRgba, 16),
        ["Rgba8"] = (Image.Format.Rgba8, 0),
    };

    /// <summary>Hands the compressed data to Godot as it is. Uses the mip chain only if it is complete down to 1x1.</summary>
    static Texture2D? ToTexture(Dds d)
    {
        var (fmt, block) = Formats[d.Format];
        long Level(int w, int h) => block == 0 ? (long)w * h * 4 : (long)Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * block;
        long full = 0; int lw = d.Width, lh = d.Height, levels = 0;
        while (true) { full += Level(lw, lh); levels++; if (lw == 1 && lh == 1) break; lw = Math.Max(1, lw / 2); lh = Math.Max(1, lh / 2); }
        bool mips = d.Mipmaps == levels && d.Data.Length >= full;
        long size = mips ? full : Level(d.Width, d.Height);
        if (d.Data.Length < size) return null;
        var data = d.Data.Length == size ? d.Data : d.Data[..(int)size];
        var img = Image.CreateFromData(d.Width, d.Height, mips, fmt, data);
        return img == null || img.IsEmpty() ? null : ImageTexture.CreateFromImage(img);
    }
}
