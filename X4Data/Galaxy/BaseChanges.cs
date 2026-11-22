using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace X4Data.Galaxy;

/// <summary>
/// Changes to an installed galaxy's own clusters and sectors (Project.BaseSectors / BaseClusters), written two ways
/// (docs/plan_galaxy_editor_modowner.md):
/// - PATCH (anybody, for any mod or the X4 galaxy): diff operations, merged into the patch mod's galaxy.xml and
///   mapdefaults.xml diffs. Every selector names the macro / connection, never a position in the file.
/// - DIRECT (the mod's owner): the mod's own unpacked files are changed in place. Only the touched attributes and
///   elements change; comments, unknown elements, line ends stay; each changed file is backed up first.
/// Step 1: cluster position (hex), sector sunlight / economy / security, resource areas.
/// </summary>
public static class BaseChanges
{
    static string N(double v) => Math.Round(v, 3).ToString(CultureInfo.InvariantCulture);
    static string Xa(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace("\"", "&quot;");
    static string ResRef(ResourceArea r) => $"sphere_{r.Size}_{r.Ware}_{r.Yield}_{r.Speed}";

    /// <summary>The galaxy.xml position of a hex (same grid as the export of own clusters).</summary>
    public static (double X, double Z) HexPos(int q, int r) => (q * GalaxyFacts.HexX, r * GalaxyFacts.HexZ);

    // ================================================================================================ PATCH
    /// <summary>
    /// Diff operations (without the &lt;diff&gt; around them) for the galaxy file and for libraries/mapdefaults.xml, and the
    /// extensions they must load after (the mods that define what is changed).
    /// </summary>
    public static (string Galaxy, string MapDefaults, SortedSet<string> Deps) Diffs(Project p, BaseGalaxy b, List<string> notes)
    {
        var gx = new StringBuilder(); var md = new StringBuilder();
        var deps = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ch in p.BaseClusters)
        {
            var ci = b.Clusters.FirstOrDefault(c => c.Macro.Equals(ch.Macro, StringComparison.OrdinalIgnoreCase));
            var con = ci == null ? null : b.ClusterConnection(ci);
            if (ci == null || con == null) { notes.Add($"Cluster {ch.Macro}: not in the galaxy any more, its move is left out."); continue; }
            if (ci.Source != "base") deps.Add(ci.Source);
            var galaxyName = (string?)con.Parent?.Parent?.Attribute("name") ?? b.Galaxy.Macro;
            var sel = $"/macros/macro[@name='{Xa(galaxyName)}']/connections/connection[@name='{Xa(ci.Connection)}']";
            var (x, z) = HexPos(ch.Q, ch.R);
            var off = con.Element("offset"); var pos = off?.Element("position");
            gx.Append($"  <!-- {Xa(ci.Name)}: moved to hex {ch.Q} / {ch.R} -->\n");
            if (pos != null)
                foreach (var (a, v) in new[] { ("x", x), ("z", z) })
                    gx.Append(pos.Attribute(a) != null
                        ? $"  <replace sel=\"{sel}/offset/position/@{a}\">{N(v)}</replace>\n"
                        : $"  <add sel=\"{sel}/offset/position\" type=\"@{a}\">{N(v)}</add>\n");
            else if (off != null) gx.Append($"  <add sel=\"{sel}/offset\"><position x=\"{N(x)}\" y=\"0\" z=\"{N(z)}\" /></add>\n");
            else gx.Append($"  <add sel=\"{sel}\"><offset><position x=\"{N(x)}\" y=\"0\" z=\"{N(z)}\" /></offset></add>\n");
        }
        foreach (var ch in p.BaseSectors.Where(c => !c.Empty))
        {
            var si = b.Clusters.SelectMany(c => c.Sectors).FirstOrDefault(s => s.Macro.Equals(ch.Macro, StringComparison.OrdinalIgnoreCase));
            if (si == null) { notes.Add($"Sector {ch.Macro}: not in the galaxy any more, its changes are left out."); continue; }
            if (si.Source != "base") deps.Add(si.Source);
            md.Append($"  <!-- {Xa(si.Name)} -->\n");
            var ds = b.Dataset(ch.Macro);
            var attrs = Attrs(ch);
            string ResXml(string pad) => ch.Res!.Count == 0 ? "<resourceareas />"
                : "<resourceareas>\n" + string.Concat(ch.Res.Select(r => $"{pad}  <resourcearea amount=\"{r.Amount}\" ref=\"{ResRef(r)}\" />\n")) + pad + "</resourceareas>";
            if (ds == null)
            {
                // no dataset at all (unusual): a new one with what was changed
                md.Append($"  <add sel=\"/defaults\">\n    <dataset macro=\"{Xa(si.Macro)}\">\n      <properties>\n");
                if (attrs.Count > 0) md.Append("        <area" + string.Concat(attrs.Select(a => $" {a.Name}=\"{N(a.Value)}\"")) + " />\n");
                if (ch.Res != null) md.Append("        " + ResXml("        ") + "\n");
                md.Append("      </properties>\n    </dataset>\n  </add>\n");
                continue;
            }
            var sel = $"/defaults/dataset[@macro='{Xa((string)ds.Attribute("macro")!)}']";
            var props = ds.Element("properties");
            if (props == null)
            {
                md.Append($"  <add sel=\"{sel}\">\n    <properties>\n");
                if (attrs.Count > 0) md.Append("      <area" + string.Concat(attrs.Select(a => $" {a.Name}=\"{N(a.Value)}\"")) + " />\n");
                if (ch.Res != null) md.Append("      " + ResXml("      ") + "\n");
                md.Append("    </properties>\n  </add>\n");
                continue;
            }
            var area = props.Element("area");
            if (area == null && attrs.Count > 0)
                md.Append($"  <add sel=\"{sel}/properties\"><area" + string.Concat(attrs.Select(a => $" {a.Name}=\"{N(a.Value)}\"")) + " /></add>\n");
            else
                foreach (var (a, v) in attrs)
                    md.Append(area!.Attribute(a) != null
                        ? $"  <replace sel=\"{sel}/properties/area/@{a}\">{N(v)}</replace>\n"
                        : $"  <add sel=\"{sel}/properties/area\" type=\"@{a}\">{N(v)}</add>\n");
            if (ch.Res != null)
            {
                if (props.Element("resourceareas") != null) md.Append($"  <replace sel=\"{sel}/properties/resourceareas\">\n    {ResXml("    ")}\n  </replace>\n");
                else if (ch.Res.Count > 0) md.Append($"  <add sel=\"{sel}/properties\">\n    {ResXml("    ")}\n  </add>\n");
            }
        }
        return (gx.ToString(), md.ToString(), deps);
    }

    static List<(string Name, double Value)> Attrs(BaseSectorChange ch)
    {
        var l = new List<(string, double)>();
        if (ch.Sunlight is double s) l.Add(("sunlight", s));
        if (ch.Economy is double e) l.Add(("economy", e));
        if (ch.Security is double c) l.Add(("security", c));
        return l;
    }

    // ================================================================================================ DIRECT
    /// <summary>
    /// The mod's own files changed in place (its owner's way). modFolder: the mod's unpacked folder (with content.xml);
    /// galaxyMacro: its galaxy; clusters: the galaxy's clusters as read (connection names); backupFolder: where the original of every changed file goes (same relative path).
    /// Returns the changed files (relative to modFolder). What is not found in the mod's own files (a cluster another
    /// mod adds, a vanilla dataset) is reported in notes and left alone.
    /// </summary>
    public static List<string> WriteDirect(string modFolder, string galaxyMacro, Project p, IReadOnlyList<BaseGalaxy.ClusterInfo> clusters, string backupFolder, List<string> notes)
    {
        var docs = new Dictionary<string, XDocument>(StringComparer.OrdinalIgnoreCase);   // relative path -> document
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<(string Rel, XDocument Doc)> Files(string sub, string pattern)
        {
            var dir = Path.Combine(modFolder, sub);
            if (!Directory.Exists(dir)) yield break;
            foreach (var f in Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var rel = Path.GetRelativePath(modFolder, f).Replace('\\', '/');
                if (!docs.TryGetValue(rel, out var d))
                {
                    try { d = XDocument.Parse(File.ReadAllText(f), LoadOptions.PreserveWhitespace); }
                    catch (Exception e) { notes.Add($"{rel}: not read ({e.Message})"); continue; }
                    docs[rel] = d;
                }
                yield return (rel, d);
            }
        }

        foreach (var ch in p.BaseClusters)
        {
            var ci = clusters.FirstOrDefault(c => c.Macro.Equals(ch.Macro, StringComparison.OrdinalIgnoreCase));
            if (ci == null) { notes.Add($"Cluster {ch.Macro}: not in the galaxy any more."); continue; }
            // the cluster's connection: in a galaxy file of the mod (or in a diff that adds it to the galaxy)
            var hit = Files("maps", "*.xml").SelectMany(f => f.Doc.Descendants("connection")
                    .Where(c => (string?)c.Attribute("name") == ci.Connection && (string?)c.Attribute("ref") == "clusters"
                                && (c.Ancestors("macro").FirstOrDefault() is not { } m || ((string?)m.Attribute("name"))?.Equals(galaxyMacro, StringComparison.OrdinalIgnoreCase) == true))
                    .Select(c => (f.Rel, Con: c))).FirstOrDefault();
            if (hit.Con == null) { notes.Add($"{ci.Name}: its place is not in this mod's files (it comes from {(ci.Source == "base" ? "the game" : ci.Source)}), not moved."); continue; }
            var (x, z) = HexPos(ch.Q, ch.R);
            var pos = Child(Child(hit.Con, "offset"), "position");
            pos.SetAttributeValue("x", N(x)); pos.SetAttributeValue("z", N(z));
            if (pos.Attribute("y") == null) pos.SetAttributeValue("y", "0");
            changed.Add(hit.Rel);
        }

        foreach (var ch in p.BaseSectors.Where(c => !c.Empty))
        {
            var hit = Files("libraries", "mapdefaults.xml").SelectMany(f => f.Doc.Descendants("dataset")
                    .Where(d => ((string?)d.Attribute("macro"))?.Equals(ch.Macro, StringComparison.OrdinalIgnoreCase) == true)
                    .Select(d => (f.Rel, Ds: d))).FirstOrDefault();
            if (hit.Ds == null) { notes.Add($"{ch.Macro}: no dataset in this mod's libraries/mapdefaults.xml, sector values not written."); continue; }
            var props = Child(hit.Ds, "properties");
            if (Attrs(ch) is { Count: > 0 } attrs)
            {
                var area = Child(props, "area");
                foreach (var (a, v) in attrs) area.SetAttributeValue(a, N(v));
            }
            if (ch.Res != null)
            {
                var ra = props.Element("resourceareas");
                if (ra == null && ch.Res.Count > 0) ra = Child(props, "resourceareas");
                if (ra != null)
                {
                    // new lines indented like the element itself + 2
                    string pad = (ra.PreviousNode as XText)?.Value is { } ws && ws.Contains('\n') ? ws[(ws.LastIndexOf('\n') + 1)..] : "";
                    ra.RemoveNodes();
                    foreach (var r in ch.Res)
                        ra.Add(new XText("\n" + pad + "  "), new XElement("resourcearea", new XAttribute("amount", r.Amount), new XAttribute("ref", ResRef(r))));
                    if (ch.Res.Count > 0) ra.Add(new XText("\n" + pad));
                }
            }
            changed.Add(hit.Rel);
        }

        foreach (var rel in changed)
        {
            var file = Path.Combine(modFolder, rel);
            var bak = Path.Combine(backupFolder, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(bak)!);
            File.Copy(file, bak, true);
            File.WriteAllBytes(file, Serialise(docs[rel], File.ReadAllBytes(file)));
        }
        return changed.OrderBy(r => r).ToList();
    }

    /// <summary>A child element, created (indented like its siblings) when missing.</summary>
    static XElement Child(XElement parent, string name)
    {
        var e = parent.Element(name);
        if (e != null) return e;
        string pad = (parent.PreviousNode as XText)?.Value is { } ws && ws.Contains('\n') ? ws[(ws.LastIndexOf('\n') + 1)..] : "";
        e = new XElement(name);
        if (parent.LastNode is XText tail && tail.Value.Contains('\n')) tail.AddBeforeSelf(new XText("\n" + pad + "  "), e);
        else parent.Add(new XText("\n" + pad + "  "), e, new XText("\n" + pad));
        return e;
    }

    /// <summary>
    /// The document written back as close to the original file as possible: its own declaration line, its byte order
    /// mark, its line ends; the parser kept comments and whitespace (PreserveWhitespace).
    /// </summary>
    /// <summary>Where the root element starts: the first tag that is no declaration, comment or doctype; -1 if none.</summary>
    static int RootStart(string t)
    {
        int i = 0;
        while ((i = t.IndexOf('<', i)) >= 0)
        {
            if (t.AsSpan(i).StartsWith("<?")) { i = t.IndexOf("?>", i, StringComparison.Ordinal); if (i < 0) return -1; i += 2; }
            else if (t.AsSpan(i).StartsWith("<!--")) { i = t.IndexOf("-->", i, StringComparison.Ordinal); if (i < 0) return -1; i += 3; }
            else if (t.AsSpan(i).StartsWith("<!")) { i = t.IndexOf('>', i); if (i < 0) return -1; i++; }
            else return i;
        }
        return -1;
    }

    /// <summary>Just after the root element's end tag (or its self-closing start tag); -1 if not found.</summary>
    static int RootEnd(string t, string name)
    {
        int i = t.LastIndexOf("</" + name, StringComparison.Ordinal);
        if (i >= 0) { int e = t.IndexOf('>', i); return e < 0 ? -1 : e + 1; }
        int s = RootStart(t); if (s < 0) return -1;
        int g = t.IndexOf("/>", s, StringComparison.Ordinal);
        return g < 0 ? -1 : g + 2;
    }

    public static byte[] Serialise(XDocument doc, byte[] original)
    {
        bool bom = original.Length >= 3 && original[0] == 0xEF && original[1] == 0xBB && original[2] == 0xBF;
        var text = new UTF8Encoding(false).GetString(original, bom ? 3 : 0, original.Length - (bom ? 3 : 0));
        bool crlf = text.Contains("\r\n");
        // before and after the root element (declaration, comments, blank lines): the original text, unchanged
        // (the parser does not keep the whitespace between those); only the root element comes from the document
        int start = RootStart(text), end = RootEnd(text, doc.Root!.Name.LocalName);
        var root = doc.Root!.ToString(SaveOptions.DisableFormatting);
        // the parser read "\r\n" as "\n": put the file's own line ends back
        // (.NET writes Windows line ends by itself: first back to "\n", then the file's own)
        root = root.Replace("\r\n", "\n");
        if (crlf) root = root.Replace("\n", "\r\n");
        var outText = start >= 0 && end > start ? text[..start] + root + text[end..] : root;
        var bytes = new UTF8Encoding(false).GetBytes(outText);
        return bom ? new byte[] { 0xEF, 0xBB, 0xBF }.Concat(bytes).ToArray() : bytes;
    }
}
