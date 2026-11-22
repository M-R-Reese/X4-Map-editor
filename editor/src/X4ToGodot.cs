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
    /// <summary>Where model building spends its time (ms): parts read, tangents, textures. For the debug log.</summary>
    public readonly System.Diagnostics.Stopwatch TimeParts = new(), TimeTangents = new(), TimeTextures = new();
    /// <summary>Use the game's "-small" textures (faster loading, less memory).</summary>
    public bool SmallTextures { get; set; }

    public X4ToGodot(GameData g) => _g = g;

    /// <summary>Performance mode: Godot's standard material for everything instead of the X4 shaders (X4Shaders,
    /// PlanetShaders, BackdropShaders), and no effect layers (IsEffect). Changing it drops the built materials; models built before keep theirs
    /// until they are built again (SectorView.SetPerformance).</summary>
    public bool Simple
    {
        get => _simple;
        set { if (_simple == value) return; _simple = value; _materials.Clear(); }
    }
    bool _simple;

    /// <summary>Builds a macro / component as a Node3D with one MeshInstance3D per part.</summary>
    public Node3D BuildObject(string name, int lod = 0)
    {
        var root = new Node3D { Name = name };
        TimeParts.Start();
        var parts = Assembly.Build(_g, name, lod, Notes).ToList();
        TimeParts.Stop();
        if (System.Environment.GetEnvironmentVariable("X4ED_MODELSIZES") != null)
            GD.Print($"model {name} lod {lod}: {parts.Count} parts, {parts.Sum(p => (long)p.Mesh.Positions.Length)} vertices");
        foreach (var p in parts)
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
            if (_simple && IsEffect(r < p.MaterialNames.Length ? p.MaterialNames[r] : "")) continue;      // performance mode: no effect layers
            var remap = new Dictionary<int, int>();
            var verts = new List<Vector3>(); var norms = new List<Vector3>(); var uvs = new List<Vector2>(); var uvs2 = new List<Vector2>(); var cols = new List<Color>();
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
                        if (x.Uvs2 != null) { var u = x.Uvs2[src]; uvs2.Add(new Vector2(u.X, u.Y)); }
                        if (x.Colors != null) cols.Add(Color.Color8(x.Colors[src * 4], x.Colors[src * 4 + 1], x.Colors[src * 4 + 2], 255));   // X4 keeps 0 in the alpha byte: not transparency
                    }
                    idx[i + k] = dst;
                }
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
            if (norms.Count == verts.Count) arrays[(int)Mesh.ArrayType.Normal] = norms.ToArray();
            if (uvs.Count == verts.Count) arrays[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();
            if (uvs2.Count == verts.Count) arrays[(int)Mesh.ArrayType.TexUV2] = uvs2.ToArray();     // planets' clouds
            if (cols.Count == verts.Count) arrays[(int)Mesh.ArrayType.Color] = cols.ToArray();
            arrays[(int)Mesh.ArrayType.Index] = idx;
            // tangents only where a normal map uses them (lit hulls): computing them for sky spheres and planets
            // cost seconds per sector (2026-11-22)
            if (norms.Count == verts.Count && uvs.Count == verts.Count && NeedsTangents(r < p.MaterialNames.Length ? p.MaterialNames[r] : ""))
            {
                // normal maps need tangents; xmf files carry none we read: computed here per triangle (Godot's own
                // MikkTSpace took 3-4 s for a sector's stations, 2026-11-22)
                TimeTangents.Start();
                var tan = Tangents(verts, norms, uvs, idx);
                TimeTangents.Stop();
                if (System.Environment.GetEnvironmentVariable("X4ED_TANGENTCHECK") != null) CompareTangents(arrays, tan);
                arrays[(int)Mesh.ArrayType.Tangent] = tan;
            }
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
    /// X4 material -> Godot material. Lit hulls and decals: X4Shaders (diffuse, normal, smooth, metal, glow, detail
    /// maps as X4 uses them). Planets: PlanetShaders; sky, nebulae, suns: BackdropShaders. Everything else (effects, glass): StandardMaterial3D, close, not exact.
    /// </summary>
    /// <summary>
    /// Tangents (x, y, z, sign per vertex, Godot's layout) from the triangles' UV directions: the direction of +u along
    /// the surface, made square to the normal; sign = -1 when +v runs along cross(normal, tangent) (Godot's convention).
    /// </summary>
    static float[] Tangents(List<Vector3> v, List<Vector3> n, List<Vector2> uv, int[] idx)
    {
        var tu = new Vector3[v.Count]; var tv = new Vector3[v.Count];
        for (int i = 0; i + 2 < idx.Length; i += 3)
        {
            int a = idx[i], b = idx[i + 1], c = idx[i + 2];
            Vector3 e1 = v[b] - v[a], e2 = v[c] - v[a];
            float du1 = uv[b].X - uv[a].X, dv1 = uv[b].Y - uv[a].Y, du2 = uv[c].X - uv[a].X, dv2 = uv[c].Y - uv[a].Y;
            float r = du1 * dv2 - du2 * dv1;
            if (MathF.Abs(r) < 1e-12f) continue;
            var sdir = (e1 * dv2 - e2 * dv1) / r;
            var tdir = (e2 * du1 - e1 * du2) / r;
            tu[a] += sdir; tu[b] += sdir; tu[c] += sdir;
            tv[a] += tdir; tv[b] += tdir; tv[c] += tdir;
        }
        var res = new float[v.Count * 4];
        for (int k = 0; k < v.Count; k++)
        {
            var nn = n[k];
            var t = tu[k] - nn * nn.Dot(tu[k]);
            if (t.LengthSquared() < 1e-20f) t = MathF.Abs(nn.X) < 0.9f ? nn.Cross(Vector3.Right) : nn.Cross(Vector3.Up);
            t = t.Normalized();
            res[k * 4] = t.X; res[k * 4 + 1] = t.Y; res[k * 4 + 2] = t.Z;
            res[k * 4 + 3] = nn.Cross(t).Dot(tv[k]) < 0 ? 1f : -1f;      // Godot's sign is the other way round (checked against MikkTSpace)
        }
        return res;
    }

    /// <summary>Check run (X4ED_TANGENTCHECK): how far our tangents differ from Godot's MikkTSpace ones.</summary>
    static void CompareTangents(Godot.Collections.Array arrays, float[] ours)
    {
        var st = new SurfaceTool(); st.CreateFromArrays(arrays); st.GenerateTangents();
        var mikk = st.CommitToArrays()[(int)Mesh.ArrayType.Tangent].AsFloat32Array();
        if (mikk.Length != ours.Length) { GD.Print($"tangent check: sizes differ {mikk.Length} / {ours.Length}"); return; }
        int n = ours.Length / 4, sameSign = 0; double dot = 0;
        for (int k = 0; k < n; k++)
        {
            if (MathF.Sign(mikk[k * 4 + 3]) == MathF.Sign(ours[k * 4 + 3])) sameSign++;
            dot += mikk[k * 4] * ours[k * 4] + mikk[k * 4 + 1] * ours[k * 4 + 1] + mikk[k * 4 + 2] * ours[k * 4 + 2];
        }
        GD.Print($"tangent check: {n} vertices, same sign {100.0 * sameSign / n:F1} %, mean direction agreement {dot / n:F3}");
    }

    /// <summary>Does this material's shader use a normal map (X4Shaders with normal_map)? Not in performance mode.</summary>
    bool NeedsTangents(string name) =>
        !_simple && name != "" && _g.Materials.Get(name) is { } x && X4Shaders.Handles(x) && x.BlendMode is not ("ADDITIVE" or "SCREEN")
        && (x.Map("normal_map") != null || x.Map("normal_detail_map") != null);

    /// <summary>Glowing / see-through effect layers (nebulae, glows, coronas, atmospheres, gate effects): left out in
    /// performance mode. The starfield stays (space without stars looks broken).</summary>
    bool IsEffect(string name) =>
        name != "" && _g.Materials.Get(name) is { } x && x.Shader != "p1_starfield"
        && (x.BlendMode is "ADDITIVE" or "SCREEN" or "PLANET_ATMOSPHERE" || x.Shader.Contains("effect"));

    Material GetMaterial(string name)
    {
        if (_materials.TryGetValue(name, out var cached)) return cached;
        var lit = name == "" || _simple ? null : _g.Materials.Get(name);     // performance mode: none of the X4 shaders
        if (lit != null && X4Shaders.Handles(lit) && lit.BlendMode is not ("ADDITIVE" or "SCREEN"))
            return _materials[name] = X4Shaders.Build(lit, GetTexture);
        // planets, moons and their atmospheres
        if (lit != null && PlanetShaders.Build(lit, GetTexture) is { } pl) return _materials[name] = pl;
        // sky, stars, nebulae, suns, rings
        if (lit != null && BackdropShaders.Build(lit, GetTexture) is { } bd) return _materials[name] = bd;
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
                // performance mode: the starfield's texture keeps 0 in its alpha, adding must not multiply by it
                if (_simple && additive && tex != null) m.Transparency = BaseMaterial3D.TransparencyEnum.Disabled;
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
        TimeTextures.Start();
        try
        {
            var dds = Dds.Load(_g.Vfs, path, SmallTextures);
            if (dds == null) Notes.Add($"texture '{path}' not found");
            else result = ToTexture(dds);
        }
        catch (Exception e) { Notes.Add($"texture '{path}': {e.Message}"); }
        TimeTextures.Stop();
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
