using System.Xml.Linq;

namespace X4Data.Vfs;

/// <summary>
/// All extensions found, which are switched on, and the load order.
/// On / off: the profile's content.xml (&lt;extension id enabled/&gt;) wins, else the extension's own content.xml.
/// An extension whose required dependency is missing or off is switched off too (as the game does).
/// Load order: dependencies first, otherwise by id (ASSUMPTION: alphabetical like the game, not verified).
/// </summary>
public sealed class ExtensionList
{
    /// <summary>Every extension found, enabled or not.</summary>
    public List<Extension> All { get; } = new();
    /// <summary>Enabled extensions in load order.</summary>
    public List<Extension> LoadOrder { get; } = new();
    /// <summary>Things the user may want to know (missing dependency, duplicate id).</summary>
    public List<string> Notes { get; } = new();

    public static ExtensionList Build(Settings s)
    {
        var list = new ExtensionList();
        var byId = new Dictionary<string, Extension>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<string> { Path.Combine(s.GameFolder, "extensions") };
        roots.AddRange(s.ExtraModFolders);
        foreach (var root in roots.Where(Directory.Exists))
        {
            bool external = root != roots[0];
            // an extra folder is either ONE mod (has content.xml itself) or a folder of mods (Steam Workshop, downloads)
            var dirs = external && File.Exists(Path.Combine(root, "content.xml"))
                ? new[] { root }
                : Directory.GetDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var dir in dirs)
            {
                Extension? ext;
                try { ext = Extension.FromFolder(dir); }
                catch (Exception e) { list.Notes.Add($"{dir}: content.xml not readable, skipped ({e.Message})"); continue; }
                if (ext == null) continue;
                if (!byId.TryAdd(ext.Id, ext)) { list.Notes.Add($"Duplicate extension id '{ext.Id}' in {dir}, skipped (the one in the game's extensions folder wins)"); continue; }
                ext.External = external;
                list.All.Add(ext);
            }
        }

        // profile list overrides content.xml's default
        var profile = Path.Combine(s.ProfileFolder, "content.xml");
        if (s.ProfileFolder != "" && File.Exists(profile))
            foreach (var e in XDocument.Load(profile).Root?.Elements("extension") ?? Enumerable.Empty<XElement>())
                if (byId.TryGetValue((string?)e.Attribute("id") ?? "", out var ext))
                {
                    ext.Enabled = Extension.IsTrue((string?)e.Attribute("enabled"), true);
                    ext.EnabledBy = "profile";
                }

        // a mod opened from another folder is wanted: on, whatever the profile says
        foreach (var ext in list.All.Where(x => x.External)) { ext.Enabled = true; ext.EnabledBy = "opened in the editor"; }

        // switch off what misses a required dependency; repeat until nothing changes (chains)
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var ext in list.All.Where(x => x.Enabled))
                foreach (var d in ext.Dependencies.Where(d => !d.Optional))
                    if (!byId.TryGetValue(d.Id, out var dep) || !dep.Enabled)
                    {
                        ext.Enabled = false; ext.EnabledBy = $"needs '{d.Id}'"; changed = true;
                        list.Notes.Add($"{ext.Id} switched off: required '{d.Id}' {(dep == null ? "not found" : "is off")}");
                        break;
                    }
        }

        // topological sort: visit dependencies (also optional ones that are on) before the extension
        var done = new HashSet<Extension>();
        var busy = new HashSet<Extension>();
        void Visit(Extension x)
        {
            if (done.Contains(x)) return;
            if (!busy.Add(x)) { list.Notes.Add($"Dependency loop at '{x.Id}'"); return; }
            foreach (var d in x.Dependencies.OrderBy(d => d.Id, StringComparer.OrdinalIgnoreCase))
                if (byId.TryGetValue(d.Id, out var dep) && dep.Enabled) Visit(dep);
            busy.Remove(x); done.Add(x); list.LoadOrder.Add(x);
        }
        foreach (var x in list.All.Where(x => x.Enabled).OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase)) Visit(x);
        return list;
    }
}
