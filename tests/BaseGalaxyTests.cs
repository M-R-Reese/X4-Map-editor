using X4Data;
using X4Data.Galaxy;
using X4Data.Text;

/// <summary>Patch projects: the installed galaxies and the vanilla galaxy read with all DLC patches.</summary>
static class BaseGalaxyTests
{
    public static void Run(GameData game, Action<string, bool, string> check)
    {
        Console.WriteLine("\n--- Installed galaxies (patch projects)");
        var list = BaseGalaxy.Installed(game);
        foreach (var i in list) Console.WriteLine($"  {i.Macro,-28} {i.Folder,-20} {i.Source,-24} {i.File}");
        var vanilla = list.FirstOrDefault(i => i.Folder.Equals("xu_ep2_universe", StringComparison.OrdinalIgnoreCase));
        check("vanilla galaxy found", vanilla != null && vanilla.Source == "base", vanilla?.Macro ?? "none");
        if (vanilla == null) return;
        var texts = new TextDb(game, 44);
        var b = BaseGalaxy.Read(game, vanilla, texts);
        var bySource = b.Clusters.GroupBy(c => c.Source).Select(g => $"{g.Key} {g.Count()}");
        check("vanilla clusters read, DLC clusters included", b.Clusters.Count > 80 && b.Clusters.Any(c => c.Source.StartsWith("ego_dlc_")), $"{b.Clusters.Count} clusters: {string.Join(", ", bySource)}");
        var ap = b.Clusters.SelectMany(c => c.Sectors).FirstOrDefault(s => s.Name == "Argon Prime");
        check("sector names from mapdefaults", ap != null, ap?.Macro ?? "Argon Prime not found");
        var off = b.Clusters.Where(c => Math.Abs(c.X - c.Q * GalaxyFacts.HexX) > 1000 || Math.Abs(c.Z - c.R * GalaxyFacts.HexZ) > 1000).ToList();
        check("vanilla clusters sit on the hex grid", off.Count == 0, off.Count == 0 ? "" : string.Join(", ", off.Take(5).Select(c => $"{c.Name} ({c.X}, {c.Z})")));
        foreach (var c in b.Clusters.Where(c => c.Sectors.Count > 1).Take(4))
            Console.WriteLine($"  {c.Name}: " + string.Join(" | ", c.Sectors.Select(s => $"{s.Name} ({s.X / 1e6:0.#}, {s.Z / 1e6:0.#}) Mm")));
        var far = b.Clusters.SelectMany(c => c.Sectors.Select(s => (c, s))).Where(t => Math.Sqrt(t.s.X * t.s.X + t.s.Z * t.s.Z) > 8e6).ToList();
        Console.WriteLine($"  sectors further than 8 000 km from their cluster centre: {far.Count}" + string.Join("", far.Take(5).Select(t => $" | {t.s.Name} ({t.s.X / 1e6:0.#}, {t.s.Z / 1e6:0.#})")));
        var apc = b.Clusters.FirstOrDefault(c => c.Sectors.Any(x => x.Name == "Argon Prime"));
        if (apc != null)
        {
            var free = new[] { (0, 2), (0, -2), (1, 1), (1, -1), (-1, 1), (-1, -1) }.Select(d => (q: apc.Q + d.Item1, r: apc.R + d.Item2)).Where(h => !b.Clusters.Any(c => c.Q == h.q && c.R == h.r)).ToList();
            Console.WriteLine($"  Argon Prime cluster at hex ({apc.Q}, {apc.R}); free neighbours: {string.Join(" ", free)}");
        }
        var gates = b.Clusters.SelectMany(c => c.Sectors).SelectMany(s => s.Gates.Select(g => (s, g))).ToList();
        check("vanilla gates read, most linked", gates.Count > 150 && gates.Count(t => t.g.LinkedTo != null) > gates.Count * 0.8,
            $"{gates.Count} gates/accelerators, {gates.Count(t => t.g.LinkedTo != null)} linked, {gates.Count(t => !t.g.Active)} switched off");
        foreach (var (s, g) in gates.Where(t => !t.g.Active || t.g.LinkedTo == null))
            Console.WriteLine($"  {(g.Active ? "on " : "OFF")} {(g.LinkedTo != null ? "linked  " : "UNLINKED")} {s.Name,-28} {g.Connection,-34} {g.Macro,-34} {s.Source}");
        PatchExport(game, b, check);
        ReadOnly(b, check);
        Voiced(game, texts, check);
        var clash = b.Clusters.GroupBy(c => (c.Q, c.R)).Where(g => g.Count() > 1).ToList();
        check("no two vanilla clusters on one hex", clash.Count == 0, string.Join(" | ", clash.Take(3).Select(g => string.Join(" + ", g.Select(c => c.Name)))));
    }

    /// <summary>A patch with a new gate in Argon Prime and a reused unused vanilla gate; every diff applied to the real game file.</summary>
    static void PatchExport(GameData game, BaseGalaxy b, Action<string, bool, string> check)
    {
        Console.WriteLine("\n--- Patch export");
        var apc = b.Clusters.First(c => c.Sectors.Any(x => x.Name == "Argon Prime"));
        var aps = apc.Sectors.First(x => x.Name == "Argon Prime");
        var unused = b.Clusters.SelectMany(c => c.Sectors.SelectMany(x => x.Gates.Select(g => (c, s: x, g)))).FirstOrDefault(t => t.g.LinkedTo == null && t.g.Active);
        var p = new Project { ModId = "tp_test", ModName = "Test patch", Prefix = "tp", BaseGalaxy = b.Galaxy.Macro, BaseFolder = b.Galaxy.Folder, BaseSource = b.Galaxy.Source };
        int fq = apc.Q + 1, fr = apc.R - 1;
        while (b.Clusters.Any(c => c.Q == fq && c.R == fr)) fr -= 2;
        var own = p.AddCluster(fq, fr);
        p.ConnectToBase(own, apc, aps, null);
        if (unused.g != null)
        {
            var own2 = p.AddCluster(unused.c.Q + 1, unused.c.R + 1 + (b.Clusters.Any(c => c.Q == unused.c.Q + 1 && c.R == unused.c.R + 1) ? 2 : 0));
            p.ConnectToBase(own2, unused.c, unused.s, unused.g);
        }
        p.AddDecor(own, "object", "landmarks_vault_01_macro", 1000, 0, 1000);
        var files = ModExport.Build(p, new ModExport.Options { Base = b, MacroFile = m => game.MacroFile(m) is { } f ? game.Vfs.Get(f)?.Path ?? f : null, ObjectClass = _ => null });
        foreach (var f in files.Keys.OrderBy(k => k)) Console.WriteLine("  " + f);
        var bad = new List<string>();
        foreach (var (path, text) in files) try { System.Xml.Linq.XDocument.Parse(text); } catch (Exception e) { bad.Add(path + ": " + e.Message); }
        check("patch files are well-formed XML", bad.Count == 0, string.Join(" | ", bad));
        check("no own galaxy, no game start, own files with prefix", !files.Keys.Any(k => k.StartsWith("maps/tp_galaxy")) && !files.ContainsKey("libraries/gamestarts.xml")
            && files.ContainsKey("maps/xu_ep2_universe/tp_clusters.xml") && files.ContainsKey("maps/xu_ep2_universe/galaxy.xml"), "");
        // every diff must hit its target in the real (merged) game file
        var notes = new List<string>();
        foreach (var (path, text) in files.Where(f => f.Value.Contains("<diff>") && f.Key.StartsWith("maps/") || f.Key.StartsWith("extensions/")))
        {
            var target = X4Data.Xml.GameXml.Load(game.Vfs, path, notes);
            if (target == null) { notes.Add($"{path}: no such game file"); continue; }
            int before = target.Descendants("connection").Count();
            X4Data.Xml.GameXml.Apply(target, System.Xml.Linq.XDocument.Parse(text).Root!, path, notes);
            int added = target.Descendants("connection").Count() - before;
            Console.WriteLine($"  {path}: +{added} connections");
            if (added == 0) notes.Add($"{path}: diff added nothing");
        }
        check("each patch diff applies to the real game file", notes.Count == 0, string.Join(" | ", notes.Take(4)));
        var galaxyDiff = files["maps/xu_ep2_universe/galaxy.xml"];
        check("galaxy diff: clusters + links to Argon Prime (new gate) and to the reused vanilla gate",
            galaxyDiff.Contains("ref=\"clusters\"") && galaxyDiff.Contains($"{aps.Connection}/tp_zo_b") && (unused.g == null || galaxyDiff.Contains(unused.g.Path)), "");
        var md = files.TryGetValue("md/tp_galaxy.xml", out var mdt) ? mdt : "";
        check("objects script runs at a start AND at the first load", md.Contains("event_game_loaded") && md.Contains("md.Setup.Start") && md.Contains("<library name=\"PlaceObjects\">"), "");
        var content = files["content.xml"];
        check("dependency on the DLC of the reused gate", unused.g == null || unused.s.Source == "base" || content.Contains($"id=\"{unused.s.Source}\"") || content.Contains("ego_dlc"), unused.s?.Source ?? "");
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "x4mapeditor_test"));
        var outDir = Path.Combine(Path.GetTempPath(), "x4mapeditor_test", "tp_test");
        if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        ModExport.Write(outDir, files);
        Console.WriteLine("  written to " + outDir);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "x4mapeditor_test", "tp_test.galaxy.json"), p.ToJson());
        check("patch connected to the galaxy: no reach warning", !p.Validate().Any(i => i.Contains("galaxy you patch")), string.Join(" | ", p.Validate()));
        var link = own.Gates.First();
        int baseBefore = p.BaseGates.Count;
        p.Unlink(link);
        check("unlinking removes the vanilla-side gate and the check notices", p.BaseGates.Count == baseBefore - 1 && p.Validate().Any(i => i.Contains("galaxy you patch")), string.Join(" | ", p.Validate()));
    }

    /// <summary>A vanilla sector opened read-only: gates with targets, fields, resources, backdrop.</summary>
    static void ReadOnly(BaseGalaxy b, Action<string, bool, string> check)
    {
        Console.WriteLine("\n--- Read-only vanilla sector");
        var c = b.Clusters.First(x => x.Sectors.Any(s => s.Name == "Argon Prime"));
        var s = c.Sectors.First(x => x.Name == "Argon Prime");
        var v = b.ReadOnlyView(c, s, new Project());
        var sec = v.Clusters[0].Sectors[0];
        var targets = sec.Gates.Select(g => g.Link != null ? v.SectorOfGate(g.Link.Value)?.Name : null).ToList();
        Console.WriteLine($"  {sec.Name}: backdrop {v.Clusters[0].BackdropRef}, {sec.Gates.Count} gates -> {string.Join(", ", targets)}; {sec.Decor.Count} fields; {sec.Res.Count} resource kinds; sunlight {sec.Sunlight}");
        check("read-only sector: gates lead to named sectors", sec.Gates.Count >= 3 && targets.All(t => t != null && t != "?"), string.Join(", ", targets));
        check("read-only sector: economy / security from mapdefaults, backdrop", sec.Economy == 0.75 && v.Clusters[0].BackdropRef != null, $"economy {sec.Economy}");
        // Argon Prime has no resource areas (9.00); Grand Exchange I has fields and resources
        var gc = b.Clusters.First(x => x.Sectors.Any(y => y.Name == "Grand Exchange I"));
        var ge = b.ReadOnlyView(gc, gc.Sectors.First(y => y.Name == "Grand Exchange I"), new Project()).Clusters[0].Sectors[0];
        Console.WriteLine($"  Grand Exchange I: {ge.Decor.Count} fields ({string.Join(", ", ge.Decor.Select(d => d.Ref).Distinct().Take(4))}), {ge.Res.Count} resource kinds, sunlight {ge.Sunlight}");
        check("read-only sector: fields and resources", ge.Decor.Count > 0 && ge.Res.Count > 5 && ge.Sunlight == 1.23, "");
    }

    /// <summary>Spoken names: the pool page 20005, free ones, the export writes the reference.</summary>
    static void Voiced(GameData game, TextDb en, Action<string, bool, string> check)
    {
        Console.WriteLine("\n--- Spoken names");
        var v = VoicedNames.Build(game, en, new TextDb(game, 49));
        var free = v.Names.Where(n => n.Free).ToList();
        Console.WriteLine($"  {v.Names.Count} spoken names, {free.Count} free, {v.Numerals.Count} numerals; free e.g. " + string.Join(", ", free.Take(12).Select(n => $"{n.English}/{n.German}")));
        var bhs = v.Names.FirstOrDefault(n => n.English == "Black Hole Sun");
        check("spoken names read, vanilla ones marked used", v.Names.Count > 200 && free.Count > 20 && bhs != null && !bhs.Free, bhs == null ? "Black Hole Sun not found" : string.Join(", ", bhs.UsedBy));
        check("numerals I .. XXV spoken", v.Numerals.Count == 25 && v.Numerals[3].Text == "IV", string.Join(" ", v.Numerals.Take(5)));
        var p = new Project();
        var c = p.AddCluster(0, 0);
        var s = p.AllSectors().First();
        s.Name = free[0].English + " II"; s.NameRef = free[0].Reference(2);
        p.ClusterOf(s.Id)!.NameRef = free[1].Reference(); p.ClusterOf(s.Id)!.Name = free[1].English;
        var files = ModExport.Build(p, new ModExport.Options { ObjectClass = _ => null });
        var md = files["libraries/mapdefaults.xml"]; var t = files["t/0001-l044.xml"];
        check("export: cluster name = pool reference, sector name = our text referring to pool + numeral, page voiced",
            md.Contains($"name=\"{free[1].Reference()}\"") && t.Contains(free[0].Reference(2)) && t.Contains("voice=\"yes\""), "");
    }
}
