namespace X4Data.Vfs;

/// <summary>
/// One view of all game files, the way the game sees them. Later layers override earlier ones:
///  1. base archives 01.cat, 02.cat ... (higher number wins; *_sig.cat skipped, they only hold signatures)
///  2. per enabled extension, in load order:
///     ext_*.cat and loose files -> under "extensions/&lt;folder&gt;/..."; subst_*.cat -> replace base game paths
///  3. extracted folders (settings): their files win over everything
/// Paths: forward slashes, compared without case (X4 ignores case).
/// </summary>
public sealed class VirtualFileSystem
{
    readonly Dictionary<string, VfsFile> _files = new(StringComparer.OrdinalIgnoreCase);

    public ExtensionList Extensions { get; }

    /// <summary>
    /// Which extensions may CHANGE the game data (diffs, entries merged into another file's list, texts that replace
    /// others): null = all. Egosoft's always may. A mod outside it still adds its own files (its galaxy, models, new
    /// names), but cannot change what the project uses: a total conversion installed next to a vanilla project no
    /// longer switches off the vanilla factions (2026-11-22, Star Wars Interworlds). Set by GameData.
    /// </summary>
    public Func<string, bool>? InScope { get; set; }

    /// <summary>May this extension (folder name) change the game data? (InScope; Egosoft's always.)</summary>
    public bool Changes(string folderName) =>
        InScope == null || folderName.StartsWith("ego_", StringComparison.OrdinalIgnoreCase) || InScope(folderName);

    /// <summary>The extension folder of a file path ("extensions/x/..."), or null for the base game.</summary>
    public static string? ExtensionOf(string path)
    {
        if (!path.StartsWith("extensions/", StringComparison.OrdinalIgnoreCase)) return null;
        int end = path.IndexOf('/', 11);
        return end > 11 ? path[11..end] : null;
    }
    public int Count => _files.Count;
    public IEnumerable<VfsFile> Files => _files.Values;

    VirtualFileSystem(ExtensionList ext) => Extensions = ext;

    public static VirtualFileSystem Build(Settings s)
    {
        var vfs = new VirtualFileSystem(ExtensionList.Build(s));
        foreach (var cat in NumberedCatalogs(s.GameFolder, "")) vfs.AddCatalog(cat, "");
        foreach (var ext in vfs.Extensions.LoadOrder)
        {
            string prefix = "extensions/" + ext.FolderName + "/";
            foreach (var cat in NumberedCatalogs(ext.Folder, "ext_")) vfs.AddCatalog(cat, prefix);
            foreach (var cat in NumberedCatalogs(ext.Folder, "subst_")) vfs.AddCatalog(cat, "");
            vfs.AddFolder(ext.Folder, prefix, skipArchives: true);   // loose files (unpacked mods) win over own archives
        }
        foreach (var f in s.ExtractedFolders) vfs.AddFolder(f, "", skipArchives: true);
        return vfs;
    }

    /// <summary>prefix01.cat, prefix02.cat ... in number order, without the _sig ones.</summary>
    static IEnumerable<string> NumberedCatalogs(string folder, string prefix) =>
        Directory.Exists(folder)
            ? Directory.GetFiles(folder, prefix + "*.cat")
                .Where(f =>
                {
                    var n = Path.GetFileNameWithoutExtension(f)[prefix.Length..];
                    return n.Length > 0 && n.All(char.IsDigit);           // "01" yes, "01_sig" no
                })
                .OrderBy(f => int.Parse(Path.GetFileNameWithoutExtension(f)[prefix.Length..]))
            : Enumerable.Empty<string>();

    void AddCatalog(string catPath, string prefix)
    {
        var cat = new Catalog(catPath);
        foreach (var e in cat.Entries)
        {
            var p = prefix + Norm(e.Path);
            _files[p] = new VfsFile(p, e.Size, e.Timestamp, cat, e, null);
        }
    }

    void AddFolder(string folder, string prefix, bool skipArchives)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (skipArchives && (ext == ".cat" || ext == ".dat" || ext == ".sig")) continue;
            var p = prefix + Norm(Path.GetRelativePath(folder, f));
            var info = new FileInfo(f);
            _files[p] = new VfsFile(p, info.Length, new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds(), null, null, f);
        }
    }

    /// <summary>Game path as the archives list it: forward slashes, no leading slash, no doubled slashes (some component
    /// files write "landmarks\\landmarks_vault_01_data", all data vaults and landmarks among them; the game reads them anyway).</summary>
    public static string Norm(string p)
    {
        p = p.Replace('\\', '/');
        while (p.Contains("//")) p = p.Replace("//", "/");
        return p.TrimStart('/');
    }

    public bool Exists(string path) => _files.ContainsKey(Norm(path));
    public VfsFile? Get(string path) => _files.GetValueOrDefault(Norm(path));

    public byte[] Read(string path) =>
        (Get(path) ?? throw new FileNotFoundException($"Not in the game files: '{path}'")).Read();

    /// <summary>All files whose path starts with the folder (e.g. "index/").</summary>
    public IEnumerable<VfsFile> Under(string folder)
    {
        var f = Norm(folder);
        return _files.Values.Where(v => v.Path.StartsWith(f, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>A file of the virtual file system: either inside an archive (Entry) or a loose file (DiskPath).</summary>
public sealed record VfsFile(string Path, long Size, long Timestamp, Catalog? Catalog, CatalogEntry? Entry, string? DiskPath)
{
    public byte[] Read() => DiskPath != null ? File.ReadAllBytes(DiskPath) : Catalog!.Read(Entry!);
    /// <summary>Where the file comes from, for messages.</summary>
    public string Source => DiskPath ?? Catalog!.CatPath;
}
