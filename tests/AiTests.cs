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
    }

    static double Dist(double x, double z, (double X, double Z) o) => Math.Sqrt((x - o.X) * (x - o.X) + (z - o.Z) * (z - o.Z));
}
