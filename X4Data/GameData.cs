using System.Xml.Linq;
using X4Data.Vfs;
using X4Data.Xml;

namespace X4Data;

/// <summary>
/// Entry point of the data layer: the file system plus the merged index files and material library.
/// Macro / component names -> file paths come from index/macros.xml and index/components.xml (base + extensions).
/// </summary>
public sealed class GameData
{
    public VirtualFileSystem Vfs { get; }
    public List<string> Notes { get; } = new();
    readonly Dictionary<string, string> _macros = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> _components = new(StringComparer.OrdinalIgnoreCase);
    Materials.MaterialLibrary? _materials;

    public GameData(Settings s)
    {
        Vfs = VirtualFileSystem.Build(s);
        ReadIndex("index/macros.xml", _macros);
        ReadIndex("index/components.xml", _components);
    }

    void ReadIndex(string path, Dictionary<string, string> into)
    {
        var doc = GameXml.Load(Vfs, path, Notes);
        foreach (var e in doc?.Root?.Elements("entry") ?? Enumerable.Empty<XElement>())
        {
            var n = (string?)e.Attribute("name"); var v = (string?)e.Attribute("value");
            if (n != null && v != null) into[n] = VirtualFileSystem.Norm(v);   // later entries (extensions) win
        }
    }

    /// <summary>Exact component names of the index (no wildcard entries).</summary>
    public IEnumerable<string> ComponentNames => _components.Keys.Where(k => !k.EndsWith('*'));
    /// <summary>Raw component element (for data the Component class does not keep, e.g. lights).</summary>
    public XElement? ComponentElement(string name)
    {
        var f = ComponentFile(name);
        if (f == null || !Vfs.Exists(f)) return null;
        return GameXml.Parse(Vfs.Read(f)).Root?.Elements("component")
            .FirstOrDefault(c => string.Equals((string?)c.Attribute("name"), name, StringComparison.OrdinalIgnoreCase));
    }

    public int MacroCount => _macros.Count;
    public int ComponentCount => _components.Count;

    /// <summary>File of a macro ("assets/.../x_macro.xml"), or null.</summary>
    public string? MacroFile(string macro) => Lookup(_macros, macro);
    public string? ComponentFile(string component) => Lookup(_components, component);

    /// <summary>
    /// Exact entry first; else wildcard entries like "character_*" (one file holds many macros), longest prefix wins.
    /// </summary>
    static string? Lookup(Dictionary<string, string> index, string name)
    {
        if (index.TryGetValue(name, out var p)) return p + ".xml";
        string? best = null; int bestLen = -1;
        foreach (var (k, v) in index)
            if (k.EndsWith('*') && k.Length - 1 > bestLen && name.StartsWith(k[..^1], StringComparison.OrdinalIgnoreCase))
            { best = v; bestLen = k.Length - 1; }
        return best == null ? null : best + ".xml";
    }

    public Materials.MaterialLibrary Materials => _materials ??= new Materials.MaterialLibrary(this);

    /// <summary>Reads a macro's root &lt;macro&gt; element.</summary>
    public XElement? Macro(string name)
    {
        var f = MacroFile(name);
        if (f == null || !Vfs.Exists(f)) return null;
        return GameXml.Parse(Vfs.Read(f)).Root?.Elements("macro")
            .FirstOrDefault(m => string.Equals((string?)m.Attribute("name"), name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Component name a macro uses (&lt;component ref=".."/&gt;).</summary>
    public string? ComponentOfMacro(string macro) => (string?)Macro(macro)?.Element("component")?.Attribute("ref");

    public Model.Component? Component(string name)
    {
        var f = ComponentFile(name);
        if (f == null || !Vfs.Exists(f)) return null;
        var el = GameXml.Parse(Vfs.Read(f)).Root?.Elements("component")
            .FirstOrDefault(c => string.Equals((string?)c.Attribute("name"), name, StringComparison.OrdinalIgnoreCase));
        return el == null ? null : Model.Component.Parse(el);
    }
}
