using System.Xml.Linq;
using System.Xml.XPath;
using X4Data.Vfs;

namespace X4Data.Xml;

/// <summary>
/// Loads a game XML file the way the game does: the base file, then the same path inside every enabled extension in
/// load order. An extension file whose root is &lt;diff&gt; is applied as a patch (add / replace / remove by XPath);
/// any other extension file has its root's children appended to the base root (e.g. index/macros.xml of a DLC).
/// </summary>
public static class GameXml
{
    public static XDocument? Load(VirtualFileSystem vfs, string relPath, List<string>? notes = null)
    {
        relPath = VirtualFileSystem.Norm(relPath);
        XDocument? doc = vfs.Exists(relPath) ? Parse(vfs.Read(relPath)) : null;
        foreach (var ext in vfs.Extensions.LoadOrder)
        {
            var p = "extensions/" + ext.FolderName + "/" + relPath;
            if (!vfs.Exists(p)) continue;
            XDocument part;
            try { part = Parse(vfs.Read(p)); }
            catch (Exception e) { notes?.Add($"{p}: not readable ({e.Message})"); continue; }
            if (part.Root == null) continue;
            if (part.Root.Name.LocalName == "diff")
            {
                if (doc == null) { notes?.Add($"{p}: diff without a base file"); continue; }
                Apply(doc, part.Root, p, notes);
            }
            else if (doc == null) doc = part;
            else doc.Root!.Add(part.Root.Elements());
        }
        return doc;
    }

    /// <summary>
    /// Parses game / mod XML. Tolerant like the game: a declaration with version="1.1" (seen in a mod's
    /// content.xml) is read as 1.0, which .NET would otherwise refuse.
    /// </summary>
    public static XDocument Parse(byte[] bytes)
    {
        int head = Math.Min(bytes.Length, 200);
        var start = System.Text.Encoding.UTF8.GetString(bytes, 0, head);
        int v = start.IndexOf("version=\"1.1\"", StringComparison.Ordinal);
        if (v < 0) v = start.IndexOf("version='1.1'", StringComparison.Ordinal);
        int declEnd = start.IndexOf("?>", StringComparison.Ordinal);
        if (v >= 0 && declEnd > v && start.TrimStart('\uFEFF', ' ', '\r', '\n').StartsWith("<?xml"))
        {
            bytes = (byte[])bytes.Clone();
            bytes[System.Text.Encoding.UTF8.GetByteCount(start[..v]) + 11] = (byte)'0';   // version="1.1" -> version="1.0" (same length)
        }
        using var ms = new MemoryStream(bytes);
        return XDocument.Load(ms);
    }

    /// <summary>Applies one &lt;diff&gt; (RFC 5261 style, as X4 uses it).</summary>
    public static void Apply(XDocument doc, XElement diff, string source, List<string>? notes)
    {
        foreach (var op in diff.Elements())
        {
            var sel = (string?)op.Attribute("sel");
            if (sel == null) continue;
            object? hit;
            try { hit = (doc.XPathEvaluate(sel) as IEnumerable<object>)?.FirstOrDefault(); }
            catch (Exception e) { notes?.Add($"{source}: bad sel '{sel}' ({e.Message})"); continue; }
            if (hit == null) { notes?.Add($"{source}: sel matches nothing: {sel}"); continue; }

            switch (op.Name.LocalName)
            {
                case "add" when hit is XElement target:
                    var type = (string?)op.Attribute("type");
                    if (type != null && type.StartsWith('@'))          // add an attribute
                        target.SetAttributeValue(type[1..], op.Value);
                    else
                    {
                        var nodes = op.Nodes().Select(Clone).ToList();
                        switch ((string?)op.Attribute("pos"))
                        {
                            case "before": target.AddBeforeSelf(nodes); break;
                            case "after": target.AddAfterSelf(nodes); break;
                            case "prepend": target.AddFirst(nodes); break;
                            default: target.Add(nodes); break;
                        }
                    }
                    break;
                case "replace" when hit is XAttribute a: a.Value = op.Value; break;
                case "replace" when hit is XElement e:
                    e.ReplaceWith(op.Nodes().Select(Clone)); break;
                case "remove" when hit is XAttribute a: a.Remove(); break;
                case "remove" when hit is XElement e: e.Remove(); break;
                default: notes?.Add($"{source}: '{op.Name.LocalName}' on {hit.GetType().Name} not supported: {sel}"); break;
            }
        }
    }

    static XNode Clone(XNode n) => n switch
    {
        XElement e => new XElement(e),
        XText t => new XText(t),
        _ => n,
    };
}
