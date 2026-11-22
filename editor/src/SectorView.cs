using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using X4Data.Galaxy;

namespace GalaxyEditor3D;

/// <summary>
/// 3D view of one sector: ecliptic (y = 0 plane: rings every 50 km, north / east axes), gates with their real model,
/// decorations (fields drawn as their volume with sample rocks / gas, objects with their real model), a marker label
/// on each that stays readable from far away, and a height line down to its foot point on the ecliptic.
/// Space: X4 (x, y, z) -> Godot (x, y, -z), see X4ToGodot.
/// Two layers: behind, the cluster's BACKDROP (sky, nebulae, planets, 100,000+ km away) in its own world whose camera
/// only copies the turn of the front camera (a sky does not move when you fly); in front, the sector with a
/// transparent background. Both use the backdrop's sun direction.
/// Mouse: click a marker = select; drag a marker = move it along its height plane; Shift + drag = move it up / down.
/// The selected item gets a gizmo (Gizmo.cs): arrows move it along one axis, rings turn it.
/// </summary>
public partial class SectorView : Control
{
    readonly App _app;
    SubViewport _vp = null!, _bgVp = null!;
    Camera3D _bgCam = null!;
    DirectionalLight3D _bgSun = null!, _sun = null!;
    Node3D _bgContent = null!, _content = null!;
    OrbitCamera _cam = null!;
    Minimap _minimap = null!;
    /// <summary>Decoration palette on the left (groups, tiles with pictures).</summary>
    public DecorPalette Palette { get; private set; } = null!;
    bool _placed;                                                   // the click placed an item: its release must not deselect
    DecorVisuals _visuals = null!;
    string? _shownBackdrop;
    readonly Dictionary<string, Node3D?> _backdrops = new();  // backdrop component -> template
    readonly Dictionary<string, Node3D?> _models = new();     // gate macro -> template built once from the game files

    /// <summary>One movable thing on screen: a gate or a decoration, all its nodes under one holder.</summary>
    sealed record Item(string Kind, int Id, Node3D Holder, Color Colour);
    readonly List<Item> _items = new();
    int? _shownSector;
    public double Reach { get; private set; } = 200_000;
    /// <summary>Camera pitch / yaw (radians) when a sector opens.</summary>
    public (float pitch, float yaw) StartView = (-0.75f, 0.5f);
    /// <summary>Screenshot checks (--near metres): camera this close to the selected item after every rebuild.</summary>
    public float? NearView;
    /// <summary>Debug: frame rate, draw calls and video memory in the top left corner of the 3D view.</summary>
    public bool ShowStats { get => _stats.Visible; set => _stats.Visible = value; }
    readonly Label _stats = new() { Visible = false, Position = new Vector2(480, 8), Modulate = new Color(1, 1, 1, 0.8f) };
    double _statsTimer;

    // dragging
    Item? _drag; Vector3 _dragGrab; bool _dragMoved; Vector2 _dragStart;
    // move / turn handles on the selected item
    Gizmo _gizmo = null!; bool _gizmoMoved;

    public SectorView(App app)
    {
        _app = app;
        app.Changed += () => { if (Visible && app.OpenSectorId != null) ShowSector(); };
    }

    public OrbitCamera Camera => _cam;

    public override void _Ready()
    {
        // back layer: the backdrop (input goes to the front layer only)
        var bgBox = new SubViewportContainer { Stretch = true, MouseFilter = MouseFilterEnum.Ignore };
        bgBox.SetAnchorsPreset(LayoutPreset.FullRect); AddChild(bgBox);
        _bgVp = new SubViewport { OwnWorld3D = true, Msaa3D = Viewport.Msaa.Msaa2X };
        bgBox.AddChild(_bgVp);
        _bgVp.AddChild(new WorldEnvironment { Environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.012f, 0.016f, 0.03f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.3f, 0.35f, 0.5f), AmbientLightEnergy = 0.08f,
            TonemapMode = Godot.Environment.ToneMapper.Filmic, GlowEnabled = true,
        } });
        _bgSun = new DirectionalLight3D(); _bgVp.AddChild(_bgSun);
        _bgCam = new Camera3D { Near = 50_000, Far = 4_000_000_000, Fov = 60 }; _bgVp.AddChild(_bgCam);
        _bgContent = new Node3D(); _bgVp.AddChild(_bgContent);

        // front layer: the sector, transparent where nothing is drawn
        var box = new SubViewportContainer { Stretch = true };
        box.SetAnchorsPreset(LayoutPreset.FullRect); AddChild(box);
        _vp = new SubViewport { OwnWorld3D = true, Msaa3D = Viewport.Msaa.Msaa2X, HandleInputLocally = true, TransparentBg = true };
        box.AddChild(_vp);
        _vp.AddChild(new WorldEnvironment { Environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.ClearColor,
            AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.55f, 0.6f, 0.7f), AmbientLightEnergy = 0.15f,
            // metal needs something to reflect (X4 lights hulls with the backdrop): a soft space sky, never drawn
            // (the background stays transparent), only seen in reflections, with the sun as a bright spot
            ReflectedLightSource = Godot.Environment.ReflectionSource.Sky,
            Sky = new Sky { SkyMaterial = new ProceduralSkyMaterial
            {
                SkyTopColor = new Color(0.16f, 0.2f, 0.3f), SkyHorizonColor = new Color(0.3f, 0.32f, 0.38f), SkyCurve = 0.2f,
                GroundBottomColor = new Color(0.05f, 0.05f, 0.07f), GroundHorizonColor = new Color(0.22f, 0.23f, 0.27f),
                SunAngleMax = 8f, SunCurve = 0.05f,
            } },
            TonemapMode = Godot.Environment.ToneMapper.Aces, TonemapWhite = 6f,
            // glow maps (lights, windows, engines) bloom as in the game
            GlowEnabled = true, GlowIntensity = 0.7f, GlowBloom = 0.05f, GlowHdrThreshold = 1.0f, GlowBlendMode = Godot.Environment.GlowBlendModeEnum.Screen,
        } });
        _sun = new DirectionalLight3D { RotationDegrees = new Vector3(-35, 40, 0) };
        _vp.AddChild(_sun);
        _cam = new OrbitCamera();
        _cam.LeftPressed += OnLeftPressed;
        _cam.LeftDragged += OnLeftDragged;
        _cam.LeftReleased += OnLeftReleased;
        _cam.RightClicked += OnRightClicked;
        // no flying while a text / number field has the keyboard (typing "w" into a name must not move the camera)
        _cam.KeysAllowed = () => IsVisibleInTree() && GetTree().Root.GuiGetFocusOwner() is not (LineEdit or TextEdit);
        _vp.AddChild(_cam);
        _content = new Node3D(); _vp.AddChild(_content);
        _gizmo = new Gizmo(); _vp.AddChild(_gizmo); _gizmo.Init(_cam);
        _visuals = new DecorVisuals(_app);

        _minimap = new Minimap(_app, this);
        AddChild(_minimap);
        Palette = new DecorPalette(_app);
        Palette.SetAnchorsPreset(LayoutPreset.LeftWide);
        AddChild(Palette);
        AddChild(_stats);
    }

    /// <summary>Camera target in X4 space (where "Add" puts new things), on the ecliptic.</summary>
    public (double X, double Y, double Z) ViewCentre() => (Math.Round(_cam.Target.X), 0, Math.Round(-_cam.Target.Z));

    /// <summary>Moves the camera target to an X4 position (minimap clicks).</summary>
    public void LookAtX4(double x, double z) { _cam.Target = new Vector3((float)x, _cam.Target.Y, (float)-z); _cam.Refresh(); }

    /// <summary>Rebuilds the scene of the open sector. Keeps the camera when the same sector is shown again.</summary>
    public void ShowSector()
    {
        if (_content == null) return;
        var s = _app.P.SectorById(_app.OpenSectorId);
        if (s == null) return;
        foreach (var c in _content.GetChildren()) { _content.RemoveChild(c); c.QueueFree(); }
        _items.Clear();
        _sun.LightEnergy = (float)Math.Clamp(s.Sunlight, 0.15, 3.0);
        // the sky camera stands where the sector sits inside the backdrop (X4 -> Godot: z mirrored)
        var place = _app.P.Place(s);
        _bgCam.Position = new Vector3((float)place.X, (float)place.Y, (float)-place.Z);
        ShowBackdrop(_app.P.ClusterOf(s.Id)!.BackdropComponent(), place);

        Reach = Math.Max(200_000, s.Gates.Select(g => Math.Sqrt(g.X * g.X + g.Z * g.Z))
            .Concat(s.Decor.Select(d => Math.Sqrt(d.X * d.X + d.Z * d.Z)))
            .Concat(s.Stations.Select(st => Math.Sqrt(st.X * st.X + st.Z * st.Z))).DefaultIfEmpty(0).Max() + 50_000);
        Ecliptic(Reach);

        foreach (var g in s.Gates)
        {
            bool sel = _app.Sel is { Kind: "gate" } x && x.Id == g.Id;
            var target = _app.P.LinkTarget(g);
            var colour = sel ? new Color("ffe066") : target != null ? new Color("cfe3ff") : new Color("ffb347");
            var model = GateModel(g.Type);
            model.Basis = new Basis(Vector3.Up, -Mathf.DegToRad((float)g.Yaw));      // yaw 0 = looks north (+z in X4)
            AddItem("gate", g.Id, g.X, g.Y, g.Z, model, g.Type == "accelerator" ? "◇" : "◯", target != null ? "→ " + target : "not connected", colour);
        }

        foreach (var d in s.Decor)
        {
            bool sel = _app.Sel is { Kind: "decor" } x && x.Id == d.Id;
            var (node, icon, label, colour) = _visuals.Build(d);
            node.Basis = DecorVisuals.Rotation(d);
            AddItem("decor", d.Id, d.X, d.Y, d.Z, node, icon, label, sel ? new Color("ffe066") : colour);
        }

        // stations placed by hand, and the sector AI's stations (owner's claim station, pirate station)
        foreach (var st in s.Stations)
        {
            bool sel = _app.Sel is { Kind: "station" } x && x.Id == st.Id;
            var colour = FactionColour(st.Faction);
            AddItem("station", st.Id, st.X, st.Y, st.Z, StationBody(colour, st.Yaw), st.Kind == "piratebase" ? "☠" : "▣", StationText(st), sel ? new Color("ffe066") : colour);
        }
        if (s.Ai is { } ai)
        {
            var (own, pir) = GalaxyOps.AiPlaces(s);
            if (ai.Owner != null)
            {
                bool sel = _app.Sel is { Kind: "aiowner" } x && x.Id == s.Id;
                var colour = FactionColour(ai.Owner);
                AddItem("aiowner", s.Id, own.X, own.Y, own.Z, StationBody(colour, own.Yaw), "◆", $"{_app.FactionName(ai.Owner)}: owner's claim station", sel ? new Color("ffe066") : colour);
            }
            if (ai.PirateStation != null && ai.PirateFaction != null)
            {
                bool sel = _app.Sel is { Kind: "aipirate" } x && x.Id == s.Id;
                var colour = FactionColour(ai.PirateFaction);
                AddItem("aipirate", s.Id, pir.X, pir.Y, pir.Z, StationBody(colour, pir.Yaw), "☠", $"{_app.FactionName(ai.PirateFaction)}: pirate station", sel ? new Color("ffe066") : colour);
            }
        }

        // handles on the selected item (gates and stations turn around "up" only)
        var selItem = SelectedItem();
        var sd = selItem?.Kind == "decor" ? _app.P.DecorById(selItem.Id) : null;
        Palette.Visible = !_app.ReadOnly;
        _gizmo.Attach(selItem != null && _app.Movable(selItem.Kind, selItem.Id) ? selItem.Holder : null, sd != null, selItem != null ? _app.YawOf(selItem.Kind, selItem.Id) : 0, sd?.Pitch ?? 0, sd?.Roll ?? 0);

        if (_shownSector != s.Id)
        {
            _cam.SetView(StartView.pitch, StartView.yaw);
            _cam.Frame(new Aabb(new Vector3((float)-Reach, -20_000, (float)-Reach), new Vector3((float)Reach * 2, 40_000, (float)Reach * 2)), 0.62f);
            _shownSector = s.Id;
        }
        if (NearView is float near && selItem != null) { _cam.Target = selItem.Holder.Position; _cam.Distance = near; _cam.Refresh(); }
    }

    /// <summary>Holder at the X4 position with the model / volume, the marker labels and the height line.</summary>
    void AddItem(string kind, int id, double x, double y, double z, Node3D body, string icon, string label, Color colour)
    {
        var holder = new Node3D { Position = new Vector3((float)x, (float)y, (float)-z) };
        holder.AddChild(body);
        holder.AddChild(Marker(icon, colour, 30, HorizontalAlignment.Center, Vector2.Zero));
        holder.AddChild(Marker(label, colour, 24, HorizontalAlignment.Left, new Vector2(26, 0)));
        var line = new MeshInstance3D { Name = "HeightLine", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        holder.AddChild(line);
        UpdateHeightLine(holder, colour);
        _content.AddChild(holder);
        _items.Add(new Item(kind, id, holder, colour));
    }

    /// <summary>The front camera's turn goes to the sky camera every frame (position stays where the sector sits in the backdrop).</summary>
    public override void _Process(double delta)
    {
        if (!Visible || _bgCam == null) return;
        _bgCam.GlobalBasis = _cam.GlobalBasis;
        _bgCam.Fov = _cam.Fov;
        if (_gizmo.Visible) _gizmo.Hover(_vp.GetMousePosition());
        if (_stats.Visible && (_statsTimer -= delta) <= 0)
        {
            _statsTimer = 0.5;
            ulong Info(RenderingServer.RenderingInfo i) => RenderingServer.GetRenderingInfo(i);
            _stats.Text = $"{Engine.GetFramesPerSecond():0} fps   draw calls {Info(RenderingServer.RenderingInfo.TotalDrawCallsInFrame)}   "
                + $"triangles {Info(RenderingServer.RenderingInfo.TotalPrimitivesInFrame) / 1000:N0}k   video memory {Info(RenderingServer.RenderingInfo.VideoMemUsed) / 1_048_576} MB   items {_items.Count}";
        }
    }

    Item? SelectedItem() => _app.Sel is { } sel ? _items.FirstOrDefault(i => i.Kind == sel.Kind && i.Id == sel.Id) : null;

    // ---------------------------------------------------------------------------------------------- select and drag
    Item? PickAt(Vector2 screen) =>
        _items.Where(i => !_cam.IsPositionBehind(i.Holder.Position))
              .Select(i => (i, d: _cam.UnprojectPosition(i.Holder.Position).DistanceTo(screen)))
              .Where(x => x.d < 24).OrderBy(x => x.d).Select(x => x.i).FirstOrDefault();

    void OnLeftPressed(Vector2 screen)
    {
        // a handle of the gizmo comes first (it sits on top of everything)
        _placed = false;
        // an item from the palette in the hand: the click places it on the ecliptic (1 km grid, Alt: free)
        if (_app.Armed is { } armed && PlaneHit(screen, 0) is { } spot)
        {
            double px = spot.X, pz = -spot.Z;
            if (!Input.IsKeyPressed(Key.Alt)) { px = Math.Round(px / 1000) * 1000; pz = Math.Round(pz / 1000) * 1000; }
            _drag = null; _placed = true;
            bool more = Input.IsKeyPressed(Key.Shift);
            if (!more) _app.Disarm();
            _app.PlaceDecor(armed, (Math.Round(px), 0, Math.Round(pz)));
            return;
        }
        var h = _gizmo.Pick(screen);
        if (h != Gizmo.Handle.None) { _drag = null; _dragStart = screen; _gizmoMoved = false; _gizmo.BeginDrag(h, screen); return; }
        _drag = PickAt(screen); _dragMoved = false; _dragStart = screen;
        if (_drag != null && PlaneHit(screen, _drag.Holder.Position.Y) is { } hit) _dragGrab = _drag.Holder.Position - hit;
    }

    void OnLeftDragged(Vector2 screen, Vector2 relative, bool shift)
    {
        if (_gizmo.Dragging != Gizmo.Handle.None) { GizmoDrag(screen); return; }
        if (_drag == null) { if (!_placed) _cam.Pan(relative); return; }  // left drag on empty space moves the view (as middle drag)
        if (!_app.Movable(_drag.Kind, _drag.Id)) return;   // vanilla things of a patched sector stay put
        if (!_dragMoved && screen.DistanceTo(_dragStart) < 5) return;
        _dragMoved = true;
        var p = _drag.Holder.Position;
        if (shift)
        {
            // up / down: screen pixels -> metres at the object's distance
            float k = _cam.Position.DistanceTo(p) * 0.0015f;
            p.Y -= relative.Y * k;
            if (PlaneHit(screen, p.Y) is { } hit) _dragGrab = p - hit;      // keep the grip for the next flat move
        }
        else if (PlaneHit(screen, p.Y) is { } hit)
        {
            var n = hit + _dragGrab;
            if (!Input.IsKeyPressed(Key.Alt)) { n.X = Mathf.Round(n.X / 1000) * 1000; n.Z = Mathf.Round(n.Z / 1000) * 1000; }   // 1 km grid, Alt = free
            p.X = n.X; p.Z = n.Z;
        }
        _drag.Holder.Position = p;
        UpdateHeightLine(_drag.Holder, _drag.Colour);
        _app.SetStatus($"x {p.X / 1000:F1} km   height {p.Y / 1000:F1} km   z {-p.Z / 1000:F1} km      (Shift: up / down, Alt: no 1 km grid)");
    }

    void OnLeftReleased(Vector2 screen)
    {
        if (_gizmo.Dragging != Gizmo.Handle.None) { GizmoCommit(); return; }
        if (_placed) { _placed = false; return; }
        var d = _drag; _drag = null;
        if (d == null)
        {
            if (screen.DistanceTo(_dragStart) < 5) { _app.Sel = null; _app.Refresh(); }   // click into empty space: deselect
            return;
        }
        if (!_dragMoved) { _app.Sel = new Selection(d.Kind, d.Id); _app.Refresh(); return; }
        var p = d.Holder.Position;
        double x = Math.Round(p.X), y = Math.Round(p.Y), z = Math.Round(-p.Z);
        _app.Sel = new Selection(d.Kind, d.Id);
        _app.Change(() =>
        {
            if (d.Kind == "gate" && _app.P.GateById(d.Id) is { } g) { g.X = x; g.Y = y; g.Z = z; }
            if (d.Kind == "decor" && _app.P.DecorById(d.Id) is { } o) { o.X = x; o.Y = y; o.Z = z; }
            _app.PlaceStation(d.Kind, d.Id, x, y, z, null);
        });
    }

    /// <summary>Gizmo handle dragged: the item follows at once (position or turn); the project changes on release.</summary>
    void GizmoDrag(Vector2 screen)
    {
        var item = SelectedItem(); if (item == null) return;
        _gizmoMoved = true;
        bool snap = !Input.IsKeyPressed(Key.Alt);
        var p = _gizmo.Drag(screen, snap);
        var h = _gizmo.Dragging;
        if (h is Gizmo.Handle.MoveX or Gizmo.Handle.MoveY or Gizmo.Handle.MoveZ)
        {
            item.Holder.Position = p;
            UpdateHeightLine(item.Holder, item.Colour);
            _app.SetStatus($"x {p.X / 1000:F1} km   height {p.Y / 1000:F1} km   z {-p.Z / 1000:F1} km      (1 km steps; Alt: free)");
        }
        else
        {
            var body = item.Holder.GetChild<Node3D>(0);                // AddItem puts the model / volume first
            body.Basis = item.Kind != "decor" ? new Basis(Vector3.Up, -Mathf.DegToRad((float)_gizmo.Yaw)) : DecorVisuals.Rotation(_gizmo.Yaw, _gizmo.Pitch, _gizmo.Roll);
            _app.SetStatus(item.Kind != "decor" ? $"facing {_gizmo.Yaw:0.#}°      (5° steps; Alt: free)"
                : $"yaw {_gizmo.Yaw:0.#}°   pitch {_gizmo.Pitch:0.#}°   roll {_gizmo.Roll:0.#}°      (5° steps; Alt: free)");
        }
    }

    /// <summary>Gizmo released: one undo step with the new position / angles.</summary>
    void GizmoCommit()
    {
        _gizmo.EndDrag();
        var item = SelectedItem();
        if (item == null || !_gizmoMoved) return;
        var p = item.Holder.Position;
        double x = Math.Round(p.X), y = Math.Round(p.Y), z = Math.Round(-p.Z);
        double yaw = _gizmo.Yaw, pitch = _gizmo.Pitch, roll = _gizmo.Roll;
        _app.Change(() =>
        {
            if (item.Kind == "gate" && _app.P.GateById(item.Id) is { } g) { g.X = x; g.Y = y; g.Z = z; g.Yaw = yaw; }
            if (item.Kind == "decor" && _app.P.DecorById(item.Id) is { } o) { o.X = x; o.Y = y; o.Z = z; o.Yaw = yaw; o.Pitch = pitch; o.Roll = roll; }
            _app.PlaceStation(item.Kind, item.Id, x, y, z, yaw);
        });
    }

    /// <summary>
    /// Right-click menu: on a gate / decoration Copy, Cut, Delete, and for gates "Connect to" a
    /// sector (new partner gate there, opposite side) or "Disconnect"; on empty space Paste at the clicked spot.
    /// </summary>
    void OnRightClicked(Vector2 screen)
    {
        if (_app.ReadOnly) return;                                     // nothing to do in a read-only sector
        if (_app.Armed != null) { _app.Disarm(); return; }             // right click puts the palette item away
        var item = PickAt(screen);
        var at = PlaneHit(screen, 0);                                 // the clicked spot on the ecliptic (Godot space)
        var menu = new PopupMenu();
        var actions = new List<Action>();
        void Add(string text, Action a, bool enabled = true) { menu.AddItem(text, actions.Count); menu.SetItemDisabled(menu.ItemCount - 1, !enabled); actions.Add(a); }

        if (item != null)
        {
            _app.Sel = new Selection(item.Kind, item.Id); _app.Refresh();
            Add("Copy  (Ctrl+C)", _app.Copy);
            Add("Cut  (Ctrl+X)", _app.Cut);
            Add("Delete  (Del)", _app.DeleteSelection);
            // gates and anomalies: connect to another sector (a new partner is created there, opposite side) or disconnect
            var gate = item.Kind == "gate" ? _app.P.GateById(item.Id) : null;
            var anomaly = item.Kind == "decor" && _app.P.DecorById(item.Id) is { } dd && _app.Decor?.Get(dd.Kind, dd.Ref)?.Category == "Anomaly" ? dd : null;
            if (gate != null || anomaly != null)
            {
                menu.AddSeparator();
                var sub = new PopupMenu { Name = "connect" };
                var here = gate != null ? _app.P.SectorOfGate(gate.Id)! : _app.P.SectorOfDecor(anomaly!.Id)!;
                // anomalies may also lead somewhere else in the same sector
                var targets = _app.P.AllSectors().Where(x => anomaly != null || x.Id != here.Id).OrderBy(x => x.Name).ToList();
                for (int i = 0; i < targets.Count; i++) sub.AddItem(targets[i].Name + (targets[i].Id == here.Id ? " (this sector)" : ""), i);
                sub.IdPressed += i =>
                {
                    var t = targets[(int)i];
                    _app.Change(() =>
                    {
                        if (gate != null) { var g2 = _app.P.AddGate(t, -gate.X, -gate.Z); g2.Type = gate.Type; _app.P.Link(gate, g2); }
                        else
                        {
                            var a2 = _app.P.AddDecor(t, anomaly!.Kind, anomaly.Ref, -anomaly.X, anomaly.Y, -anomaly.Z);
                            _app.P.LinkDecor(anomaly, a2);
                        }
                    });
                };
                menu.AddChild(sub);
                menu.AddSubmenuNodeItem("Connect to (new partner there)", sub);
                if ((gate?.Link ?? anomaly?.Link) != null)
                    Add("Disconnect", () => _app.Change(() => { if (gate != null) _app.P.Unlink(gate); else _app.P.UnlinkDecor(anomaly!); }));
            }
        }
        else
        {
            Add("Paste here  (Ctrl+V)", () => { if (at is { } p) _app.Paste(Math.Round(p.X), Math.Round(-p.Z)); }, _app.CanPaste && at != null);
            if (at is { } spot)
            {
                // a station of a kind at the clicked spot (1 km grid); owner = the sector's owner, else the first that has one
                var sub = new PopupMenu { Name = "stations" };
                var kinds = GalaxyOps.StationKinds;
                for (int i = 0; i < kinds.Length; i++) sub.AddItem(kinds[i].Label, i);
                sub.IdPressed += i => _app.AddStation(kinds[(int)i].Kind, Math.Round(spot.X / 1000) * 1000, Math.Round(-spot.Z / 1000) * 1000);
                menu.AddChild(sub);
                menu.AddSubmenuNodeItem("Add station here", sub);
            }
        }
        if (_app.OpenSectorId is int open)
        {
            menu.AddSeparator();
            Add("Clear sector ...", () => _app.ClearSector(open));      // asks first
        }
        menu.IdPressed += id => actions[(int)id]();
        menu.PopupHide += () => { _app.WindowClosed(menu); menu.QueueFree(); };
        AddChild(menu);
        menu.Position = (Vector2I)(GetScreenPosition() + screen);
        menu.Popup();
        _app.OpenWindow(menu, menu.Hide);                            // Esc closes it like every window
    }

    /// <summary>Where the mouse ray meets the horizontal plane at height y (Godot space), or null.</summary>
    Vector3? PlaneHit(Vector2 screen, float y)
    {
        var o = _cam.ProjectRayOrigin(screen); var dir = _cam.ProjectRayNormal(screen);
        if (Math.Abs(dir.Y) < 1e-4f) return null;
        float t = (y - o.Y) / dir.Y;
        return t > 0 ? o + dir * t : null;
    }

    /// <summary>Item positions for the minimap (X4 space x / z, colour, selected).</summary>
    public IEnumerable<(string kind, int id, float x, float z, Color colour)> MapItems() =>
        _items.Select(i => (i.Kind, i.Id, i.Holder.Position.X, -i.Holder.Position.Z, i.Colour));

    // ---------------------------------------------------------------------------------------------- backdrop
    /// <summary>Loads (once) and shows a backdrop component; turns both suns towards its sun as seen from the sector.</summary>
    void ShowBackdrop(string name, (double X, double Y, double Z) place)
    {
        if (_app.Conv == null || _app.Game == null) return;
        SunFrom(name, place);
        if (name == _shownBackdrop) return;
        foreach (var c in _bgContent.GetChildren()) { _bgContent.RemoveChild(c); c.QueueFree(); }
        _shownBackdrop = name;
        if (!_backdrops.TryGetValue(name, out var tpl))
        {
            tpl = _app.Conv.BuildObject(name);
            if (tpl.GetChildCount() == 0) { tpl.Free(); tpl = null; }
            _backdrops[name] = tpl;
        }
        if (tpl != null) _bgContent.AddChild(tpl.Duplicate());
    }

    void SunFrom(string name, (double X, double Y, double Z) place)
    {
        var light = X4Data.Space.Backdrops.Light(_app.Game!, name);
        if (light == null) return;
        // light shines FROM the sun: direction from the sector's place to the sun light
        var d = new System.Numerics.Vector3(light.SunPosition.X - (float)place.X, light.SunPosition.Y - (float)place.Y, light.SunPosition.Z - (float)place.Z);
        var toSun = new Vector3(d.X, d.Y, -d.Z).Normalized();
        var up = Math.Abs(toSun.Y) > 0.95f ? Vector3.Forward : Vector3.Up;
        foreach (var sun in new[] { _sun, _bgSun })
        {
            sun.Basis = Basis.LookingAt(-toSun, up);
            sun.LightColor = new Color(light.SunColour.X, light.SunColour.Y, light.SunColour.Z);
        }
        _bgSun.LightEnergy = light.SunIntensity;
    }

    // ---------------------------------------------------------------------------------------------- pieces
    static Label3D Marker(string text, Color col, int size, HorizontalAlignment align, Vector2 offset) => new()
    {
        Text = text, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, FixedSize = true, PixelSize = 0.0009f,
        FontSize = size, OutlineSize = 6, NoDepthTest = true, Modulate = col, HorizontalAlignment = align, Offset = offset,
    };

    /// <summary>The model templates are not in the scene tree: free them when the view goes.</summary>
    public override void _ExitTree()
    {
        foreach (var t in _models.Values.Concat(_backdrops.Values)) t?.Free();
        _models.Clear(); _backdrops.Clear();
        _visuals.Free();
    }

    static Color FactionColour(string faction) =>
        GalaxyFacts.Factions.TryGetValue(faction, out var f) ? new Color(f.Colour) : new Color("c8c8c8");

    string StationText(Station st)
    {
        string what = st.Kind == "factory" ? "factory: " + _app.WareName(st.Ware) : GalaxyOps.StationLabel(st.Kind);
        if (st.Kind == "piratebase" && st.Ref != null && _app.Factions?.Stations.FirstOrDefault(t => t.Id == st.Ref) is { } pt) what = pt.Tag switch
        {
            "piratedepot" => "pirate depot", "anarchyport" => "anarchy port", "piratedock" => "pirate dock", _ => "pirate base",
        };
        return $"{_app.FactionName(st.Faction)} {what}";
    }

    /// <summary>
    /// A station marker until real station models are built: a hub with four arms (about 6 km across, the size of a mid
    /// station) in the faction's colour, turned by its facing (one arm points where the station faces).
    /// </summary>
    static Node3D StationBody(Color colour, double yaw)
    {
        var mat = new StandardMaterial3D { AlbedoColor = colour, Metallic = 0.2f, Roughness = 0.6f, EmissionEnabled = true, Emission = colour, EmissionEnergyMultiplier = 0.45f };
        var holder = new Node3D { Basis = new Basis(Vector3.Up, -Mathf.DegToRad((float)yaw)) };
        holder.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 900, BottomRadius = 900, Height = 1600, RadialSegments = 12 }, MaterialOverride = mat });
        for (int i = 0; i < 4; i++)
        {
            var arm = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(400, 400, i == 0 ? 3400 : 2400) }, MaterialOverride = mat };
            arm.Basis = new Basis(Vector3.Up, i * Mathf.Pi / 2);
            arm.Position = arm.Basis * new Vector3(0, 0, i == 0 ? -1700 : -1200);    // arm 0 (longest) = facing (north = -z in Godot)
            holder.AddChild(arm);
        }
        return holder;
    }

    Node3D GateModel(string type)
    {
        string macro = type == "accelerator" ? "props_gates_orb_accelerator_01_macro" : "props_gates_anc_gate_macro";
        if (!_models.TryGetValue(macro, out var tpl) && _app.Conv != null)
        {
            tpl = _app.Conv.BuildObject(macro);
            if (tpl.GetChildCount() == 0) { tpl.Free(); tpl = null; }
            _models[macro] = tpl;
        }
        if (tpl != null) return (Node3D)tpl.Duplicate();
        // no game files (yet): a plain ring of the gate's size, standing upright
        var ring = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = 1700, OuterRadius = 2000, Rings = 48, RingSegments = 8 } };
        ring.RotationDegrees = new Vector3(90, 0, 0);
        var holder = new Node3D(); holder.AddChild(ring);
        return holder;
    }

    /// <summary>The y = 0 plane: rings every 50 km (brighter every 100 km, labelled), north / east axes.</summary>
    void Ecliptic(double reach)
    {
        var im = new ImmediateMesh();
        var mat = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, VertexColorUseAsAlbedo = true, Transparency = BaseMaterial3D.TransparencyEnum.Alpha };
        im.SurfaceBegin(Mesh.PrimitiveType.Lines, mat);
        for (int km = 50; km * 1000 <= reach; km += 50)
        {
            var col = km % 100 == 0 ? new Color(0.35f, 0.45f, 0.6f, 0.55f) : new Color(0.25f, 0.32f, 0.42f, 0.35f);
            float r = km * 1000;
            for (int i = 0; i < 128; i++)
            {
                float a0 = MathF.Tau * i / 128, a1 = MathF.Tau * (i + 1) / 128;
                im.SurfaceSetColor(col); im.SurfaceAddVertex(new Vector3(r * MathF.Sin(a0), 0, -r * MathF.Cos(a0)));
                im.SurfaceSetColor(col); im.SurfaceAddVertex(new Vector3(r * MathF.Sin(a1), 0, -r * MathF.Cos(a1)));
            }
            if (km % 100 == 0)
                _content.AddChild(new Label3D { Text = km + " km", Position = new Vector3(0, 0, -r), Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                    FixedSize = true, PixelSize = 0.0007f, FontSize = 22, Modulate = new Color(0.45f, 0.55f, 0.7f) });
        }
        float R = (float)reach;
        var axis = new Color(0.35f, 0.45f, 0.6f, 0.5f);
        foreach (var (a, b) in new[] { (new Vector3(-R, 0, 0), new Vector3(R, 0, 0)), (new Vector3(0, 0, R), new Vector3(0, 0, -R)) })
        { im.SurfaceSetColor(axis); im.SurfaceAddVertex(a); im.SurfaceSetColor(axis); im.SurfaceAddVertex(b); }
        im.SurfaceEnd();
        _content.AddChild(new MeshInstance3D { Mesh = im, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        foreach (var (t, p) in new[] { ("N", new Vector3(0, 0, -R)), ("E", new Vector3(R, 0, 0)) })
            _content.AddChild(new Label3D { Text = t, Position = p, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, FixedSize = true, PixelSize = 0.0009f, FontSize = 28, Modulate = new Color(0.6f, 0.75f, 1f) });
    }

    /// <summary>Line from the holder down (or up) to the ecliptic, with a small ring at the foot point (holder-local).</summary>
    static void UpdateHeightLine(Node3D holder, Color col)
    {
        var line = holder.GetNode<MeshInstance3D>("HeightLine");
        var im = new ImmediateMesh();
        var mat = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(col, 0.7f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha };
        im.SurfaceBegin(Mesh.PrimitiveType.Lines, mat);
        var foot = new Vector3(0, -holder.Position.Y, 0);
        if (Math.Abs(holder.Position.Y) > 1) { im.SurfaceAddVertex(Vector3.Zero); im.SurfaceAddVertex(foot); }
        for (int i = 0; i < 32; i++)
        {
            float a0 = MathF.Tau * i / 32, a1 = MathF.Tau * (i + 1) / 32, r = 3000;
            im.SurfaceAddVertex(foot + new Vector3(r * MathF.Cos(a0), 0, r * MathF.Sin(a0)));
            im.SurfaceAddVertex(foot + new Vector3(r * MathF.Cos(a1), 0, r * MathF.Sin(a1)));
        }
        im.SurfaceEnd();
        line.Mesh = im;
    }
}
