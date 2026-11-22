using System.Text.Json;
using System.Text.Json.Serialization;

namespace X4Data.Galaxy;

/// <summary>
/// The galaxy project: the same JSON as the HTML editor (tools/galaxy_editor/galaxy_editor.html), "format" 1.
/// New fields (y heights) are optional, so both editors read each other's files; fields this code does not know
/// are kept (Extra) and written back unchanged.
/// Units: metres; sector positions relative to the sector centre; x = east, z = north, y = up (X4 axes).
/// </summary>
public sealed class Project
{
    public int Format { get; set; } = 1;
    public string ModId { get; set; } = "my_galaxy";
    public string ModName { get; set; } = "My Galaxy";
    public string Prefix { get; set; } = "mg";
    public int TextPage { get; set; } = 400900;
    /// <summary>Languages the export writes t/0001-lNNN.xml for (all hold the same texts). Empty = all game languages.</summary>
    public List<int> TextLanguages { get; set; } = new() { 44, 49 };
    /// <summary>PATCH project: the installed galaxy this project adds to (its macro, e.g. "XU_EP2_universe_macro"); null = own galaxy.</summary>
    public string? BaseGalaxy { get; set; }
    /// <summary>Folder of the patched galaxy under maps/ (e.g. "xu_ep2_universe") and the extension that defines it ("base" = the game).</summary>
    public string? BaseFolder { get; set; }
    public string? BaseSource { get; set; }
    /// <summary>PATCH: gates on the side of the patched galaxy that our gates lead to (new ones or reused vanilla ones).</summary>
    public List<BaseGate> BaseGates { get; set; } = new();
    /// <summary>
    /// Changes to the installed galaxy's OWN sectors and clusters (by macro name), docs/plan_galaxy_editor_modowner.md.
    /// One list for both ways out: the patch export writes them as diffs (anybody, for any mod), "Write into the mod"
    /// writes them into the mod's own unpacked files (its owner). Only what differs from the game files is kept.
    /// </summary>
    public List<BaseSectorChange> BaseSectors { get; set; } = new();
    public List<BaseClusterChange> BaseClusters { get; set; } = new();
    public BaseSectorChange? BaseSectorChangeOf(string macro) => BaseSectors.FirstOrDefault(c => c.Macro.Equals(macro, StringComparison.OrdinalIgnoreCase));
    public BaseClusterChange? BaseClusterChangeOf(string macro) => BaseClusters.FirstOrDefault(c => c.Macro.Equals(macro, StringComparison.OrdinalIgnoreCase));
    /// <summary>Anything changed in the installed galaxy itself?</summary>
    public bool HasBaseChanges => BaseSectors.Count > 0 || BaseClusters.Count > 0;
    public string Author { get; set; } = "M.R. Reese";
    public bool ReplaceVanillaStations { get; set; }
    public int NextId { get; set; } = 1;
    public int? StartSector { get; set; }
    /// <summary>The game start of an OWN galaxy (a patch uses the game's own starts).</summary>
    public GameStart Start { get; set; } = new();
    /// <summary>OWN galaxy: vanilla systems that also run here (GalaxySystems.Groups ids: factionai, missions, khaak).</summary>
    public List<string> Systems { get; set; } = new() { "factionai", "missions" };
    public List<Cluster> Clusters { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
    /// <summary>Read-only VIEW of a patched sector only (never saved): view gate id -> id of our BaseGate it shows (movable).</summary>
    [JsonIgnore] public Dictionary<int, int> ViewBaseGates { get; } = new();

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Reads a project file; throws InvalidDataException if it is not one.</summary>
    public static Project FromJson(string text)
    {
        Project? p;
        try { p = JsonSerializer.Deserialize<Project>(text, Json); }
        catch (JsonException e) { throw new InvalidDataException("This is not a galaxy project file (" + e.Message + ")."); }
        if (p == null || p.Format != 1) throw new InvalidDataException("This is not a galaxy project file.");
        return p;
    }

    public Project Clone() => FromJson(ToJson());
}

public sealed class Cluster
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>
    /// Spoken name: a text reference to the game's voiced name pool, e.g. "{20005,1004}" or "{20005,1004} {20402,2}"
    /// (X4Data.Text.VoicedNames). Name then holds the English text for the editor. Null = Name is written as typed (silent).
    /// </summary>
    public string? NameRef { get; set; }
    public string Desc { get; set; } = "";
    /// <summary>Hex column / row (flat-top hexes, rows of a column share its parity).</summary>
    public int Q { get; set; }
    public int R { get; set; }
    /// <summary>Vanilla cluster backdrop number 1 .. 50 (planets, sun, nebula of that cluster).</summary>
    public int Backdrop { get; set; } = 1;
    /// <summary>
    /// Backdrop component by name (new in the 3D editor; DLC backdrops have free names). Null = "Cluster_" + Backdrop
    /// (two digits). When the component is a "Cluster_NN", Backdrop holds NN too, so the HTML editor exports the same.
    /// </summary>
    public string? BackdropRef { get; set; }
    /// <summary>The backdrop component this cluster uses.</summary>
    public string BackdropComponent() => BackdropRef ?? $"Cluster_{Backdrop:00}";
    public string Music { get; set; } = "";
    public List<Sector> Sectors { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class Sector
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>
    /// Spoken name: a text reference to the game's voiced name pool, e.g. "{20005,1004}" or "{20005,1004} {20402,2}"
    /// (X4Data.Text.VoicedNames). Name then holds the English text for the editor. Null = Name is written as typed (silent).
    /// </summary>
    public string? NameRef { get; set; }
    public string Desc { get; set; } = "";
    public double Sunlight { get; set; } = 1.0;
    public double Economy { get; set; } = 0.5;
    public double Security { get; set; } = 0.5;
    /// <summary>
    /// Position of the sector inside its cluster, i.e. inside the backdrop (metres; null = the usual slot position).
    /// Egosoft uses this too: 27 vanilla sectors sit up to 290,000 km from their cluster centre.
    /// </summary>
    public double? PlaceX { get; set; }
    public double? PlaceY { get; set; }
    public double? PlaceZ { get; set; }
    public List<ResourceArea> Res { get; set; } = new();
    public List<Gate> Gates { get; set; } = new();
    public List<Station> Stations { get; set; } = new();
    /// <summary>Decoration placed in the 3D editor (fields and objects). The HTML editor keeps but ignores it.</summary>
    public List<Decoration> Decor { get; set; } = new();
    /// <summary>Faction AI of the sector (owner at start, pirates, what the AI may do there). Null = all default.</summary>
    public SectorAi? Ai { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
/// What the faction AI does in a sector (docs/plan_galaxy_editor_ai.md step 1). The game has no "owner" field: a sector
/// belongs to the faction with a CLAIMING station there, so Owner exports one claim station (defence platform) of that
/// faction; then vanilla rules bring its factories, ships and expansion. Pirates: a pirate station (base, dock, depot,
/// anarchy port) does NOT claim: the sector keeps its owner, or stays free (a pirate haven).
/// </summary>
public sealed class SectorAi
{
    /// <summary>Faction id that owns the sector at game start, or null (ownerless).</summary>
    public string? Owner { get; set; }
    /// <summary>stations.xml id of a pirate station placed here (e.g. "pirate_freeport"), or null.</summary>
    public string? PirateStation { get; set; }
    /// <summary>Faction of that pirate station (a stations.xml entry may serve several factions).</summary>
    public string? PirateFaction { get; set; }
    /// <summary>false = mapdefaults factionlogic="false": no faction claims or invades it while it has no owner.</summary>
    public bool FactionLogic { get; set; } = true;
    /// <summary>Factions whose AI ignores this sector (vanilla manager signal 'add_excluded_sector').</summary>
    public List<string> Excluded { get; set; } = new();
    /// <summary>mapdefaults area tags (anarchy, allowrandomanomaly, ...).</summary>
    public List<string> Tags { get; set; } = new();
    /// <summary>Place of the owner's claim station, moved by hand in 3D; null = automatic (away from gates and objects).</summary>
    public Spot? OwnerAt { get; set; }
    /// <summary>Place of the pirate station, moved by hand in 3D; null = automatic.</summary>
    public Spot? PirateAt { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore] public bool IsDefault => Owner == null && PirateStation == null && FactionLogic && Excluded.Count == 0 && Tags.Count == 0;
}

/// <summary>
/// Game start of an own galaxy, with the main options of the game's own personalised (custom) start. The ship is copied
/// with its loadout from one of the game's starts (StartCatalog). Personalised = a second start that opens the game's
/// own start editor in this galaxy, with these values as its defaults.
/// </summary>
public sealed class GameStart
{
    /// <summary>Name in the game's start menu; null = the mod name.</summary>
    public string? Name { get; set; }
    /// <summary>Description in the start menu; null = "Start in (sector), in the galaxy of (mod)".</summary>
    public string? Desc { get; set; }
    /// <summary>Player's name; null = the game's default name.</summary>
    public string? PlayerName { get; set; }
    /// <summary>Character macro of the player (look); _f_ / _m_ in the name = female / male.</summary>
    public string Look { get; set; } = "character_player_custom_f_asi_macro";
    public long Money { get; set; } = 100_000;
    /// <summary>Id of the game start whose player ship (with loadout) is copied.</summary>
    public string ShipFrom { get; set; } = "custom_creative";
    /// <summary>Known at start: "start" (start sector), "cluster" (its cluster), "all" (every sector of the galaxy).</summary>
    public string Known { get; set; } = "all";
    /// <summary>The game's common start research (encyclopedia basket research_gamestart_common).</summary>
    public bool Research { get; set; } = true;
    /// <summary>Also a personalised start: the game's own start editor in this galaxy, with limits (relations, story, own blueprints).</summary>
    public bool Personalised { get; set; } = true;
    /// <summary>Also a creative start: the start editor without limits, like the game's "Custom start (creative)".</summary>
    public bool Creative { get; set; } = true;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
/// One decoration: a field (Kind "region" = a region_definitions entry, placed by the cluster like vanilla) or an
/// object (Kind "object" = a macro, placed in a zone). Position relative to the sector centre, metres, X4 axes.
/// </summary>
public sealed class Decoration
{
    public int Id { get; set; }
    public string Kind { get; set; } = "region";
    public string Ref { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    /// <summary>Turn about the vertical axis (degrees, 0 = as defined, positive = clockwise seen from above).</summary>
    public double Yaw { get; set; }
    /// <summary>Tilt (degrees) about the east axis, then roll about the north axis: fields can lie at an angle to the ecliptic.</summary>
    public double Pitch { get; set; }
    public double Roll { get; set; }
    /// <summary>Anomalies (wormholes) only: id of the partner anomaly, or null. Linked both ways like gates.</summary>
    public int? Link { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>A changed sector of the installed galaxy: null = as in the game files.</summary>
public sealed class BaseSectorChange
{
    public string Macro { get; set; } = "";
    public double? Sunlight { get; set; }
    public double? Economy { get; set; }
    public double? Security { get; set; }
    /// <summary>The whole new list of resource areas (null = unchanged; empty = none).</summary>
    public List<ResourceArea>? Res { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool Empty => Sunlight == null && Economy == null && Security == null && Res == null;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>A moved cluster of the installed galaxy: its new hex.</summary>
public sealed class BaseClusterChange
{
    public string Macro { get; set; } = "";
    public int Q { get; set; }
    public int R { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class ResourceArea
{
    public int Amount { get; set; } = 2;
    public string Size { get; set; } = "medium";
    public string Ware { get; set; } = "ore";
    public string Yield { get; set; } = "medium";
    public string Speed { get; set; } = "average";
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
/// PATCH: a gate in a sector of the patched galaxy (e.g. a vanilla sector), partner of one of our gates.
/// Existing = path "cluster conn/sector conn/zone conn/gate conn" of an unused vanilla gate that is reused;
/// null = a new gate the patch adds to that sector (own zone, added by a diff of the sector).
/// </summary>
public sealed class BaseGate
{
    public int Id { get; set; }
    public string SectorMacro { get; set; } = "";
    public string SectorName { get; set; } = "";
    public string? Existing { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double Yaw { get; set; }
    public string Type { get; set; } = "gate";
    /// <summary>Id of our gate it leads to.</summary>
    public int? Link { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class Gate
{
    public int Id { get; set; }
    public double X { get; set; }
    /// <summary>Height above the ecliptic (new in the 3D editor; the HTML editor ignores it).</summary>
    public double Y { get; set; }
    public double Z { get; set; }
    /// <summary>Facing in degrees: 0 = north (+z), 90 = east (+x).</summary>
    public double Yaw { get; set; }
    /// <summary>"gate" or "accelerator".</summary>
    public string Type { get; set; } = "gate";
    /// <summary>Id of the partner gate, or null.</summary>
    public int? Link { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>A place and facing in a sector (metres, degrees; yaw 0 = north).</summary>
public sealed class Spot
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double Yaw { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
/// A station placed in a sector (3D view): exported to god.xml at this place. Kind = GalaxyOps.StationKinds; the game picks
/// the faction's station of that kind (as vanilla), a factory is built for Ware. Pirate stations name their stations.xml id (Ref).
/// </summary>
public sealed class Station
{
    public int Id { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    /// <summary>Facing in degrees (0 = north), new in the 3D editor.</summary>
    public double Yaw { get; set; }
    public string Kind { get; set; } = "tradestation";
    public string Faction { get; set; } = "argon";
    public string Ware { get; set; } = "energycells";
    /// <summary>Pirate stations: the stations.xml id (e.g. "pirate_freeport"); null for the others.</summary>
    public string? Ref { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
