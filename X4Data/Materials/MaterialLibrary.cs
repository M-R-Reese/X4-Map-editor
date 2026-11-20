using System.Globalization;
using System.Xml.Linq;
using X4Data.Xml;

namespace X4Data.Materials;

/// <summary>
/// libraries/material_library.xml (+ material_library_1.xml, + extension patches): "collection.material" ->
/// shader, blend mode, texture maps (BitMap) and numbers (Float). Only what the viewer needs.
/// </summary>
public sealed class MaterialLibrary
{
    public sealed class Material
    {
        public string Name = "", Shader = "", BlendMode = "NONE";
        public Dictionary<string, string> Maps = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, float> Floats = new(StringComparer.OrdinalIgnoreCase);
        public string? Map(string n) => Maps.TryGetValue(n, out var v) && v != "" ? v : null;
        public float Float(string n, float fallback) => Floats.TryGetValue(n, out var v) ? v : fallback;
    }

    readonly Dictionary<string, Material> _byName = new(StringComparer.OrdinalIgnoreCase);
    public int Count => _byName.Count;

    public MaterialLibrary(GameData g)
    {
        foreach (var file in new[] { "libraries/material_library.xml", "libraries/material_library_1.xml" })
        {
            var doc = GameXml.Load(g.Vfs, file, g.Notes);
            foreach (var col in doc?.Root?.Elements("collection") ?? Enumerable.Empty<XElement>())
            {
                var cn = (string?)col.Attribute("name") ?? "";
                foreach (var m in col.Elements("material"))
                {
                    var mat = new Material
                    {
                        Name = cn + "." + (string?)m.Attribute("name"),
                        Shader = (string?)m.Attribute("shader") ?? "",
                        BlendMode = (string?)m.Attribute("blendmode") ?? "NONE",
                    };
                    foreach (var p in m.Element("properties")?.Elements("property") ?? Enumerable.Empty<XElement>())
                    {
                        var n = (string?)p.Attribute("name") ?? ""; var v = (string?)p.Attribute("value") ?? "";
                        switch ((string?)p.Attribute("type"))
                        {
                            case "BitMap": mat.Maps[n] = Vfs.VirtualFileSystem.Norm(v); break;
                            case "Float" when float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f): mat.Floats[n] = f; break;
                        }
                    }
                    _byName[mat.Name] = mat;
                }
            }
        }
    }

    public Material? Get(string name) => _byName.GetValueOrDefault(name);
}
