using System.Globalization;
using System.Numerics;
using System.Xml.Linq;

namespace X4Data.Model;

/// <summary>
/// A component file: where its geometry lies and its connections. A connection carries an offset (position +
/// quaternion, relative to its parent part, or to the component if it has no parent) and the parts placed there.
/// Coordinates stay in X4's space (left-handed, metres); the Godot side converts.
/// </summary>
public sealed class Component
{
    public string Name { get; init; } = "";
    public string Class { get; init; } = "";
    /// <summary>Folder of the part models, e.g. "assets/props/gates/props_anc_gate_01_data".</summary>
    public string Geometry { get; init; } = "";
    public List<Connection> Connections { get; } = new();

    public sealed class Connection
    {
        public string Name = "";
        public string Tags = "";
        public string? Parent;                           // name of the part this connection hangs on
        public Vector3 Position;
        public Quaternion Rotation = Quaternion.Identity;
        public List<Part> Parts = new();
        public bool HasTag(string t) => (" " + Tags + " ").Contains(" " + t + " ");
    }

    public sealed class Part
    {
        public string Name = "";
        public Connection Owner = null!;
        /// <summary>Per LOD index: material slot id -> "collection.material".</summary>
        public SortedDictionary<int, Dictionary<int, string>> Lods = new();
    }

    public static Component Parse(XElement c)
    {
        var comp = new Component
        {
            Name = (string?)c.Attribute("name") ?? "",
            Class = (string?)c.Attribute("class") ?? "",
            Geometry = Vfs.VirtualFileSystem.Norm((string?)c.Element("source")?.Attribute("geometry") ?? ""),
        };
        foreach (var x in c.Element("connections")?.Elements("connection") ?? Enumerable.Empty<XElement>())
        {
            var con = new Connection
            {
                Name = (string?)x.Attribute("name") ?? "",
                Tags = (string?)x.Attribute("tags") ?? "",
                Parent = (string?)x.Attribute("parent"),
            };
            var off = x.Element("offset");
            if (off?.Element("position") is { } p) con.Position = new Vector3(F(p, "x"), F(p, "y"), F(p, "z"));
            if (off?.Element("quaternion") is { } q) con.Rotation = new Quaternion(F(q, "qx"), F(q, "qy"), F(q, "qz"), F(q, "qw"));
            foreach (var pe in x.Element("parts")?.Elements("part") ?? Enumerable.Empty<XElement>())
            {
                var part = new Part { Name = (string?)pe.Attribute("name") ?? "", Owner = con };
                foreach (var lod in pe.Element("lods")?.Elements("lod") ?? Enumerable.Empty<XElement>())
                {
                    var mats = new Dictionary<int, string>();
                    foreach (var m in lod.Element("materials")?.Elements("material") ?? Enumerable.Empty<XElement>())
                        mats[(int)F(m, "id")] = (string?)m.Attribute("ref") ?? "";
                    part.Lods[(int)F(lod, "index")] = mats;
                }
                con.Parts.Add(part);
            }
            comp.Connections.Add(con);
        }
        return comp;
    }

    static float F(XElement e, string a) =>
        float.TryParse((string?)e.Attribute(a), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;

    /// <summary>Every part, by name (parent lookups).</summary>
    public Dictionary<string, Part> PartsByName() =>
        Connections.SelectMany(c => c.Parts).GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Transform of a part relative to the component: its connection's offset, then the parent part's, up the chain.
    /// Returned as a matrix in X4 space (row vectors, System.Numerics convention).
    /// </summary>
    public Matrix4x4 PartTransform(Part part)
    {
        var byName = PartsByName();
        var m = Matrix4x4.Identity;
        var con = part.Owner;
        for (int guard = 0; con != null && guard < 64; guard++)
        {
            m *= Matrix4x4.CreateFromQuaternion(con.Rotation) * Matrix4x4.CreateTranslation(con.Position);
            con = con.Parent != null && byName.TryGetValue(con.Parent, out var pp) ? pp.Owner : null!;
        }
        return m;
    }
}
