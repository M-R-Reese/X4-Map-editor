using X4Data.Galaxy;

/// <summary>Milestone 3 checks: galaxy model, operations, checks, file round trip (also with unknown fields).</summary>
static class GalaxyTests
{
    public static void Run(Action<string, bool, string> check)
    {
        Console.WriteLine("\n--- Galaxy model (milestone 3)");
        var p = new Project();
        var a = p.AddCluster(0, 0); var b = p.AddCluster(2, 0); var c = p.AddCluster(1, 1);
        var b2 = p.AddSector(p.ClusterOf(b.Id)!);
        check("clusters and sectors added", p.Clusters.Count == 3 && p.AllSectors().Count() == 4 && p.StartSector == a.Id, $"{p.AllSectors().Count()} sectors");
        p.ConnectSectors(a, b); p.ConnectSectors(b, c);
        var issues = p.Validate();
        check("one sector unreachable is reported", issues.Count == 1 && issues[0].Contains("cannot be reached"), string.Join(" | ", issues));
        p.ConnectSectors(b, b2);
        check("connected galaxy has no problems", p.Validate().Count == 0, string.Join(" | ", p.Validate()));
        // 9.00 yields: field (where) + resource area (how much)
        var rs = p.AllSectors().First();
        var fieldWares = new Dictionary<string, string[]> { ["f_ore"] = new[] { "ore", "silicon", "rawkhaakscrap" } };
        IEnumerable<string> Wares(string r) => fieldWares.TryGetValue(r, out var w) ? w : Array.Empty<string>();
        p.AddDecor(rs, "region", "f_ore", 0, 0, 0);
        check("field without resource area is reported", p.Validate(Wares).Any(i => i.Contains("no ore resource area")), string.Join(" | ", p.Validate(Wares)));
        var added = rs.AddResourcesFor(Wares("f_ore"), 25_000);
        check("placing a field adds its resource areas (known wares only, size from radius)",
            added.SequenceEqual(new[] { "ore", "silicon" }) && rs.Res.All(r => r.Size == "small") && rs.AddResourcesFor(Wares("f_ore"), 25_000).Count == 0,
            string.Join(", ", rs.Res.Select(r => $"{r.Ware} {r.Size}")));
        check("field + resource areas: no yield warning", !p.Validate(Wares).Any(i => i.Contains("resource area")), string.Join(" | ", p.Validate(Wares)));
        rs.Res.Add(new ResourceArea { Ware = "helium" });
        check("resource area without a field is reported", p.Validate(Wares).Any(i => i.Contains("helium resource area without a field")), "");

        var g = a.Gates[0];
        check("gate 180 km out, facing the centre", Math.Abs(Math.Sqrt(g.X * g.X + g.Z * g.Z) - 180_000) < 2 && g.Yaw == GalaxyOps.FaceCentre(g.X, g.Z), $"x {g.X} z {g.Z} yaw {g.Yaw}");

        // round trip, also with a field this code does not know (written by a newer / older editor)
        var json = p.ToJson().Replace("\"modId\"", "\"futureField\": [1, 2],\n  \"modId\"");
        var q = Project.FromJson(json);
        check("file round trip keeps everything", q.ToJson() == Project.FromJson(q.ToJson()).ToJson() && q.ToJson().Contains("futureField"), "");
        check("gate links survive", q.GateById(g.Link)?.Link == g.Id, "");

        // file written by the HTML editor (format 1, no y fields, numbers as JS writes them)
        const string html = "{\"format\":1,\"modId\":\"t\",\"modName\":\"T\",\"prefix\":\"tt\",\"textPage\":400900,\"author\":\"x\",\"replaceVanillaStations\":false,\"nextId\":4,\"startSector\":2,"
            + "\"clusters\":[{\"id\":1,\"name\":\"C\",\"desc\":\"\",\"q\":0,\"r\":0,\"backdrop\":3,\"music\":\"\",\"sectors\":[{\"id\":2,\"name\":\"S\",\"desc\":\"\",\"sunlight\":1.2,\"economy\":0.5,\"security\":0.5,"
            + "\"res\":[{\"amount\":2,\"size\":\"medium\",\"ware\":\"ore\",\"yield\":\"medium\",\"speed\":\"average\"}],\"gates\":[{\"id\":3,\"x\":-180000,\"z\":0,\"yaw\":90,\"type\":\"gate\",\"link\":null}],\"stations\":[]}]}]}";
        var h = Project.FromJson(html);
        check("HTML editor file loads", h.AllSectors().Single().Gates.Single().Yaw == 90 && h.Clusters[0].Backdrop == 3 && h.SectorById(2)!.Sunlight == 1.2, "");
        bool rejected = false;
        try { Project.FromJson("{\"format\":7}"); } catch (InvalidDataException) { rejected = true; }
        check("other files rejected", rejected, "");

        var dec = p.AddDecor(a, "region", "p1_40km_asteroid_field", 50_000, 2_000, -30_000);
        var back = Project.FromJson(p.ToJson());
        check("decoration saved and read", back.DecorById(dec.Id)?.Ref == "p1_40km_asteroid_field" && back.SectorOfDecor(dec.Id)?.Id == a.Id, "");

        var w1 = p.AddDecor(a, "object", "wormhole_v1_standalone_macro", 10_000, 0, 10_000);
        var w2 = p.AddDecor(c, "object", "wormhole_v1_standalone_macro", -10_000, 0, -10_000);
        p.LinkDecor(w1, w2);
        p.DeleteSector(c.Id);
        check("deleting a sector unlinks its anomalies", w1.Link == null, "");

        p.DeleteSector(b.Id);
        check("deleting a sector unlinks its gates", p.AllSectors().SelectMany(s => s.Gates).All(x => x.Link == null || p.GateById(x.Link) != null), "");

        // random resources: always at least one known ware, every value from the editor's lists, nividium / scrap rare
        var rsec = new Sector(); var rng = new Random(7); int nivid = 0, ore = 0, empty = 0;
        for (int i = 0; i < 500; i++)
        {
            rsec.RandomResources(rng);
            if (rsec.Res.Count == 0) empty++;
            if (rsec.Res.Any(r => r.Ware == "nividium")) nivid++;
            if (rsec.Res.Any(r => r.Ware == "ore")) ore++;
            if (rsec.Res.Any(r => !GalaxyFacts.ResWare.Contains(r.Ware) || !GalaxyFacts.ResSize.Contains(r.Size) || !GalaxyFacts.ResYield.Contains(r.Yield) || !GalaxyFacts.ResSpeed.Contains(r.Speed))) empty += 1000;
        }
        check("random resources: never empty, valid values, ore common, nividium rare", empty == 0 && ore > 300 && nivid < 80, $"ore {ore}, nividium {nivid} of 500");

        // clear sector / all / map
        var cp = new Project(); var c1 = cp.AddCluster(0, 0); var c2 = cp.AddCluster(1, 0);
        cp.ConnectSectors(c1, c2);
        c1.Res.Add(new ResourceArea()); c1.Ai = new SectorAi { Owner = "argon" }; cp.AddDecor(c1, "object", "x", 0, 0, 0);
        cp.ClearSector(c1);
        check("clear sector: empty, name kept, partner gate unlinked",
            c1.Gates.Count == 0 && c1.Decor.Count == 0 && c1.Res.Count == 0 && c1.Ai == null && c1.Name != "" && c2.Gates.All(g => g.Link == null), "");
        cp.ClearMap();
        check("clear map: no clusters, no start sector", cp.Clusters.Count == 0 && cp.StartSector == null && cp.BaseGates.Count == 0, "");
    }
}
