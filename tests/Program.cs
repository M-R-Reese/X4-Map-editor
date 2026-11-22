// Checks of the X4 data layer against the real game files.
// Run: dotnet run --project tools/galaxy_editor_3d/tests -- [settings.json]
// Without a settings file the folders are guessed like at the editor's first start (Steam library list,
// Documents\Egosoft\X4). Exit code 0 = all passed.
using System.Diagnostics;
using System.Security.Cryptography;
using X4Data;
using X4Data.Vfs;

int failed = 0;
void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail != "" ? "  (" + detail + ")" : "")}");
    if (!ok) failed++;
}
static string Md5(byte[] b) => Convert.ToHexString(MD5.HashData(b)).ToLowerInvariant();

var s = args.Length > 0 ? Settings.Load(args[0]) : new Settings();
s.Guess();
string reference = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../X4_Reference"));

Check("settings usable", s.Problems().Count == 0, string.Join("; ", s.Problems()));

// ---- 1. archives only (no extracted folder)
var sw = Stopwatch.StartNew();
var vfs = VirtualFileSystem.Build(s);
Check("archives read", vfs.Count > 100000, $"{vfs.Count} files in {sw.ElapsedMilliseconds} ms");

Console.WriteLine("\nExtensions in load order:");
foreach (var e in vfs.Extensions.LoadOrder) Console.WriteLine($"  {e.Id,-28} v{e.Version,-5} ({e.EnabledBy})");
Console.WriteLine("Switched off:");
foreach (var e in vfs.Extensions.All.Where(e => !e.Enabled)) Console.WriteLine($"  {e.Id,-28} ({e.EnabledBy})");
foreach (var n in vfs.Extensions.Notes) Console.WriteLine("  note: " + n);
Console.WriteLine();

// every DLC must come before a mod that depends on it
var order = vfs.Extensions.LoadOrder.Select(e => e.Id).ToList();
bool depsFirst = vfs.Extensions.LoadOrder.All(e => e.Dependencies.All(d => !order.Contains(d.Id) || order.IndexOf(d.Id) < order.IndexOf(e.Id)));
Check("dependencies load first", depsFirst);

// md5 of files read from the archives = md5 written in the .cat (proves offsets and sizes)
var fromArchives = vfs.Files.Where(f => f.Entry != null && f.Size > 0).ToList();
var rnd = new Random(4711);
var sample = Enumerable.Range(0, 400).Select(_ => fromArchives[rnd.Next(fromArchives.Count)])
    .Concat(new[] { "index/macros.xml", "index/components.xml", "extensions/ego_dlc_terran/index/macros.xml" }
        .Select(p => vfs.Get(p)).Where(f => f != null).Select(f => f!))
    .ToList();
var bad = sample.Where(f => Md5(f.Read()) != f.Entry!.Md5).Select(f => f.Path).ToList();
Check("md5 of 400 random files + index files", bad.Count == 0, bad.Count == 0 ? $"{sample.Count} files" : "bad: " + string.Join(", ", bad.Take(5)));

// same file from the archives and from X4_Reference (extracted base game) -> same bytes
var refMacros = Path.Combine(reference, "index", "macros.xml");
if (File.Exists(refMacros))
{
    var a = vfs.Read("index/macros.xml");
    Check("index/macros.xml = X4_Reference copy", a.AsSpan().SequenceEqual(File.ReadAllBytes(refMacros)), $"{a.Length} bytes");
}
else Console.WriteLine("SKIP  X4_Reference not found at " + reference);

Check("DLC file under extensions/<folder>/", vfs.Exists("extensions/ego_dlc_terran/index/macros.xml"));
Check("loose mod file (ds_core unpacked)", vfs.Get("extensions/ds_core/content.xml")?.DiskPath != null);
var gate = vfs.Under("assets/props/gates/").FirstOrDefault(f => f.Path.EndsWith(".xmf", StringComparison.OrdinalIgnoreCase));
Check("gate model found", gate != null, gate?.Path ?? "");

// ---- 2. with the extracted folder on top: it wins
if (Directory.Exists(reference))
{
    s.ExtractedFolders.Add(reference);
    var vfs2 = VirtualFileSystem.Build(s);
    Check("extracted folder wins", vfs2.Get("index/macros.xml")?.DiskPath != null, vfs2.Get("index/macros.xml")?.Source ?? "");
}

// ---- 3. models, materials, textures (archives only, as a player without extracted files)
s.ExtractedFolders.Clear();
var game = new GameData(s);
foreach (var n in game.Notes.Take(10)) Console.WriteLine("  note: " + n);
ModelTests.Run(game, Check);
GalaxyTests.Run(Check);
ExportTests.Run(game, Check);
BaseGalaxyTests.Run(game, Check);
AiTests.Run(game, Check);
StartTests.Run(game, Check);
BaseChangeTests.Run(game, Check);

// ---- a mod opened from another folder (menu "Open a mod from another folder"): the Star Wars mod in reference\ (not installed)
var swDir = Directory.Exists(Path.Combine(reference, "..", "reference")) ? Directory.GetDirectories(Path.GetFullPath(Path.Combine(reference, "..", "reference")), "X4 SW Interworlds*").FirstOrDefault() : null;
var swMod = swDir != null ? Path.Combine(swDir, "starwarsmod_m1") : null;
if (swMod != null && File.Exists(Path.Combine(swMod, "content.xml")))
{
    Console.WriteLine("\n--- Mod from another folder");
    var s2 = Settings.Load(args.Length > 0 ? args[0] : "nonexistent"); s2.Guess();
    s2.ExtraModFolders = new() { swMod };
    var g2 = new GameData(s2);
    var sw2 = g2.Vfs.Extensions.All.FirstOrDefault(e => e.External);
    Console.WriteLine($"  {sw2?.Id} \"{sw2?.Name}\" enabled {sw2?.Enabled} ({sw2?.EnabledBy}); deps {string.Join(", ", sw2?.Dependencies.Select(d => d.Id + (d.Optional ? "?" : "")) ?? Array.Empty<string>())}");
    var gal = X4Data.Galaxy.BaseGalaxy.Installed(g2);
    foreach (var i in gal) Console.WriteLine($"  galaxy {i.Macro} {i.Folder} {i.Source}");
    Check("mod from another folder read, switched on", sw2 != null && sw2.Enabled, sw2?.EnabledBy ?? "not found");
    Check("its galaxy offered for a patch", gal.Any(i => i.Source.Equals(sw2?.FolderName, StringComparison.OrdinalIgnoreCase)), string.Join(", ", gal.Select(i => i.Source)));
    // a full conversion: its galaxy is read whole, with its own names (texts in t/0001.xml, no t/0001-l044.xml) and fields
    var own = gal.FirstOrDefault(i => i.Source.Equals(sw2?.FolderName, StringComparison.OrdinalIgnoreCase) && !i.Folder.Contains("showcase", StringComparison.OrdinalIgnoreCase));
    if (own != null)
    {
        var tx2 = new X4Data.Text.TextDb(g2, 44);
        X4Data.Galaxy.BaseGalaxy? read2 = null; string err = "";
        try { read2 = X4Data.Galaxy.BaseGalaxy.Read(g2, own, tx2); } catch (Exception e) { err = e.Message; }
        var secs = read2?.Clusters.SelectMany(c => c.Sectors).ToList() ?? new();
        Check("full conversion's galaxy read (empty numbers in its map files tolerated)", secs.Count > 100, $"{secs.Count} sectors {err}");
        Check("its sector names come from its t/0001.xml (the file for all languages)", secs.Count > 0 && secs.Count(s => s.Name != s.Macro) > secs.Count * 0.9,
            $"{secs.Count(s => s.Name != s.Macro)} of {secs.Count} named; " + string.Join(" | ", secs.Take(4).Select(s => s.Name)));
        var dc2 = new X4Data.Space.DecorCatalog(g2, tx2);
        Check("its own fields offered in the palette (its globalregion definitions)", dc2.Items.Count(i => i.Source == sw2!.FolderName && i.Kind == "region") > 50,
            $"{dc2.Items.Count(i => i.Source == sw2!.FolderName && i.Kind == "region")} fields");
        Check("its backdrops listed", X4Data.Space.Backdrops.List(g2).Count(b => b.Source == sw2!.FolderName) > 50, "");
        var ml2 = new X4Data.Audio.MusicLibrary(g2);
        Check("its music found", ml2.All.Any(m => m.Tracks.Any(t => t.File.Contains(sw2!.FolderName, StringComparison.OrdinalIgnoreCase))), "");
        var replaced = ml2.All.Where(m => m.Source == "base" && m.Tracks.Count > 0 && m.Tracks.All(t => t.File.Contains(sw2!.FolderName, StringComparison.OrdinalIgnoreCase))).ToList();
        Check("Egosoft music whose tracks the mod replaced stays Egosoft's (offered in vanilla projects)", ml2.Get("music_cluster_complete")?.Source == "base",
            $"{replaced.Count} Egosoft ids with only the mod's tracks: " + string.Join(", ", replaced.Take(5).Select(m => m.Id)));
    }
}
DecorTests.Run(game, Check);
{
    // music: entries with several tracks (<sample><select start/>) and with one (<sample start/>) are both read
    var ml = new X4Data.Audio.MusicLibrary(game);
    Check("music: nearly all music entries of the game have tracks (single <sample start> too)", ml.All.Count > 150 && ml.Get("music_highway_v1_t1") != null, $"{ml.All.Count} entries");
}

Console.WriteLine(failed == 0 ? "\nALL PASSED" : $"\n{failed} FAILED");
return failed == 0 ? 0 : 1;
