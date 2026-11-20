using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using X4Data.Galaxy;
using X4Data.Space;

namespace GalaxyEditor3D;

/// <summary>
/// How decorations look in the 3D view (a rough picture, not the game's rendering):
///   field (region): a faint shell in the shape of its boundary (sphere, cylinder, or tube along its spline) with
///     outline lines, and sample rocks (asteroid fields, coloured by ware) or gas puffs (gas clouds, nebulae) inside;
///   object: the real game model (template built once per macro).
/// ASSUMPTION (not verified in the game): a cylinder's axis is vertical and "linear" is half its height.
/// </summary>
public sealed class DecorVisuals
{
    readonly App _app;
    readonly Dictionary<string, Node3D?> _objects = new();

    public DecorVisuals(App app) => _app = app;

    public static readonly Dictionary<string, Color> CategoryColour = new()
    {
        ["Asteroid field"] = new Color("c8915a"), ["Gas cloud"] = new Color("6fa8ff"), ["Nebula"] = new Color("c77dff"),
        ["Wreck field"] = new Color("d06a4a"), ["Other"] = new Color("aaaaaa"),
    };
    static readonly Dictionary<string, Color> WareColour = new()
    {
        ["ore"] = new Color("a0704a"), ["silicon"] = new Color("8a9bb0"), ["ice"] = new Color("dff2ff"), ["nividium"] = new Color("b070ff"),
        ["hydrogen"] = new Color("6fa8ff"), ["helium"] = new Color("ffb066"), ["methane"] = new Color("7fe08a"), ["rawscrap"] = new Color("c06040"),
    };

    /// <summary>Basis from yaw / pitch / roll in mirrored space (X4 -> Godot flips the sign of yaw and pitch, not of roll).</summary>
    public static Basis Rotation(Decoration d) => Rotation(d.Yaw, d.Pitch, d.Roll);

    /// <summary>X4 yaw / pitch / roll (degrees) -> Godot basis (mirrored z: yaw and pitch turn the other way).</summary>
    public static Basis Rotation(double yaw, double pitch, double roll) =>
        new Basis(Vector3.Up, -Mathf.DegToRad((float)yaw)) * new Basis(Vector3.Right, -Mathf.DegToRad((float)pitch)) * new Basis(Vector3.Back, Mathf.DegToRad((float)roll));

    /// <summary>Body node, marker icon, label and colour of a decoration.</summary>
    public (Node3D body, string icon, string label, Color colour) Build(Decoration d)
    {
        var item = _app.Decor?.Get(d.Kind, d.Ref);
        var label = item?.Label ?? d.Ref;
        int cut = label.IndexOf(" · shape of", StringComparison.Ordinal);
        if (cut > 0) label = label[..cut];                                    // the marker only needs the content
        if (item is { Egosoft: false }) label = "⚠ " + label;
        if (d.Kind == "region")
        {
            var def = _app.Decor?.Regions.Get(d.Ref);
            var col = def != null && CategoryColour.TryGetValue(def.Category, out var c) ? c : CategoryColour["Other"];
            return (def != null ? Region(def, d.Id) : new Node3D(), "◆", label, col);
        }
        if (item?.Category == "Anomaly")
        {
            var target = d.Link != null ? _app.P.SectorOfDecor(d.Link.Value)?.Name : null;
            return (Object(d.Ref), "✺", target != null ? $"{label} → {target}" : $"{label} (not connected)", target != null ? new Color("c77dff") : new Color("ffb347"));
        }
        return (Object(d.Ref), "■", label, new Color("e6e6e6"));
    }

    Node3D Object(string macro)
    {
        if (!_objects.TryGetValue(macro, out var tpl) && _app.Conv != null)
        {
            tpl = _app.Conv.BuildObject(macro);
            if (tpl.GetChildCount() == 0) { tpl.Free(); tpl = null; }
            _objects[macro] = tpl;
        }
        if (tpl != null) return (Node3D)tpl.Duplicate();
        var box = new Node3D();
        box.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(800, 800, 800) } });
        return box;
    }

    // ---------------------------------------------------------------------------------------------- fields
    Node3D Region(Regions.Def def, int seed)
    {
        var root = new Node3D();
        var col = CategoryColour.TryGetValue(def.Category, out var c) ? c : CategoryColour["Other"];
        var shellMat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            AlbedoColor = new Color(col, 0.05f), CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        var lines = new ImmediateMesh();
        var lineMat = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, AlbedoColor = new Color(col, 0.55f) };
        lines.SurfaceBegin(Mesh.PrimitiveType.Lines, lineMat);
        var rnd = new Random(seed * 7919 + def.Name.Length);
        var samples = new List<Vector3>();                          // random points inside the volume (Godot space)
        float r = def.R;

        if (def.Boundary == "splinetube" && def.Spline.Count >= 2)
        {
            var path = Regions.SamplePath(def, Math.Max(1000, r)).Select(p => new Vector3(p.X, p.Y, -p.Z)).ToList();
            for (int i = 0; i < path.Count; i++)
            {
                var dir = (i + 1 < path.Count ? path[i + 1] - path[i] : path[i] - path[i - 1]).Normalized();
                Circle(lines, path[i], dir, r, 20);
                if (i > 0) { lines.SurfaceAddVertex(path[i - 1]); lines.SurfaceAddVertex(path[i]); }
            }
            int n = Count(def, path.Count * 4);
            for (int k = 0; k < n; k++) samples.Add(path[rnd.Next(path.Count)] + InBall(rnd) * r);
        }
        else if (def.Boundary == "cylinder")
        {
            float h = Math.Max(def.Linear * 2, r * 0.05f);
            root.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = r, BottomRadius = r, Height = h, RadialSegments = 48, Rings = 1 }, MaterialOverride = shellMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
            Circle(lines, new Vector3(0, h / 2, 0), Vector3.Up, r, 64); Circle(lines, new Vector3(0, -h / 2, 0), Vector3.Up, r, 64);
            for (int i = 0; i < 8; i++) { float a = MathF.Tau * i / 8; var p = new Vector3(r * MathF.Cos(a), 0, r * MathF.Sin(a)); lines.SurfaceAddVertex(p + Vector3.Up * h / 2); lines.SurfaceAddVertex(p - Vector3.Up * h / 2); }
            int n = Count(def, 400);
            for (int k = 0; k < n; k++) { var b = InDisc(rnd) * r; samples.Add(new Vector3(b.X, ((float)rnd.NextDouble() - 0.5f) * h, b.Y)); }
        }
        else
        {
            root.AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = r, Height = 2 * r, RadialSegments = 32, Rings = 16 }, MaterialOverride = shellMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
            Circle(lines, Vector3.Zero, Vector3.Up, r, 64); Circle(lines, Vector3.Zero, Vector3.Right, r, 64); Circle(lines, Vector3.Zero, Vector3.Back, r, 64);
            int n = Count(def, 400);
            for (int k = 0; k < n; k++) samples.Add(InBall(rnd) * r);
        }
        lines.SurfaceEnd();
        root.AddChild(new MeshInstance3D { Mesh = lines, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        if (samples.Count > 0) root.AddChild(Fill(def, samples, rnd));
        return root;
    }

    /// <summary>How many sample rocks / puffs: more for denser fields, capped so big fields stay fast.</summary>
    static int Count(Regions.Def def, int cap) => (int)Math.Clamp(def.Density * 90, 60, Math.Min(cap, 600));

    /// <summary>Asteroid fields: small lit rocks coloured by ware; gas / nebula: big soft additive puffs; wrecks: rust boxes.</summary>
    static MultiMeshInstance3D Fill(Regions.Def def, List<Vector3> pts, Random rnd)
    {
        bool rocks = def.Category is "Asteroid field" or "Wreck field";
        float size = rocks ? Math.Clamp(def.Size / 120f, 250, 2500) : Math.Clamp(def.Size / 18f, 1500, 12000);
        Mesh mesh = def.Category == "Wreck field" ? new BoxMesh { Size = Vector3.One * size }
                  : new SphereMesh { Radius = size / 2, Height = size, RadialSegments = rocks ? 7 : 12, Rings = rocks ? 4 : 6 };
        var mat = rocks
            ? new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 0.95f }
            : new StandardMaterial3D { VertexColorUseAsAlbedo = true, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                                       Transparency = BaseMaterial3D.TransparencyEnum.Alpha, BlendMode = BaseMaterial3D.BlendModeEnum.Add };
        mesh.SurfaceSetMaterial(0, mat);
        var palette = (def.AsteroidWares.Count > 0 ? def.AsteroidWares : def.Gases).Select(w => WareColour.TryGetValue(w, out var c) ? c : new Color("9a9a9a")).ToList();
        if (palette.Count == 0) palette.Add(CategoryColour.TryGetValue(def.Category, out var cc) ? cc : new Color("9a9a9a"));
        var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, Mesh = mesh, InstanceCount = pts.Count };
        for (int i = 0; i < pts.Count; i++)
        {
            float s = 0.4f + (float)rnd.NextDouble() * 1.2f;
            var basis = new Basis(new Vector3((float)rnd.NextDouble(), (float)rnd.NextDouble(), (float)rnd.NextDouble()).Normalized(), (float)rnd.NextDouble() * MathF.Tau).Scaled(Vector3.One * s);
            mm.SetInstanceTransform(i, new Transform3D(basis, pts[i]));
            var c = palette[rnd.Next(palette.Count)];
            mm.SetInstanceColor(i, rocks ? c : new Color(c, 0.10f));
        }
        return new MultiMeshInstance3D { Multimesh = mm, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
    }

    static void Circle(ImmediateMesh im, Vector3 centre, Vector3 normal, float r, int seg)
    {
        var a = Math.Abs(normal.Dot(Vector3.Up)) > 0.9f ? Vector3.Right : Vector3.Up;
        var u = normal.Cross(a).Normalized() * r; var v = normal.Cross(u).Normalized() * r;
        for (int i = 0; i < seg; i++)
        {
            float a0 = MathF.Tau * i / seg, a1 = MathF.Tau * (i + 1) / seg;
            im.SurfaceAddVertex(centre + u * MathF.Cos(a0) + v * MathF.Sin(a0));
            im.SurfaceAddVertex(centre + u * MathF.Cos(a1) + v * MathF.Sin(a1));
        }
    }

    static Vector3 InBall(Random r)
    {
        while (true)
        {
            var p = new Vector3((float)r.NextDouble() * 2 - 1, (float)r.NextDouble() * 2 - 1, (float)r.NextDouble() * 2 - 1);
            if (p.LengthSquared() <= 1) return p;
        }
    }

    static Vector2 InDisc(Random r)
    {
        while (true)
        {
            var p = new Vector2((float)r.NextDouble() * 2 - 1, (float)r.NextDouble() * 2 - 1);
            if (p.LengthSquared() <= 1) return p;
        }
    }

    public void Free() { foreach (var t in _objects.Values) t?.Free(); _objects.Clear(); }
}
