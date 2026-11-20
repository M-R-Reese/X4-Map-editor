using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using X4Data.Xml;

namespace X4Data.Space;

/// <summary>
/// libraries/region_definitions.xml (+ extension patches): the fields a cluster places into its sectors
/// (asteroids, gas, nebulae, wrecks). Each definition has a boundary (sphere, cylinder or splinetube) and fields.
/// The game has no text names for them, so a readable name is built from the content.
/// </summary>
public sealed class Regions
{
    public sealed class Def
    {
        public string Name = "";
        public string Boundary = "";            // sphere, cylinder, splinetube
        public float R, Linear;                 // radius; cylinder: "linear" size (see Height)
        public List<SplinePoint> Spline = new();
        public float Density = 1;
        public List<string> AsteroidWares = new();   // ore, silicon, ice, nividium ... (from asteroid groups)
        public List<string> Gases = new();           // hydrogen, helium, methane ... (nebula resources)
        public List<string> FogMedia = new();        // volumetric fog media (look only)
        public bool Debris, Objects, Nebula;
        public string Category = "Other";             // Asteroid field, Gas cloud, Nebula, Wreck field, Other
        public string Label = "";
        public bool Hidden;                           // audio / global / test regions: not offered
        public string Source = "base";                // where the definition comes from (patched by an extension = its folder)

        /// <summary>Rough size in metres (diameter, or length of a spline tube).</summary>
        public float Size => Boundary == "splinetube" ? SplineLength() + 2 * R : 2 * R;
        public float SplineLength()
        {
            float l = 0;
            for (int i = 1; i < Spline.Count; i++) l += Vector3.Distance(Spline[i - 1].P, Spline[i].P);
            return l;
        }
    }

    /// <summary>Spline point: position, tangent direction, handle lengths in / out (cubic Bezier between points).</summary>
    public sealed record SplinePoint(Vector3 P, Vector3 T, float In, float Out);

    public List<Def> All { get; } = new();
    public Def? Get(string name) => All.FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    static readonly string[] HiddenPrefixes = { "audioregion", "globalregion", "specialregion", "tutorial", "unittest", "demo_", "cardinalsregion" };
    static readonly Regex VanillaShape = new(@"^region\d?_cluster_(\d+)_sector_?(\d+)(.*)$", RegexOptions.IgnoreCase);

    public Regions(GameData g)
    {
        var doc = GameXml.Load(g.Vfs, "libraries/region_definitions.xml", g.Notes);
        foreach (var r in doc?.Root?.Elements("region") ?? Enumerable.Empty<XElement>())
        {
            var d = new Def { Name = (string?)r.Attribute("name") ?? "", Density = F(r, "density", 1) };
            var b = r.Element("boundary");
            d.Boundary = (string?)b?.Attribute("class") ?? "sphere";
            var size = b?.Element("size");
            d.R = F(size, "r"); d.Linear = F(size, "linear");
            foreach (var sp in b?.Elements("splineposition") ?? Enumerable.Empty<XElement>())
                d.Spline.Add(new SplinePoint(new Vector3(F(sp, "x"), F(sp, "y"), F(sp, "z")), new Vector3(F(sp, "tx"), F(sp, "ty"), F(sp, "tz")), F(sp, "inlength"), F(sp, "outlength")));

            var fields = r.Element("fields");
            foreach (var a in fields?.Elements("asteroid") ?? Enumerable.Empty<XElement>())
            {
                // groupref "asteroid_ore_l" -> ore; "asteroid_ice_xs" -> ice
                var gr = ((string?)a.Attribute("groupref") ?? (string?)a.Attribute("ref") ?? "").ToLowerInvariant();
                var parts = gr.Split('_');
                var ware = parts.Length >= 2 && parts[0] == "asteroid" ? parts[1] : "";
                if (ware != "" && !d.AsteroidWares.Contains(ware)) d.AsteroidWares.Add(ware);
            }
            foreach (var n in fields?.Elements("nebula") ?? Enumerable.Empty<XElement>())
            {
                d.Nebula = true;
                foreach (var res in ((string?)n.Attribute("resources") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    if (!d.Gases.Contains(res)) d.Gases.Add(res);
            }
            foreach (var f in fields?.Elements("volumetricfog") ?? Enumerable.Empty<XElement>())
            {
                var med = (string?)f.Attribute("medium") ?? "";
                if (med != "" && !d.FogMedia.Contains(med)) d.FogMedia.Add(med);
            }
            d.Debris = fields?.Elements("debris").Any() == true;
            d.Objects = fields?.Elements("object").Any() == true;

            d.Hidden = HiddenPrefixes.Any(p => d.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) || d.R <= 0;
            // wrecks before gas: a wreck field may carry some gas too (p1_wreckfield_station_10km has hydrogen)
            d.Category = d.AsteroidWares.Count > 0 ? "Asteroid field"
                       : d.Debris || d.Name.Contains("wreck", StringComparison.OrdinalIgnoreCase) ? "Wreck field"
                       : d.Gases.Count > 0 ? "Gas cloud"
                       : d.Nebula || d.FogMedia.Count > 0 ? "Nebula"
                       : "Other";
            d.Label = MakeLabel(d);
            All.Add(d);
        }
        All.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));

        // where each definition comes from: base file, else the first extension whose patch names it
        const string lib = "libraries/region_definitions.xml";
        var baseNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (g.Vfs.Exists(lib))
            foreach (var r in GameXml.Parse(g.Vfs.Read(lib)).Root?.Elements("region") ?? Enumerable.Empty<XElement>())
                baseNames.Add((string?)r.Attribute("name") ?? "");
        foreach (var ext in g.Vfs.Extensions.LoadOrder)
        {
            var p = "extensions/" + ext.FolderName + "/" + lib;
            if (!g.Vfs.Exists(p)) continue;
            XDocument patch;
            try { patch = GameXml.Parse(g.Vfs.Read(p)); } catch { continue; }
            foreach (var r in patch.Descendants("region"))
            {
                var n = (string?)r.Attribute("name") ?? "";
                if (baseNames.Contains(n)) continue;
                var d = Get(n);
                if (d != null && d.Source == "base") d.Source = ext.FolderName;
            }
        }
    }

    static string MakeLabel(Def d)
    {
        string content = d.Category switch
        {
            "Asteroid field" => string.Join(", ", d.AsteroidWares),
            "Gas cloud" => string.Join(", ", d.Gases),
            "Nebula" => d.FogMedia.Count > 0 ? string.Join(", ", d.FogMedia.Take(2)) : "fog",
            "Wreck field" => d.Gases.Count > 0 ? "wrecks, " + string.Join(", ", d.Gases) : "wrecks",
            _ => d.Name,
        };
        string size = d.Boundary == "splinetube" ? $"tube {d.Size / 1000:N0} km long" : $"{d.Size / 1000:N0} km";
        var m = VanillaShape.Match(d.Name);
        string from = m.Success ? $" · shape of cluster {m.Groups[1].Value} sector {m.Groups[2].Value.TrimStart('0')}{m.Groups[3].Value.Replace('_', ' ')}" : "";
        return $"{d.Category}: {content} · {size}{from}";
    }

    static float F(XElement? e, string a, float d = 0) =>
        e != null && float.TryParse((string?)e.Attribute(a), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : d;

    /// <summary>Points along a splinetube (X4 space, relative to the region's position), about every 'step' metres.</summary>
    public static List<Vector3> SamplePath(Def d, float step = 2000)
    {
        var pts = new List<Vector3>();
        for (int i = 1; i < d.Spline.Count; i++)
        {
            var a = d.Spline[i - 1]; var b = d.Spline[i];
            Vector3 p0 = a.P, p1 = a.P + a.T * a.Out, p2 = b.P - b.T * b.In, p3 = b.P;
            int n = Math.Max(2, (int)(Vector3.Distance(p0, p3) / step));
            for (int k = (i == 1 ? 0 : 1); k <= n; k++)
            {
                float t = k / (float)n, u = 1 - t;
                pts.Add(u * u * u * p0 + 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t * p3);
            }
        }
        return pts;
    }
}
