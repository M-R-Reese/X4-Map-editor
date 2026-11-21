using X4Data;
using X4Data.Space;
using X4Data.Text;

/// <summary>Milestone 4 checks: texts, region definitions, decoration catalogue.</summary>
static class DecorTests
{
    public static void Run(GameData g, Action<string, bool, string> check)
    {
        Console.WriteLine("\n--- Decoration (milestone 4)");
        var t = new TextDb(g, 44);
        check("texts read", t.Count > 50000 && t.Get(20001, 701) == "Jump Gate", $"{t.Count} texts, {{20001,701}} = '{t.Get(20001, 701)}'");
        check("text comment removed", TextDb.Clean("(do not translate)headquarters") == "headquarters" && TextDb.Clean(@"a \(b\) c") == "a (b) c", "");
        check("text references resolved", t.Lookup("{20001,701}") == "Jump Gate" && !(t.Get(1001, 4865) ?? "{").Contains('{'), t.Get(1001, 4865) ?? "");

        var cat = new DecorCatalog(g, t);
        var r = cat.Regions;
        check("region definitions", r.All.Count > 150, $"{r.All.Count} definitions, {r.All.Count(d => !d.Hidden)} offered");
        var f40 = r.Get("p1_40km_asteroid_field");
        check("asteroid field read", f40 != null && f40.Boundary == "cylinder" && f40.R == 25000 && f40.AsteroidWares.Contains("ore") && f40.AsteroidWares.Contains("nividium"), f40?.Label ?? "");
        var h = r.Get("p1_40km_hydrogen_field");
        check("gas cloud read", h?.Category == "Gas cloud" && h.Gases.Contains("hydrogen"), h?.Label ?? "");
        var tube = r.Get("region_cluster_01_sector_001_a");
        check("spline tube read", tube != null && tube.Spline.Count > 5 && Regions.SamplePath(tube).Count > 50, tube?.Label ?? "");
        foreach (var grp in cat.Items.GroupBy(i => i.Category))
            Console.WriteLine($"  {grp.Key,-16} {grp.Count(),4}  e.g. {string.Join(" | ", grp.Take(2).Select(i => i.Label))}");
        check("decoration catalogue", cat.Items.Count(i => i.Kind == "object") > 100 && cat.Categories.Count() >= 6, $"{cat.Items.Count} items");
        check("objects have names", cat.Items.Where(i => i.Kind == "object").All(i => i.Label != ""), "");

        // random resources + fields: every resource area has a field holding its ware, every field's wares have an area
        var fields = cat.Regions.All.Where(f => !f.Hidden && (f.Source == "base" || f.Source.StartsWith("ego_", StringComparison.OrdinalIgnoreCase)))
            .Select(f => (f.Name, (IReadOnlyList<string>)f.Wares.ToList(), (double)f.R)).ToList();
        var rp = new X4Data.Galaxy.Project(); var rs = X4Data.Galaxy.GalaxyOps.AddCluster(rp, 0, 0); X4Data.Galaxy.GalaxyOps.AddGate(rp, rs, 150_000, 0);
        var rng = new Random(3); int bad = 0, nFields = 0, nAreas = 0; var dropped = new HashSet<string>(); var issues = new List<string>();
        for (int i = 0; i < 100; i++)
        {
            rs.Decor.Clear();
            X4Data.Galaxy.GalaxyOps.RandomResources(rs, rng);
            var before = rs.Res.Select(r => r.Ware).ToHashSet();
            var placed = X4Data.Galaxy.GalaxyOps.AddFieldsForResources(rp, rs, fields, rng);
            dropped.UnionWith(before.Except(rs.Res.Select(r => r.Ware)));
            nFields += placed.Count; nAreas += rs.Res.Count;
            var v = X4Data.Galaxy.GalaxyOps.Validate(rp, r => cat.Regions.Get(r) is { } d ? d.Wares : Enumerable.Empty<string>()).Where(x => x.Contains("resource area")).ToList();
            if (v.Count > 0 || rs.Res.Count == 0) { bad++; issues.AddRange(v); }
            if (placed.Any(d => Math.Sqrt(d.X * d.X + d.Z * d.Z) < 29_000 || Math.Sqrt(d.X * d.X + d.Z * d.Z) > 151_000)) bad++;
        }
        check("random resources place fields: no field / area warning in 100 sectors", bad == 0,
            $"{nFields} fields for {nAreas} areas; dropped (no field holds them): {string.Join(", ", dropped)}; {string.Join(" | ", issues.Distinct().Take(3))}");
    }
}
