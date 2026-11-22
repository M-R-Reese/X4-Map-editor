using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace X4Data.Space;

/// <summary>
/// Cluster backdrops: components "Cluster_NN" (assets/environments/cluster, DLCs add their own). A backdrop holds the
/// sky sphere, nebulae, planets and moons (100,000 to 400,000 km away) and the lights: the omni light with a lens
/// effect is the sun, the ambient light tints the dark side.
/// </summary>
public static class Backdrops
{
    /// <summary>
    /// One backdrop. Number = NN of "Cluster_NN" (null for freely named ones). Source = "base" or the extension folder.
    /// Egosoft = base game or an Egosoft DLC (ego_*); anything else belongs to another mod author.
    /// </summary>
    public sealed record Entry(int? Number, string Component, string Source, bool Egosoft);

    /// <summary>
    /// Every backdrop the loaded game has: components of class "celestialbody" in an environments/cluster(s) folder
    /// (that is where the base game, all DLCs and full conversions keep them; names are free, e.g. "Cluster_01", "taris").
    /// Base game first, then DLCs, then other mods; by number / name inside each.
    /// </summary>
    public static List<Entry> List(GameData g)
    {
        var re = new Regex(@"^Cluster_(\d+)$", RegexOptions.IgnoreCase);
        var list = new List<Entry>();
        foreach (var name in g.ComponentNames)
        {
            var file = g.ComponentFile(name);
            if (file == null || !Regex.IsMatch(file, @"/environments/clusters?/", RegexOptions.IgnoreCase) || !g.Vfs.Exists(file)) continue;
            if (ComponentClass(g, name, file) != "celestialbody") continue;
            var src = file.StartsWith("extensions/", StringComparison.OrdinalIgnoreCase) ? file.Split('/')[1] : "base";
            var m = re.Match(name);
            list.Add(new Entry(m.Success ? int.Parse(m.Groups[1].Value) : null, name, src, src == "base" || src.StartsWith("ego_", StringComparison.OrdinalIgnoreCase)));
        }
        return list.OrderBy(e => e.Source == "base" ? 0 : e.Egosoft ? 1 : 2).ThenBy(e => e.Source)
                   .ThenBy(e => e.Number ?? int.MaxValue).ThenBy(e => e.Component, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static readonly Regex ComponentTag = new(@"<component\s[^>]*>", RegexOptions.Compiled);
    static readonly Regex Attr = new(@"\b(name|class)\s*=\s*""([^""]*)""", RegexOptions.Compiled);

    /// <summary>
    /// The class of a component, read from the top of its file: cluster files hold thousands of stars and planets,
    /// parsing all of that only for this one attribute took 4 s at start (2026-11-22). Whole file only as fallback.
    /// </summary>
    static string? ComponentClass(GameData g, string name, string file)
    {
        try
        {
            var bytes = g.Vfs.Read(file);
            var head = System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 16384));
            foreach (Match tag in ComponentTag.Matches(head))
            {
                string? n = null, c = null;
                foreach (Match a in Attr.Matches(tag.Value)) { if (a.Groups[1].Value == "name") n = a.Groups[2].Value; else c = a.Groups[2].Value; }
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return c;
            }
            return (string?)g.ComponentElement(name)?.Attribute("class");
        }
        catch { return null; }
    }

    /// <summary>SunPosition: where the sun light sits in the backdrop (metres, X4 space); SunDirection: from the cluster centre.</summary>
    public sealed record Lighting(Vector3 SunPosition, Vector3 SunDirection, Vector3 SunColour, float SunIntensity, Vector3 AmbientColour);

    /// <summary>Sun and ambient light of a backdrop (X4 space; direction = from the sector towards the sun).</summary>
    public static Lighting? Light(GameData g, string component)
    {
        var c = g.ComponentElement(component);
        var lights = c?.Descendants("lights").FirstOrDefault();
        if (lights == null) return null;
        static float F(XElement? e, string a, float d = 0) =>
            e != null && float.TryParse((string?)e.Attribute(a), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : d;
        static Vector3 Pos(XElement e) { var p = e.Element("offset")?.Element("position"); return new Vector3(F(p, "x"), F(p, "y"), F(p, "z")); }
        static Vector3 Rgb(XElement e) => new(F(e, "r", 255) / 255f, F(e, "g", 255) / 255f, F(e, "b", 255) / 255f);

        // the sun: omni light with a lens effect, else the strongest omni
        var sun = lights.Elements("omni").OrderByDescending(o => o.Attribute("lenseffect") != null).ThenByDescending(o => F(o, "intensity")).FirstOrDefault();
        var amb = lights.Elements("ambient").FirstOrDefault();
        if (sun == null) return null;
        var pos = Pos(sun);
        var dir = pos.LengthSquared() > 0 ? Vector3.Normalize(pos) : Vector3.UnitY;
        return new Lighting(pos, dir, Rgb(sun), F(sun, "intensity", 1), amb != null ? Rgb(amb) * F(amb, "intensity", 1) : Vector3.Zero);
    }
}
