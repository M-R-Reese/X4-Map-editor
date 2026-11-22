// Developer probe (not shipped). Modes:
//   parts <component>     : parts of a component (size, distance, materials, colours)
//   mapobjects            : every macro the maps (base + DLC) place into zones / clusters, by class, with name ids
using System.Numerics; using System.Xml.Linq; using X4Data; using X4Data.Model; using X4Data.Xml;
// --extra <folder>: also load the mods in that folder (e.g. a full conversion from reference\)
var st = new Settings();
st.Guess();                                         // game and profile folder as at the editor's first start
int xi = Array.IndexOf(args, "--extra");
if (xi >= 0)
{
    st.ExtraModFolders.Add(args[xi + 1]);
    args = args.Where((_, i) => i != xi && i != xi + 1).ToArray();
}
var g = new GameData(st);
foreach (var e in g.Vfs.Extensions.All.Where(e => st.ExtraModFolders.Any(f => e.Folder.StartsWith(f))))
    Console.WriteLine($"extra mod: {e.Id} '{e.Name}' enabled={e.Enabled} ({e.EnabledBy}) folder {e.FolderName}");
foreach (var n in g.Vfs.Extensions.Notes) Console.WriteLine("note: " + n);
var mode = args.Length > 0 ? args[0] : "parts";
if (mode == "parts")
{
    var notes = new List<string>();
    foreach (var p in Assembly.Build(g, args.Length > 1 ? args[1] : "Cluster_01", 0, notes))
    {
        var w = p.Mesh.Positions.Select(v => Vector3.Transform(v, p.Transform)).ToList();
        var min = w.Aggregate(new Vector3(float.MaxValue), Vector3.Min); var max = w.Aggregate(new Vector3(float.MinValue), Vector3.Max);
        var m = g.Materials.Get(p.MaterialNames.FirstOrDefault() ?? "");
        Console.WriteLine($"{p.PartName,-14} centre dist {((min + max) / 2).Length():E2} size {(max - min).Length():E2} verts {p.Mesh.Positions.Length,6} {string.Join("/", p.MaterialNames)} [{m?.Shader} {m?.BlendMode}]");
    }
}
else if (mode == "material")
{
    // material <collection.name> ... : the material's XML as the game merged it (base + extensions)
    foreach (var file in new[] { "libraries/material_library.xml", "libraries/material_library_1.xml" })
    {
        var doc = GameXml.Load(g.Vfs, file, null);
        foreach (var want in args.Skip(1))
        {
            var parts = want.Split('.', 2);
            var m = doc?.Root?.Elements("collection").Where(c => (string?)c.Attribute("name") == parts[0])
                .SelectMany(c => c.Elements("material")).FirstOrDefault(x => (string?)x.Attribute("name") == parts[^1]);
            if (m != null) Console.WriteLine($"== {file}: {m}");
        }
    }
}
else if (mode == "texture")
{
    // texture <path> ... : size and format of game textures
    foreach (var t in args.Skip(1)) { var d = X4Data.Textures.Dds.Load(g.Vfs, t); Console.WriteLine(d == null ? $"{t}: not found" : $"{t}: {d.Width}x{d.Height} {d.Format} mips {d.Mipmaps}"); }
}
else if (mode == "texstats")
{
    // texstats <path> ... : average R G B A of the top mip from the block end colours (DXT1 / DXT5): which channels hold data
    foreach (var t in args.Skip(1))
    {
        var d = X4Data.Textures.Dds.Load(g.Vfs, t);
        if (d == null || (d.Format != "Dxt1" && d.Format != "Dxt5")) { Console.WriteLine($"{t}: {d?.Format ?? "not found"}"); continue; }
        int bs = d.Format == "Dxt5" ? 16 : 8, blocks = Math.Max(1, d.Width / 4) * Math.Max(1, d.Height / 4);
        double r = 0, gg = 0, b = 0, a = 0, rMin = 1, rMax = 0, aMin = 1, aMax = 0; int n = 0;
        for (int i = 0; i < blocks; i += 7, n++)
        {
            int o = i * bs; double al = 1;
            if (bs == 16) { al = (d.Data[o] + d.Data[o + 1]) / 510.0; o += 8; }
            int c0 = d.Data[o] | d.Data[o + 1] << 8, c1 = d.Data[o + 2] | d.Data[o + 3] << 8;
            double R = (((c0 >> 11) & 31) + ((c1 >> 11) & 31)) / 62.0, G = (((c0 >> 5) & 63) + ((c1 >> 5) & 63)) / 126.0, B = ((c0 & 31) + (c1 & 31)) / 62.0;
            r += R; gg += G; b += B; a += al; rMin = Math.Min(rMin, R); rMax = Math.Max(rMax, R); aMin = Math.Min(aMin, al); aMax = Math.Max(aMax, al);
        }
        Console.WriteLine($"{t}: R {r / n:F2} ({rMin:F2}-{rMax:F2})  G {gg / n:F2}  B {b / n:F2}  A {a / n:F2} ({aMin:F2}-{aMax:F2})");
    }
}
else if (mode == "mapobjects")
{
    var count = new Dictionary<string, int>();
    var mapFilter = args.Length > 1 ? args[1] : "";      // e.g. "starwars": only map files whose path contains this
    foreach (var f in g.Vfs.Files.Where(f => f.Path.Contains("maps/") && f.Path.Contains(mapFilter, StringComparison.OrdinalIgnoreCase) && (f.Path.EndsWith("zones.xml") || f.Path.EndsWith("clusters.xml") || f.Path.EndsWith("sectors.xml"))))
    {
        XDocument d; try { d = GameXml.Parse(f.Read()); } catch { continue; }
        foreach (var m in d.Descendants("macro").Where(m => m.Attribute("ref") != null && m.Parent?.Name == "connection"))
        {
            var r = (string)m.Attribute("ref")!;
            if (r.StartsWith("Cluster_") || r.StartsWith("Zone") || r.StartsWith("tzone") || r.Contains("Sector") || r.Contains("SuperHighway") || r.Contains("Highway")) continue;
            count[r] = count.GetValueOrDefault(r) + 1;
        }
    }
    foreach (var (r, n) in count.OrderBy(k => k.Key))
    {
        var mac = g.Macro(r);
        var cls = (string?)mac?.Attribute("class") ?? "?";
        var name = (string?)mac?.Element("properties")?.Element("identification")?.Attribute("name") ?? "";
        Console.WriteLine($"{n,4}  {cls,-14} {r,-55} {name}");
    }
}
else if (mode == "content")
{
    // how clusters place their backdrop ("content" connection): offset / rotation used?
    var filter = args.Length > 1 ? args[1] : "";
    int n = 0, withPos = 0, withRot = 0;
    var comps = new Dictionary<string, int>();
    foreach (var f in g.Vfs.Files.Where(f => f.Path.Contains("maps/") && f.Path.EndsWith("clusters.xml") && f.Path.Contains(filter, StringComparison.OrdinalIgnoreCase)))
    {
        XDocument d; try { d = GameXml.Parse(f.Read()); } catch { continue; }
        foreach (var c in d.Descendants("connection").Where(c => (string?)c.Attribute("ref") == "content"))
        {
            n++;
            var off = c.Element("offset");
            var pos = off?.Element("position"); var rot = off?.Element("quaternion") ?? off?.Element("rotation");
            bool hasPos = pos != null && new[] { "x", "y", "z" }.Any(a => (string?)pos.Attribute(a) is { } v && v != "0");
            if (hasPos) withPos++;
            if (rot != null) withRot++;
            var comp = (string?)c.Descendants("component").FirstOrDefault()?.Attribute("ref") ?? "?";
            comps[comp] = comps.GetValueOrDefault(comp) + 1;
            if ((hasPos || rot != null) && n < 400) Console.WriteLine($"{f.Path}: {comp} {pos} {rot}");
        }
    }
    Console.WriteLine($"{n} content connections, {withPos} with a position, {withRot} with a rotation");
    Console.WriteLine("backdrop components: " + string.Join(", ", comps.OrderByDescending(k => k.Value).Take(15).Select(k => $"{k.Key} x{k.Value}")));
}
else if (mode == "component")
{
    // where a component lives and its class
    foreach (var name in args.Skip(1))
    {
        var file = g.ComponentFile(name);
        var el = g.ComponentElement(name);
        Console.WriteLine($"{name}: file {file}, class {(string?)el?.Attribute("class")}, geometry {(string?)el?.Element("source")?.Attribute("geometry")}");
    }
    // all celestialbody components, grouped by folder
    var cb = new Dictionary<string, int>();
    foreach (var n in g.ComponentNames)
    {
        var file = g.ComponentFile(n) ?? "";
        if (!file.Contains("environments", StringComparison.OrdinalIgnoreCase)) continue;
        var el = g.ComponentElement(n);
        if ((string?)el?.Attribute("class") != "celestialbody") continue;
        var folder = file[..file.LastIndexOf('/')];
        cb[folder] = cb.GetValueOrDefault(folder) + 1;
    }
    foreach (var (k, v) in cb) Console.WriteLine($"celestialbody: {v,4} in {k}");
}
else if (mode == "sectoroffsets")
{
    // offsets of sectors inside their cluster (largest first)
    var filter = args.Length > 1 ? args[1] : "";
    var list = new List<(double d, string s)>();
    foreach (var f in g.Vfs.Files.Where(f => f.Path.Contains("maps/") && f.Path.EndsWith("clusters.xml") && f.Path.Contains(filter, StringComparison.OrdinalIgnoreCase)))
    {
        XDocument d; try { d = GameXml.Parse(f.Read()); } catch { continue; }
        foreach (var c in d.Descendants("connection").Where(c => (string?)c.Attribute("ref") == "sectors"))
        {
            var p = c.Element("offset")?.Element("position");
            double P(string a) => double.TryParse((string?)p?.Attribute(a), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
            var q = c.Element("offset")?.Element("quaternion");
            list.Add((Math.Sqrt(P("x") * P("x") + P("y") * P("y") + P("z") * P("z")), $"{(string?)c.Attribute("name")} x {P("x"):E2} y {P("y"):E2} z {P("z"):E2}{(q != null ? " ROTATED" : "")}"));
        }
    }
    Console.WriteLine($"{list.Count} sectors, {list.Count(x => x.d > 0)} with an offset, largest:");
    foreach (var x in list.OrderByDescending(x => x.d).Take(8)) Console.WriteLine("  " + x.s);
}
else if (mode == "classes")
{
    // macros by class (only classes that could be decoration), with a few examples and how many have a text name
    var wanted = new HashSet<string> { "asteroid", "recyclable", "object", "celestialbody", "datavault", "buildstorage", "lockbox", "collectablewares", "collectableammo", "collectableshieldrestore", "satellite", "resourceprobe", "navbeacon" };
    var by = new Dictionary<string, List<(string name, string text)>>();
    foreach (var f in g.Vfs.Files.Where(f => f.Path.Contains("/macros/") && f.Path.EndsWith("_macro.xml")))
    {
        XElement? root; try { root = GameXml.Parse(f.Read()).Root; } catch { continue; }
        foreach (var m in root?.Elements("macro") ?? Enumerable.Empty<XElement>())
        {
            var cls = (string?)m.Attribute("class") ?? "";
            if (!wanted.Contains(cls)) continue;
            var name = (string?)m.Attribute("name") ?? "";
            var text = (string?)m.Element("properties")?.Element("identification")?.Attribute("name") ?? "";
            (by.TryGetValue(cls, out var l) ? l : by[cls] = new()).Add((name, text));
        }
    }
    foreach (var (cls, l) in by.OrderByDescending(k => k.Value.Count))
    {
        Console.WriteLine($"{cls}: {l.Count} macros, {l.Count(x => x.text != "")} with a name");
        var prefixes = l.GroupBy(x => string.Join("_", x.name.Split('_').Take(2))).OrderByDescending(x => x.Count()).Take(12);
        Console.WriteLine("   " + string.Join(", ", prefixes.Select(p => $"{p.Key}* x{p.Count()}")));
    }
}
else if (mode == "factions")
{
    // factions (base + DLCs merged) with tags, station categories per faction, faction logic managers, map tags
    var t = new X4Data.Text.TextDb(g);
    string Tx(string? r) { var m = System.Text.RegularExpressions.Regex.Match(r ?? "", @"\{(\d+),\s*(\d+)\}"); return m.Success ? t.Get(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) ?? r! : r ?? ""; }
    foreach (var f in GameXml.Load(g.Vfs, "libraries/factions.xml")!.Root!.Elements("faction"))
        Console.WriteLine($"faction {(string?)f.Attribute("id"),-18} '{Tx((string?)f.Attribute("name"))}' race={(string?)f.Attribute("primaryrace")} active={(string?)f.Attribute("active")} tags=[{(string?)f.Attribute("tags")}]");
    foreach (var s in GameXml.Load(g.Vfs, "libraries/stations.xml")!.Root!.Elements("station"))
        foreach (var c in s.Elements("category"))
            Console.WriteLine($"station {(string?)s.Attribute("id"),-36} tags={(string?)c.Attribute("tags"),-22} faction={(string?)c.Attribute("faction")}");
    var tags = new SortedDictionary<string, int>();
    foreach (var a in GameXml.Load(g.Vfs, "libraries/mapdefaults.xml")!.Descendants("area"))
        foreach (var x in ((string?)a.Attribute("tags") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)) tags[x] = tags.GetValueOrDefault(x) + 1;
    Console.WriteLine("map tags: " + string.Join(", ", tags.Select(k => $"{k.Key} x{k.Value}")));
    foreach (var f in g.Vfs.Files.Where(f => f.Path.Contains("md/factionlogic") || f.Path.Contains("md/setup_dlc")))
        foreach (var m in System.Text.RegularExpressions.Regex.Matches(System.Text.Encoding.UTF8.GetString(f.Read()), @"name=""Faction"" value=""(faction\.\w+)""").Select(m => m.Groups[1].Value).Distinct())
            Console.WriteLine($"manager {m} in {f.Path}");
}
else if (mode == "galaxychecks")
{
    // every md / aiscript file (base, DLCs) that checks for the X4 galaxy, with the cue around each check
    foreach (var f in g.Vfs.Files.Where(f => (f.Path.Contains("md/") || f.Path.Contains("aiscripts/")) && f.Path.EndsWith(".xml")).OrderBy(f => f.Path))
    {
        var t = System.Text.Encoding.UTF8.GetString(f.Read());
        if (!t.Contains("xu_ep2_universe")) continue;
        var lines = t.Split('\n'); string cue = "";
        for (int i = 0; i < lines.Length; i++)
        {
            var m = System.Text.RegularExpressions.Regex.Match(lines[i], @"<(cue|library|diff|add|replace) [^>]*?(name|sel)=""([^""]*)""");
            if (m.Success) cue = m.Groups[3].Value;
            if (lines[i].Contains("xu_ep2_universe")) Console.WriteLine($"{f.Path}:{i + 1} [{cue}] {lines[i].Trim()[..Math.Min(150, lines[i].Trim().Length)]}");
        }
    }
}
else if (mode == "showlines")
{
    // file, first line, count: print those lines of a game file (VFS path)
    var lines = System.Text.Encoding.UTF8.GetString(g.Vfs.Read(args[1])).Split((char)10);
    int from = int.Parse(args[2]), n = int.Parse(args[3]);
    for (int i = from - 1; i < Math.Min(lines.Length, from - 1 + n); i++) Console.WriteLine($"{i + 1}: {lines[i].TrimEnd()}");
}
else if (mode == "galaxies")
{
    // every galaxy the editor would offer for a patch, and which files the extra mods bring under maps/
    foreach (var i in X4Data.Galaxy.BaseGalaxy.Installed(g)) Console.WriteLine($"galaxy {i.Macro} folder {i.Folder} source {i.Source}");
    foreach (var f in g.Vfs.Files.Where(f => f.Path.Contains("maps/") && f.Path.EndsWith("galaxy.xml")).Take(40)) Console.WriteLine($"file {f.Path} disk={f.DiskPath ?? f.Entry?.Catalog.CatPath}");
}
else if (mode == "readgalaxy")
{
    // reads one installed galaxy as the editor does (patch base); prints the error with its stack
    var info = X4Data.Galaxy.BaseGalaxy.Installed(g).First(i => i.Macro.Equals(args[1], StringComparison.OrdinalIgnoreCase));
    try { var bg = X4Data.Galaxy.BaseGalaxy.Read(g, info, new X4Data.Text.TextDb(g, 44)); Console.WriteLine($"read ok: {bg.Clusters.Count} clusters, {bg.Clusters.Sum(c => c.Sectors.Count)} sectors, {bg.Clusters.Sum(c => c.Sectors.Sum(s => s.Gates.Count))} gates"); }
    catch (Exception e) { Console.WriteLine(e); }
}
