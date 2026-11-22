using System.Xml.Linq;
using X4Data.Text;
using X4Data.Xml;

namespace X4Data.Galaxy;

/// <summary>
/// An installed galaxy a PATCH project adds to (vanilla "xu_ep2_universe" with all DLC patches applied, or a mod's own
/// galaxy): its clusters and sectors with their real macro names, positions and names. Read-only: the editor shows
/// them on the hex map; the patch export refers to them by macro name.
/// Facts: galaxy.xml = macro class="galaxy", clusters connected with ref="clusters" at x / z on the hex grid
/// (15 000 km per column, 8 660 km per half row); a cluster's sectors are its connections with ref="sectors";
/// names in libraries/mapdefaults.xml (dataset macro = ...). DLCs patch the vanilla galaxy with diffs at
/// extensions/ego_dlc_*/maps/xu_ep2_universe/galaxy.xml (GameXml.Load applies them).
/// </summary>
public sealed class BaseGalaxy
{
    public sealed record Info(string Macro, string Folder, string Source, string File)
    {
        /// <summary>Readable name: the vanilla galaxy is "X4 (vanilla + DLCs)", a mod galaxy its folder and mod.</summary>
        public string Label => Folder.Equals("xu_ep2_universe", StringComparison.OrdinalIgnoreCase) ? "X4 galaxy (vanilla + installed DLCs)" : $"{Folder} (mod {Source})";
    }

    /// <summary>
    /// A gate (or accelerator) of a base sector. Path = "cluster conn/sector conn/zone conn/gate conn" as galaxy.xml's
    /// destination links name it. X / Y / Z: position in the sector (zone offset + gate offset), Yaw in degrees.
    /// Active = false: switched off in the map (state active="false"), e.g. a gate the Boron DLC links and opens later.
    /// LinkedTo: the path of the other end, null = not linked to anything (with all DLCs / mods applied).
    /// </summary>
    public sealed record GateInfo(string Path, string ZoneMacro, string ZoneConnection, string Connection, string Macro,
                                  double X, double Y, double Z, double Yaw, bool Active, string? LinkedTo, string ZoneSource);

    public sealed record SectorInfo(string Macro, string Name, double X, double Y, double Z, string Source)
    {
        public string Connection { get; init; } = "";
        public List<GateInfo> Gates { get; } = new();
    }
    public sealed class ClusterInfo
    {
        public string Macro = "", Connection = "", Name = "", Source = "base";
        public double X, Z;
        public int Q, R;
        public List<SectorInfo> Sectors = new();
    }

    public Info Galaxy { get; }
    public List<ClusterInfo> Clusters { get; } = new();

    /// <summary>Galaxies the installed game and its extensions define (not the diffs that patch one).</summary>
    public static List<Info> Installed(GameData g)
    {
        var list = new List<Info>();
        foreach (var f in g.Vfs.Files.Where(f => f.Path.EndsWith("/galaxy.xml", StringComparison.OrdinalIgnoreCase) && f.Path.Contains("maps/", StringComparison.OrdinalIgnoreCase)))
        {
            XElement? root;
            try { root = GameXml.Parse(f.Read()).Root; } catch { continue; }
            if (root?.Name.LocalName != "macros") continue;                  // a <diff>: patches a galaxy, is none
            foreach (var m in root.Elements("macro").Where(m => (string?)m.Attribute("class") == "galaxy"))
            {
                var parts = f.Path.Split('/');
                var source = parts[0].Equals("extensions", StringComparison.OrdinalIgnoreCase) ? parts[1] : "base";
                var folder = parts[^2];
                // Timelines brings scenario / test galaxies of its own: nobody patches those
                if (source.StartsWith("ego_", StringComparison.OrdinalIgnoreCase) && !folder.Equals("xu_ep2_universe", StringComparison.OrdinalIgnoreCase)) continue;
                var name = (string)m.Attribute("name")!;
                if (list.All(i => !i.Macro.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    list.Add(new Info(name, folder, source, f.Path));
            }
        }
        // vanilla first, then by folder
        return list.OrderBy(i => i.Source == "base" ? 0 : 1).ThenBy(i => i.Folder, StringComparer.OrdinalIgnoreCase).ToList();
    }

    BaseGalaxy(Info info) => Galaxy = info;

    // kept from Read for the read-only sector view: game data, merged macro files, mapdefaults datasets
    GameData? _game;
    readonly Dictionary<string, XDocument?> _cache = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, XElement> _datasets = new(StringComparer.OrdinalIgnoreCase);

    XElement? MacroMerged(string name)
    {
        // the macro's file with all patches of the DLCs / mods applied (a plain Macro() read would miss them)
        var file = _game?.MacroFile(name);
        if (file == null) return null;
        if (!_cache.TryGetValue(file, out var doc)) _cache[file] = doc = GameXml.Load(_game!.Vfs, file, _game.Notes);
        return doc?.Root?.Elements("macro").FirstOrDefault(m => string.Equals((string?)m.Attribute("name"), name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A number attribute as the game reads it: missing, empty ("") or not a number = null (the caller's default).
    /// Some mods write such values (an empty coordinate); a plain cast would throw and the whole galaxy be lost.
    /// </summary>
    static double? Num(XAttribute? a) =>
        a != null && double.TryParse(a.Value.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;
    static int? Int(XAttribute? a) => Num(a) is double v ? (int)Math.Round(v) : null;

    public static BaseGalaxy Read(GameData g, Info info, TextDb texts)
    {
        var b = new BaseGalaxy(info) { _game = g };
        XElement? MacroMerged(string name) => b.MacroMerged(name);
        string SourceOf(string macro)
        {
            var f = g.MacroFile(macro) ?? "";
            return f.StartsWith("extensions/", StringComparison.OrdinalIgnoreCase) ? f.Split('/')[1] : "base";
        }

        // names: mapdefaults datasets (all extensions merged)
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var md = GameXml.Load(g.Vfs, "libraries/mapdefaults.xml", g.Notes);
        foreach (var ds in md?.Root?.Elements("dataset") ?? Enumerable.Empty<XElement>())
        {
            var m = (string?)ds.Attribute("macro");
            if (m != null) b._datasets[m] = ds;
            var n = texts.Lookup((string?)ds.Element("properties")?.Element("identification")?.Attribute("name"));
            if (m != null && !string.IsNullOrWhiteSpace(n)) names[m] = n;
        }

        var galaxy = MacroMerged(info.Macro);
        // links: <connection ref="destination" path="../A/B/C/D"><macro path="../../../../../E/F/G/H" .../> (both directions)
        var links = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in galaxy?.Element("connections")?.Elements("connection").Where(c => (string?)c.Attribute("ref") == "destination") ?? Enumerable.Empty<XElement>())
        {
            string? a = Trim((string?)d.Attribute("path")), b2 = Trim((string?)d.Element("macro")?.Attribute("path"));
            if (a == null || b2 == null) continue;
            links[a] = b2; links[b2] = a;
        }
        static string? Trim(string? path) => path?.Replace("../", "").Trim('/');
        foreach (var con in galaxy?.Element("connections")?.Elements("connection").Where(c => (string?)c.Attribute("ref") == "clusters") ?? Enumerable.Empty<XElement>())
        {
            var macro = (string?)con.Element("macro")?.Attribute("ref");
            if (macro == null) continue;
            var pos = con.Element("offset")?.Element("position");
            double x = Num(pos?.Attribute("x")) ?? 0, z = Num(pos?.Attribute("z")) ?? 0;
            var c = new ClusterInfo
            {
                Macro = macro, Connection = (string?)con.Attribute("name") ?? "", X = x, Z = z, Source = SourceOf(macro),
                Name = names.TryGetValue(macro, out var cn) ? cn : macro,
                Q = (int)Math.Round(x / GalaxyFacts.HexX), R = (int)Math.Round(z / GalaxyFacts.HexZ),
            };
            foreach (var sc in MacroMerged(macro)?.Element("connections")?.Elements("connection").Where(s => (string?)s.Attribute("ref") == "sectors") ?? Enumerable.Empty<XElement>())
            {
                var sm = (string?)sc.Element("macro")?.Attribute("ref");
                if (sm == null) continue;
                var sp = sc.Element("offset")?.Element("position");
                var sector = new SectorInfo(sm, names.TryGetValue(sm, out var sn) ? sn : sm,
                    Num(sp?.Attribute("x")) ?? 0, Num(sp?.Attribute("y")) ?? 0, Num(sp?.Attribute("z")) ?? 0, SourceOf(sm))
                { Connection = (string?)sc.Attribute("name") ?? "" };
                // gates: the sector's zones (offset in the sector) and their gate connections (offset in the zone)
                foreach (var zc in MacroMerged(sm)?.Element("connections")?.Elements("connection").Where(z => (string?)z.Attribute("ref") == "zones") ?? Enumerable.Empty<XElement>())
                {
                    var zm = (string?)zc.Element("macro")?.Attribute("ref");
                    if (zm == null) continue;
                    var zp = zc.Element("offset")?.Element("position");
                    double zx = Num(zp?.Attribute("x")) ?? 0, zy = Num(zp?.Attribute("y")) ?? 0, zz = Num(zp?.Attribute("z")) ?? 0;
                    foreach (var gc in MacroMerged(zm)?.Element("connections")?.Elements("connection").Where(g => (string?)g.Attribute("ref") == "gates") ?? Enumerable.Empty<XElement>())
                    {
                        var gp = gc.Element("offset")?.Element("position");
                        var gq = gc.Element("offset")?.Element("quaternion");
                        double qy = Num(gq?.Attribute("qy")) ?? 0, qw = Num(gq?.Attribute("qw")) ?? 1;
                        var path = $"{c.Connection}/{sector.Connection}/{(string?)zc.Attribute("name")}/{(string?)gc.Attribute("name")}";
                        sector.Gates.Add(new GateInfo(path, zm, (string?)zc.Attribute("name") ?? "", (string?)gc.Attribute("name") ?? "",
                            (string?)gc.Element("macro")?.Attribute("ref") ?? "",
                            zx + (Num(gp?.Attribute("x")) ?? 0), zy + (Num(gp?.Attribute("y")) ?? 0), zz + (Num(gp?.Attribute("z")) ?? 0),
                            Math.Round(2 * Math.Atan2(qy, qw) * 180 / Math.PI, 1),
                            (string?)(gc.Element("macro")?.Element("properties") ?? gc.Element("properties"))?.Element("state")?.Attribute("active") != "false",   // under the gate's <macro>
                            links.TryGetValue(path, out var other) ? other : null, SourceOf(zm)));
                    }
                }
                c.Sectors.Add(sector);
            }
            b.Clusters.Add(c);
        }
        return b;
    }

    /// <summary>Sunlight, economy, security and resource areas of a sector as the game files have them (all mods applied).</summary>
    public (double Sunlight, double Economy, double Security, List<ResourceArea> Res) SectorValues(string macro)
    {
        var res = new List<ResourceArea>();
        if (!_datasets.TryGetValue(macro, out var ds)) return (1, 0.5, 0.5, res);
        var area = ds.Element("properties")?.Element("area");
        foreach (var ra in ds.Element("properties")?.Element("resourceareas")?.Elements("resourcearea") ?? Enumerable.Empty<XElement>())
        {
            // ref = sphere_<size>_<ware>_<yield>_<speed>
            var parts = ((string?)ra.Attribute("ref") ?? "").Split('_');
            if (parts.Length < 5 || parts[0] != "sphere") continue;
            res.Add(new ResourceArea { Amount = Int(ra.Attribute("amount")) ?? 1, Size = parts[1], Ware = string.Join("_", parts[2..^2]), Yield = parts[^2], Speed = parts[^1] });
        }
        return (Num(area?.Attribute("sunlight")) ?? 1, Num(area?.Attribute("economy")) ?? 0.5, Num(area?.Attribute("security")) ?? 0.5, res);
    }

    public static ResourceArea CopyRes(ResourceArea r) => new() { Amount = r.Amount, Size = r.Size, Ware = r.Ware, Yield = r.Yield, Speed = r.Speed };

    /// <summary>The mapdefaults dataset of a macro (all mods applied), for the writers: its own spelling of the name, what it has.</summary>
    public XElement? Dataset(string macro) => _datasets.TryGetValue(macro, out var ds) ? ds : null;

    /// <summary>The galaxy's connection of a cluster (all mods applied), for the writers: has it an offset / position?</summary>
    public XElement? ClusterConnection(ClusterInfo c) =>
        MacroMerged(Galaxy.Macro)?.Element("connections")?.Elements("connection").FirstOrDefault(x => (string?)x.Attribute("name") == c.Connection);

    /// <summary>
    /// READ-ONLY view of a sector of this galaxy, as a small project the sector view can show: one cluster with the
    /// sector (backdrop, place in it, sunlight, resources, the cluster's fields that lie in this sector, gates) and
    /// stand-in sectors the gates lead to (only their names matter). Our patch's gates in that sector (edit.BaseGates)
    /// are shown too, linked to our sector. Vanilla wrecks / data vaults are not in the map files (md/placedobjects.xml
    /// creates them) and stations come from god.xml: neither is shown.
    /// </summary>
    public Project ReadOnlyView(ClusterInfo c, SectorInfo s, Project edit)
    {
        var view = new Project { ModName = c.Name, BaseGalaxy = Galaxy.Macro };
        int id = 1;
        var cl = new Cluster { Id = id++, Name = c.Name, Q = c.Q, R = c.R };
        var cm = MacroMerged(c.Macro);
        var backdrop = (string?)cm?.Element("connections")?.Elements("connection").FirstOrDefault(x => (string?)x.Attribute("ref") == "content")
                                      ?.Element("macro")?.Element("component")?.Attribute("ref");
        if (backdrop != null) cl.BackdropRef = backdrop;
        var sec = new Sector { Id = id++, Name = s.Name, PlaceX = s.X, PlaceY = s.Y, PlaceZ = s.Z };
        cl.Sectors.Add(sec);
        view.Clusters.Add(cl);

        // the sector's values as in the game files, then what the project changed
        var v = SectorValues(s.Macro);
        sec.Sunlight = v.Sunlight; sec.Economy = v.Economy; sec.Security = v.Security; sec.Res = v.Res;
        if (edit.BaseSectorChangeOf(s.Macro) is { } ch)
        {
            sec.Sunlight = ch.Sunlight ?? sec.Sunlight; sec.Economy = ch.Economy ?? sec.Economy; sec.Security = ch.Security ?? sec.Security;
            if (ch.Res != null) sec.Res = ch.Res.Select(CopyRes).ToList();
        }

        // fields: region connections of the cluster, given to the sector whose place in the cluster is nearest
        foreach (var rc in cm?.Element("connections")?.Elements("connection").Where(x => (string?)x.Attribute("ref") == "regions") ?? Enumerable.Empty<XElement>())
        {
            var region = (string?)rc.Element("macro")?.Element("properties")?.Element("region")?.Attribute("ref");
            if (region == null || region.StartsWith("audioregion", StringComparison.OrdinalIgnoreCase)) continue;   // sound only, nothing to see
            var off = rc.Element("offset");
            var pos = off?.Element("position");
            double x = Num(pos?.Attribute("x")) ?? 0, y = Num(pos?.Attribute("y")) ?? 0, z = Num(pos?.Attribute("z")) ?? 0;
            var nearest = c.Sectors.OrderBy(o => (o.X - x) * (o.X - x) + (o.Z - z) * (o.Z - z)).First();
            if (nearest.Macro != s.Macro) continue;
            var d = new Decoration { Id = id++, Kind = "region", Ref = region, X = x - s.X, Y = y - s.Y, Z = z - s.Z };
            var rot = off?.Element("rotation");
            if (rot != null) { d.Yaw = Num(rot.Attribute("yaw")) ?? 0; d.Pitch = Num(rot.Attribute("pitch")) ?? 0; d.Roll = Num(rot.Attribute("roll")) ?? 0; }
            else if (off?.Element("quaternion") is { } q)
                d.Yaw = Math.Round(2 * Math.Atan2(Num(q.Attribute("qy")) ?? 0, Num(q.Attribute("qw")) ?? 1) * 180 / Math.PI, 1);   // turn about "up" only
            sec.Decor.Add(d);
        }

        // where every gate path of the galaxy is: "cluster conn/sector conn" -> sector name
        var sectorOfPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var oc in Clusters) foreach (var os in oc.Sectors) sectorOfPath[$"{oc.Connection}/{os.Connection}"] = os.Name;
        string Target(string path) { var p = path.Split('/'); return p.Length >= 2 && sectorOfPath.TryGetValue($"{p[0]}/{p[1]}", out var n) ? n : "?"; }
        var others = new Cluster { Id = id++, Name = "(elsewhere)", Q = 9999, R = 9999 };
        view.Clusters.Add(others);
        void Link(Gate g, string to)
        {
            var stub = new Sector { Id = id++, Name = to };
            var back = new Gate { Id = id++, Link = g.Id };
            stub.Gates.Add(back); others.Sectors.Add(stub);
            g.Link = back.Id;
        }
        foreach (var gi in s.Gates)
        {
            var g = new Gate { Id = id++, X = gi.X, Y = gi.Y, Z = gi.Z, Yaw = gi.Yaw, Type = gi.Macro.Contains("accel", StringComparison.OrdinalIgnoreCase) ? "accelerator" : "gate" };
            var ours = edit.BaseGates.FirstOrDefault(bg => bg.Existing == gi.Path);
            if (ours != null && edit.SectorOfGate(ours.Link ?? -1) is { } own) Link(g, own.Name + " (your patch)");
            else if (gi.LinkedTo != null) Link(g, Target(gi.LinkedTo) + (gi.Active ? "" : " (switched off)"));
            sec.Gates.Add(g);
        }
        // new gates our patch adds to this sector
        foreach (var bg in edit.BaseGates.Where(bg => bg.Existing == null && bg.SectorMacro.Equals(s.Macro, StringComparison.OrdinalIgnoreCase)))
        {
            var g = new Gate { Id = id++, X = bg.X, Y = bg.Y, Z = bg.Z, Yaw = bg.Yaw, Type = bg.Type };
            view.ViewBaseGates[g.Id] = bg.Id;                                  // the editor lets this one move
            Link(g, (edit.SectorOfGate(bg.Link ?? -1)?.Name ?? "?") + " (new gate of your patch)");
            sec.Gates.Add(g);
        }
        view.NextId = id;
        return view;
    }
}
