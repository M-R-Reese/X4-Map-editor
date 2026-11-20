using System.Xml.Linq;
using X4Data.Galaxy;
using X4Data;

/// <summary>Milestone 5 checks: mod export (files well-formed, the new 3D data written, safe folder handling).</summary>
static class ExportTests
{
    /// <summary>Folder of the last test export (for a look by hand / the X4 script check).</summary>
    public static string OutFolder => Path.Combine(Path.GetTempPath(), "x4mapeditor_test", "tg_test");

    public static void Run(GameData game, Action<string, bool, string> check)
    {
        Console.WriteLine("\n--- Mod export (milestone 5)");
        var p = new Project { ModId = "tg_test", ModName = "Test (galaxy)", Prefix = "tg" };
        var a = p.AddCluster(0, 0); var b = p.AddCluster(2, 0);
        var b2 = p.AddSector(p.ClusterOf(b.Id)!);
        p.ConnectSectors(a, b); p.ConnectSectors(b, b2);
        a.Gates[0].Y = 1500;
        a.PlaceX = 2.5e8; a.PlaceZ = -1e7;
        p.ClusterOf(a.Id)!.BackdropRef = "cluster_500_macro_backdrop";
        var field = p.AddDecor(a, "region", "p1_40km_asteroid_field", 50_000, 2_000, -30_000); field.Yaw = 45;
        var vault = p.AddDecor(b, "object", "landmarks_vault_01_macro", 20_000, 0, 20_000);
        var w1 = p.AddDecor(a, "object", "wormhole_v1_standalone_macro", 10_000, 0, 10_000);
        var w2 = p.AddDecor(b2, "object", "wormhole_v1_standalone_macro", -10_000, 500, -10_000); w2.Yaw = -60;
        p.LinkDecor(w1, w2);
        var lone = p.AddDecor(b2, "object", "wormhole_v1_macro", 0, 0, 30_000);

        var files = ModExport.Build(p, new ModExport.Options
        {
            ObjectClass = m => m.StartsWith("landmarks_vault") ? "datavault" : m.StartsWith("wormhole") ? "anomaly" : null,
            Dependencies = new[] { "base", "ego_dlc_boron", "ego_dlc_boron" },
        });
        var bad = new List<string>();
        foreach (var (path, text) in files)
            try { XDocument.Parse(text); } catch (Exception e) { bad.Add(path + ": " + e.Message); }
        check("all exported files are well-formed XML", bad.Count == 0, bad.Count == 0 ? $"{files.Count} files" : string.Join(" | ", bad));

        var n = new ModExport.Names(p);
        var clusters = XDocument.Parse(files["maps/tg_galaxy/clusters.xml"]);
        var secA = clusters.Descendants("connection").First(c => (string?)c.Attribute("name") == n.Sector(p.ClusterOf(a.Id)!, a) + "_connection");
        check("sector placed in its backdrop", (string?)secA.Element("offset")?.Element("position")?.Attribute("x") == "250000000", secA.ToString().Split('\n')[2].Trim());
        var reg = clusters.Descendants("connection").FirstOrDefault(c => (string?)c.Attribute("ref") == "regions");
        check("field as region of the cluster, at sector place + position, turned",
            reg != null && (string?)reg.Element("offset")?.Element("position")?.Attribute("x") == "250050000"
            && (string?)reg.Element("offset")?.Element("rotation")?.Attribute("yaw") == "45"
            && (string?)reg.Descendants("region").First().Attribute("ref") == "p1_40km_asteroid_field", reg?.ToString() ?? "none");
        check("backdrop by component name", files["maps/tg_galaxy/clusters.xml"].Contains("ref=\"cluster_500_macro_backdrop\""), "");
        check("gate height in its zone position", files["maps/tg_galaxy/sectors.xml"].Contains("y=\"1500\""), "");

        var md = files.TryGetValue("md/tg_galaxy.xml", out var mdText) ? mdText : "";
        check("object script: 4 objects, 1 linked pair, 1 vault leak",
            CountOf(md, "<create_object") == 4 && CountOf(md, "<add_anomaly_destination") == 2 && CountOf(md, "<create_signal_leak") == 1
            && md.Contains($"anomaly=\"$Objects.{{{w1.Id}}}\" destination=\"$Objects.{{{w2.Id}}}\""), $"{CountOf(md, "<create_object")} objects");
        check("lone anomaly created, not linked", md.Contains($"$Objects.{{{lone.Id}}}") && !md.Contains($"destination=\"$Objects.{{{lone.Id}}}\""), "");
        check("turned object has its rotation", md.Contains("yaw=\"-60deg\""), "");
        var content = XDocument.Parse(files["content.xml"]).Root!;
        check("DLC dependency written once, base not", content.Elements("dependency").Count(d => (string?)d.Attribute("id") == "ego_dlc_boron") == 1
            && !content.Elements("dependency").Any(d => (string?)d.Attribute("id") == "base"), "");
        check("text files for the default languages 44 + 49 only", files.Keys.Count(k => k.StartsWith("t/")) == 2 && files.ContainsKey("t/0001-l049.xml")
            && content.Elements("text").Count() == 2, string.Join(", ", files.Keys.Where(k => k.StartsWith("t/"))));
        var pAll = p.Clone(); pAll.TextLanguages = new() { 7, 44 };
        var two = ModExport.Build(pAll, new ModExport.Options());
        check("chosen languages written (Russian + English)", two.ContainsKey("t/0001-l007.xml") && two.ContainsKey("t/0001-l044.xml") && !two.ContainsKey("t/0001-l049.xml")
            && two["t/0001-l007.xml"].Contains("<language id=\"7\">"), "");
        pAll.TextLanguages = new();
        var all = ModExport.Build(pAll, new ModExport.Options());
        check("no language ticked = all 16 game languages", all.Keys.Count(k => k.StartsWith("t/")) == 16 && all.ContainsKey("t/0001-l380.xml"), $"{all.Keys.Count(k => k.StartsWith("t/"))} files");
        var used = X4Data.Text.TextPages.Used(game.Vfs);
        check("page scan: base game pages found (1001 wares)", used.TryGetValue(1001, out var w) && w.Contains("base game"), $"{used.Count} pages in use");
        check("page scan: extensions' pages found with the mod's name", game.Vfs.Extensions.LoadOrder.Count == 0 || used.Values.Any(who => who.Any(x => x != "base game")),
            string.Join("; ", used.Where(u => u.Key >= 100000).Take(4).Select(u => $"{u.Key}: {string.Join(", ", u.Value)}")));
        check("brackets in names kept (escaped for the text files)", files["t/0001-l044.xml"].Contains("Test \\(galaxy\\)"), "");

        // folder handling: write, write again (old files go), refuse a foreign folder
        var root = Path.GetDirectoryName(OutFolder)!;
        if (Directory.Exists(root)) Directory.Delete(root, true);
        ModExport.Write(OutFolder, files);
        check("export written", File.Exists(Path.Combine(OutFolder, "md", "tg_galaxy.xml")) && File.Exists(Path.Combine(OutFolder, ModExport.MarkerFile)), OutFolder);
        File.WriteAllText(Path.Combine(OutFolder, "my_notes.txt"), "kept");
        var fewer = new Dictionary<string, string>(files); fewer.Remove("md/tg_galaxy.xml");
        ModExport.Write(OutFolder, fewer);
        check("re-export removes its old files, keeps others", !File.Exists(Path.Combine(OutFolder, "md", "tg_galaxy.xml")) && File.Exists(Path.Combine(OutFolder, "my_notes.txt")), "");
        ModExport.Write(OutFolder, files);
        var foreign = Path.Combine(root, "someone_elses_mod");
        Directory.CreateDirectory(foreign); File.WriteAllText(Path.Combine(foreign, "content.xml"), "<content />");
        bool refused = false;
        try { ModExport.Write(foreign, files); } catch (InvalidOperationException) { refused = true; }
        check("foreign folder is never touched", refused && Directory.GetFiles(foreign).Length == 1, "");
    }

    static int CountOf(string s, string what) { int n = 0, i = 0; while ((i = s.IndexOf(what, i, StringComparison.Ordinal)) >= 0) { n++; i += what.Length; } return n; }
}
