using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using X4Data;
using X4Data.Galaxy;

namespace GalaxyEditor3D;

/// <summary>What is selected: a sector (galaxy view) or a gate (sector view).</summary>
public record Selection(string Kind, int Id);

/// <summary>
/// Main window: top bar, galaxy map (2D) or sector view (3D), side panel, status line.
/// Holds the project, undo / redo, selection and the open file. Every edit goes through Change().
/// Command line for checks (after "--"): --open file.json, --sector id, --select gate:id / decor:id, --flipnormals, --debugreport (with --screenshot), --debugwindow, --palette group, --menu, --patch galaxymacro, --setup,
/// --view pitch,yaw, --play musicid, --screenshot file.png.
/// </summary>
public partial class App : Control
{
    /// <summary>Visible name and version (window title, dialogs). The Godot project name stays "X4 Galaxy Editor 3D"
    /// on purpose: it names the user data folder (settings, autosave).</summary>
    public const string AppName = "X4 Map Editor", Version = "0.1 beta";
    public Project P = new();
    public Selection? Sel;
    public int? OpenSectorId;                       // null = galaxy view
    public string Tool = "select";                  // galaxy: select / connect (connect = pick the target for ConnectFrom)
    public int? ConnectFrom;                        // sector whose "Connect to ..." (right-click menu) waits for a target
    public GameData? Game;                          // null until the game files are read (or no settings yet)
    public X4ToGodot? Conv;
    /// <summary>Backdrops the installed game has (base + DLCs + mods), empty until the game files are read.</summary>
    public List<X4Data.Space.Backdrops.Entry> Backdrops = new();
    public MusicPlayer Music = null!;
    /// <summary>Decoration palette (fields + objects) and the game's texts, null until the game files are read.</summary>
    public X4Data.Space.DecorCatalog? Decor;
    /// <summary>Factions and station types of the installed game (sector AI section); null until read.</summary>
    public X4Data.Galaxy.FactionCatalog? Factions;
    /// <summary>The game's own starts (start ship with loadout, personalised start options); null until read.</summary>
    public X4Data.Galaxy.StartCatalog? Starts;
    public X4Data.Text.TextDb? Texts;
    public event Action? Changed;                   // views redraw on this

    readonly Stack<string> _undo = new(), _redo = new();
    string? _file;
    bool _dirty;
    string SettingsFile => ProjectSettings.GlobalizePath("user://settings.json");
    string AutosaveFile => ProjectSettings.GlobalizePath("user://autosave.galaxy.json");

    GalaxyView _galaxy = null!;
    SectorView _sector = null!;
    SidePanel _side = null!;
    Label _status = null!, _crumb = null!;
    Button _back = null!;
    string? _screenshot, _paletteGroup, _pendingPatch, _baseSector; bool _voiceShot; int _shotFrames = -1;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        DebugLog.CatchUnhandled();
        DebugLog.Add($"{AppName} {Version} started on {OS.GetName()} {OS.GetVersion()}");
        BuildUi();
        try { if (File.Exists(AutosaveFile)) P = Project.FromJson(File.ReadAllText(AutosaveFile)); } catch { /* broken autosave: start empty */ }

        var args = OS.GetCmdlineUserArgs();
        string? Arg(string n) { int i = Array.IndexOf(args, n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        if (Arg("--open") is { } f) LoadFile(f);
        _screenshot = Arg("--screenshot");
        _galaxy.CentreGalaxy();
        if (Arg("--view") is { } v && v.Split(',') is { Length: 2 } pv)                // screenshot checks: camera pitch,yaw in degrees
            _sector.StartView = (Mathf.DegToRad(float.Parse(pv[0], System.Globalization.CultureInfo.InvariantCulture)), Mathf.DegToRad(float.Parse(pv[1], System.Globalization.CultureInfo.InvariantCulture)));
        if (int.TryParse(Arg("--sector"), out var sid) && P.SectorById(sid) != null) OpenSector(sid);
        if (Arg("--select") is { } sel && sel.Split(':') is { Length: 2 } ps && int.TryParse(ps[1], out var selId)) Sel = new Selection(ps[0], selId);   // screenshot check of the gizmo
        if (Array.IndexOf(args, "--flipnormals") >= 0) X4Shaders.ToggleNormalGreen();
        if (float.TryParse(Arg("--near"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var near)) _sector.NearView = near;
        Refresh();

        var s = Settings.Load(SettingsFile);
        s.Guess();
        if (Array.IndexOf(args, "--setup") >= 0) ShowSetup(s);                    // screenshot check of the folder window
        else if (s.Problems().Count == 0 && (File.Exists(SettingsFile) || _screenshot != null)) StartGame(s);
        else ShowSetup(s);
        if (_screenshot != null) _shotFrames = 90;
        _paletteGroup = Arg("--palette");
        if (Arg("--menupage") is { } mp) _menuPage = mp;
        if (int.TryParse(Arg("--rightclick"), out var rc)) CallDeferred(nameof(RightClickCheck), rc);   // check runs: sector menu
        if (Arg("--connectcheck") is { } cc && cc.Split(',') is { Length: 2 } ccp) CallDeferred(nameof(ConnectCheck), int.Parse(ccp[0]), int.Parse(ccp[1]));   // check runs: gate choice
        if (Arg("--mapzoom") is { } mz && mz.Split(',') is { Length: 3 } mzp) CallDeferred(nameof(MapZoom), float.Parse(mzp[0]), int.Parse(mzp[1]), int.Parse(mzp[2]));   // check runs: px,q,r
        if (Arg("--unfold") is { } uf) SidePanel.Unfold(uf.Split(','));     // check runs: side panel sections shown open                         // check runs: a page of the start menu
        _baseSector = Arg("--basesector");                                    // check runs: open this sector of the patched galaxy read-only
        _voiceShot = Array.IndexOf(args, "--voice") >= 0;                     // check runs: spoken-name window of the selected sector
        if (_paletteGroup != null && _screenshot != null) _shotFrames = 900;   // pictures need time
        // start menu, unless a check run opens something directly (--menu shows it anyway)
        if (Array.IndexOf(args, "--menu") >= 0 || (Arg("--open") == null && Arg("--sector") == null && _screenshot == null && Array.IndexOf(args, "--setup") < 0)) ShowStartMenu();
        if (Arg("--patch") is { } patchGalaxy) _pendingPatch = patchGalaxy;   // check runs: new patch project for this galaxy macro
        if (Array.IndexOf(args, "--debugwindow") >= 0) CallDeferred(nameof(ShowDebug));
        if (Array.IndexOf(args, "--settings") >= 0) _side.ShowProjectSettings();              // screenshot check of the project settings
        if (Array.IndexOf(args, "--clearcheck") >= 0) CallDeferred(nameof(ClearMap));        // screenshot check of the "Are you sure?" box   // screenshot check of the debug window
    }

    // ---------------------------------------------------------------------------------------------- layout
    void BuildUi()
    {
        var bg = new ColorRect { Color = new Color("12161c") }; bg.SetAnchorsPreset(LayoutPreset.FullRect); AddChild(bg);
        var col = new VBoxContainer(); col.SetAnchorsPreset(LayoutPreset.FullRect); col.AddThemeConstantOverride("separation", 0); AddChild(col);

        var top = new PanelContainer(); col.AddChild(top);
        var bar = new HBoxContainer(); bar.AddThemeConstantOverride("separation", 6); top.AddChild(bar);
        // the app's name is in the window title only
        Btn(bar, "Menu", ShowStartMenu, "New galaxy, new patch, open, recent projects");
        var project = new MenuButton { Text = "Project ▾", FocusMode = FocusModeEnum.None, Flat = false, TooltipText = "Open, save, project settings" };
        var pm = project.GetPopup();
        pm.AddItem("Open ...", 0); pm.AddItem("Save", 1); pm.SetItemShortcut(1, Shortcut(Key.S), true); pm.AddItem("Save as ...", 2);
        pm.AddSeparator(); pm.AddItem("Project settings ...", 3);
        pm.IdPressed += id =>
        {
            switch (id)
            {
                case 0: OpenDialog(); break;
                case 1: Save(false); break;
                case 2: Save(true); break;
                case 3: LeaveReadOnly(); Sel = null; _side.ShowProjectSettings(); break;
            }
        };
        bar.AddChild(project);
        bar.AddChild(new VSeparator());
        IconBtn(bar, SaveSvg, () => Save(false), "Save (Ctrl+S)");
        _undoBtn = IconBtn(bar, UndoSvg, Undo, "Undo (Ctrl+Z)"); _redoBtn = IconBtn(bar, RedoSvg, Redo, "Redo (Ctrl+Y)");
        bar.AddChild(new VSeparator());
        _back = Btn(bar, "◀ Galaxy", BackToGalaxy);
        _crumb = new Label { Modulate = new Color(1, 1, 1, 0.6f) }; bar.AddChild(_crumb);
        bar.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        Btn(bar, "Folders...", () => ShowSetup(Settings.Load(SettingsFile)));
        Btn(bar, "Debug...", ShowDebug, "Look checks and a debug report (zip) to send with a bug report.");
        Btn(bar, "Export mod...", ExportMod, "Writes the galaxy as a mod into the export folder (Folders...), subfolder = mod folder name (Project settings).");

        var main = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill }; main.AddThemeConstantOverride("separation", 0); col.AddChild(main);
        var viewArea = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true }; main.AddChild(viewArea);
        _galaxy = new GalaxyView(this); _galaxy.SetAnchorsPreset(LayoutPreset.FullRect); viewArea.AddChild(_galaxy);
        Previews = new Previews(this); AddChild(Previews);           // before the sector view: its palette listens to it
        _sector = new SectorView(this) { Visible = false }; _sector.SetAnchorsPreset(LayoutPreset.FullRect); viewArea.AddChild(_sector);
        var sideScroll = new ScrollContainer { CustomMinimumSize = new Vector2(350, 0), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        main.AddChild(sideScroll);
        _side = new SidePanel(this) { SizeFlagsHorizontal = SizeFlags.ExpandFill }; sideScroll.AddChild(_side);

        Music = new MusicPlayer(); AddChild(Music);
        Music.StateChanged += () => _side.Rebuild();

        var statusBar = new PanelContainer(); col.AddChild(statusBar);
        _status = new Label { ClipText = true, Modulate = new Color(1, 1, 1, 0.7f) }; statusBar.AddChild(_status);
    }

    // Undo / redo: the usual curved arrows, drawn as SVG (no font needs to have the glyph)
    const string UndoSvg = "<svg xmlns='http://www.w3.org/2000/svg' width='20' height='20' viewBox='0 0 24 24' fill='none' stroke='#e6e9ee' stroke-width='2.2' stroke-linecap='round' stroke-linejoin='round'><path d='M9 14 4 9l5-5'/><path d='M4 9h10.5a5.5 5.5 0 0 1 0 11H11'/></svg>";
    const string RedoSvg = "<svg xmlns='http://www.w3.org/2000/svg' width='20' height='20' viewBox='0 0 24 24' fill='none' stroke='#e6e9ee' stroke-width='2.2' stroke-linecap='round' stroke-linejoin='round'><path d='m15 14 5-5-5-5'/><path d='M20 9H9.5a5.5 5.5 0 0 0 0 11H13'/></svg>";
    // Save: a floppy disk; Dice: random choice (side panel)
    const string SaveSvg = "<svg xmlns='http://www.w3.org/2000/svg' width='20' height='20' viewBox='0 0 24 24' fill='none' stroke='#e6e9ee' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M5 3h11.2a2 2 0 0 1 1.4.6l2.8 2.8a2 2 0 0 1 .6 1.4V19a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z'/><path d='M7 21v-7h10v7'/><path d='M7 3v4h8'/></svg>";
    public const string DiceSvg = "<svg xmlns='http://www.w3.org/2000/svg' width='18' height='18' viewBox='0 0 24 24' fill='none' stroke='#e6e9ee' stroke-width='2' stroke-linejoin='round'><rect x='3' y='3' width='18' height='18' rx='3'/><g fill='#e6e9ee' stroke='none'><circle cx='8' cy='8' r='1.6'/><circle cx='16' cy='8' r='1.6'/><circle cx='12' cy='12' r='1.6'/><circle cx='8' cy='16' r='1.6'/><circle cx='16' cy='16' r='1.6'/></g></svg>";
    Button _undoBtn = null!, _redoBtn = null!;

    /// <summary>An icon drawn from SVG text (no font needs to have the glyph).</summary>
    public static Texture2D SvgIcon(string svg) { var img = new Image(); img.LoadSvgFromString(svg, 1f); return ImageTexture.CreateFromImage(img); }

    static Button IconBtn(Container parent, string svg, Action onPress, string tip)
    {
        var b = new Button { Icon = SvgIcon(svg), TooltipText = tip, FocusMode = FocusModeEnum.None, Flat = true };
        b.Pressed += onPress; parent.AddChild(b); return b;
    }
    static Shortcut Shortcut(Key k) => new() { Events = new Godot.Collections.Array { new InputEventKey { Keycode = k, CtrlPressed = true } } };

    static Button Btn(Container parent, string text, Action onPress, string tip = "")
    {
        var b = new Button { Text = text, TooltipText = tip, FocusMode = FocusModeEnum.None };
        b.Pressed += onPress; parent.AddChild(b); return b;
    }

    // ---------------------------------------------------------------------------------------------- state
    /// <summary>Every edit: remember the state before (undo), apply, mark unsaved, autosave, redraw.</summary>
    public void Change(Action edit)
    {
        if (ReadOnly) { ChangeInView(edit); return; }
        _undo.Push(P.ToJson()); if (_undo.Count > 200) TrimUndo();
        _redo.Clear();
        edit();
        _dirty = true; Autosave(); Refresh();
    }

    void TrimUndo() { var keep = _undo.Take(200).Reverse().ToList(); _undo.Clear(); foreach (var k in keep) _undo.Push(k); }

    void Undo() { if (_undo.Count == 0) return; if (ReadOnly) { _redo.Push(_editP!.ToJson()); _editP = Project.FromJson(_undo.Pop()); _dirty = true; Autosave(); RebuildView(); return; } _redo.Push(P.ToJson()); P = Project.FromJson(_undo.Pop()); FixSelection(); _dirty = true; Autosave(); Refresh(); }
    void Redo() { if (_redo.Count == 0) return; if (ReadOnly) { _undo.Push(_editP!.ToJson()); _editP = Project.FromJson(_redo.Pop()); _dirty = true; Autosave(); RebuildView(); return; } _undo.Push(P.ToJson()); P = Project.FromJson(_redo.Pop()); FixSelection(); _dirty = true; Autosave(); Refresh(); }

    void FixSelection()
    {
        if (OpenSectorId != null && P.SectorById(OpenSectorId) == null) { OpenSectorId = null; }
        if (Sel != null && FindSelected() == null) Sel = null;
    }

    /// <summary>The selected sector, gate or decoration object, or null.</summary>
    public object? FindSelected() => Sel?.Kind switch
    {
        "sector" => P.SectorById(Sel.Id),
        "gate" => P.GateById(Sel.Id),
        "decor" => P.DecorById(Sel.Id),
        "station" => P.StationById(Sel.Id),
        "aiowner" or "aipirate" => P.SectorById(Sel.Id)?.Ai,
        _ => null,
    };

    void Autosave() { try { File.WriteAllText(AutosaveFile, EditP.ToJson()); } catch { /* autosave is a convenience */ } }

    public void Refresh()
    {
        bool inSector = OpenSectorId != null;
        _galaxy.Visible = !inSector; _sector.Visible = inSector;
        _undoBtn.Disabled = _undo.Count == 0; _redoBtn.Disabled = _redo.Count == 0;
        _back.Visible = inSector;
        var s = P.SectorById(OpenSectorId);
        _crumb.Text = s != null ? P.ClusterOf(s.Id)!.Name + " / " + s.Name + (ReadOnly ? "   (read-only)" : "") : "";
        GetWindow().Title = (_dirty ? "* " : "") + EditP.ModName + $" - {AppName} {Version}" + (_file != null ? " - " + Path.GetFileName(_file) : "");
        SetStatus((inSector
            ? "SECTOR:  WASD: fly  Q/E: down/up  Shift: faster  Right drag: turn  Left / middle drag on empty space: move the view  Wheel: zoom  F: frame  Click: select  Drag a marker: move it (Shift: height)  Del / right-click: delete  Arrows / rings: move / turn  Right-click: menu  R: turn  N: normal maps  Ctrl+C/X/V  Esc: galaxy"
            : Tool == "connect" ? "GALAXY:  click the sector to connect to (yours or one of the patched galaxy).   Esc: cancel"
            : "GALAXY:  WASD: move  Q/E: zoom  Shift: faster  double-click an empty hex: new cluster   Drag a sector: move its cluster   Double-click a sector: open it   Right-click a sector: connect, open, delete   Wheel: zoom   Right / middle drag: pan") + _gameInfo);
        _side.Rebuild();
        FollowMusic();
        Changed?.Invoke();
    }

    /// <summary>The cluster that is "current": of the open sector, else of the selected sector.</summary>
    public Cluster? CurrentCluster =>
        P.SectorById(OpenSectorId) is { } os ? P.ClusterOf(os.Id) : Sel is { Kind: "sector" } x ? P.ClusterOf(x.Id) : null;

    /// <summary>While music plays, it follows the current cluster's music (silence if the cluster has none).</summary>
    void FollowMusic()
    {
        if (Music?.Current == null) return;
        var c = CurrentCluster;
        if (c == null) return;                                // nothing selected: keep playing
        if (c.Music == "") Music.Stop(); else if (c.Music != Music.Current) Music.Play(c.Music);
    }

    public void SetStatus(string t) { _status.Text = t; DebugLog.Add(t); }
    string _gameInfo = "";                          // appended to the hints once the game files are read
    void RightClickCheck(int sid) => _galaxy.MenuOf(sid);
    void MapZoom(float gs, int q, int r) => _galaxy.ZoomTo(gs, q, r);
    void ConnectCheck(int a, int b) => _galaxy.ConnectCheck(a, b);
    public void SetTool(string t) { Tool = t; if (t != "connect") ConnectFrom = null; Refresh(); }
    /// <summary>Right-click menu "Connect to ...": the next click on a sector (own or of the patched galaxy) connects.</summary>
    public void StartConnect(int sectorId) { ConnectFrom = sectorId; SetTool("connect"); }

    /// <summary>Camera target of the sector view in X4 metres (where new things are put).</summary>
    public (double X, double Y, double Z) SectorViewCentre() => _sector.ViewCentre();

    /// <summary>Warning before anything from another author's mod is used (using another author's work without permission may count as stealing).</summary>
    // ---------------------------------------------------------------------------------------------- decoration palette
    /// <summary>Preview pictures of the palette (rendered in the background).</summary>
    public Previews? Previews;
    /// <summary>The palette item "in the hand": the next click into the sector places it.</summary>
    public X4Data.Space.DecorCatalog.Item? Armed { get; private set; }

    public void Arm(X4Data.Space.DecorCatalog.Item item)
    {
        Armed = item;
        _sector.Palette.ShowArmed();
        Input.SetDefaultCursorShape(Input.CursorShape.Cross);
        SetStatus($"Click into the sector to place: {item.Label}.   Shift + click: place more.   Esc / right click: stop.");
    }

    public void Disarm()
    {
        if (Armed == null) return;
        Armed = null;
        _sector.Palette.ShowArmed();
        Input.SetDefaultCursorShape(Input.CursorShape.Arrow);
        SetStatus("");
    }

    /// <summary>Adds a decoration to the open sector at an X4 position (null = view centre); asks first for another author's item.</summary>
    public void PlaceDecor(X4Data.Space.DecorCatalog.Item item, (double X, double Y, double Z)? at)
    {
        var s = P.SectorById(OpenSectorId);
        if (s == null) return;
        if (item.Kind == "station")             // palette group "Stations": Ref = kind:faction:stations.xml id
        {
            var parts = item.Ref.Split(':');
            var c = at ?? _sector.ViewCentre();
            AddStation(parts[0], parts[1], parts[2] == "" ? null : parts[2], c.X, c.Z);
            return;
        }
        void Place()
        {
            List<string> added = new();
            Change(() =>
            {
                var c = at ?? _sector.ViewCentre();
                var d = P.AddDecor(s, item.Kind, item.Ref, c.X, c.Y, c.Z);
                Sel = new Selection("decor", d.Id);
                // a field with minable wares: the sector also needs resource areas of them, else it gives nothing
                if (item.Kind == "region" && Decor?.Regions.Get(item.Ref) is { } def)
                    added = s.AddResourcesFor(FieldWares(item.Ref), def.R);
            });
            if (added.Count > 0) SetStatus($"Resource areas added for {string.Join(", ", added)} (medium yield): see \"Resources\" of the sector.");
        }
        if (!item.Egosoft) Confirm(ForeignWarning(item.Source), Place); else Place();
    }

    /// <summary>Minable wares of a field (region definition): its asteroids, gases, scrap.</summary>
    public IEnumerable<string> FieldWares(string regionRef) =>
        Decor?.Regions.Get(regionRef) is { } d ? d.Wares : Enumerable.Empty<string>();

    /// <summary>The checks, with the field / resource area check when the game data is read.</summary>
    public List<string> Validate() { var o = P.Validate(Decor != null ? FieldWares : null); o.AddRange(P.ValidateAi(Factions)); return o; }

    public string ForeignWarning(string folder)
    {
        var ext = Game?.Vfs.Extensions.All.FirstOrDefault(e => e.FolderName.Equals(folder, StringComparison.OrdinalIgnoreCase));
        return $"This comes from the mod \"{ext?.Name ?? folder}\"{(ext != null ? $" ({ext.Id})" : "")}, not from Egosoft.\n\n"
             + "Your galaxy will then NEED that mod: players without it see nothing there.\n"
             + "Copying its files into your own mod without the author's permission is not allowed and can count as stealing.\n\n"
             + "Use it anyway?";
    }

    public void OpenSector(int sid) { OpenSectorId = sid; Sel = null; Tool = "select"; ConnectFrom = null; Refresh(); _sector.ShowSector(); }
    public void BackToGalaxy() { if (LeaveReadOnly()) return; var sid = OpenSectorId; OpenSectorId = null; Sel = sid != null ? new Selection("sector", sid.Value) : null; Refresh(); }

    public void DeleteSelection()
    {
        if (Sel == null) return;
        if (Sel.Kind == "sector" && P.SectorById(Sel.Id) is { } s)
            Confirm($"Delete sector \"{s.Name}\" with its gates?", () => Change(() => { P.DeleteSector(s.Id); Sel = null; }));
        else if (Sel.Kind == "gate" && P.GateById(Sel.Id) is { } g)
            Change(() => { var gs = P.SectorOfGate(g.Id)!; P.Unlink(g); gs.Gates.Remove(g); Sel = null; });
        else if (Sel.Kind == "decor" && P.DecorById(Sel.Id) is { } d)
            Change(() => { P.UnlinkDecor(d); P.SectorOfDecor(d.Id)!.Decor.Remove(d); Sel = null; });
        else if (Sel.Kind == "station" && P.StationById(Sel.Id) is { } st)
            Change(() => { P.SectorOfStation(st.Id)!.Stations.Remove(st); Sel = null; });
        // the sector AI's stations: deleting one = no owner / no pirate station (as the dropdowns of "AI and factions")
        else if (Sel.Kind is "aiowner" or "aipirate" && P.SectorById(Sel.Id) is { Ai: { } ai } sa && Sel.Kind is var kind)
            Change(() =>
            {
                if (kind == "aiowner") { ai.Owner = null; ai.OwnerAt = null; } else { ai.PirateStation = ai.PirateFaction = null; ai.PirateAt = null; }
                if (ai.IsDefault) sa.Ai = null;
                Sel = null;
            });
    }

    // ---------------------------------------------------------------------------------------------- stations
    /// <summary>A faction's name from the game files (its id until they are read).</summary>
    public string FactionName(string id) => Factions?.Get(id)?.Name ?? id;
    public string WareName(string id) => Factions?.WareNames.GetValueOrDefault(id) ?? id;

    /// <summary>Facing of a gate, station or AI station (degrees), for the gizmo's ring.</summary>
    public double YawOf(string kind, int id) => kind switch
    {
        "gate" => P.GateById(id)?.Yaw ?? 0,
        "decor" => P.DecorById(id)?.Yaw ?? 0,
        "station" => P.StationById(id)?.Yaw ?? 0,
        "aiowner" => P.SectorById(id) is { } s ? GalaxyOps.AiPlaces(s).Owner.Yaw : 0,
        "aipirate" => P.SectorById(id) is { } s2 ? GalaxyOps.AiPlaces(s2).Pirate.Yaw : 0,
        _ => 0,
    };

    /// <summary>Inside Change(): a station / AI station moved or turned in 3D (yaw null = keep). AI stations keep the place from now on.</summary>
    public void PlaceStation(string kind, int id, double x, double y, double z, double? yaw)
    {
        if (kind == "station" && P.StationById(id) is { } st) { st.X = x; st.Y = y; st.Z = z; if (yaw is { } w) st.Yaw = GalaxyOps.NormYaw(w); }
        else if (kind is "aiowner" or "aipirate" && P.SectorById(id) is { Ai: { } ai } s)
        {
            var (own, pir) = GalaxyOps.AiPlaces(s);
            var at = kind == "aiowner" ? own : pir;
            var spot = new Spot { X = x, Y = y, Z = z, Yaw = GalaxyOps.NormYaw(yaw ?? at.Yaw) };
            if (kind == "aiowner") ai.OwnerAt = spot; else ai.PirateAt = spot;
        }
    }

    /// <summary>
    /// New station in the open sector at x / z (X4 metres). Owner: the sector's owner if it has that kind, else the first
    /// faction that has; pirate stations: the first pirate station of the game; factories: the owner's first product.
    /// </summary>
    /// <summary>
    /// The palette group "Stations": one item per kind and faction that has it in the game (pirate stations: per station),
    /// added to the decoration catalogue so the palette shows them like the decorations (picture = StationPicture).
    /// </summary>
    void AddStationItems()
    {
        if (Decor == null || Factions == null) return;
        Decor.Items.RemoveAll(i => i.Kind == "station");
        foreach (var (kind, label, _) in GalaxyOps.StationKinds)
            foreach (var (faction, sref, _) in StationOwners(kind))
            {
                var src = Factions.Get(faction)?.Source ?? "base";
                string what = kind != "piratebase" ? label.ToLowerInvariant()
                    : Factions.Stations.FirstOrDefault(t => t.Id == sref)?.Tag switch { "piratedepot" => "pirate depot", "anarchyport" => "anarchy port", _ => "pirate base" };
                Decor.Items.Add(new X4Data.Space.DecorCatalog.Item("Station", "station", $"{kind}:{faction}:{sref}", $"{FactionName(faction)} {what}", src,
                    src == "base" || src.StartsWith("ego_", StringComparison.OrdinalIgnoreCase)));
            }
    }

    public void AddStation(string kind, double x, double z)
    {
        if (ReadOnly || P.SectorById(OpenSectorId) is not { } s) return;
        var owners = StationOwners(kind);
        string faction = s.Ai?.Owner is { } o && owners.Any(t => t.Faction == o) ? o : owners.FirstOrDefault().Faction ?? "argon";
        AddStation(kind, faction, kind == "piratebase" ? owners.FirstOrDefault(t => t.Faction == faction).Ref : null, x, z);
    }

    /// <summary>New station of this kind and owner in the open sector (factory: the owner's first product).</summary>
    public void AddStation(string kind, string faction, string? sref, double x, double z)
    {
        if (ReadOnly || P.SectorById(OpenSectorId) is not { } s) return;
        Change(() =>
        {
            var st = P.AddStation(s, kind, faction, x, z);
            if (kind == "piratebase") st.Ref = sref;
            if (kind == "factory") st.Ware = Factions?.ProductWares.GetValueOrDefault(faction)?.FirstOrDefault() ?? "energycells";
            Sel = new Selection("station", st.Id);
        });
    }

    /// <summary>
    /// Who can own a station of this kind (from the game files): factions with a station of that tag, factions with
    /// factories (factory), pirate stations with their faction (piratebase: Ref = stations.xml id). Game not read: the editor's list.
    /// </summary>
    public List<(string Faction, string? Ref, string Label)> StationOwners(string kind)
    {
        var fc = Factions;
        if (fc == null) return GalaxyFacts.Factions.Keys.Select(f => (f, (string?)null, f)).ToList();
        bool Usable(string f) => fc.Get(f) is { Active: true, Hidden: false } && f != "player";
        if (kind == "piratebase")
            return fc.PirateStations.Select(t => (t.Faction, (string?)t.Id, $"{FactionName(t.Faction)}: {t.Tag switch { "piratedepot" => "depot", "anarchyport" => "anarchy port", _ => "base" }} ({t.Id})")).ToList();
        if (kind == "factory")
            return fc.ProductWares.Keys.Where(Usable).OrderBy(FactionName).Select(f => (f, (string?)null, FactionName(f))).ToList();
        var tag = GalaxyOps.StationKinds.First(k => k.Kind == kind).Tag;
        return fc.Stations.Where(t => t.Tag == tag && Usable(t.Faction)).Select(t => t.Faction).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(FactionName).Select(f => (f, (string?)null, FactionName(f))).ToList();
    }

    // ---------------------------------------------------------------------------------------------- read-only sectors
    Project? _editP;                                // the project while a sector of the patched galaxy is shown
    /// <summary>A sector of the galaxy you patch is open: P is a throw-away view of it, every change is refused.</summary>
    public bool ReadOnly => _editP != null;
    /// <summary>The project being edited (also while a read-only sector is shown).</summary>
    public Project EditP => _editP ?? P;

    /// <summary>Shows a sector of the patched galaxy in the 3D view, read-only (BaseGalaxy.ReadOnlyView).</summary>
    public void OpenBaseSector(BaseGalaxy.ClusterInfo c, BaseGalaxy.SectorInfo s)
    {
        if (Base == null) return;
        Disarm();
        var view = Base.ReadOnlyView(c, s, EditP);
        _editP ??= P;
        _roCluster = c; _roSector = s;
        P = view;
        OpenSectorId = view.Clusters[0].Sectors[0].Id; Sel = null; Tool = "select";
        Refresh(); _sector.ShowSector();
        SetStatus($"{s.Name} ({(s.Source == "base" ? "vanilla" : s.Source)}), read-only: gates, fields, resources, backdrop. Stations, wrecks and data vaults are not shown."
            + (view.ViewBaseGates.Count > 0 ? "   Your patch's new gate CAN be moved: drag it, or select it for arrows / ring." : "") + "   Esc: back");
    }

    BaseGalaxy.ClusterInfo? _roCluster; BaseGalaxy.SectorInfo? _roSector;

    /// <summary>Opens the patched sector of one of our base gates in 3D with that gate selected (to move it).</summary>
    public void OpenBaseGate(BaseGate bg)
    {
        var hit = Base?.Clusters.SelectMany(c => c.Sectors.Select(s => (c, s))).FirstOrDefault(t => t.s.Macro.Equals(bg.SectorMacro, StringComparison.OrdinalIgnoreCase));
        if (hit?.s == null) { SetStatus($"{bg.SectorName} is not shown: the galaxy you patch is not read yet."); return; }
        OpenBaseSector(hit.Value.c, hit.Value.s);
        var vid = P.ViewBaseGates.FirstOrDefault(kv => kv.Value == bg.Id).Key;
        if (vid != 0) { Sel = new Selection("gate", vid); Refresh(); }
    }

    /// <summary>Can this item of the open sector be moved? In a read-only sector only the new gates of our patch.</summary>
    public bool Movable(string kind, int id) => !ReadOnly || (kind == "gate" && P.ViewBaseGates.ContainsKey(id));

    /// <summary>The read-only view built again from the edited project (after a move, undo, redo); selection and camera stay.</summary>
    void RebuildView()
    {
        if (!ReadOnly || Base == null || _roCluster == null || _roSector == null) return;
        P = Base.ReadOnlyView(_roCluster, _roSector, _editP!);
        if (Sel != null && P.GateById(Sel.Id) == null && P.DecorById(Sel.Id) == null) Sel = null;
        Refresh();
    }

    /// <summary>
    /// A change while a patched sector is shown: run it on the view, keep what it did to OUR new gates (position, facing:
    /// one undo step of the project), refuse everything else. The view is then built again from the project.
    /// </summary>
    void ChangeInView(Action edit)
    {
        var before = P.ToJson();
        edit();
        var after = Project.FromJson(P.ToJson());
        var orig = Project.FromJson(before);
        var moves = new List<(int baseId, double X, double Y, double Z, double Yaw)>();
        foreach (var (vid, bid) in P.ViewBaseGates)
            if (after.GateById(vid) is { } g && orig.GateById(vid) is { } o)
            {
                if (g.X != o.X || g.Y != o.Y || g.Z != o.Z || g.Yaw != o.Yaw) moves.Add((bid, g.X, g.Y, g.Z, g.Yaw));   // copied: g is reset below
                g.X = o.X; g.Y = o.Y; g.Z = o.Z; g.Yaw = o.Yaw;               // to see if anything ELSE changed
            }
        bool other = after.ToJson() != before;
        if (moves.Count > 0)
        {
            _undo.Push(_editP!.ToJson()); if (_undo.Count > 200) TrimUndo();
            _redo.Clear();
            foreach (var (bid, x, y, z, yaw) in moves)
                if (_editP.BaseGateById(bid) is { } bg) { bg.X = x; bg.Y = y; bg.Z = z; bg.Yaw = GalaxyOps.NormYaw(yaw); }
            _dirty = true; Autosave();
        }
        if (other) SetStatus("Read-only: this sector belongs to the galaxy you patch; only your patch's new gates can be moved. Ctrl+C copies a field to paste it into your own sector.");
        RebuildView();
    }

    /// <summary>Back from a read-only sector to the project (galaxy view). False if none was open.</summary>
    bool LeaveReadOnly()
    {
        if (_editP == null) return false;
        P = _editP; _editP = null;
        OpenSectorId = null; Sel = null;
        Refresh();
        return true;
    }

    /// <summary>The spoken-name chooser next to a cluster / sector name.</summary>
    public void ShowVoicePicker(string? currentRef, Action<string, string?> use)
    {
        var w = new VoicePicker(this, currentRef, use);
        AddChild(w);
        w.PopupCentered();
    }

    /// <summary>
    /// Question box with the buttons "No" (left, the default: Enter / Esc / close = No) and "Yes" (right).
    /// The dialog's own OK button is the "No"; "Yes" is an extra button placed right of it.
    /// </summary>
    public void Confirm(string text, Action yes, Action? no = null)
    {
        var d = new AcceptDialog { DialogText = text, Title = "Please confirm", OkButtonText = "No", DialogAutowrap = true, MinSize = new Vector2I(460, 0) };
        d.AddButton("Yes", true, "yes");
        d.CustomAction += a => { if (a != "yes") return; d.QueueFree(); yes(); };
        d.Confirmed += () => { d.QueueFree(); no?.Invoke(); };
        d.Canceled += () => { d.QueueFree(); no?.Invoke(); };
        AddChild(d); d.PopupCentered();
    }

    // ---------------------------------------------------------------------------------------------- clear
    const string Contents = "gates, fields, objects, resource areas and AI settings";

    /// <summary>Sector view, right-click: empties the open sector (name and settings stay).</summary>
    public void ClearSector(int sid)
    {
        if (ReadOnly || P.SectorById(sid) is not { } s) return;
        Confirm($"This will delete all {Contents} of sector \"{s.Name}\".\nIts gate connections to other sectors are cut as well.\n\nAre you sure?",
            () => Change(() => { P.ClearSector(P.SectorById(sid)!); Sel = null; }));
    }

    /// <summary>Galaxy, right-click: empties every sector; clusters, names and places on the map stay.</summary>
    public void ClearAllSectors()
    {
        int n = P.AllSectors().Count();
        if (n == 0) return;
        Confirm($"This will delete all {Contents} of all {n} sectors.\nAll gate connections are gone. Clusters, names and their places on the map stay.\n\nAre you sure?",
            () => Change(() => { foreach (var s in P.AllSectors().ToList()) P.ClearSector(s); Sel = null; }));
    }

    /// <summary>Galaxy, right-click: removes all clusters and sectors (project settings stay).</summary>
    public void ClearMap()
    {
        if (P.Clusters.Count == 0) return;
        Confirm($"This will delete all {P.Clusters.Count} clusters with all {P.AllSectors().Count()} sectors: the map is empty afterwards.\nProject settings stay.\n\nAre you sure?",
            () => Change(() => { P.ClearMap(); Sel = null; OpenSectorId = null; }));
    }

    void Alert(string text) { var d = new AcceptDialog { DialogText = text, Title = AppName }; d.Confirmed += d.QueueFree; AddChild(d); d.PopupCentered(); }

    // ---------------------------------------------------------------------------------------------- files
    void NewProject()
    {
        LeaveReadOnly();
        void Do() { P = new Project(); _undo.Clear(); _redo.Clear(); Sel = null; OpenSectorId = null; _file = null; _dirty = false; Autosave(); _galaxy.CentreGalaxy(); Refresh(); }
        if (P.Clusters.Count == 0 || !_dirty) Do(); else Confirm("Start a new, empty galaxy? Unsaved changes are lost.", Do);
    }

    void OpenDialog()
    {
        LeaveReadOnly();
        var d = new FileDialog { FileMode = FileDialog.FileModeEnum.OpenFile, Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = true, Filters = new[] { "*.json ; Galaxy project" } };
        d.FileSelected += f => { LoadFile(f); _galaxy.CentreGalaxy(); Refresh(); };
        AddChild(d); d.PopupCentered(new Vector2I(900, 600));
    }

    void LoadFile(string f)
    {
        try { P = Project.FromJson(File.ReadAllText(f)); }
        catch (Exception e) { Alert(e.Message); return; }
        _file = f; _undo.Clear(); _redo.Clear(); Sel = null; OpenSectorId = null; _dirty = false; Autosave();
        AddRecent(f); LoadBase();
    }

    void Save(bool ask)
    {
        LeaveReadOnly();
        if (_file == null || ask)
        {
            var d = new FileDialog { FileMode = FileDialog.FileModeEnum.SaveFile, Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = true,
                                     Filters = new[] { "*.json ; Galaxy project" }, CurrentFile = P.ModId + ".galaxy.json" };
            d.FileSelected += f => { _file = f; Save(false); };
            AddChild(d); d.PopupCentered(new Vector2I(900, 600));
            return;
        }
        try { File.WriteAllText(_file, P.ToJson()); _dirty = false; AddRecent(_file); Refresh(); SetStatus("Project saved: " + _file); }
        catch (Exception e) { Alert("Saving failed: " + e.Message); }
    }

    // ---------------------------------------------------------------------------------------------- mod export
    /// <summary>
    /// Export: open checks -> ask; folder refused if another author's (no marker file); existing export -> ask;
    /// then write (X4Data.Galaxy.ModExport) and say how to start it in the game.
    /// </summary>
    void ExportMod()
    {
        LeaveReadOnly();
        if (P.BaseGalaxy != null && Base == null)
        { Alert("This patch adds to a galaxy that is not read yet (game files still loading, or its DLC / mod is missing)."); return; }
        var issues = Validate();
        if (issues.Count > 0)
            Confirm("There are open checks:\n\n- " + string.Join("\n- ", issues.Take(8)) + (issues.Count > 8 ? "\n..." : "") + "\n\nExport anyway?", ExportPage);
        else ExportPage();
    }

    /// <summary>The text page must be free: two files with the same page id overwrite each other's texts in the game.</summary>
    void ExportPage()
    {
        var who = PageOwners();
        if (who.Count > 0)
            Confirm($"Text page {P.TextPage} is already used by: {string.Join(", ", who)}.\nThe sector names would overwrite (or be overwritten by) those texts. Choose another page in the project settings.\n\nExport anyway?", ExportStep2);
        else ExportStep2();
    }

    /// <summary>Who else uses the project's text page (base game or other mods; this project's own earlier export does not count). Empty = free or game not read.</summary>
    public List<string> PageOwners()
    {
        if (Game == null) return new();
        var used = X4Data.Text.TextPages.Used(Game.Vfs, e => e.FolderName.Equals(P.ModId, StringComparison.OrdinalIgnoreCase));
        return used.TryGetValue(P.TextPage, out var who) ? who.ToList() : new();
    }

    void ExportStep2()
    {
        var s = Settings.Load(SettingsFile);
        if (s.ExportFolder == "" || !Directory.Exists(s.ExportFolder))
        { Alert($"The export folder \"{s.ExportFolder}\" does not exist. Set it under \"Folders...\" (usually the game's \"extensions\" folder)."); return; }
        var folder = Path.Combine(s.ExportFolder, P.ModId);
        if (X4Data.Galaxy.ModExport.CheckFolder(folder) is { } why) { Alert(why); return; }
        if (File.Exists(Path.Combine(folder, X4Data.Galaxy.ModExport.MarkerFile)))
            Confirm($"\"{folder}\" holds an earlier export of this editor. Replace it?", () => ExportWrite(folder));
        else ExportWrite(folder);
    }

    void ExportWrite(string folder)
    {
        // what the galaxy needs from DLCs / other mods: becomes the mod's dependency list
        var deps = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        string IdOf(string source) =>
            Game?.Vfs.Extensions.LoadOrder.FirstOrDefault(e => e.FolderName.Equals(source, StringComparison.OrdinalIgnoreCase))?.Id ?? source;
        foreach (var c in P.Clusters)
        {
            var b = Backdrops.FirstOrDefault(e => e.Component.Equals(c.BackdropComponent(), StringComparison.OrdinalIgnoreCase));
            if (b != null) { if (b.Source != "base") deps.Add(IdOf(b.Source)); }
            else if (Game != null) missing.Add($"{c.Name}: backdrop {c.BackdropComponent()}");
        }
        foreach (var d in P.AllSectors().SelectMany(x => x.Decor))
        {
            var item = Decor?.Get(d.Kind, d.Ref);
            if (item != null) { if (item.Source != "base") deps.Add(IdOf(item.Source)); }
            else if (Decor != null) missing.Add($"{P.SectorOfDecor(d.Id)?.Name}: {d.Ref}");
        }
        var opts = new X4Data.Galaxy.ModExport.Options
        {
            ObjectClass = m => Game?.Macro(m)?.Attribute("class")?.Value,
            Dependencies = deps,
            Base = Base,
            Factions = Factions,
            Starts = Starts,
            Script = f => Game == null ? null : X4Data.Xml.GameXml.Load(Game.Vfs, f),
            // the game file defining a macro, spelled as in the archive (Linux cares about upper / lower case)
            MacroFile = m => Game?.MacroFile(m) is { } f ? Game.Vfs.Get(f)?.Path ?? f : null,
        };
        var files = X4Data.Galaxy.ModExport.Build(P, opts);
        try { X4Data.Galaxy.ModExport.Write(folder, files); }
        catch (Exception e) { Alert("Export failed: " + e.Message); return; }
        SetStatus($"Exported {files.Count} files to {folder}");
        var text = P.BaseGalaxy != null
            ? $"Exported {files.Count} files to\n{folder}\n\nIn the game: Extensions -> enable \"{P.ModName}\" -> restart. Your sectors join the galaxy: in a new game, and (like a DLC) in a saved game when it is loaded."
            : $"Exported {files.Count} files to\n{folder}\n\nIn the game: Extensions -> enable \"{P.ModName}\" -> restart -> New Game -> \"{P.ModName}\".";
        if (deps.Count > 0) text += "\n\nThe mod needs: " + string.Join(", ", deps) + " (written as dependencies).";
        if (Game == null) text += "\n\nThe game files were not read: DLC dependencies could not be checked.";
        if (opts.Notes.Count > 0) text += "\n\nNotes:\n- " + string.Join("\n- ", opts.Notes.Take(8));
        if (missing.Count > 0) text += "\n\nNot found in the installed game (the game will skip them):\n- " + string.Join("\n- ", missing.Take(8));
        Alert(text);
    }

    // ---------------------------------------------------------------------------------------------- game data
    void ShowSetup(Settings s)
    {
        var layer = new CanvasLayer { Layer = 10 }; AddChild(layer);
        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.6f) }; dim.SetAnchorsPreset(LayoutPreset.FullRect); layer.AddChild(dim);
        var panel = new SetupPanel(s);
        panel.Done += st => { st.Save(SettingsFile); CloseWindow(layer); StartGame(st); };
        panel.Cancelled += () =>
        {
            CloseWindow(layer);
            if (Game == null) SetStatus("No game files read: gates are shown as plain rings. \"Folders...\" opens the folder window again.");
        };
        var centre = new CenterContainer(); centre.SetAnchorsPreset(LayoutPreset.FullRect); layer.AddChild(centre);
        centre.AddChild(panel);
        OpenWindow(layer, layer.QueueFree);
    }

    // ---------------------------------------------------------------------------------------------- windows
    // Every window / overlay the app opens is listed here with its close action, newest last.
    // Esc closes the newest one first (Godot's own dialogs close themselves on Esc: they are separate windows).
    readonly List<(object owner, Action close)> _windows = new();

    public void OpenWindow(object owner, Action close)
    {
        _windows.RemoveAll(w => w.owner == owner);
        _windows.Add((owner, close));
    }

    /// <summary>Closes the window of this owner (runs its close action once).</summary>
    public void CloseWindow(object owner)
    {
        int i = _windows.FindLastIndex(w => w.owner == owner);
        if (i < 0) return;
        var close = _windows[i].close;
        _windows.RemoveAt(i);
        close();
    }

    /// <summary>Forget a window that closed itself (no close action run).</summary>
    public void WindowClosed(object owner) => _windows.RemoveAll(w => w.owner == owner);

    /// <summary>Esc with a window open: close the front one, before any text field or view sees the key.</summary>
    public override void _Input(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape } && _windows.Count > 0)
        {
            CloseWindow(_windows[^1].owner);
            GetViewport().SetInputAsHandled();
        }
    }

    async void StartGame(Settings s)
    {
        SetStatus("Reading the game files ...");
        try
        {
            Game = await Task.Run(() => new GameData(s));
            Backdrops = await Task.Run(() => X4Data.Space.Backdrops.List(Game));
            Texts = await Task.Run(() => new X4Data.Text.TextDb(Game, 44));
            Decor = await Task.Run(() => new X4Data.Space.DecorCatalog(Game, Texts));
            Factions = await Task.Run(() => new X4Data.Galaxy.FactionCatalog(Game, Texts));
            Starts = await Task.Run(() => new X4Data.Galaxy.StartCatalog(Game, Texts));
            AddStationItems();
            Music.SetGame(Game);
            Conv = new X4ToGodot(Game);
            if (OS.GetCmdlineUserArgs() is var a && Array.IndexOf(a, "--play") is var pi && pi >= 0 && pi + 1 < a.Length) Music.Play(a[pi + 1]);   // check of the music
            Refresh();
            _gameInfo = $"      |  Game files: {Game.Vfs.Count:N0} files, {Game.Vfs.Extensions.LoadOrder.Count} extensions";
            Refresh();
            if (OpenSectorId != null) _sector.ShowSector();
            _sector.Palette.Rebuild();
            PreloadGalaxies();
            LoadBase();
            ReportOpenedMod();
            RefreshStartMenu();
            if (_pendingPatch != null && BaseGalaxy.Installed(Game).FirstOrDefault(i => i.Macro.Equals(_pendingPatch, StringComparison.OrdinalIgnoreCase)) is { } pp)
            { _pendingPatch = null; NewPatchProject(pp); }
            if (_paletteGroup != null) _sector.Palette.OpenGroup(_paletteGroup);
        }
        catch (Exception e) { SetStatus("Game files could not be read: " + e.Message); }
    }

    // ---------------------------------------------------------------------------------------------- keys, screenshot
    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (e is not InputEventKey k || !k.Pressed || k.Echo) return;
        if (k.CtrlPressed && k.Keycode == Key.Z) Undo();
        else if (k.CtrlPressed && k.Keycode == Key.Y) Redo();
        else if (k.CtrlPressed && k.Keycode == Key.S) Save(false);
        else if (k.Keycode == Key.Delete) DeleteSelection();
        else if (k.Keycode == Key.Escape && Armed != null) Disarm();
        else if (k.Keycode == Key.Escape) { if (Tool != "select") SetTool("select"); else if (OpenSectorId != null) BackToGalaxy(); }
        else if (k.CtrlPressed && k.Keycode == Key.C) Copy();
        else if (k.CtrlPressed && k.Keycode == Key.X) Cut();
        else if (k.CtrlPressed && k.Keycode == Key.V && OpenSectorId != null) { var c = _sector.ViewCentre(); Paste(c.X, c.Z); }
        else if (k.Keycode == Key.N && !k.CtrlPressed && OpenSectorId != null)
        {
            // which way the normal maps' green channel points: compare the hull bumps, keep the one that looks right
            X4Shaders.ToggleNormalGreen();
            SetStatus(X4Shaders.FlipNormalGreen ? "Normal maps: green flipped (DirectX style). N switches back." : "Normal maps: green as stored (OpenGL style). N switches.");
        }
        else if (k.Keycode == Key.R && Sel?.Kind == "gate" && P.GateById(Sel.Id) is { } g)
            Change(() => g.Yaw = GalaxyOps.NormYaw(g.Yaw + (k.ShiftPressed ? -15 : 15)));
        else if (k.Keycode == Key.R && Sel?.Kind == "decor" && P.DecorById(Sel.Id) is { } d)
            Change(() => d.Yaw = GalaxyOps.NormYaw(d.Yaw + (k.ShiftPressed ? -15 : 15)));
        else if (k.Keycode == Key.R && Sel is { Kind: "station" or "aiowner" or "aipirate" } rs && OpenSectorId != null)
        {
            var (kind, id) = (rs.Kind, rs.Id);
            var place = kind == "station" ? P.StationById(id) is { } st ? new Spot { X = st.X, Y = st.Y, Z = st.Z } : null
                : P.SectorById(id) is { } sec ? (kind == "aiowner" ? GalaxyOps.AiPlaces(sec).Owner : GalaxyOps.AiPlaces(sec).Pirate) : null;
            if (place != null) Change(() => PlaceStation(kind, id, place.X, place.Y, place.Z, YawOf(kind, id) + (k.ShiftPressed ? -15 : 15)));
        }
        else return;
        AcceptEvent();
    }

    // ---------------------------------------------------------------------------------------------- clipboard
    // Gates and decorations of a sector. A copied gate keeps type and facing, not its connection.
    (string kind, string json)? _clip;
    public bool CanPaste => _clip != null;

    public void Copy()
    {
        if (Sel?.Kind == "gate" && P.GateById(Sel.Id) is { } g) _clip = ("gate", System.Text.Json.JsonSerializer.Serialize(g));
        else if (Sel?.Kind == "decor" && P.DecorById(Sel.Id) is { } d) _clip = ("decor", System.Text.Json.JsonSerializer.Serialize(d));
        else if (Sel?.Kind == "station" && P.StationById(Sel.Id) is { } st) _clip = ("station", System.Text.Json.JsonSerializer.Serialize(st));
        else return;
        SetStatus("Copied. Ctrl+V or right-click > Paste puts it into the open sector.");
    }

    public void Cut() { if (Sel?.Kind is "gate" or "decor" or "station") { Copy(); DeleteSelection(); } }

    /// <summary>Puts the clipboard into the open sector at x / z (X4 metres), height as copied.</summary>
    public void Paste(double x, double z)
    {
        if (_clip is not { } clip || P.SectorById(OpenSectorId) is not { } s) return;
        Change(() =>
        {
            if (clip.kind == "gate")
            {
                var g = System.Text.Json.JsonSerializer.Deserialize<Gate>(clip.json)!;
                g.Id = P.NextId++; g.Link = null; g.X = Math.Round(x); g.Z = Math.Round(z);
                s.Gates.Add(g); Sel = new Selection("gate", g.Id);
            }
            else if (clip.kind == "station")
            {
                var st = System.Text.Json.JsonSerializer.Deserialize<Station>(clip.json)!;
                st.Id = P.NextId++; st.X = Math.Round(x); st.Z = Math.Round(z);
                s.Stations.Add(st); Sel = new Selection("station", st.Id);
            }
            else
            {
                var d = System.Text.Json.JsonSerializer.Deserialize<Decoration>(clip.json)!;
                d.Id = P.NextId++; d.Link = null; d.X = Math.Round(x); d.Z = Math.Round(z);
                s.Decor.Add(d); Sel = new Selection("decor", d.Id);
            }
        });
    }

    public override void _Process(double delta)
    {
        if (_shotFrames < 0 || --_shotFrames > 0) return;
        if (Conv == null && _shotFrames == 0 && OpenSectorId != null) { _shotFrames = 30; return; }   // wait until game files and models are ready
        if (_shotFrames == 0 && (_pendingPatch != null || (P.BaseGalaxy != null && Base == null))) { _shotFrames = 30; return; }   // patch: wait for the base galaxy
        if (_shotFrames == 0 && _baseSector == "gate" && Base != null && EditP.BaseGates.FirstOrDefault(b => b.Existing == null) is { } firstNew)
        {
            _baseSector = null; OpenBaseGate(firstNew);
            if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--movegate") >= 0 && Sel is { } mg)   // check run: a move must stay
                Change(() => { if (P.GateById(mg.Id) is { } g) g.X += 50_000; });
            _shotFrames = 120; return;
        }
        if (_shotFrames == 0 && _baseSector != null && Base != null)
        {
            var hit = Base.Clusters.SelectMany(c => c.Sectors.Select(x => (c, x))).FirstOrDefault(t => t.x.Name == _baseSector);
            _baseSector = null;
            if (hit.x != null) { OpenBaseSector(hit.c, hit.x); _shotFrames = 120; return; }
        }
        if (_shotFrames == 0 && _voiceShot && Texts != null && Game != null)
        {
            _voiceShot = false;
            var vs = P.SectorById(Sel?.Id) ?? P.AllSectors().FirstOrDefault();
            if (vs != null) { ShowVoicePicker(vs.NameRef, (_, _) => { }); _shotFrames = 400; return; }
        }
        _shotFrames = -1;
        GetViewport().GetTexture().GetImage().SavePng(_screenshot);
        GD.Print("screenshot saved: " + _screenshot);
        if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--debugreport") >= 0)   // check of the debug report
        { _debugShot = GetViewport().GetTexture().GetImage(); GD.Print("debug report: " + CreateDebugReport(true)); }
        GetTree().Quit();
    }
}
