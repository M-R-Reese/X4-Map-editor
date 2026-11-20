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
/// Command line for checks (after "--"): --open file.json, --sector id, --setup, --view pitch,yaw, --play musicid, --screenshot file.png.
/// </summary>
public partial class App : Control
{
    /// <summary>Visible name and version (window title, dialogs). The Godot project name stays "X4 Galaxy Editor 3D"
    /// on purpose: it names the user data folder (settings, autosave).</summary>
    public const string AppName = "X4 Map Editor", Version = "0.1 beta";
    public Project P = new();
    public Selection? Sel;
    public int? OpenSectorId;                       // null = galaxy view
    public string Tool = "select";                  // galaxy: select / connect
    public GameData? Game;                          // null until the game files are read (or no settings yet)
    public X4ToGodot? Conv;
    /// <summary>Backdrops the installed game has (base + DLCs + mods), empty until the game files are read.</summary>
    public List<X4Data.Space.Backdrops.Entry> Backdrops = new();
    public MusicPlayer Music = null!;
    /// <summary>Decoration palette (fields + objects) and the game's texts, null until the game files are read.</summary>
    public X4Data.Space.DecorCatalog? Decor;
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
    Button _back = null!, _toolSelect = null!, _toolConnect = null!;
    string? _screenshot; int _shotFrames = -1;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
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
        Refresh();

        var s = Settings.Load(SettingsFile);
        s.Guess();
        if (Array.IndexOf(args, "--setup") >= 0) ShowSetup(s);                    // screenshot check of the folder window
        else if (s.Problems().Count == 0 && (File.Exists(SettingsFile) || _screenshot != null)) StartGame(s);
        else ShowSetup(s);
        if (_screenshot != null) _shotFrames = 90;
    }

    // ---------------------------------------------------------------------------------------------- layout
    void BuildUi()
    {
        var bg = new ColorRect { Color = new Color("12161c") }; bg.SetAnchorsPreset(LayoutPreset.FullRect); AddChild(bg);
        var col = new VBoxContainer(); col.SetAnchorsPreset(LayoutPreset.FullRect); col.AddThemeConstantOverride("separation", 0); AddChild(col);

        var top = new PanelContainer(); col.AddChild(top);
        var bar = new HBoxContainer(); bar.AddThemeConstantOverride("separation", 6); top.AddChild(bar);
        bar.AddChild(new Label { Text = $"{AppName} {Version} " });
        Btn(bar, "New", NewProject); Btn(bar, "Open...", OpenDialog); Btn(bar, "Save project", () => Save(false)); Btn(bar, "Save as...", () => Save(true));
        bar.AddChild(new VSeparator());
        Btn(bar, "Undo", Undo, "Ctrl+Z"); Btn(bar, "Redo", Redo, "Ctrl+Y");
        bar.AddChild(new VSeparator());
        _toolSelect = Btn(bar, "Select / move", () => SetTool("select")); _toolSelect.ToggleMode = true;
        _toolConnect = Btn(bar, "Connect sectors", () => SetTool("connect")); _toolConnect.ToggleMode = true;
        _back = Btn(bar, "◀ Galaxy", BackToGalaxy);
        _crumb = new Label { Modulate = new Color(1, 1, 1, 0.6f) }; bar.AddChild(_crumb);
        bar.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        Btn(bar, "Project settings", () => { Sel = null; _side.ShowProjectSettings(); });
        Btn(bar, "Folders...", () => ShowSetup(Settings.Load(SettingsFile)));
        var export = Btn(bar, "Export mod...", () => { });
        export.Disabled = true; export.TooltipText = "Comes with milestone 5. Until then: open the same project file in the HTML editor and export there.";

        var main = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill }; main.AddThemeConstantOverride("separation", 0); col.AddChild(main);
        var viewArea = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true }; main.AddChild(viewArea);
        _galaxy = new GalaxyView(this); _galaxy.SetAnchorsPreset(LayoutPreset.FullRect); viewArea.AddChild(_galaxy);
        _sector = new SectorView(this) { Visible = false }; _sector.SetAnchorsPreset(LayoutPreset.FullRect); viewArea.AddChild(_sector);
        var sideScroll = new ScrollContainer { CustomMinimumSize = new Vector2(350, 0), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        main.AddChild(sideScroll);
        _side = new SidePanel(this) { SizeFlagsHorizontal = SizeFlags.ExpandFill }; sideScroll.AddChild(_side);

        Music = new MusicPlayer(); AddChild(Music);
        Music.StateChanged += () => _side.Rebuild();

        var statusBar = new PanelContainer(); col.AddChild(statusBar);
        _status = new Label { ClipText = true, Modulate = new Color(1, 1, 1, 0.7f) }; statusBar.AddChild(_status);
    }

    static Button Btn(Container parent, string text, Action onPress, string tip = "")
    {
        var b = new Button { Text = text, TooltipText = tip, FocusMode = FocusModeEnum.None };
        b.Pressed += onPress; parent.AddChild(b); return b;
    }

    // ---------------------------------------------------------------------------------------------- state
    /// <summary>Every edit: remember the state before (undo), apply, mark unsaved, autosave, redraw.</summary>
    public void Change(Action edit)
    {
        _undo.Push(P.ToJson()); if (_undo.Count > 200) TrimUndo();
        _redo.Clear();
        edit();
        _dirty = true; Autosave(); Refresh();
    }

    void TrimUndo() { var keep = _undo.Take(200).Reverse().ToList(); _undo.Clear(); foreach (var k in keep) _undo.Push(k); }

    void Undo() { if (_undo.Count == 0) return; _redo.Push(P.ToJson()); P = Project.FromJson(_undo.Pop()); FixSelection(); _dirty = true; Autosave(); Refresh(); }
    void Redo() { if (_redo.Count == 0) return; _undo.Push(P.ToJson()); P = Project.FromJson(_redo.Pop()); FixSelection(); _dirty = true; Autosave(); Refresh(); }

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
        _ => null,
    };

    void Autosave() { try { File.WriteAllText(AutosaveFile, P.ToJson()); } catch { /* autosave is a convenience */ } }

    public void Refresh()
    {
        bool inSector = OpenSectorId != null;
        _galaxy.Visible = !inSector; _sector.Visible = inSector;
        _toolSelect.Visible = _toolConnect.Visible = !inSector;
        _toolSelect.SetPressedNoSignal(Tool == "select"); _toolConnect.SetPressedNoSignal(Tool == "connect");
        _back.Visible = inSector;
        var s = P.SectorById(OpenSectorId);
        _crumb.Text = s != null ? P.ClusterOf(s.Id)!.Name + " / " + s.Name : "";
        GetWindow().Title = (_dirty ? "* " : "") + P.ModName + $" - {AppName} {Version}" + (_file != null ? " - " + Path.GetFileName(_file) : "");
        SetStatus((inSector
            ? "SECTOR:  WASD: fly  Q/E: down/up  Shift: faster  Right drag: turn  Middle drag: move  Wheel: zoom  F: frame  Click: select  Drag: move (Shift: height)  Right-click: menu  R: turn  Ctrl+C/X/V  Esc: galaxy"
            : Tool == "connect" ? "GALAXY:  drag from one sector to another to create a pair of connected gates.   Esc: back to Select"
            : "GALAXY:  double-click an empty hex: new cluster   Drag a sector: move its cluster   Double-click a sector: open it   Wheel: zoom   Right / middle drag: pan") + _gameInfo);
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

    public void SetStatus(string t) => _status.Text = t;
    string _gameInfo = "";                          // appended to the hints once the game files are read
    public void SetTool(string t) { Tool = t; Refresh(); }

    /// <summary>Camera target of the sector view in X4 metres (where new things are put).</summary>
    public (double X, double Y, double Z) SectorViewCentre() => _sector.ViewCentre();

    public void OpenSector(int sid) { OpenSectorId = sid; Sel = null; Tool = "select"; Refresh(); _sector.ShowSector(); }
    public void BackToGalaxy() { var sid = OpenSectorId; OpenSectorId = null; Sel = sid != null ? new Selection("sector", sid.Value) : null; Refresh(); }

    public void DeleteSelection()
    {
        if (Sel == null) return;
        if (Sel.Kind == "sector" && P.SectorById(Sel.Id) is { } s)
            Confirm($"Delete sector \"{s.Name}\" with its gates?", () => Change(() => { P.DeleteSector(s.Id); Sel = null; }));
        else if (Sel.Kind == "gate" && P.GateById(Sel.Id) is { } g)
            Change(() => { var gs = P.SectorOfGate(g.Id)!; P.Unlink(g); gs.Gates.Remove(g); Sel = null; });
        else if (Sel.Kind == "decor" && P.DecorById(Sel.Id) is { } d)
            Change(() => { P.UnlinkDecor(d); P.SectorOfDecor(d.Id)!.Decor.Remove(d); Sel = null; });
    }

    public void Confirm(string text, Action yes, Action? no = null)
    {
        var d = new ConfirmationDialog { DialogText = text, Title = "Please confirm" };
        d.Confirmed += yes; d.Confirmed += d.QueueFree; d.Canceled += d.QueueFree;
        if (no != null) d.Canceled += no;
        AddChild(d); d.PopupCentered();
    }

    void Alert(string text) { var d = new AcceptDialog { DialogText = text, Title = AppName }; d.Confirmed += d.QueueFree; AddChild(d); d.PopupCentered(); }

    // ---------------------------------------------------------------------------------------------- files
    void NewProject()
    {
        void Do() { P = new Project(); _undo.Clear(); _redo.Clear(); Sel = null; OpenSectorId = null; _file = null; _dirty = false; Autosave(); _galaxy.CentreGalaxy(); Refresh(); }
        if (P.Clusters.Count == 0 || !_dirty) Do(); else Confirm("Start a new, empty galaxy? Unsaved changes are lost.", Do);
    }

    void OpenDialog()
    {
        var d = new FileDialog { FileMode = FileDialog.FileModeEnum.OpenFile, Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = true, Filters = new[] { "*.json ; Galaxy project" } };
        d.FileSelected += f => { LoadFile(f); _galaxy.CentreGalaxy(); Refresh(); };
        AddChild(d); d.PopupCentered(new Vector2I(900, 600));
    }

    void LoadFile(string f)
    {
        try { P = Project.FromJson(File.ReadAllText(f)); }
        catch (Exception e) { Alert(e.Message); return; }
        _file = f; _undo.Clear(); _redo.Clear(); Sel = null; OpenSectorId = null; _dirty = false; Autosave();
    }

    void Save(bool ask)
    {
        if (_file == null || ask)
        {
            var d = new FileDialog { FileMode = FileDialog.FileModeEnum.SaveFile, Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = true,
                                     Filters = new[] { "*.json ; Galaxy project" }, CurrentFile = P.ModId + ".galaxy.json" };
            d.FileSelected += f => { _file = f; Save(false); };
            AddChild(d); d.PopupCentered(new Vector2I(900, 600));
            return;
        }
        try { File.WriteAllText(_file, P.ToJson()); _dirty = false; Refresh(); SetStatus("Project saved: " + _file); }
        catch (Exception e) { Alert("Saving failed: " + e.Message); }
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
            Music.SetGame(Game);
            Conv = new X4ToGodot(Game);
            if (OS.GetCmdlineUserArgs() is var a && Array.IndexOf(a, "--play") is var pi && pi >= 0 && pi + 1 < a.Length) Music.Play(a[pi + 1]);   // check of the music
            Refresh();
            _gameInfo = $"      |  Game files: {Game.Vfs.Count:N0} files, {Game.Vfs.Extensions.LoadOrder.Count} extensions";
            Refresh();
            if (OpenSectorId != null) _sector.ShowSector();
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
        else if (k.Keycode == Key.Escape) { if (Tool != "select") SetTool("select"); else if (OpenSectorId != null) BackToGalaxy(); }
        else if (k.CtrlPressed && k.Keycode == Key.C) Copy();
        else if (k.CtrlPressed && k.Keycode == Key.X) Cut();
        else if (k.CtrlPressed && k.Keycode == Key.V && OpenSectorId != null) { var c = _sector.ViewCentre(); Paste(c.X, c.Z); }
        else if (k.Keycode == Key.R && Sel?.Kind == "gate" && P.GateById(Sel.Id) is { } g)
            Change(() => g.Yaw = GalaxyOps.NormYaw(g.Yaw + (k.ShiftPressed ? -15 : 15)));
        else if (k.Keycode == Key.R && Sel?.Kind == "decor" && P.DecorById(Sel.Id) is { } d)
            Change(() => d.Yaw = GalaxyOps.NormYaw(d.Yaw + (k.ShiftPressed ? -15 : 15)));
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
        else return;
        SetStatus("Copied. Ctrl+V or right-click > Paste puts it into the open sector.");
    }

    public void Cut() { if (Sel?.Kind is "gate" or "decor") { Copy(); DeleteSelection(); } }

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
        _shotFrames = -1;
        GetViewport().GetTexture().GetImage().SavePng(_screenshot);
        GD.Print("screenshot saved: " + _screenshot);
        GetTree().Quit();
    }
}
