using System.Xml.Linq;
using X4Data;
using X4Data.Galaxy;

/// <summary>Game start of an own galaxy: the game's starts read (ships with loadout, personalised start options), export.</summary>
static class StartTests
{
    public static void Run(GameData game, Action<string, bool, string> check)
    {
        Console.WriteLine("\n--- Game start (options of the personalised start)");
        var sc = new StartCatalog(game, new X4Data.Text.TextDb(game, 44));
        check("game starts with a ship read (no tutorials)", sc.Starts.Count >= 8 && sc.Starts.All(s => !s.Id.StartsWith("tutorial")) && sc.Get("custom_creative") != null,
            $"{sc.Starts.Count}: " + string.Join(", ", sc.Starts.Select(s => $"{s.ShipName} ({s.Id})")));
        check("start ships come with their loadout", sc.Starts.All(s => s.ShipXml.Contains("<loadout")), string.Join(", ", sc.Starts.Where(s => !s.ShipXml.Contains("<loadout")).Select(s => s.Id)));
        var vals = sc.CustomItems.Select(i => (string?)XElement.Parse(i).Attribute("value")).ToList();
        check("personalised start options copied, X4-only ones left out", vals.Contains("ship") && vals.Contains("playermoney") && vals.Contains("sector") && !vals.Contains("universestorystates") && !vals.Contains("universeringhighways"),
            string.Join(" ", vals));
        check("creative start's inventory and blueprints", sc.Extras.Contains("<inventory") && sc.Extras.Contains("<blueprints"), "");

        var dlc = sc.Starts.FirstOrDefault(s => s.Source != "base");
        var p = new Project { ModId = "tg_start", ModName = "Start test", Prefix = "ts" };
        var a = p.AddCluster(0, 0); var b = p.AddCluster(1, 0); p.ConnectSectors(a, b);
        p.Start = new GameStart { PlayerName = "Test Pilot", Look = "character_player_custom_m_afr_macro", Money = 250_000, ShipFrom = dlc?.Id ?? "custom_creative", Known = "start" };
        var files = ModExport.Build(p, new ModExport.Options { Starts = sc });
        ModExport.Write(Path.Combine(Path.GetTempPath(), "x4mapeditor_test", "ts_start"), files);
        var gsText = files.GetValueOrDefault("libraries/gamestarts.xml") ?? "";
        XDocument? doc = null; try { doc = XDocument.Parse(gsText); } catch (Exception e) { check("gamestarts.xml well-formed", false, e.Message); }
        var starts = doc?.Root?.Element("add")?.Elements("gamestart").ToList() ?? new();
        check("three starts: fixed, personalised and creative (customeditor)", starts.Count == 3 && starts.Skip(1).All(s => (string?)s.Attribute("tags") == "customeditor" && s.Element("custom")?.Elements("item").Count() == sc.CustomItems.Count)
            && (string?)starts[2].Attribute("id") == "ts_gamestart_creative", string.Join(", ", starts.Select(s => (string?)s.Attribute("id"))));
        var lua = files.GetValueOrDefault("ui/ts_creative.lua") ?? "";
        check("creative: ui.xml loads the Lua after ego_gameoptions; Lua sets the creative flag for our start only",
            files.GetValueOrDefault("ui.xml")?.Contains("<file name=\"ui/ts_creative.lua\"/>") == true && files["ui.xml"].Contains("ego_gameoptions")
            && lua.Contains("\"ts_gamestart_creative\"") && lua.Contains("menu.param[5] = 1") && lua.Contains("CustomGameMenu"), "");
        p.Start.Creative = false;
        check("no creative start -> no Lua, no ui.xml", !ModExport.Build(p, new ModExport.Options { Starts = sc }).ContainsKey("ui.xml"), "");
        p.Start.Creative = true;
        var pl = starts.FirstOrDefault()?.Element("player");
        check("player: look, male, money, own name, ship copied", (string?)pl?.Attribute("macro") == "character_player_custom_m_afr_macro" && (string?)pl?.Attribute("female") == "false"
            && (string?)pl?.Attribute("money") == "250000" && (string?)pl?.Element("ship")?.Attribute("macro") == (dlc?.ShipMacro ?? "ship_arg_s_scout_01_a_macro"), pl?.Element("ship")?.Attribute("macro")?.Value ?? "");
        check("known space: only the start sector", pl?.Element("knownspace")?.Elements("space").Count() == 1, "");
        if (dlc != null) check($"DLC ship start -> dependency on {dlc.Source}", files.GetValueOrDefault("content.xml")?.Contains($"id=\"{dlc.Source}\"") == true, "");
    }
}
