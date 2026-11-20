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

    public static List<PlacedPart> Build(GameData g, string name, int lod, List<string> notes)
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
        return list;
    }
}
