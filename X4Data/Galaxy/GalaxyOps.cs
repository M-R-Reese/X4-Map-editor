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
        ["boron"] = ("boron", "#4fd0ff"), ["buccaneers"] = ("paranid", "#b8b8b8"),
        ["fallensplit"] = ("split", "#a0522d"), ["yaki"] = ("argon", "#d94f9a"), ["loanshark"] = ("argon", "#8f6bd6"),
        ["scavenger"] = ("argon", "#8a9a5b"), ["kaori"] = ("argon", "#f0d37a"),
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
        p.BaseGates.RemoveAll(b => b.Id == g.Link);                   // a vanilla-side gate exists only for its link
        g.Link = null;
    }

    /// <summary>Name of the sector a gate leads to: one of ours, or (patch) a sector of the patched galaxy; null = not connected.</summary>
    public static string? LinkTarget(this Project p, Gate g) =>
        g.Link == null ? null
        : p.BaseGates.FirstOrDefault(b => b.Id == g.Link && b.Link == g.Id) is { } bg ? bg.SectorName
        : p.SectorOfGate(g.Link.Value)?.Name;

    public static BaseGate? BaseGateById(this Project p, int? id) => id == null ? null : p.BaseGates.FirstOrDefault(b => b.Id == id);

    /// <summary>
    /// PATCH: connects one of our sectors with a sector of the patched galaxy. Our new gate faces the base cluster;
    /// on the base side either an unused vanilla gate is reused (reuse != null) or a new gate is placed 180 km from the
    /// centre towards our sector, away from the gates already there. Returns our gate.
    /// </summary>
    public static Gate ConnectToBase(this Project p, Sector own, BaseGalaxy.ClusterInfo bc, BaseGalaxy.SectorInfo bs, BaseGalaxy.GateInfo? reuse)
    {
        var wa = p.SectorWorld(own);
        double dx = bc.Q * GalaxyFacts.HexX - wa.X, dz = bc.R * GalaxyFacts.HexZ - wa.Z, len = Math.Max(1e-9, Math.Sqrt(dx * dx + dz * dz));
        dx /= len; dz /= len;
        var taken = bs.Gates.Select(g => (g.X, g.Z)).Concat(p.BaseGates.Where(b => b.SectorMacro == bs.Macro).Select(b => (b.X, b.Z))).ToList();
        (double x, double z) Free(double ux, double uz, List<(double X, double Z)> others)
        {
            double ang = Math.Atan2(uz, ux), x, z; int tries = 0;
            do { x = Math.Cos(ang) * 180_000; z = Math.Sin(ang) * 180_000; ang += 0.35; tries++; }
            while (tries < 18 && others.Any(o => Math.Sqrt((o.X - x) * (o.X - x) + (o.Z - z) * (o.Z - z)) < 40_000));
            return (x, z);
        }
        var mine = Free(dx, dz, own.Gates.Select(g => (g.X, g.Z)).ToList());
        var g = p.AddGate(own, mine.x, mine.z);
        var bg = new BaseGate { Id = NewId(p), SectorMacro = bs.Macro, SectorName = bs.Name, Link = g.Id };
        if (reuse != null) { bg.Existing = reuse.Path; bg.X = reuse.X; bg.Y = reuse.Y; bg.Z = reuse.Z; bg.Yaw = reuse.Yaw; bg.Type = reuse.Macro.Contains("accelerator") ? "accelerator" : "gate"; }
        else { var b = Free(-dx, -dz, taken); bg.X = Math.Round(b.x); bg.Z = Math.Round(b.z); bg.Yaw = FaceCentre(b.x, b.z); }
        p.BaseGates.Add(bg);
        g.Link = bg.Id;
        return g;
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
    /// <summary>
    /// Connects two of our sectors. useA / useB = an unconnected gate already there (null = a new gate, placed towards
    /// the other sector, 180 km out, aside from the gates already there). Returns a's gate.
    /// </summary>
    public static Gate ConnectSectors(this Project p, Sector a, Sector b, Gate? useA = null, Gate? useB = null)
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
        var ga = useA ?? Place(a, dx, dz); var gb = useB ?? Place(b, -dx, -dz);
        p.Link(ga, gb);
        return ga;
    }

    /// <summary>Problems a player would hit (same checks as the HTML editor).</summary>
    /// <summary>
    /// Resource areas for the wares of a field that the sector does not have yet (9.00: a field only gives yield where a
    /// resource area of its ware is set in the sector's mapdefaults). Size from the field's radius
    /// (regionyields.xml spheres: tiny 20, small 30, medium 50, large 100, huge 200 km), medium yield, average refill.
    /// Returns the wares added.
    /// </summary>
    public static List<string> AddResourcesFor(this Sector s, IEnumerable<string> wares, double fieldRadius)
    {
        var size = fieldRadius <= 20_000 ? "tiny" : fieldRadius <= 30_000 ? "small" : fieldRadius <= 50_000 ? "medium" : fieldRadius <= 100_000 ? "large" : "huge";
        var added = new List<string>();
        foreach (var w in wares.Distinct())
        {
            if (!GalaxyFacts.ResWare.Contains(w) || s.Res.Any(r => r.Ware == w)) continue;
            s.Res.Add(new ResourceArea { Ware = w, Size = size, Amount = 1 });
            added.Add(w);
        }
        return added;
    }

    /// <summary>
    /// Chance of each ware in a random sector, and the sector's random "richness" (0.5 .. 1.4) multiplies all of them:
    /// some sectors come out poor, some rich. Ore is the most common, nividium and scrap are rare (roughly as in vanilla).
    /// </summary>
    static readonly (string Ware, double Chance)[] RandomWares =
    {
        ("ore", 0.8), ("silicon", 0.6), ("ice", 0.45), ("hydrogen", 0.5), ("helium", 0.35), ("methane", 0.35), ("nividium", 0.08), ("rawscrap", 0.1),
    };

    /// <summary>Replaces the sector's resource areas by random ones (at least one ware). Weighted lists: medium is the most likely.</summary>
    public static void RandomResources(this Sector s, Random rng)
    {
        static string Pick(Random r, params string[] list) => list[r.Next(list.Length)];
        s.Res.Clear();
        double richness = 0.5 + rng.NextDouble() * 0.9;
        foreach (var (ware, chance) in RandomWares)
            if (rng.NextDouble() < chance * richness)
                s.Res.Add(new ResourceArea
                {
                    Ware = ware,
                    Amount = 1 + rng.Next(ware is "nividium" or "rawscrap" ? 2 : 4),
                    Size = Pick(rng, "small", "medium", "medium", "large"),
                    Yield = Pick(rng, "low", "medium", "medium", "high", ware == "nividium" ? "low" : "veryhigh"),
                    Speed = Pick(rng, "slow", "average", "average", "average", "fast"),
                });
        if (s.Res.Count == 0) s.Res.Add(new ResourceArea { Ware = "ore", Amount = 1 + rng.Next(3) });
    }

    /// <summary>
    /// Random generation, second half: every resource area needs a field holding its ware (9.00: the area says how much,
    /// the field where). For each ware without a field one is placed: a field (region definition) that holds that ware,
    /// preferring one that covers several missing wares and brings no other minable ware, 15 .. 60 km in radius.
    /// Placed at a random spot 30 .. 150 km from the centre, away from gates and other placed things, flat, random turn.
    /// Wares no field holds lose their resource area. Returns the placed fields; their other wares get areas too.
    /// fields: candidates (region name, minable wares, radius in metres), Egosoft ones only.
    /// </summary>
    public static List<Decoration> AddFieldsForResources(this Project p, Sector s, IReadOnlyList<(string Ref, IReadOnlyList<string> Wares, double R)> fields, Random rng)
    {
        var placed = new List<Decoration>();
        var radius = new Dictionary<int, double>();
        double RadiusOf(Decoration d) => radius.TryGetValue(d.Id, out var r) ? r : fields.FirstOrDefault(f => f.Ref == d.Ref).R;
        HashSet<string> Missing()
        {
            var have = s.Decor.Where(d => d.Kind == "region").SelectMany(d => fields.FirstOrDefault(f => f.Ref == d.Ref).Wares ?? Array.Empty<string>()).ToHashSet();
            return s.Res.Select(r => r.Ware).Where(w => !have.Contains(w)).ToHashSet();
        }
        for (var missing = Missing(); missing.Count > 0; missing = Missing())
        {
            var wanted = s.Res.Select(r => r.Ware).ToHashSet();
            var cands = fields.Where(f => f.Wares.Any(missing.Contains)).ToList();
            if (cands.Count == 0) { s.Res.RemoveAll(r => missing.Contains(r.Ware)); break; }    // no field holds it: nothing to mine, area goes
            // best: covers most missing wares, brings fewest unwanted ones, normal size
            int Score(IReadOnlyList<string> w) => w.Count(missing.Contains) * 10 - w.Count(x => GalaxyFacts.ResWare.Contains(x) && !wanted.Contains(x)) * 6;
            var normal = cands.Where(f => f.R >= 15_000 && f.R <= 60_000).ToList();
            if (normal.Count > 0) cands = normal;
            int best = cands.Max(f => Score(f.Wares));
            var pick = cands.Where(f => Score(f.Wares) == best).ToList()[rng.Next(cands.Count(f => Score(f.Wares) == best))];
            // spot: 40 tries, keep the one furthest from gates and the other placed things (relative to their sizes)
            (double X, double Z) spot = (0, 0); double bestGap = double.MinValue;
            for (int i = 0; i < 40; i++)
            {
                double a = rng.NextDouble() * Math.PI * 2, dist = 30_000 + rng.NextDouble() * 120_000;
                double x = dist * Math.Sin(a), z = dist * Math.Cos(a);
                double gap = s.Gates.Select(g => Math.Sqrt((g.X - x) * (g.X - x) + (g.Z - z) * (g.Z - z)) - pick.R - 20_000)
                    .Concat(s.Decor.Select(d => Math.Sqrt((d.X - x) * (d.X - x) + (d.Z - z) * (d.Z - z)) - pick.R - (d.Kind == "region" ? RadiusOf(d) : 5_000)))
                    .DefaultIfEmpty(1e9).Min();
                if (gap > bestGap) { bestGap = gap; spot = (x, z); }
            }
            var dec = p.AddDecor(s, "region", pick.Ref, spot.X, 0, spot.Z);
            dec.Yaw = rng.Next(72) * 5 - 180;
            radius[dec.Id] = pick.R;
            placed.Add(dec);
            s.AddResourcesFor(pick.Wares, pick.R);       // its other minable wares need an area as well
        }
        return placed;
    }

    /// <summary>Empties a sector: gates (their partners lose the link), fields, objects, stations, resource areas, AI. Name and settings stay.</summary>
    public static void ClearSector(this Project p, Sector s)
    {
        foreach (var g in s.Gates) p.Unlink(g);
        foreach (var d in s.Decor) p.UnlinkDecor(d);
        s.Gates.Clear(); s.Decor.Clear(); s.Stations.Clear(); s.Res.Clear();
        s.Ai = null;
    }

    /// <summary>Removes every cluster and sector (and the patched galaxy's gates that only existed for our links). Ids keep counting.</summary>
    public static void ClearMap(this Project p)
    {
        p.Clusters.Clear();
        p.BaseGates.Clear();
        p.StartSector = null;
    }

    /// <summary>Checks before export. fieldWares: the minable wares of a field (region definition), if the game data is read.</summary>
    /// <summary>
    /// Places of the sector AI's stations on the ecliptic (until they can be placed by hand, plan step 2): owner's claim
    /// station on a 40 km ring, pirate station on an 80 km ring, each at the angle (15° steps) furthest from gates,
    /// placed objects and the other station. Same project = same places (export after export).
    /// </summary>
    /// <summary>Station kinds placeable in a sector: id, label, the station tag the game selects by (null = factory, by ware).</summary>
    public static readonly (string Kind, string Label, string? Tag)[] StationKinds =
    {
        ("tradestation", "Trade station", "tradestation"), ("shipyard", "Shipyard", "shipyard"), ("wharf", "Wharf", "wharf"),
        ("equipmentdock", "Equipment dock", "equipmentdock"), ("defence", "Defence platform", "defence"), ("factory", "Factory", null),
        ("piratebase", "Pirate station", "piratebase"),
    };
    public static string StationLabel(string kind) => StationKinds.FirstOrDefault(k => k.Kind == kind).Label ?? kind;

    public static Station AddStation(this Project p, Sector s, string kind, string faction, double x, double z)
    {
        var st = new Station { Id = p.NextId++, Kind = kind, Faction = faction, X = Math.Round(x), Z = Math.Round(z) };
        s.Stations.Add(st);
        return st;
    }
    public static Station? StationById(this Project p, int? id) => id == null ? null : p.AllSectors().SelectMany(s => s.Stations).FirstOrDefault(x => x.Id == id);
    public static Sector? SectorOfStation(this Project p, int id) => p.AllSectors().FirstOrDefault(s => s.Stations.Any(x => x.Id == id));

    /// <summary>Where the sector AI's stations stand: moved by hand (OwnerAt / PirateAt) or the automatic places (AiSpots).</summary>
    public static (Spot Owner, Spot Pirate) AiPlaces(Sector s)
    {
        var auto = AiSpots(s);
        return (s.Ai?.OwnerAt ?? new Spot { X = auto.Owner.X, Z = auto.Owner.Z }, s.Ai?.PirateAt ?? new Spot { X = auto.Pirate.X, Z = auto.Pirate.Z });
    }

    public static ((double X, double Z) Owner, (double X, double Z) Pirate) AiSpots(Sector s)
    {
        var avoid = s.Gates.Select(g => (g.X, g.Z)).Concat(s.Decor.Where(d => d.Kind == "object").Select(d => (d.X, d.Z)))
            .Concat(s.Stations.Select(st => (st.X, st.Z))).ToList();
        (double X, double Z) Best(double ring)
        {
            (double X, double Z) best = (0, ring); double bestD = -1;
            for (int i = 0; i < 24; i++)
            {
                double a = i * Math.PI / 12, x = Math.Round(ring * Math.Sin(a)) + 0.0, z = Math.Round(ring * Math.Cos(a)) + 0.0;   // + 0.0: no "-0" in the files
                double d = avoid.Count == 0 ? 0 : avoid.Min(o => Math.Sqrt((o.Item1 - x) * (o.Item1 - x) + (o.Item2 - z) * (o.Item2 - z)));
                if (d > bestD + 1) { bestD = d; best = (x, z); }
            }
            return best;
        }
        var owner = Best(40_000);
        avoid.Add(owner);
        return (owner, Best(80_000));
    }

    /// <summary>Checks of the sector AI against the installed game (factions); null catalog = game not read, only the plain checks.</summary>
    public static List<string> ValidateAi(this Project p, FactionCatalog? fc)
    {
        var o = new List<string>();
        bool any = false;
        foreach (var s in p.AllSectors())
        {
            if (s.Ai is not { } ai) continue;
            any |= ai.Owner != null || ai.PirateStation != null || ai.Excluded.Count > 0;
            if (ai.Owner != null && ai.Excluded.Contains(ai.Owner)) o.Add($"{s.Name}: owner {fc?.Get(ai.Owner)?.Name ?? ai.Owner} is also on the \"ignored by\" list: its AI would neglect its own sector.");
            if (ai.Owner != null && s.Stations.Any(st => st.Faction != ai.Owner && st.Kind is "defence" or "shipyard" or "wharf" or "equipmentdock" or "tradestation"))
                o.Add($"{s.Name}: stations of another faction claim it too: the sector would be contested.");
            if (fc == null) continue;
            if (ai.Owner != null && !fc.Owners.Any(f => f.Id == ai.Owner)) o.Add($"{s.Name}: owner \"{ai.Owner}\" cannot own sectors in the installed game (faction or DLC missing?).");
            if (ai.PirateStation != null && !fc.PirateStations.Any(t => t.Id == ai.PirateStation && t.Faction == ai.PirateFaction))
                o.Add($"{s.Name}: pirate station \"{ai.PirateStation}\" ({ai.PirateFaction}) is not in the installed game.");
            foreach (var t in ai.Tags.Where(t => !fc.MapTags.ContainsKey(t))) o.Add($"{s.Name}: the game does not use the sector tag \"{t}\".");
        }
        // stations placed by hand: can the game build them? (the owner needs a station of that kind / a factory of that ware)
        foreach (var s in p.AllSectors())
        {
            var claimers = s.Stations.Where(st => st.Kind is not ("factory" or "piratebase")).Select(st => st.Faction).Distinct().ToList();
            if (s.Ai?.Owner == null && claimers.Count > 1) o.Add($"{s.Name}: stations of {claimers.Count} factions claim it: the sector would be contested.");
            any |= s.Stations.Count > 0;
            if (fc == null) continue;
            foreach (var st in s.Stations)
            {
                var tag = StationKinds.FirstOrDefault(k => k.Kind == st.Kind).Tag;
                string who = fc.Get(st.Faction)?.Name ?? st.Faction;
                if (st.Kind == "piratebase")
                {
                    if (!fc.PirateStations.Any(t => t.Id == st.Ref && t.Faction == st.Faction)) o.Add($"{s.Name}: pirate station \"{st.Ref}\" ({who}) is not in the installed game.");
                }
                else if (st.Kind == "factory")
                {
                    if (!(fc.ProductWares.GetValueOrDefault(st.Faction)?.Contains(st.Ware) ?? false)) o.Add($"{s.Name}: {who} has no factory for {fc.WareNames.GetValueOrDefault(st.Ware, st.Ware)} in the game: nothing would be built.");
                }
                else if (tag != null && !fc.Stations.Any(t => t.Tag == tag && t.Faction.Equals(st.Faction, StringComparison.OrdinalIgnoreCase)))
                    o.Add($"{s.Name}: {who} has no {StationLabel(st.Kind).ToLowerInvariant()} in the game: nothing would be built.");
            }
        }
        // finding B of the plan: the vanilla faction AI only runs in the X4 galaxy (xu_ep2_universe) for now
        if (any && p.BaseGalaxy == null)
            o.Add("Own galaxy: the game's faction AI only runs in the X4 galaxy so far: owners and pirate stations are placed, but no faction builds or expands (comes in a later step).");
        return o;
    }

    public static List<string> Validate(this Project p, Func<string, IEnumerable<string>>? fieldWares = null)
    {
        var o = new List<string>(); var ss = p.AllSectors().ToList();
        if (ss.Count == 0) { o.Add("The galaxy is empty: double-click a hex."); return o; }
        if (!System.Text.RegularExpressions.Regex.IsMatch(p.Prefix, "^[a-z][a-z0-9]{1,7}$")) o.Add("Project settings: prefix must be 2 to 8 lower-case letters / digits.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(p.ModId, "^[a-z][a-z0-9_]+$")) o.Add("Project settings: mod folder must be lower-case letters, digits, underscore.");
        // a patch uses the game's own starts; its sectors are reached from the galaxy it is added to (gates: next step)
        if (p.BaseGalaxy == null && p.SectorById(p.StartSector) == null) o.Add("No start sector chosen.");
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
        // 9.00: yield needs BOTH a field with the ware (where) and a resource area of the ware (how much)
        if (fieldWares != null)
            foreach (var s in ss)
            {
                var inFields = s.Decor.Where(d => d.Kind == "region").SelectMany(d => fieldWares(d.Ref)).Where(GalaxyFacts.ResWare.Contains).ToHashSet();
                var inAreas = s.Res.Select(r => r.Ware).ToHashSet();
                foreach (var w in inFields.Except(inAreas)) o.Add($"{s.Name}: a field holds {w}, but there is no {w} resource area: it gives no {w}.");
                foreach (var w in inAreas.Except(inFields)) o.Add($"{s.Name}: {w} resource area without a field holding {w}: nothing to mine.");
            }
        // PATCH: every own sector must be reachable from the galaxy it is added to
        if (p.BaseGalaxy != null)
        {
            var seen = new HashSet<int>(); var todo = new Stack<Sector>();
            foreach (var s in ss.Where(s => s.Gates.Any(g => p.BaseGateById(g.Link) != null))) if (seen.Add(s.Id)) todo.Push(s);
            while (todo.Count > 0)
                foreach (var g in todo.Pop().Gates)
                    if (g.Link != null && p.SectorOfGate(g.Link.Value) is { } t && seen.Add(t.Id)) todo.Push(t);
            var cut = ss.Where(s => !seen.Contains(s.Id)).ToList();
            if (seen.Count == 0) o.Add("No sector is connected to the galaxy you patch: in \"Connect sectors\" mode, drag from your sector onto one of its sectors.");
            else if (cut.Count > 0) o.Add($"{cut.Count} sector(s) cannot be reached from the galaxy you patch, e.g. {cut[0].Name}.");
        }
        foreach (var n in ss.GroupBy(s => s.Name).Where(g => g.Count() > 1)) o.Add($"Sector name \"{n.Key}\" is used {n.Count()} times.");
        var start = p.SectorById(p.StartSector);
        if (start != null && p.BaseGalaxy == null)
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
