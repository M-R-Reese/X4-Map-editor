using System.Xml.Linq;
using X4Data.Text;
using X4Data.Xml;

namespace X4Data.Space;

/// <summary>
/// Everything a sector can be decorated with, grouped for dropdowns, named like the game names it:
///   regions (fields from region_definitions: asteroid fields, gas clouds, nebulae, wreck fields),
///   objects (macros: wrecks, landmarks, data vaults, nav beacons, single asteroids) with their text name.
/// Ware boxes, lockboxes, collectables, station modules and ships are not decoration (scripts / population editor).
/// </summary>
public sealed class DecorCatalog
{
    public sealed record Item(string Category, string Kind, string Ref, string Label, string Source, bool Egosoft);

    public List<Item> Items { get; } = new();
    public Regions Regions { get; }
    public IEnumerable<string> Categories => Items.Select(i => i.Category).Distinct();

    /// <summary>Object classes and name prefixes offered as decoration (category, class, name prefixes; empty = all of the class).</summary>
    static readonly (string Category, string Class, string[] Prefixes)[] ObjectRules =
    {
        ("Wreck", "recyclable", Array.Empty<string>()),
        ("Landmark", "object", new[] { "landmarks_", "torus_maze", "par_elder" }),
        ("Data vault", "datavault", Array.Empty<string>()),
        ("Anomaly", "anomaly", new[] { "wormhole_" }),             // wormholes; a pair is linked by script at game start
        ("Nav beacon", "navbeacon", Array.Empty<string>()),
        ("Single asteroid", "asteroid", new[] { "env_ast", "env_asteroid", "asteroid_special", "asteroid_highyield" }),
    };

    public DecorCatalog(GameData g, TextDb texts)
    {
        Regions = new Regions(g);
        foreach (var d in Regions.All.Where(d => !d.Hidden))
            Items.Add(new Item(d.Category, "region", d.Name, d.Label, d.Source, d.Source == "base" || d.Source.StartsWith("ego_", StringComparison.OrdinalIgnoreCase)));

        // objects: scan the macro files of the index (base + extensions)
        foreach (var f in g.Vfs.Files.Where(f => f.Path.Contains("/macros/", StringComparison.OrdinalIgnoreCase) && f.Path.EndsWith("_macro.xml", StringComparison.OrdinalIgnoreCase)))
        {
            XElement? root;
            try { root = GameXml.Parse(f.Read()).Root; } catch { continue; }
            foreach (var m in root?.Elements("macro") ?? Enumerable.Empty<XElement>())
            {
                var cls = (string?)m.Attribute("class") ?? "";
                var name = (string?)m.Attribute("name") ?? "";
                var rule = ObjectRules.FirstOrDefault(r => r.Class == cls && (r.Prefixes.Length == 0 || r.Prefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))));
                if (rule.Category == null) continue;
                if (g.MacroFile(name) == null) continue;                       // not in the index: the game cannot place it
                var text = texts.Lookup((string?)m.Element("properties")?.Element("identification")?.Attribute("name"));
                var src = f.Path.StartsWith("extensions/", StringComparison.OrdinalIgnoreCase) ? f.Path.Split('/')[1] : "base";
                // many macros share one text name ("Wreck"): the readable rest of the id tells them apart
                var detail = name.Replace("_macro", "");
                foreach (var p in new[] { "env_debris_", "landmarks_", "env_deco_", "env_asteroid_", "env_ast_", "asteroid_" })
                    if (detail.StartsWith(p, StringComparison.OrdinalIgnoreCase)) { detail = detail[p.Length..]; break; }
                detail = detail.Replace('_', ' ');
                var label = string.IsNullOrWhiteSpace(text) ? detail : $"{text} ({detail})";
                Items.Add(new Item(rule.Category, "object", name, label, src, src == "base" || src.StartsWith("ego_", StringComparison.OrdinalIgnoreCase)));
            }
        }
        // identical labels (e.g. two 50 km helium clouds): add the id so they can be told apart
        foreach (var dup in Items.GroupBy(i => i.Label).Where(x => x.Count() > 1).ToList())
            foreach (var i in dup.ToList()) Items[Items.IndexOf(i)] = i with { Label = $"{i.Label} [{i.Ref}]" };
        Items.Sort((a, b) => a.Category != b.Category ? string.Compare(a.Category, b.Category, StringComparison.Ordinal)
                                                        : string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
    }

    public Item? Get(string kind, string reference) => Items.FirstOrDefault(i => i.Kind == kind && i.Ref.Equals(reference, StringComparison.OrdinalIgnoreCase));
}
