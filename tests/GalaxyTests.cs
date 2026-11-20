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
    }
}
