using System.Text.RegularExpressions;
using System.Xml.Linq;
using X4Data.Text;
using X4Data.Xml;

namespace X4Data.Galaxy;

/// <summary>
/// What the installed game (base + DLCs + switched-on mods, merged) says about factions, for the sector "AI" section.
/// All read from the game files, nothing hard-coded (docs/plan_galaxy_editor_ai.md, step 1):
/// - libraries/factions.xml: name, primary race, tags ("claimspace" = can own sectors, "pirate", "hidden"), active;
/// - md/factionlogic.xml (+ DLC diffs): which factions have a faction logic manager (the AI that builds and expands);
/// - libraries/stations.xml -> stationgroups.xml -> constructionplans.xml -> module macros: which station a faction
///   can place that CLAIMS a sector (a module with &lt;ownership claim="1"/&gt;: defence platforms), and its pirate stations;
/// - libraries/god.xml and jobs.xml: how many factories / ship jobs come to a sector a faction owns (info line);
/// - libraries/mapdefaults.xml: the sector tags the game uses (anarchy, allowrandomanomaly ...).
/// </summary>
public sealed class FactionCatalog
{
    public sealed record Faction(string Id, string Name, string Race, IReadOnlySet<string> Tags, bool Active, bool Manager, string Source)
    {
        public bool Pirate => Tags.Contains("pirate");
        public bool Hidden => Tags.Contains("hidden");
        /// <summary>A station of this faction that claims a sector: its stations.xml tag (e.g. "defence"), or null.</summary>
        public string? ClaimTag { get; init; }
    }

    /// <summary>A station type the game can place for a faction (stations.xml entry): tag used by god.xml select, and whether it claims.</summary>
    public sealed record StationType(string Id, string Tag, string Faction, bool Claims, string Source);

    readonly Dictionary<string, Faction> _factions = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyCollection<Faction> All => _factions.Values;
    public List<StationType> Stations { get; } = new();
    /// <summary>Sector tags in mapdefaults.xml with how many sectors use them.</summary>
    public SortedDictionary<string, int> MapTags { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>faction id -> god.xml products that may go to a sector that faction owns (class galaxy, faction list).</summary>
    public Dictionary<string, int> Products { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>ware id -> its name in the game (English text).</summary>
    public Dictionary<string, string> WareNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>faction -> the wares its galaxy-wide god.xml factories make (factory stations placed by hand).</summary>
    public Dictionary<string, SortedSet<string>> ProductWares { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>faction id -> jobs.xml ship jobs placed in sectors of that faction (class galaxy, faction list).</summary>
    public Dictionary<string, int> Jobs { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>sector tag -> god.xml products for OWNERLESS sectors with that tag (anarchy: Scale Plate drug factories).</summary>
    public Dictionary<string, List<string>> TagProducts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Notes { get; } = new();

    /// <summary>Pirate station tags (vanilla piratebase, piratedock, piratedepot, anarchyport); a pirate faction's non-claiming stations.</summary>
    static readonly HashSet<string> PirateTags = new(StringComparer.OrdinalIgnoreCase) { "piratebase", "piratedock", "piratedepot", "anarchyport" };

    public Faction? Get(string? id) => id != null && _factions.TryGetValue(id, out var f) ? f : null;

    /// <summary>Factions that can own a sector at game start: can claim space, have a faction AI, and a claiming station.</summary>
    public IEnumerable<Faction> Owners => All.Where(f => f.Active && !f.Hidden && f.Id != "player" && f.Tags.Contains("claimspace") && f.Manager && f.ClaimTag != null)
        .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Factions with a faction AI that may be kept out of a sector (the manager's excluded list).</summary>
    public IEnumerable<Faction> Excludable => All.Where(f => f.Active && !f.Hidden && f.Id != "player" && f.Manager).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Pirate stations (bases, docks, depots, anarchy ports): of pirate factions, never claiming a sector.</summary>
    public IEnumerable<StationType> PirateStations => Stations.Where(s => PirateTags.Contains(s.Tag) && !s.Claims && Get(s.Faction) is { Active: true });

    public FactionCatalog(GameData g, TextDb? texts)
    {
        string Tx(string? r)
        {
            var m = Regex.Match(r ?? "", @"\{(\d+),\s*(\d+)\}");
            return m.Success ? texts?.Get(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) ?? r! : r ?? "";
        }
        XElement? Root(string f) => GameXml.Load(g.Vfs, f, Notes)?.Root;

        // ---- faction logic managers: <param name="Faction" value="faction.x"/> in md/factionlogic.xml and its DLC diffs
        var managers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in g.Vfs.Files.Where(f => Regex.IsMatch(f.Path, @"(^|/)md/factionlogic\.xml$", RegexOptions.IgnoreCase)))
            foreach (Match m in Regex.Matches(System.Text.Encoding.UTF8.GetString(f.Read()), @"name=""Faction""\s+value=""faction\.(\w+)"""))
                managers.Add(m.Groups[1].Value);

        // ---- stations.xml -> group -> construction plan -> does a module claim?
        var groups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);       // group -> plan
        foreach (var gr in Root("libraries/stationgroups.xml")?.Elements("group") ?? Enumerable.Empty<XElement>())
            if ((string?)gr.Attribute("name") is { } n && gr.Elements("select").Select(s => (string?)s.Attribute("constructionplan")).FirstOrDefault(p => p != null) is { } plan)
                groups[n] = plan;
        var plans = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);  // plan -> module macros
        foreach (var p in Root("libraries/constructionplans.xml")?.Elements("plan") ?? Enumerable.Empty<XElement>())
            if ((string?)p.Attribute("id") is { } id)
                plans[id] = p.Elements("entry").Select(e => (string?)e.Attribute("macro")).OfType<string>().Distinct().ToList();
        var claimCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        bool Claims(string macro)
        {
            if (claimCache.TryGetValue(macro, out var c)) return c;
            var m = g.Macro(macro);
            return claimCache[macro] = (string?)m?.Element("properties")?.Element("ownership")?.Attribute("claim") == "1";
        }
        foreach (var st in Root("libraries/stations.xml")?.Elements("station") ?? Enumerable.Empty<XElement>())
        {
            var id = (string?)st.Attribute("id") ?? ""; var grp = (string?)st.Attribute("group");
            bool claims = grp != null && groups.TryGetValue(grp, out var plan) && plans.TryGetValue(plan, out var mods) && mods.Any(Claims);
            foreach (var cat in st.Elements("category"))
            {
                var tags = ((string?)cat.Attribute("tags") ?? "").Trim('[', ']', ' ');
                foreach (var fac in FactionList((string?)cat.Attribute("faction")))
                    foreach (var tag in tags.Split(',', ' ').Where(t => t != ""))
                        Stations.Add(new StationType(id, tag, fac, claims, "base"));
            }
        }

        // ---- which extension adds a faction / station (folder name; "base" = the game): needed as a dependency of the mod
        var facSource = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var stSource = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in g.Vfs.Files.OrderBy(f => f.Path.StartsWith("extensions/", StringComparison.OrdinalIgnoreCase) ? 1 : 0))
        {
            var m = Regex.Match(f.Path, @"^(?:extensions/([^/]+)/)?libraries/(factions|stations)\.xml$", RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            var src = m.Groups[1].Success ? m.Groups[1].Value : "base";
            var into = m.Groups[2].Value.Equals("factions", StringComparison.OrdinalIgnoreCase) ? facSource : stSource;
            foreach (Match x in Regex.Matches(System.Text.Encoding.UTF8.GetString(f.Read()), @"<(?:faction|station)\s+id=""([^""]+)"""))
                into.TryAdd(x.Groups[1].Value, src);
        }
        for (int i = 0; i < Stations.Count; i++) Stations[i] = Stations[i] with { Source = stSource.GetValueOrDefault(Stations[i].Id, "base") };

        // ---- factions
        foreach (var f in Root("libraries/factions.xml")?.Elements("faction") ?? Enumerable.Empty<XElement>())
        {
            var id = (string?)f.Attribute("id"); if (id == null) continue;
            var tags = ((string?)f.Attribute("tags") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // the tag a claiming station of this faction has; "defence" first (vanilla's claim platforms), then any other
            var claim = Stations.Where(s => s.Faction.Equals(id, StringComparison.OrdinalIgnoreCase) && s.Claims)
                .OrderBy(s => s.Tag == "defence" ? 0 : 1).Select(s => s.Tag).FirstOrDefault();
            _factions[id] = new Faction(id, Tx((string?)f.Attribute("name")) is { Length: > 0 } nm ? nm : id, (string?)f.Attribute("primaryrace") ?? "argon",
                tags, (string?)f.Attribute("active") != "0", managers.Contains(id), facSource.GetValueOrDefault(id, "base")) { ClaimTag = claim };
        }

        // ---- map tags
        foreach (var a in Root("libraries/mapdefaults.xml")?.Descendants("area") ?? Enumerable.Empty<XElement>())
            foreach (var t in ((string?)a.Attribute("tags") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)) MapTags[t] = MapTags.GetValueOrDefault(t) + 1;

        // ---- god.xml products: by owner faction list (galaxy-wide locations), and for ownerless tagged sectors
        foreach (var p in Root("libraries/god.xml")?.Element("products")?.Elements("product") ?? Enumerable.Empty<XElement>())
        {
            var loc = p.Element("location"); if (loc == null || (string?)loc.Attribute("class") != "galaxy") continue;
            var facs = FactionList((string?)loc.Attribute("faction")).ToList();
            var tags = ((string?)loc.Attribute("tags") ?? "").Trim('[', ']').Split(',', ' ', StringSplitOptions.RemoveEmptyEntries);
            if (facs.Contains("ownerless"))
                foreach (var t in tags) (TagProducts.TryGetValue(t, out var l) ? l : TagProducts[t] = new()).Add((string?)p.Attribute("ware") ?? "?");
            else if (tags.Length == 0)                    // tagged ones only go to vanilla sectors with that tag
                foreach (var f in facs)
                {
                    Products[f] = Products.GetValueOrDefault(f) + 1;
                    if ((string?)p.Attribute("ware") is { } w) (ProductWares.TryGetValue(f, out var ws) ? ws : ProductWares[f] = new()).Add(w);
                }
        }
        foreach (var w in Root("libraries/wares.xml")?.Elements("ware") ?? Enumerable.Empty<XElement>())
            if ((string?)w.Attribute("id") is { } wid && Tx((string?)w.Attribute("name")) is { Length: > 0 } wn && !wn.StartsWith("{")) WareNames[wid] = wn;
        foreach (var j in Root("libraries/jobs.xml")?.Elements("job") ?? Enumerable.Empty<XElement>())
        {
            var loc = j.Element("location"); if (loc == null || (string?)loc.Attribute("class") != "galaxy") continue;
            foreach (var f in FactionList((string?)loc.Attribute("faction"))) Jobs[f] = Jobs.GetValueOrDefault(f) + 1;
        }
    }

    /// <summary>"argon", "[argon, antigone]", "faction.argon", "[faction.player]" -> ids.</summary>
    static IEnumerable<string> FactionList(string? v) =>
        (v ?? "").Trim('[', ']', ' ').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.StartsWith("faction.", StringComparison.OrdinalIgnoreCase) ? x[8..] : x).Where(x => x != "");
}
