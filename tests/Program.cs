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
DecorTests.Run(game, Check);

Console.WriteLine(failed == 0 ? "\nALL PASSED" : $"\n{failed} FAILED");
return failed == 0 ? 0 : 1;
