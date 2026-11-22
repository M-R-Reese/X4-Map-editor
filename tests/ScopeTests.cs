using X4Data;
using X4Data.Galaxy;
using X4Data.Text;

/// <summary>
/// Mod scope (2026-11-22): a total conversion installed next to a vanilla project must not change the game data the
/// project uses. Uses the Star Wars Interworlds copy in reference\ when it is there (it switches the vanilla factions
/// off and renames the Xenon); skipped otherwise.
/// </summary>
static class ScopeTests
{
    public static void Run(Settings s, Action<string, bool, string> check)
    {
        Console.WriteLine("\n--- Mod scope (other mods do not change the project's game data)");
        var refDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "reference"));
        var sw = Directory.Exists(refDir) ? Directory.GetDirectories(refDir, "X4 SW Interworlds*").FirstOrDefault() : null;
        if (sw == null) { Console.WriteLine("SKIP  no Star Wars Interworlds in reference\\"); return; }
        var s2 = new Settings { GameFolder = s.GameFolder, ProfileFolder = s.ProfileFolder, ExtraModFolders = new() { sw } };

        var all = new GameData(s2);                                           // as the game: every mod changes the data
        var scoped = new GameData(s2, _ => new HashSet<string>());            // a vanilla project: Egosoft only
        var fAll = new FactionCatalog(all, new TextDb(all, 44));
        var fScoped = new FactionCatalog(scoped, new TextDb(scoped, 44));
        bool ArgonYard(FactionCatalog f) => f.Get("argon") is { Active: true } && f.Stations.Any(t => t.Faction == "argon" && t.Tag == "shipyard");
        check("total conversion read as the game: vanilla factions switched off", !ArgonYard(fAll), $"argon active: {fAll.Get("argon")?.Active}");
        check("vanilla project: Argon active with its shipyard, total conversion ignored", ArgonYard(fScoped), $"argon active: {fScoped.Get("argon")?.Active}");
        check("vanilla project: Xenon keep their own name", fScoped.Get("xenon")?.Name == "Xenon", fScoped.Get("xenon")?.Name ?? "?");
    }
}
