using System.Xml.Linq;

namespace X4Data.Vfs;

/// <summary>One extension (DLC or mod): a folder with a content.xml.</summary>
public sealed class Extension
{
    /// <summary>Id from content.xml (what dependencies and the profile list name).</summary>
    public string Id { get; init; } = "";
    /// <summary>Folder name: the game addresses the extension's files as extensions/&lt;folder&gt;/...</summary>
    public string FolderName { get; init; } = "";
    public string Folder { get; init; } = "";
    public string Name { get; init; } = "";
    public int Version { get; init; }
    public bool Enabled { get; set; }
    /// <summary>Why it is on or off (content.xml default / profile list).</summary>
    public string EnabledBy { get; set; } = "";
    public List<Dependency> Dependencies { get; } = new();

    public sealed record Dependency(string Id, bool Optional);

    public static Extension? FromFolder(string folder)
    {
        var file = Path.Combine(folder, "content.xml");
        if (!File.Exists(file)) return null;
        var root = Xml.GameXml.Parse(File.ReadAllBytes(file)).Root;
        if (root == null || root.Name.LocalName != "content") return null;
        var ext = new Extension
        {
            Id = (string?)root.Attribute("id") ?? Path.GetFileName(folder),
            FolderName = Path.GetFileName(folder.TrimEnd('\\', '/')),
            Folder = folder,
            Name = (string?)root.Attribute("name") ?? "",
            Version = int.TryParse((string?)root.Attribute("version"), out var v) ? v : 0,
            // content.xml "enabled" is the default when the profile does not list the extension (missing = on)
            Enabled = IsTrue((string?)root.Attribute("enabled"), true),
            EnabledBy = "content.xml",
        };
        foreach (var d in root.Elements("dependency"))
        {
            var id = (string?)d.Attribute("id");
            if (id == null) continue;                 // <dependency version="900"/> = game version, not an extension
            ext.Dependencies.Add(new Dependency(id, IsTrue((string?)d.Attribute("optional"), false)));
        }
        return ext;
    }

    internal static bool IsTrue(string? s, bool fallback) =>
        s == null ? fallback : s is "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase);

    public override string ToString() => $"{Id} v{Version}";
}
