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
    public string Author { get; set; } = "M.R. Reese";
    public bool ReplaceVanillaStations { get; set; }
    public int NextId { get; set; } = 1;
    public int? StartSector { get; set; }
    public List<Cluster> Clusters { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

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

public sealed class ResourceArea
{
    public int Amount { get; set; } = 2;
    public string Size { get; set; } = "medium";
    public string Ware { get; set; } = "ore";
    public string Yield { get; set; } = "medium";
    public string Speed { get; set; } = "average";
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

/// <summary>Stations are kept for the later population editor; the galaxy / sector editors only carry them along.</summary>
public sealed class Station
{
    public int Id { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public string Kind { get; set; } = "tradestation";
    public string Faction { get; set; } = "argon";
    public string Ware { get; set; } = "energycells";
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
