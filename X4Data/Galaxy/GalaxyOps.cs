namespace X4Data.Galaxy;

/// <summary>
/// Facts and operations of the galaxy model, ported from the HTML editor (same numbers, same rules).
/// </summary>
public static class GalaxyFacts
{
    public const double HexX = 15_000_000, HexZ = 8_660_000;   // galaxy.xml: cluster offset per hex column / half row (metres)
    public const double SlotM = 1_000_000;                     // offset of sectors inside a multi-sector cluster (metres)
    public const double SectorR = 250_000;                     // drawn sector radius, only a guide
    public const int Backdrops = 50;                           // vanilla cluster backdrops Cluster_01 .. Cluster_50
    /// <summary>Sector slot positions by sector count (units of SlotM; x, z).</summary>
    public static readonly int[][][] Slots =
    {
        Array.Empty<int[]>(),
        new[] { new[] { 0, 0 } },
        new[] { new[] { -1, 1 }, new[] { 1, -1 } },
        new[] { new[] { -1, 1 }, new[] { -1, -1 }, new[] { 1, 0 } },
    };
    public static readonly string[] Music =
    {
        "", "music_soundtrack_argon", "music_soundtrack_paranid", "music_soundtrack_teladi", "music_soundtrack_xenon",
        "music_soundtrack_periphery", "music_cluster_adventures_promise", "music_cluster_antigone_memorial", "music_cluster_circle_of_deceit",
        "music_cluster_complete", "music_cluster_frontier_edge", "music_cluster_getsu_fune", "music_cluster_ore_belt",
        "music_cluster_sanctum_verge", "music_cluster_tempting_fumes", "music_cluster_turquoise_sea", "music_cluster_void_of_opportunity",
    };
    /// <summary>faction id -> (race, map colour). Same table as the HTML editor.</summary>
    public static readonly Dictionary<string, (string Race, string Colour)> Factions = new()
    {
        ["argon"] = ("argon", "#3f8cff"), ["antigone"] = ("argon", "#7fb2ff"), ["hatikvah"] = ("argon", "#59d6d6"),
        ["paranid"] = ("paranid", "#b05cff"), ["holyorder"] = ("paranid", "#ff7bd0"), ["alliance"] = ("paranid", "#c9a0ff"),
        ["teladi"] = ("teladi", "#9ad13c"), ["ministry"] = ("teladi", "#c8e06a"), ["scaleplate"] = ("teladi", "#d6c23a"),
        ["xenon"] = ("xenon", "#ff4a4a"),
        ["split"] = ("split", "#ff8a3c"), ["freesplit"] = ("split", "#ffb07a"), ["court"] = ("split", "#d9a05a"),
        ["terran"] = ("terran", "#e6e6e6"), ["pioneers"] = ("terran", "#9fd8c8"),
        ["boron"] = ("boron", "#4fd0ff"), ["buccaneers"] = ("argon", "#b8b8b8"),
    };
    public static readonly string[] ResSize = { "tiny", "small", "medium", "large", "huge" };
    public static readonly string[] ResWare = { "ore", "silicon", "ice", "nividium", "hydrogen", "helium", "methane", "rawscrap" };
    public static readonly string[] ResYield = { "verylow", "low", "medium", "high", "veryhigh" };
    public static readonly string[] ResSpeed = { "veryslow", "slow", "average", "fast", "veryfast" };
}

public static class GalaxyOps
{
    public static IEnumerable<Sector> AllSectors(this Project p) => p.Clusters.SelectMany(c => c.Sectors);
    public static Cluster? ClusterOf(this Project p, int sid) => p.Clusters.FirstOrDefault(c => c.Sectors.Any(s => s.Id == sid));
    public static Sector? SectorById(this Project p, int? sid) => sid == null ? null : p.AllSectors().FirstOrDefault(s => s.Id == sid);
    public static Sector? SectorOfGate(this Project p, int gid) => p.AllSectors().FirstOrDefault(s => s.Gates.Any(g => g.Id == gid));
    public static Gate? GateById(this Project p, int? gid) => gid == null ? null : p.AllSectors().SelectMany(s => s.Gates).FirstOrDefault(g => g.Id == gid);
    public static Decoration? DecorById(this Project p, int? id) => id == null ? null : p.AllSectors().SelectMany(s => s.Decor).FirstOrDefault(d => d.Id == id);
    public static Sector? SectorOfDecor(this Project p, int id) => p.AllSectors().FirstOrDefault(s => s.Decor.Any(d => d.Id == id));
    public static Decoration AddDecor(this Project p, Sector s, string kind, string reference, double x, double y, double z)
    {
        var d = new Decoration { Id = p.NextId++, Kind = kind, Ref = reference, X = Math.Round(x), Y = Math.Round(y), Z = Math.Round(z) };
        s.Decor.Add(d);
        return d;
    }
    public static void UnlinkDecor(this Project p, Decoration d)
    {
        if (d.Link == null) return;
        var o = p.DecorById(d.Link);
        if (o != null) o.Link = null;
        d.Link = null;
    }
    public static void LinkDecor(this Project p, Decoration a, Decoration b) { p.UnlinkDecor(a); p.UnlinkDecor(b); a.Link = b.Id; b.Link = a.Id; }
    public static Cluster? ClusterAt(this Project p, int q, int r) => p.Clusters.FirstOrDefault(c => c.Q == q && c.R == r);
    static int NewId(Project p) => p.NextId++;

    public static Sector AddCluster(this Project p, int q, int r)
    {
        var c = new Cluster { Id = NewId(p), Name = "Cluster " + (p.Clusters.Count + 1), Q = q, R = r, Backdrop = 1 + p.Clusters.Count % GalaxyFacts.Backdrops };
        p.Clusters.Add(c);
        var s = p.AddSector(c);
        s.Name = c.Name;
        p.StartSector ??= s.Id;
        return s;
    }

    public static Sector AddSector(this Project p, Cluster c)
    {
        var s = new Sector { Id = NewId(p), Name = c.Name + " " + new string('I', c.Sectors.Count + 1) };
        c.Sectors.Add(s);
        return s;
    }

    public static void DeleteSector(this Project p, int sid)
    {
        var c = p.ClusterOf(sid); var s = p.SectorById(sid);
        if (c == null || s == null) return;
        foreach (var g in s.Gates) p.Unlink(g);
        foreach (var d in s.Decor) p.UnlinkDecor(d);
        c.Sectors.Remove(s);
        if (c.Sectors.Count == 0) p.Clusters.Remove(c);
        if (p.StartSector == sid) p.StartSector = p.AllSectors().FirstOrDefault()?.Id;
    }

    public static void Unlink(this Project p, Gate g)
    {
        if (g.Link == null) return;
        var o = p.GateById(g.Link);
        if (o != null) o.Link = null;
        g.Link = null;
    }

    public static void Link(this Project p, Gate a, Gate b) { p.Unlink(a); p.Unlink(b); a.Link = b.Id; b.Link = a.Id; }

    /// <summary>Yaw so that a gate at (x, z) looks at the sector centre.</summary>
    public static double FaceCentre(double x, double z) => Math.Round(Math.Atan2(-x, -z) * 180 / Math.PI);

    public static double NormYaw(double a) => Math.Round(((a + 180) % 360 + 360) % 360 - 180);

    public static Gate AddGate(this Project p, Sector s, double x, double z)
    {
        var g = new Gate { Id = NewId(p), X = Math.Round(x), Z = Math.Round(z), Yaw = FaceCentre(x, z) };
        s.Gates.Add(g);
        return g;
    }

    /// <summary>World position of a sector (metres, galaxy space) = cluster hex + slot offset.</summary>
    public static (double X, double Z) SectorWorld(this Project p, Sector s)
    {
        var c = p.ClusterOf(s.Id)!;
        var slot = GalaxyFacts.Slots[c.Sectors.Count][c.Sectors.IndexOf(s)];
        return (c.Q * GalaxyFacts.HexX + slot[0] * GalaxyFacts.SlotM, c.R * GalaxyFacts.HexZ + slot[1] * GalaxyFacts.SlotM);
    }

    /// <summary>Default position of a sector inside its cluster: the slot (0 for a one-sector cluster).</summary>
    public static (double X, double Y, double Z) SlotPlace(this Project p, Sector s)
    {
        var c = p.ClusterOf(s.Id)!;
        var slot = GalaxyFacts.Slots[c.Sectors.Count][c.Sectors.IndexOf(s)];
        return (slot[0] * GalaxyFacts.SlotM, 0, slot[1] * GalaxyFacts.SlotM);
    }

    /// <summary>Where the sector sits inside its cluster / backdrop (its own setting, else the slot).</summary>
    public static (double X, double Y, double Z) Place(this Project p, Sector s)
    {
        var d = p.SlotPlace(s);
        return (s.PlaceX ?? d.X, s.PlaceY ?? d.Y, s.PlaceZ ?? d.Z);
    }

    /// <summary>New gate pair: each gate 180 km out towards the other sector, looking at its sector centre.</summary>
    public static Gate ConnectSectors(this Project p, Sector a, Sector b)
    {
        var wa = p.SectorWorld(a); var wb = p.SectorWorld(b);
        double dx = wb.X - wa.X, dz = wb.Z - wa.Z, len = Math.Max(1e-9, Math.Sqrt(dx * dx + dz * dz));
        dx /= len; dz /= len;
        Gate Place(Sector s, double ux, double uz)
        {
            double ang = Math.Atan2(uz, ux), x, z; int tries = 0;
            do { x = Math.Cos(ang) * 180_000; z = Math.Sin(ang) * 180_000; ang += 0.35; tries++; }   // step aside if a gate is already there
            while (tries < 18 && s.Gates.Any(g => Math.Sqrt((g.X - x) * (g.X - x) + (g.Z - z) * (g.Z - z)) < 40_000));
            return p.AddGate(s, x, z);
        }
        var ga = Place(a, dx, dz); var gb = Place(b, -dx, -dz);
        p.Link(ga, gb);
        return ga;
    }

    /// <summary>Problems a player would hit (same checks as the HTML editor).</summary>
    public static List<string> Validate(this Project p)
    {
        var o = new List<string>(); var ss = p.AllSectors().ToList();
        if (ss.Count == 0) { o.Add("The galaxy is empty: double-click a hex."); return o; }
        if (!System.Text.RegularExpressions.Regex.IsMatch(p.Prefix, "^[a-z][a-z0-9]{1,7}$")) o.Add("Project settings: prefix must be 2 to 8 lower-case letters / digits.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(p.ModId, "^[a-z][a-z0-9_]+$")) o.Add("Project settings: mod folder must be lower-case letters, digits, underscore.");
        if (p.SectorById(p.StartSector) == null) o.Add("No start sector chosen.");
        static double D(double ax, double az, double bx, double bz) => Math.Sqrt((ax - bx) * (ax - bx) + (az - bz) * (az - bz));
        foreach (var s in ss)
        {
            if (string.IsNullOrWhiteSpace(s.Name)) o.Add("A sector has no name.");
            foreach (var g in s.Gates)
            {
                if (g.Link == null) o.Add($"{s.Name}: a gate is not connected.");
                if (D(g.X, g.Z, 0, 0) < 20_000) o.Add($"{s.Name}: a gate is closer than 20 km to the centre.");
            }
            for (int i = 0; i < s.Gates.Count; i++)
                for (int j = i + 1; j < s.Gates.Count; j++)
                    if (D(s.Gates[i].X, s.Gates[i].Z, s.Gates[j].X, s.Gates[j].Z) < 15_000) o.Add($"{s.Name}: two gates are closer than 15 km.");
            foreach (var st in s.Stations)
            {
                if (!GalaxyFacts.Factions.ContainsKey(st.Faction)) o.Add($"{s.Name}: unknown owner \"{st.Faction}\" (race for the station design is guessed as argon).");
                foreach (var g in s.Gates) if (D(st.X, st.Z, g.X, g.Z) < 10_000) o.Add($"{s.Name}: a station is closer than 10 km to a gate.");
            }
        }
        foreach (var n in ss.GroupBy(s => s.Name).Where(g => g.Count() > 1)) o.Add($"Sector name \"{n.Key}\" is used {n.Count()} times.");
        var start = p.SectorById(p.StartSector);
        if (start != null)
        {
            var seen = new HashSet<int> { start.Id }; var todo = new Stack<Sector>(); todo.Push(start);
            while (todo.Count > 0)
                foreach (var g in todo.Pop().Gates)
                    if (g.Link != null && p.SectorOfGate(g.Link.Value) is { } t && seen.Add(t.Id)) todo.Push(t);
            var cut = ss.Where(s => !seen.Contains(s.Id)).ToList();
            if (cut.Count > 0) o.Add($"{cut.Count} sector(s) cannot be reached from the start sector, e.g. {cut[0].Name}.");
        }
        return o;
    }
}
