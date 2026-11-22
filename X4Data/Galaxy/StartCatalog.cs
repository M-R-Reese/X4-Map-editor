using System.Text.RegularExpressions;
using System.Xml.Linq;
using X4Data.Text;
using X4Data.Xml;

namespace X4Data.Galaxy;

/// <summary>
/// The game's own game starts (libraries/gamestarts.xml, base + DLC diffs), read for the project's game start:
/// - every start's player ship WITH its loadout (a start ship needs every engine / weapon / shield slot named; copying a
///   vanilla start's ship is the only way to get a valid one without knowing each ship's connections);
/// - the option list of the vanilla "Custom start (creative)" (its &lt;custom&gt; items: what the player may change in the
///   game's own start editor), for the personalised start; story and highway items of the X4 galaxy are left out;
/// - its inventory and blueprints (repair laser, scanner, paint mods), given to every start.
/// </summary>
public sealed class StartCatalog
{
    /// <summary>A vanilla start that has a player ship. ShipXml = its &lt;ship&gt; element as written there.</summary>
    public sealed record Start(string Id, string Name, string Source, string ShipMacro, string ShipName, string ShipXml);

    public List<Start> Starts { get; } = new();
    /// <summary>The creative custom start's &lt;item&gt; elements (XML text), minus the X4-galaxy-only ones.</summary>
    public List<string> CustomItems { get; } = new();
    /// <summary>Values of CustomItems that were left out (they only make sense in the X4 galaxy).</summary>
    public static readonly string[] LeftOut = { "universestorystates", "universeringhighways", "universelinearhighways" };
    /// <summary>The creative start's &lt;inventory&gt; and &lt;blueprints&gt; (XML text).</summary>
    public string Extras { get; } = "";
    public List<string> Notes { get; } = new();

    public const string DefaultStart = "custom_creative";
    public const string CreativeId = "custom_creative";

    public Start? Get(string? id) => Starts.FirstOrDefault(s => s.Id == id);

    public StartCatalog(GameData g, TextDb? texts)
    {
        string Tx(string? r)
        {
            if (r == null) return "";
            return Regex.Replace(r, @"\{(\d+),\s*(\d+)\}", m => texts?.Get(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) ?? m.Value);
        }
        // which extension brings each start (DLC starts need that DLC)
        var source = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in g.Vfs.Files.OrderBy(f => f.Path.StartsWith("extensions/", StringComparison.OrdinalIgnoreCase) ? 1 : 0))
        {
            var m = Regex.Match(f.Path, @"^(?:extensions/([^/]+)/)?libraries/gamestarts\.xml$", RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            var src = m.Groups[1].Success ? m.Groups[1].Value : "base";
            foreach (Match x in Regex.Matches(System.Text.Encoding.UTF8.GetString(f.Read()), @"<gamestart\s+id=""([^""]+)"""))
                source.TryAdd(x.Groups[1].Value, src);
        }
        var root = GameXml.Load(g.Vfs, "libraries/gamestarts.xml", Notes)?.Root;
        foreach (var gs in root?.Elements("gamestart") ?? Enumerable.Empty<XElement>())
        {
            var id = (string?)gs.Attribute("id") ?? "";
            var tags = ((string?)gs.Attribute("tags") ?? "").Split(' ');
            if (tags.Contains("tutorial") || (string?)gs.Attribute("debug") == "true") continue;
            if (gs.Element("player")?.Element("ship") is not { } ship || (string?)ship.Attribute("macro") is not { } macro) continue;
            // Timelines scenarios are challenges (their ships may carry scenario-only gear); a ship without loadout is bare
            if (id.StartsWith("scenario_", StringComparison.OrdinalIgnoreCase) || tags.Contains("scenario") || ship.Element("loadout") == null) continue;
            var src = source.GetValueOrDefault(id, "base");
            if (src != "base" && !src.StartsWith("ego_", StringComparison.OrdinalIgnoreCase)) continue;   // Egosoft only: another author's start is not ours to copy
            var shipName = Tx((string?)g.Macro(macro)?.Element("properties")?.Element("identification")?.Attribute("name"));
            Starts.Add(new Start(id, Tx((string?)gs.Attribute("name")).Trim(), src, macro,
                shipName == "" ? macro : shipName, ship.ToString()));

            if (id == CreativeId)
            {
                foreach (var it in gs.Element("custom")?.Elements("item") ?? Enumerable.Empty<XElement>())
                    if (!LeftOut.Contains((string?)it.Attribute("value"))) CustomItems.Add(it.ToString());
                var p = gs.Element("player")!;
                Extras = string.Concat(new[] { p.Element("inventory"), p.Element("blueprints") }.OfType<XElement>().Select(e => e.ToString() + "\n"));
            }
        }
        Starts.Sort((a, b) => string.Compare(a.ShipName + a.Name, b.ShipName + b.Name, StringComparison.OrdinalIgnoreCase));
    }
}
