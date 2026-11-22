using System.Xml.Linq;
using X4Data;
using X4Data.Galaxy;

/// <summary>Sector AI, step 1 (docs/plan_galaxy_editor_ai.md): factions read from the installed game, export of owner, pirates, flags.</summary>
static class AiTests
{
    public static void Run(GameData game, Action<string, bool, string> check)
    {
        Console.WriteLine("\n--- Sector AI (owner, pirates, faction logic)");
        var fc = new FactionCatalog(game, new X4Data.Text.TextDb(game, 44));
        var owners = fc.Owners.Select(f => f.Id).ToList();
        check("owners: Argon, Teladi, Xenon can own sectors (claim station 'defence')",
            owners.Contains("argon") && owners.Contains("teladi") && owners.Contains("xenon") && fc.Get("argon")!.ClaimTag == "defence", string.Join(", ", owners));
        check("pirate owners: Hatikvah and Duke's Buccaneers (claimspace + pirate)", owners.Contains("hatikvah") && owners.Contains("buccaneers") && fc.Get("buccaneers")!.Pirate, "");
        check("Scale Plate is a pirate faction that cannot own a sector (no claimspace)", fc.Get("scaleplate") is { Pirate: true } && !owners.Contains("scaleplate"), "");
        check("never offered: player, hidden, inactive (Trinity)", !owners.Contains("player") && !owners.Contains("civilian") && !owners.Contains("trinity"), "");
        var pir = fc.PirateStations.ToList();
        check("pirate stations: Scale Plate base + dock, Hatikvah freeport, Buccaneer depot, anarchy port",
            pir.Any(t => t.Id == "pirate_piratebase") && pir.Any(t => t.Id == "pirate_freeport" && t.Faction == "hatikvah") && pir.Any(t => t.Id == "buccaneer_depot_m")
            && pir.Any(t => t.Id == "scaleplate_anarchy_port"), string.Join(", ", pir.Select(t => $"{t.Id}/{t.Faction}")));
        check("pirate stations claim nothing; the pirate dock (build modules) is left out", pir.All(t => !t.Claims) && pir.All(t => t.Id != "pirate_piratedock"), "");
        check("map tags read (anarchy, allowrandomanomaly)", fc.MapTags.ContainsKey("anarchy") && fc.MapTags.ContainsKey("allowrandomanomaly"), string.Join(", ", fc.MapTags.Keys));
        check("anarchy: Scale Plate drug factories for ownerless sectors", fc.TagProducts.TryGetValue("anarchy", out var dr) && dr.Contains("spaceweed"), dr == null ? "" : string.Join(", ", dr));
        check("factories / jobs counted for the Argon", fc.Products.GetValueOrDefault("argon") > 10 && fc.Jobs.GetValueOrDefault("argon") > 10,
            $"{fc.Products.GetValueOrDefault("argon")} products, {fc.Jobs.GetValueOrDefault("argon")} jobs");
        if (game.Vfs.Extensions.LoadOrder.Any(e => e.Id == "ego_dlc_pirate"))
            check("DLC faction: Vigor Syndicate owner, from ego_dlc_pirate", owners.Contains("loanshark") && fc.Get("loanshark")!.Source == "ego_dlc_pirate", "");

        // ---- export, patch project into the X4 galaxy
        var p = new Project { ModId = "tg_ai", ModName = "AI test", Prefix = "ta", BaseGalaxy = "XU_EP2_universe_macro", BaseFolder = "xu_ep2_universe", BaseSource = "base" };
        var a = p.AddCluster(0, 0); var b = p.AddCluster(2, 0);
        p.ConnectSectors(a, b);
        a.Ai = new SectorAi { Owner = "hatikvah", PirateStation = "pirate_freeport", PirateFaction = "hatikvah", Excluded = { "argon", "xenon" } };
        b.Ai = new SectorAi { FactionLogic = false, Tags = { "anarchy", "allowrandomanomaly" }, PirateStation = "buccaneer_depot_m", PirateFaction = "buccaneers" };
        var vanilla = BaseGalaxy.Installed(game).First(i => i.Folder.Equals("xu_ep2_universe", StringComparison.OrdinalIgnoreCase));
        var bg = BaseGalaxy.Read(game, vanilla, new X4Data.Text.TextDb(game, 44));
        var files = ModExport.Build(p, new ModExport.Options { Base = bg, Factions = fc, MacroFile = m => game.MacroFile(m), ObjectClass = _ => null });
        // written out for a look by hand / the X4 script check
        ModExport.Write(Path.Combine(Path.GetTempPath(), "x4mapeditor_test", "ta_ai"), files);
        var bad = new List<string>();
        foreach (var (path, text) in files)
            try { XDocument.Parse(text); } catch (Exception e) { bad.Add(path + ": " + e.Message); }
        check("AI export: all files well-formed XML", bad.Count == 0, string.Join(" | ", bad));
        var god = files.GetValueOrDefault("libraries/god.xml") ?? "";
        check("owner: Hatikvah claim station (select tags [defence]) in sector A", god.Contains("id=\"ta_ai_s" ) && god.Contains("owner=\"hatikvah\" type=\"factory\"") && god.Contains("tags=\"[defence]\""), "");
        check("pirates: freeport (piratebase) and Buccaneer depot", god.Contains("type=\"piratebase\"") && god.Contains("tags=\"[piratedepot]\"") && god.Contains("tags=\"[piratebase]\""), "");
        var md = files.GetValueOrDefault("libraries/mapdefaults.xml") ?? "";
        check("sector B: factionlogic=\"false\" and tags", md.Contains("factionlogic=\"false\"") && md.Contains("tags=\"anarchy allowrandomanomaly\""), "");
        var script = files.GetValueOrDefault("md/ta_galaxy.xml") ?? "";
        check("excluded factions signalled (Argon, Xenon) at start and load", script.Contains("[faction.argon, 'add_excluded_sector', macro.") && script.Contains("[faction.xenon, 'add_excluded_sector'")
            && script.Contains("ExcludeOnLoad"), "");
        check("running games: 3 god stations created once by id", script.Contains("GodOnLoad") && script.Split("<create_god_station").Length - 1 == 3, "");
        var sp = GalaxyOps.AiSpots(a);
        check("claim and pirate station away from the gates (>= 30 km)", a.Gates.All(g => Dist(g.X, g.Z, sp.Owner) > 30_000 && Dist(g.X, g.Z, sp.Pirate) > 30_000), $"{sp.Owner} / {sp.Pirate}");
        a.Ai.Excluded.Add("hatikvah");
        check("check: owner on its own ignored list is reported", p.ValidateAi(fc).Any(t => t.Contains("ignored by")), "");
        a.Ai.Excluded.Remove("hatikvah");

        // ---- step 2: stations placed in 3D, AI stations moved by hand
        Console.WriteLine("\n--- Stations (3D view)");
        check("station owners: Argon have wharfs, the Xenon no trade station", fc.Stations.Any(t => t.Tag == "wharf" && t.Faction == "argon") && !fc.Stations.Any(t => t.Tag == "tradestation" && t.Faction == "xenon"), "");
        check("factory wares per faction and ware names read", fc.ProductWares.GetValueOrDefault("argon")?.Contains("energycells") == true && fc.WareNames.GetValueOrDefault("energycells") == "Energy Cells",
            string.Join(", ", fc.ProductWares.GetValueOrDefault("argon")?.Take(8) ?? Enumerable.Empty<string>()));
        var wharf = p.AddStation(b, "wharf", "argon", 20_000, -30_000); wharf.Yaw = 90; wharf.Y = 2000;
        var fab = p.AddStation(b, "factory", "argon", -20_000, 10_000); fab.Ware = "energycells";
        var den = p.AddStation(b, "piratebase", "scaleplate", 50_000, 50_000); den.Ref = "scaleplate_anarchy_port";
        a.Ai.OwnerAt = new Spot { X = 12_000, Y = 500, Z = -7_000, Yaw = 45 };
        var xenTrade = p.AddStation(b, "tradestation", "xenon", 0, 90_000);
        check("check: a station the owner does not have is reported", p.ValidateAi(fc).Any(t => t.Contains("Xenon") && t.Contains("trade station")), string.Join(" | ", p.ValidateAi(fc)));
        check("check: two claiming factions in one sector = contested", p.ValidateAi(fc).Any(t => t.Contains("contested")), "");
        b.Stations.Remove(xenTrade);
        check("check: valid stations raise no station warning", !p.ValidateAi(fc).Any(t => t.Contains("nothing would be built") || t.Contains("not in the installed game")), string.Join(" | ", p.ValidateAi(fc)));
        files = ModExport.Build(p, new ModExport.Options { Base = bg, Factions = fc, MacroFile = m => game.MacroFile(m), ObjectClass = _ => null });
        ModExport.Write(Path.Combine(Path.GetTempPath(), "x4mapeditor_test", "ta_ai"), files);
        god = files.GetValueOrDefault("libraries/god.xml") ?? "";
        check("wharf: place, height and facing in god.xml", god.Contains("x=\"20000\" y=\"2000\" z=\"-30000\" pitch=\"0\" roll=\"0\" yaw=\"90\"") && god.Contains("tags=\"[wharf]\""), "");
        check("factory: energy cells product of the Argon", god.Contains("ware=\"energycells\" owner=\"argon\" type=\"factory\""), "");
        check("pirate station: its own tag (anarchy port)", god.Contains("owner=\"scaleplate\"") && god.Contains("tags=\"[anarchyport]\""), "");
        check("owner's claim station at the place set by hand", god.Contains("x=\"12000\" y=\"500\" z=\"-7000\" pitch=\"0\" roll=\"0\" yaw=\"45\""), "");
        check("running games: the placed stations are created too", (files.GetValueOrDefault("md/ta_galaxy.xml") ?? "").Contains("<create_god_factory"), "");

        // ---- step 3: own galaxy, vanilla faction AI and missions also run there (diffs of the galaxy checks)
        Console.WriteLine("\n--- Game systems in an own galaxy");
        var own = new Project { ModId = "tg_sys", ModName = "Systems test", Prefix = "tq" };
        var oa = own.AddCluster(0, 0); var ob = own.AddCluster(1, 0); own.ConnectSectors(oa, ob);
        var so = new ModExport.Options { Script = f => X4Data.Xml.GameXml.Load(game.Vfs, f) };
        var of = ModExport.Build(own, so);
        ModExport.Write(Path.Combine(Path.GetTempPath(), "x4mapeditor_test", "tq_sys"), of);
        string ourMacro = "tq_galaxy_macro";
        var fl = of.GetValueOrDefault("md/factionlogic.xml") ?? "";
        check("faction AI: factionlogic.xml managers also start in our galaxy", fl.Contains("//cue[@name='FactionLogicManagers']") && fl.Contains("macro." + ourMacro), fl.Length > 300 ? fl[..300] : fl);
        check("default groups: faction AI + missions, no Kha'ak", of.ContainsKey("md/finalisestations.xml") && of.ContainsKey("md/gmc_dynamic.xml") && !of.ContainsKey("md/khaak_activity.xml"),
            string.Join(", ", of.Keys.Where(k => k.StartsWith("md/"))));
        check("setup.xml: only the Start cue (patrol jobs), no Heretic's End / vanilla patches", of.GetValueOrDefault("md/setup.xml") is { } su && su.Contains("[@name='Start']") && !su.Contains("Patch") && !su.Contains("HereticsEnd"), "");
        // every diff applied to the game's script: each selector finds its node, and the X4 galaxy check stays in
        var badSel = new List<string>();
        int applied = 0;
        foreach (var (path, text) in of.Where(f => GalaxySystems.Scripts.Any(s => s.File == f.Key)))
        {
            var doc = X4Data.Xml.GameXml.Load(game.Vfs, path)!;
            var notes = new List<string>();
            X4Data.Xml.GameXml.Apply(doc, XDocument.Parse(text).Root!, "test", notes);
            badSel.AddRange(notes.Select(n => path + ": " + n));
            foreach (var r in XDocument.Parse(text).Root!.Elements("replace"))
            {
                applied++;
                if (!doc.Descendants().Any(e => (string?)e.Attribute("value") == r.Value)) badSel.Add(path + ": not applied " + (string?)r.Attribute("sel"));
            }
        }
        check("every galaxy diff applies to the game's script (selector found, value replaced)", badSel.Count == 0 && applied >= 8, $"{applied} replaced; " + string.Join(" | ", badSel.Take(4)));
        check("X4 galaxy unchanged: the old check stays, ours is added with 'or'",
            GalaxySystems.Widen("player.galaxy.macro == macro.xu_ep2_universe_macro", ourMacro) == "(player.galaxy.macro == macro.xu_ep2_universe_macro or player.galaxy.macro == macro.tq_galaxy_macro)"
            && GalaxySystems.Widen("player.galaxy.macro.ismacro.{macro.xu_ep2_universe_macro}", ourMacro)!.Contains(" or player.galaxy.macro.ismacro.{macro.tq_galaxy_macro})"), "");
        own.Systems.Add("khaak"); own.Systems.Remove("missions");
        var of2 = ModExport.Build(own, so);
        check("switches: Kha'ak on, missions off", of2.ContainsKey("md/khaak_activity.xml") && !of2.ContainsKey("md/gmc_dynamic.xml"), "");
        check("a patch project writes no galaxy diffs (the X4 galaxy runs them anyway)", !files.ContainsKey("md/factionlogic.xml"), "");
    }

    static double Dist(double x, double z, (double X, double Z) o) => Math.Sqrt((x - o.X) * (x - o.X) + (z - o.Z) * (z - o.Z));
}
