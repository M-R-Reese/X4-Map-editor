using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using X4Data;
using X4Data.Galaxy;

namespace GalaxyEditor3D;

/// <summary>
/// Start menu (shown when the editor opens, and by "Menu" in the top bar): New galaxy, New patch (adds sectors to an
/// installed galaxy: vanilla with its DLCs, or a mod's galaxy), Open project, Continue (last session), recent projects.
/// A PATCH project shows the galaxy it adds to dimmed and locked on the hex map (Base).
/// </summary>
public partial class App
{
    /// <summary>The galaxy a patch project adds to (read from the installed game); null for an own galaxy.</summary>
    public BaseGalaxy? Base { get; private set; }
    CanvasLayer? _startMenu;
    string RecentFile => ProjectSettings.GlobalizePath("user://recent.json");

    public BaseGalaxy.ClusterInfo? BaseClusterAt(int q, int r) => Base?.Clusters.FirstOrDefault(c => c.Q == q && c.R == r);

    /// <summary>Reads the patched galaxy of the project (after the game files, after New patch / Open).</summary>
    async void LoadBase()
    {
        Base = null;
        if (P.BaseGalaxy == null) { Refresh(); return; }
        if (Game == null || Texts == null) { SetStatus("The galaxy this patch adds to is shown once the game files are read."); return; }
        var info = (InstalledGalaxies ?? BaseGalaxy.Installed(Game)).FirstOrDefault(i => i.Macro.Equals(P.BaseGalaxy, StringComparison.OrdinalIgnoreCase));
        if (info == null) { SetStatus($"The galaxy \"{P.BaseFolder ?? P.BaseGalaxy}\" this patch adds to is not installed (mod \"{P.BaseSource}\" missing or switched off?)."); Refresh(); return; }
        var want = P.BaseGalaxy;
        BaseGalaxy read;
        try { read = await ReadGalaxy(info); }                        // usually read already in the background
        catch (Exception e) { SetStatus($"The galaxy {info.Label} could not be read: {e.Message}"); return; }
        if (P.BaseGalaxy != want) return;                              // another project was opened meanwhile
        Base = read;
        var clash = P.Clusters.Where(c => BaseClusterAt(c.Q, c.R) != null).Select(c => c.Name).ToList();
        SetStatus($"Patch for {info.Label}: {read.Clusters.Count} clusters shown dimmed." + (clash.Count > 0 ? $"  On a taken hex: {string.Join(", ", clash)}." : ""));
        if (P.Clusters.Count == 0) _galaxy.CentreGalaxy();
        Refresh();
    }

    // ---------------------------------------------------------------------------------------------- recent projects
    List<string> Recent()
    {
        try { return File.Exists(RecentFile) ? (JsonSerializer.Deserialize<List<string>>(File.ReadAllText(RecentFile)) ?? new()).Where(File.Exists).ToList() : new(); }
        catch { return new(); }
    }

    void AddRecent(string f)
    {
        var list = Recent(); list.RemoveAll(x => x.Equals(f, StringComparison.OrdinalIgnoreCase)); list.Insert(0, f);
        try { File.WriteAllText(RecentFile, JsonSerializer.Serialize(list.Take(8).ToList())); } catch { /* not important */ }
    }

    // ---------------------------------------------------------------------------------------------- installed galaxies
    // Read in the background right after the game files (vanilla first), so "New patch" opens at once.
    List<BaseGalaxy.Info>? _installed;
    readonly Dictionary<string, Task<BaseGalaxy>> _galaxyCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Installed galaxies (vanilla first); null while the game files are read.</summary>
    List<BaseGalaxy.Info>? InstalledGalaxies => _installed;

    async void PreloadGalaxies()
    {
        var g = Game; var t = Texts;
        _installed = null; _galaxyCache.Clear();
        if (g == null || t == null) return;
        var list = await Task.Run(() => BaseGalaxy.Installed(g));
        if (g != Game) return;                                         // game files read again meanwhile
        _installed = list;
        RefreshStartMenu();
        foreach (var info in list)                                    // one after the other: the archives are read by one reader
        {
            var task = Task.Run(() => BaseGalaxy.Read(g, info, t));
            _galaxyCache[info.Macro] = task;
            try { await task; } catch (Exception e) { DebugLog.Add($"galaxy {info.Macro} not readable: {e.Message}"); }
            if (g != Game) return;
            RefreshStartMenu();
        }
    }

    /// <summary>The installed galaxy read (from the cache, else now).</summary>
    Task<BaseGalaxy> ReadGalaxy(BaseGalaxy.Info info)
    {
        if (!_galaxyCache.TryGetValue(info.Macro, out var task) || task.IsFaulted)
        {
            var g = Game!; var t = Texts!;
            _galaxyCache[info.Macro] = task = Task.Run(() => BaseGalaxy.Read(g, info, t));
        }
        return task;
    }

    // ---------------------------------------------------------------------------------------------- the menu
    // A compact main page; "New patch", "Recent projects" and "Mods from other folders" are sub-pages with a Back button.
    string _menuPage = "main";

    public void ShowStartMenu()
    {
        LeaveReadOnly();
        CloseStartMenu();
        var layer = new CanvasLayer { Layer = 9 }; AddChild(layer); _startMenu = layer;
        var bg = new ColorRect { Color = new Color(0.03f, 0.04f, 0.06f, 0.94f) }; bg.SetAnchorsPreset(LayoutPreset.FullRect); layer.AddChild(bg);
        var centre = new CenterContainer(); centre.SetAnchorsPreset(LayoutPreset.FullRect); layer.AddChild(centre);
        var panel = new PanelContainer { CustomMinimumSize = new Vector2(520, 0) }; centre.AddChild(panel);
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color("1b2028"), BorderColor = new Color("3a4350"), BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1, CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8, CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8 });
        var m = new MarginContainer(); foreach (var side in new[] { "left", "right", "top", "bottom" }) m.AddThemeConstantOverride("margin_" + side, 20);
        // scrolls only if the window is very small
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, VerticalScrollMode = ScrollContainer.ScrollMode.Auto };
        panel.AddChild(scroll); m.SizeFlagsHorizontal = SizeFlags.ExpandFill; scroll.AddChild(m);
        var box = new VBoxContainer(); box.AddThemeConstantOverride("separation", 6); m.AddChild(box);
        // the panel as high as its content, at most the window
        m.Resized += () => scroll.CustomMinimumSize = new Vector2(480, Math.Min(m.GetCombinedMinimumSize().Y, GetViewport().GetVisibleRect().Size.Y - 80));

        var title = new Label { Text = AppName, HorizontalAlignment = HorizontalAlignment.Center }; title.AddThemeFontSizeOverride("font_size", 24); box.AddChild(title);
        box.AddChild(new Label { Text = $"{Version}   ·   M.R. Reese", HorizontalAlignment = HorizontalAlignment.Center, Modulate = new Color(1, 1, 1, 0.5f) });
        box.AddChild(new HSeparator());

        // a menu entry: button (15 pt), a short grey state on its right (e.g. "3 installed"); the explanation is the tooltip
        Button Entry(string text, string state, Action a, string tooltip)
        {
            var b = new Button { Text = text, Alignment = HorizontalAlignment.Left, CustomMinimumSize = new Vector2(0, 34), TooltipText = tooltip };
            b.AddThemeFontSizeOverride("font_size", 15);
            b.Pressed += a; box.AddChild(b);
            if (state != "")
            {
                // inside the button, right-aligned; clicks go through to the button
                var l = new Label { Text = state, Modulate = new Color(1, 1, 1, 0.45f), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
                l.AddThemeFontSizeOverride("font_size", 12);
                l.SetAnchorsPreset(LayoutPreset.FullRect); l.OffsetRight = -10;
                b.AddChild(l);
            }
            return b;
        }
        Button Item(string text, Action a, string tooltip = "")
        {
            var b = new Button { Text = text, Alignment = HorizontalAlignment.Left, Flat = true, TooltipText = tooltip, ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
            b.AddThemeFontSizeOverride("font_size", 14);
            b.Pressed += a; box.AddChild(b); return b;
        }
        void Page(string page) { _menuPage = page; ShowStartMenu(); }
        // sub-page title with Back; the page's explanation shows on hover (ⓘ)
        void Heading(string text, string? tip = null)
        {
            var row = new HBoxContainer(); box.AddChild(row);
            var back = new Button { Text = "◀ Back", Flat = true }; back.Pressed += () => Page("main"); row.AddChild(back);
            var l = new Label { Text = text + (tip != null ? "  ⓘ" : ""), VerticalAlignment = VerticalAlignment.Center, TooltipText = tip ?? "",
                                MouseFilter = tip != null ? Control.MouseFilterEnum.Stop : Control.MouseFilterEnum.Ignore };
            l.AddThemeFontSizeOverride("font_size", 17); row.AddChild(l);
        }
        void Note(string text)
        {
            var l = new Label { Text = text, Modulate = new Color(1, 1, 1, 0.5f), AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(460, 0) };
            l.AddThemeFontSizeOverride("font_size", 12); box.AddChild(l);
        }
        void AskIfDirty(Action go) { if (_dirty && P.Clusters.Count > 0) Confirm("The open project has unsaved changes. Continue anyway (they are lost)?", go); else go(); }
        bool open = P.Clusters.Count > 0 || P.BaseGalaxy != null;

        switch (_menuPage)
        {
            case "patch":
                Heading("New patch for an installed galaxy", "Your sectors are added to a galaxy that is already installed. It is shown dimmed on the map; you place your clusters in free hexes and connect them to its sectors. Like the DLCs, the new sectors also appear in running games.\n\nA mod's galaxy missing? Main menu: Mods from other folders.");
                if (Game == null || InstalledGalaxies == null) Note("Reading the game files ...");
                else
                    foreach (var info in InstalledGalaxies)
                    {
                        var ext = info.Source == "base" ? null : Game.Vfs.Extensions.All.FirstOrDefault(e => e.FolderName.Equals(info.Source, StringComparison.OrdinalIgnoreCase));
                        var label = info.Source == "base" ? "X4 galaxy (vanilla + your DLCs)" : $"{info.Folder}   (mod: {ext?.Name ?? info.Source}{(ext?.External == true ? ", other folder" : "")})";
                        bool ready = _galaxyCache.TryGetValue(info.Macro, out var t) && t.IsCompletedSuccessfully;
                        int n = ready ? t!.Result.Clusters.Count : 0;
                        var chosen = info;
                        Item($"→ {label}" + (ready ? $"   ·   {n} clusters" : "   ·   reading …"), () => AskIfDirty(() => { NewPatchProject(chosen); _menuPage = "main"; CloseStartMenu(); }), info.File);
                    }
                break;

            case "recent":
                Heading("Recent projects");
                foreach (var f in Recent())
                {
                    var file = f;
                    Item(Path.GetFileName(f), () => AskIfDirty(() => { LoadFile(file); _galaxy.CentreGalaxy(); Refresh(); _menuPage = "main"; CloseStartMenu(); }), f);
                }
                break;

            case "mods":
                Heading("Mods from other folders", "Mods that are not in the game's extensions folder (downloaded, unpacked, your work folder). The editor reads them as switched on: a galaxy of theirs is offered under \"New patch\", their backdrops, fields and objects appear in the lists. The game itself does not see them.");
                var extra = Settings.Load(SettingsFile).ExtraModFolders;
                foreach (var f in extra)
                {
                    var row = new HBoxContainer(); box.AddChild(row);
                    var names = Game?.Vfs.Extensions.All.Where(e => e.External && e.Folder.StartsWith(f, StringComparison.OrdinalIgnoreCase)).Select(e => e.Name).ToList();
                    var l = new Label { Text = (names is { Count: > 0 } ? string.Join(", ", names.Take(3)) + (names.Count > 3 ? " …" : "") : Path.GetFileName(f)), TooltipText = f, ClipText = true, SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Pass };
                    l.AddThemeFontSizeOverride("font_size", 14); row.AddChild(l);
                    var folder = f;
                    var x = new Button { Text = "× remove", Flat = true, TooltipText = "Stop reading this folder" };
                    x.Pressed += () => { var s = Settings.Load(SettingsFile); s.ExtraModFolders.Remove(folder); s.Save(SettingsFile); SetStatus("Reading the game files again ..."); StartGame(s); };
                    row.AddChild(x);
                }
                if (extra.Count == 0) Note("None yet.");
                Item("+ Open a mod folder ...", OpenModFolder, "Choose a mod folder (with content.xml) or a folder that holds several mods.");
                break;

            default:
                if (open) Entry($"Continue: {P.ModName}" + (P.BaseGalaxy != null ? " (patch)" : ""), "", CloseStartMenu, "Where you left off (kept automatically).");
                Entry("New galaxy", "", () => AskIfDirty(() => { NewOwnProject(); CloseStartMenu(); }),
                    "Your own galaxy with its own game start (a full conversion or a small test galaxy).");
                var galaxyState = InstalledGalaxies == null ? "reading …" : $"{InstalledGalaxies.Count} installed";
                Entry("New patch  ▸", galaxyState, () => Page("patch"), "Add sectors to an installed galaxy (vanilla or a mod's).");
                Entry("Open project ...", "", () => { CloseStartMenu(); OpenDialog(); }, "A saved project (*.galaxy.json).");
                var recent = Recent();
                if (recent.Count > 0) Entry("Recent projects  ▸", $"{recent.Count}", () => Page("recent"), string.Join("\n", recent.Take(8).Select(Path.GetFileName)) + (recent.Count > 8 ? "\n…" : ""));
                var mods = Settings.Load(SettingsFile).ExtraModFolders.Count;
                Entry("Mods from other folders  ▸", mods > 0 ? $"{mods} folder{(mods == 1 ? "" : "s")}" : "", () => Page("mods"), "Read a mod that is not in the game's extensions folder.");
                break;
        }

        box.AddChild(new HSeparator());
        var foot = new HBoxContainer(); box.AddChild(foot);
        Btn(foot, "Folders...", () => ShowSetup(Settings.Load(SettingsFile)));
        Btn(foot, "Debug...", ShowDebug);
        foot.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        if (open) Btn(foot, "Close", () => { _menuPage = "main"; CloseStartMenu(); });
    }

    string? _openedMod;

    /// <summary>Menu "Open a mod from another folder": add it to the settings' mod folders and read the game files again.</summary>
    void OpenModFolder()
    {
        var d = new FileDialog { FileMode = FileDialog.FileModeEnum.OpenDir, Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = true, Title = "Folder of the mod (with content.xml)" };
        d.DirSelected += dir =>
        {
            dir = Path.GetFullPath(dir);
            bool one = File.Exists(Path.Combine(dir, "content.xml"));
            bool many = !one && Directory.GetDirectories(dir).Any(x => File.Exists(Path.Combine(x, "content.xml")));
            if (!one && !many) { Alert($"No mod in\n{dir}\n\nA mod folder has a content.xml (or choose a folder whose subfolders are mods)."); return; }
            var ext = Path.Combine(Settings.Load(SettingsFile).GameFolder, "extensions");
            if (Path.GetFullPath(dir).StartsWith(Path.GetFullPath(ext), StringComparison.OrdinalIgnoreCase))
            { Alert("This mod is in the game's extensions folder: the editor reads it already (if it is switched on in the game)."); return; }
            var s = Settings.Load(SettingsFile);
            if (!s.ExtraModFolders.Contains(dir, StringComparer.OrdinalIgnoreCase)) s.ExtraModFolders.Add(dir);
            s.Save(SettingsFile);
            _openedMod = dir; _menuPage = "patch";
            SetStatus("Reading the game files with the mod ...");
            StartGame(s);
        };
        AddChild(d); d.PopupCentered(new Vector2I(900, 600));
    }

    /// <summary>After reading the game files: what the opened mod brought (called by StartGame).</summary>
    void ReportOpenedMod()
    {
        if (_openedMod == null || Game == null) return;
        var dir = _openedMod; _openedMod = null;
        var exts = Game.Vfs.Extensions.All.Where(e => e.External && e.Folder.StartsWith(dir, StringComparison.OrdinalIgnoreCase)).ToList();
        var galaxies = BaseGalaxy.Installed(Game).Where(i => exts.Any(e => e.FolderName.Equals(i.Source, StringComparison.OrdinalIgnoreCase))).ToList();
        var off = exts.Where(e => !e.Enabled).Select(e => $"{e.Name}: {e.EnabledBy}").ToList();
        SetStatus($"Opened {string.Join(", ", exts.Select(e => e.Name).Take(3))}: "
            + (galaxies.Count > 0 ? $"its galaxy is listed under \"New patch\" ({string.Join(", ", galaxies.Select(g => g.Folder))})." : "no galaxy of its own (its backdrops, fields and objects are in the lists).")
            + (off.Count > 0 ? "  NOT READ: " + string.Join("; ", off) : ""));
    }

    void CloseStartMenu() { _startMenu?.QueueFree(); _startMenu = null; }

    /// <summary>Game files read: the menu's patch list can be filled now.</summary>
    void RefreshStartMenu() { if (_startMenu != null) ShowStartMenu(); }

    void NewOwnProject()
    {
        P = new Project(); _undo.Clear(); _redo.Clear(); Sel = null; OpenSectorId = null; _file = null; _dirty = false;
        Base = null; Autosave(); _galaxy.CentreGalaxy(); Refresh();
    }

    void NewPatchProject(BaseGalaxy.Info info)
    {
        P = new Project
        {
            BaseGalaxy = info.Macro, BaseFolder = info.Folder, BaseSource = info.Source,
            ModName = "My sectors", ModId = "my_sectors", Prefix = "ms",
        };
        _undo.Clear(); _redo.Clear(); Sel = null; OpenSectorId = null; _file = null; _dirty = false;
        Autosave(); LoadBase(); Refresh();
    }
}
