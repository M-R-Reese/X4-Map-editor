using System.Text;
using System.Xml.Linq;
using X4Data;
using X4Data.Galaxy;
using X4Data.Xml;

/// <summary>
/// Changes to an installed galaxy's own clusters and sectors (docs/plan_galaxy_editor_modowner.md, step 1):
/// as a patch (diffs, applied here to the game's files) and directly into a mod's own files (owner).
/// </summary>
static class BaseChangeTests
{
    public static void Run(GameData game, Action<string, bool, string> check)
    {
        Console.WriteLine("\n--- Changing an installed galaxy (patch and direct)");
        var info = BaseGalaxy.Installed(game).First(i => i.Folder.Equals("xu_ep2_universe", StringComparison.OrdinalIgnoreCase));
        var b = BaseGalaxy.Read(game, info, new X4Data.Text.TextDb(game, 44));
        var apc = b.Clusters.First(c => c.Sectors.Any(s => s.Name == "Argon Prime"));
        var ap = apc.Sectors.First(s => s.Name == "Argon Prime");
        var free = Enumerable.Range(1, 40).Select(k => (q: apc.Q + 2 * k, r: apc.R)).First(h => !b.Clusters.Any(c => c.Q == h.q && c.R == h.r));

        // ---- PATCH: no own clusters, only changes
        var p = new Project { ModId = "tg_change", ModName = "Change test", Prefix = "tc", BaseGalaxy = info.Macro, BaseFolder = info.Folder, BaseSource = info.Source };
        p.BaseClusters.Add(new BaseClusterChange { Macro = apc.Macro, Q = free.q, R = free.r });
        p.BaseSectors.Add(new BaseSectorChange { Macro = ap.Macro, Sunlight = 2.5, Security = 0.1,
            Res = new() { new ResourceArea { Amount = 3, Size = "large", Ware = "ore", Yield = "high", Speed = "fast" } } });
        var o = new ModExport.Options { Base = b, MacroFile = m => game.MacroFile(m), ObjectClass = _ => null };
        var files = ModExport.Build(p, o);
        ModExport.Write(Path.Combine(Path.GetTempPath(), "x4mapeditor_test", "tc_change"), files);
        var gtext = files.GetValueOrDefault(info.File) ?? "";
        check("patch: galaxy diff only moves the cluster (no empty add of own clusters)", gtext.Contains("/offset/position/@x") && !gtext.Contains("/connections\">"), gtext.Length > 400 ? gtext[..400] : gtext);

        var galaxy = GameXml.Load(game.Vfs, info.File)!;
        var notes = new List<string>();
        GameXml.Apply(galaxy, XDocument.Parse(gtext).Root!, "test", notes);
        var con = galaxy.Root!.Descendants("connection").First(c => (string?)c.Attribute("name") == apc.Connection);
        var pos = con.Element("offset")!.Element("position")!;
        check("patch applied: the cluster sits on its new hex", Math.Abs(double.Parse((string)pos.Attribute("x")!) - free.q * GalaxyFacts.HexX) < 1
            && Math.Abs(double.Parse((string)pos.Attribute("z")!) - free.r * GalaxyFacts.HexZ) < 1 && notes.Count == 0, string.Join(" | ", notes));

        var mtext = files.GetValueOrDefault("libraries/mapdefaults.xml") ?? "";
        var md = GameXml.Load(game.Vfs, "libraries/mapdefaults.xml")!;
        notes.Clear();
        GameXml.Apply(md, XDocument.Parse(mtext).Root!, "test", notes);
        var ds = md.Root!.Elements("dataset").First(d => ((string?)d.Attribute("macro"))!.Equals(ap.Macro, StringComparison.OrdinalIgnoreCase));
        var area = ds.Element("properties")!.Element("area")!;
        var ras = ds.Element("properties")!.Element("resourceareas")!.Elements("resourcearea").ToList();
        check("patch applied: sunlight, security changed, economy as before; resources replaced",
            (string?)area.Attribute("sunlight") == "2.5" && (string?)area.Attribute("security") == "0.1"
            && (string?)area.Attribute("economy") == (string?)b.Dataset(ap.Macro)!.Element("properties")!.Element("area")!.Attribute("economy")
            && ras.Count == 1 && (string?)ras[0].Attribute("ref") == "sphere_large_ore_high_fast" && notes.Count == 0, string.Join(" | ", notes) + " " + mtext[..Math.Min(500, mtext.Length)]);

        // ---- DIRECT: an exported own galaxy plays the mod; its files are changed in place
        var own = new Project { ModId = "tz_direct", ModName = "Direct test", Prefix = "tz" };
        var c1 = own.AddCluster(0, 0); var c2 = own.AddCluster(2, 0); own.ConnectSectors(c1, c2);
        c1.Res.Add(new ResourceArea { Amount = 1, Ware = "ice" });
        var dir = Path.Combine(Path.GetTempPath(), "x4mapeditor_test", "tz_direct");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        ModExport.Write(dir, ModExport.Build(own, new ModExport.Options()));
        var gfile = Directory.GetFiles(Path.Combine(dir, "maps"), "galaxy.xml", SearchOption.AllDirectories).Single();
        var mfile = Path.Combine(dir, "libraries", "mapdefaults.xml");
        // as a mod author's file might be: Windows line ends, a comment of his own
        File.WriteAllText(mfile, File.ReadAllText(mfile).Replace("\r\n", "\n").Replace("\n", "\r\n").Replace("<diff>", "<diff>\r\n  <!-- my own note -->"));
        var gBefore = File.ReadAllBytes(gfile); var mBefore = File.ReadAllBytes(mfile);
        check("direct: a file written back unchanged is the same, byte for byte",
            BaseChanges.Serialise(XDocument.Parse(File.ReadAllText(gfile), LoadOptions.PreserveWhitespace), gBefore).SequenceEqual(gBefore)
            && BaseChanges.Serialise(XDocument.Parse(File.ReadAllText(mfile), LoadOptions.PreserveWhitespace), mBefore).SequenceEqual(mBefore), "");

        var gdoc = XDocument.Parse(File.ReadAllText(gfile));
        var gal = gdoc.Root!.Element("macro")!;
        var cc = gal.Element("connections")!.Elements("connection").First(c => (string?)c.Attribute("ref") == "clusters");
        var clusters = new List<BaseGalaxy.ClusterInfo> { new() { Macro = (string)cc.Element("macro")!.Attribute("ref")!, Connection = (string)cc.Attribute("name")!, Name = "C1", Source = "tz_direct" } };
        var secMacro = XDocument.Parse(File.ReadAllText(mfile)).Descendants("dataset").First(d => d.Element("properties")?.Element("area") != null).Attribute("macro")!.Value;
        var chg = new Project();
        chg.BaseClusters.Add(new BaseClusterChange { Macro = clusters[0].Macro, Q = 3, R = 1 });
        chg.BaseSectors.Add(new BaseSectorChange { Macro = secMacro, Sunlight = 3.3, Res = new() { new ResourceArea { Amount = 2, Ware = "ore" }, new ResourceArea { Amount = 1, Ware = "silicon", Size = "small" } } });
        var bak = Path.Combine(Path.GetTempPath(), "x4mapeditor_test", "tz_direct_backup");
        if (Directory.Exists(bak)) Directory.Delete(bak, true);
        notes.Clear();
        var written = BaseChanges.WriteDirect(dir, (string)gal.Attribute("name")!, chg, clusters, bak, notes);
        check("direct: galaxy.xml and mapdefaults.xml changed, nothing else", written.Count == 2 && notes.Count == 0, string.Join(", ", written) + " | " + string.Join(" | ", notes));
        var gAfter = File.ReadAllText(gfile);
        var npos = XDocument.Parse(gAfter).Descendants("connection").First(c => (string?)c.Attribute("name") == clusters[0].Connection).Element("offset")!.Element("position")!;
        check("direct: cluster moved to hex 3 / 1", (string?)npos.Attribute("x") == (3 * GalaxyFacts.HexX).ToString(System.Globalization.CultureInfo.InvariantCulture)
            && (string?)npos.Attribute("z") == GalaxyFacts.HexZ.ToString(System.Globalization.CultureInfo.InvariantCulture), npos.ToString());
        var mAfter = File.ReadAllText(mfile);
        var nds = XDocument.Parse(mAfter).Descendants("dataset").First(d => (string?)d.Attribute("macro") == secMacro);
        check("direct: sunlight and resources written, the author's comment and line ends kept",
            (string?)nds.Element("properties")!.Element("area")!.Attribute("sunlight") == "3.3"
            && nds.Element("properties")!.Element("resourceareas")!.Elements("resourcearea").Count() == 2
            && mAfter.Contains("<!-- my own note -->") && !mAfter.Replace("\r\n", "").Contains('\n'), "");
        check("direct: originals backed up", File.Exists(Path.Combine(bak, "libraries", "mapdefaults.xml"))
            && File.ReadAllBytes(Path.Combine(bak, "libraries", "mapdefaults.xml")).SequenceEqual(mBefore), "");
        // only the changed lines differ in the galaxy file
        var diffLines = gAfter.Split('\n').Zip(Encoding.UTF8.GetString(gBefore).Split('\n')).Count(t => t.First != t.Second);
        check("direct: in galaxy.xml only the position line differs", diffLines == 1, $"{diffLines} lines differ");
        notes.Clear();
        chg.BaseClusters[0].Macro = "nowhere_macro";
        BaseChanges.WriteDirect(dir, (string)gal.Attribute("name")!, new Project { BaseClusters = { new BaseClusterChange { Macro = "vanilla_macro" } } },
            new List<BaseGalaxy.ClusterInfo> { new() { Macro = "vanilla_macro", Connection = "not_in_this_mod", Name = "Vanilla", Source = "base" } }, bak, notes);
        check("direct: a cluster that is not in the mod's own files is reported, not invented", notes.Any(n => n.Contains("not in this mod's files")), string.Join(" | ", notes));
    }
}
