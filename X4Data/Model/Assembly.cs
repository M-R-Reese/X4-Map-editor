using System.Numerics;

namespace X4Data.Model;

/// <summary>
/// Builds an object from a macro (or component) name: every visible part with its model file, transform and
/// materials, in X4 space. No Godot here; the Godot side turns each PlacedPart into a mesh instance.
/// </summary>
public static class Assembly
{
    public sealed record PlacedPart(string PartName, string XmfPath, Matrix4x4 Transform, Xmf Mesh, string[] MaterialNames);

    /// <summary>Tags of connections that carry nothing to see (game logic, collision, animation dummies).</summary>
    static readonly string[] SkipTags = { "geometryinvisible", "triggerpart" };

    /// <summary>
    /// The parts of a macro / component, and of the macros its macro file attaches at the component's connections
    /// (e.g. a Paranid monument = top + centre + containers, each its own macro), up to 4 levels deep.
    /// </summary>
    public static List<PlacedPart> Build(GameData g, string name, int lod, List<string> notes) => Build(g, name, lod, notes, 0);

    static List<PlacedPart> Build(GameData g, string name, int lod, List<string> notes, int depth)
    {
        var compName = g.ComponentOfMacro(name) ?? name;     // a macro points to its component; else take it as component
        var comp = g.Component(compName);
        if (comp == null) { notes.Add($"component '{compName}' not found"); return new(); }

        var list = new List<PlacedPart>();
        foreach (var con in comp.Connections)
        {
            if (SkipTags.Any(con.HasTag)) continue;
            foreach (var part in con.Parts)
            {
                // the file of the wanted LOD, else the nearest finer one, else lod0
                string? file = null; int usedLod = lod;
                for (int l = lod; l >= 0 && file == null; l--)
                {
                    var f = $"{comp.Geometry}/{part.Name}-lod{l}.xmf";
                    if (g.Vfs.Exists(f)) { file = f; usedLod = l; }
                }
                if (file == null) continue;                    // part without geometry (dummy, effect anchor)
                Xmf mesh;
                try { mesh = Xmf.Read(g.Vfs.Read(file)); }
                catch (Exception e) { notes.Add($"{file}: {e.Message}"); continue; }

                // material per xmf slot: the component's choice for this LOD (slot id = index + 1), else the xmf's own name
                part.Lods.TryGetValue(usedLod, out var compMats);
                if (compMats == null) part.Lods.TryGetValue(0, out compMats);
                var mats = mesh.Materials.Select((m, i) => compMats != null && compMats.TryGetValue(i + 1, out var r) && r != "" ? r : m.Name).ToArray();
                list.Add(new PlacedPart(part.Name, file, comp.PartTransform(part), mesh, mats));
            }
        }

        // macros attached by the macro file: <connection ref="(component connection)"><macro ref="(child macro)" ... />
        var macro = g.Macro(name);
        if (macro != null && depth < 4)
            foreach (var mc in macro.Element("connections")?.Elements("connection") ?? Enumerable.Empty<System.Xml.Linq.XElement>())
            {
                var childName = (string?)mc.Element("macro")?.Attribute("ref");
                var at = (string?)mc.Attribute("ref");
                if (childName == null || at == null) continue;
                var con = comp.Connections.FirstOrDefault(c => c.Name.Equals(at, StringComparison.OrdinalIgnoreCase));
                if (con == null || SkipTags.Any(con.HasTag)) continue;
                var where = comp.ConnectionTransform(con);
                foreach (var p in Build(g, childName, lod, notes, depth + 1))
                    list.Add(p with { PartName = childName + "/" + p.PartName, Transform = p.Transform * where });
            }
        return list;
    }
}
