using System.Text.Json;

namespace X4Data;

/// <summary>Folders the user picks at first start. Saved as JSON (the Godot app keeps it in user://settings.json).</summary>
public sealed class Settings
{
    /// <summary>X4 install folder (holds X4.exe, 01.cat ... and the extensions folder).</summary>
    public string GameFolder { get; set; } = "";
    /// <summary>Profile folder Documents\Egosoft\X4\&lt;id&gt;: its content.xml says which mods are switched on. Optional.</summary>
    public string ProfileFolder { get; set; } = "";
    /// <summary>Mods outside the game's extensions folder: a mod folder (has content.xml) or a folder of mods (each a
    /// subfolder with content.xml, e.g. Steam Workshop). Those mods are read as switched on. Optional.</summary>
    public List<string> ExtraModFolders { get; set; } = new();
    /// <summary>Folders with extracted game files (like X4_Reference). Their files win over the archives. Optional.</summary>
    public List<string> ExtractedFolders { get; set; } = new();
    /// <summary>Where the mod export goes (default: the game's extensions folder).</summary>
    public string ExportFolder { get; set; } = "";
    /// <summary>Performance mode: no X4 look shaders (hulls, sky, planets), no glow, no anti-aliasing (View menu).</summary>
    public bool PerformanceMode { get; set; }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load(string file) =>
        File.Exists(file) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(file)) ?? new Settings() : new Settings();

    public void Save(string file) => File.WriteAllText(file, JsonSerializer.Serialize(this, Json));

    /// <summary>
    /// First-start guesses: game folder from Steam's library list (libraryfolders.vdf), profile = the only numbered
    /// folder in Documents\Egosoft\X4, export = the game's extensions folder. Only fills empty fields.
    /// </summary>
    public void Guess()
    {
        if (GameFolder == "")
        {
            var steamRoots = new[] {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) + @"\Steam",
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + @"\Steam" };
            var libraries = new List<string>(steamRoots);
            foreach (var root in steamRoots)
            {
                var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(vdf)) continue;
                // lines like:  "path"		"X:\\Games\\Steam"
                foreach (var line in File.ReadLines(vdf))
                {
                    var t = line.Trim();
                    if (!t.StartsWith("\"path\"")) continue;
                    var parts = t.Split('"', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (parts.Length >= 2) libraries.Add(parts[^1].Replace(@"\\", @"\"));
                }
            }
            GameFolder = libraries.Select(l => Path.Combine(l, "steamapps", "common", "X4 Foundations"))
                .FirstOrDefault(f => File.Exists(Path.Combine(f, "01.cat"))) ?? "";
        }
        if (ProfileFolder == "")
        {
            var x4 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Egosoft", "X4");
            var ids = Directory.Exists(x4)
                ? Directory.GetDirectories(x4).Where(d => Path.GetFileName(d).All(char.IsDigit) && File.Exists(Path.Combine(d, "content.xml"))).ToList()
                : new List<string>();
            if (ids.Count == 1) ProfileFolder = ids[0];
        }
        if (ExportFolder == "" && GameFolder != "") ExportFolder = Path.Combine(GameFolder, "extensions");
    }

    /// <summary>Returns what is wrong with the settings (empty list = usable).</summary>
    public List<string> Problems()
    {
        var p = new List<string>();
        if (!File.Exists(Path.Combine(GameFolder, "01.cat")))
            p.Add($"Game folder has no 01.cat: '{GameFolder}'");
        if (ProfileFolder != "" && !File.Exists(Path.Combine(ProfileFolder, "content.xml")))
            p.Add($"Profile folder has no content.xml: '{ProfileFolder}'");
        foreach (var f in ExtraModFolders.Concat(ExtractedFolders))
            if (!Directory.Exists(f)) p.Add($"Folder not found: '{f}'");
        return p;
    }
}
