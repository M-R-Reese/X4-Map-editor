using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using X4Data.Galaxy;

namespace GalaxyEditor3D;

/// <summary>
/// The galaxy map (2D), ported from the HTML editor: flat-top hex grid, clusters with up to 3 sectors, gate links.
/// Game +z (north) is up on the screen.
/// </summary>
public partial class GalaxyView : Control
{
    readonly App _app;
    float _gx, _gy, _gs = 60;                       // pan (px) and hex radius (px)
    (int q, int r)? _hover;
    Drag? _drag;
    static readonly Font F = ThemeDB.FallbackFont;

    sealed class Drag { public string Kind = ""; public Sector? From; public Cluster? Cluster; public Vector2 Start, At; public bool Moved, Right; }
    Vector2 _mouse;                                 // last mouse position (line of "Connect to ...")

    public GalaxyView(App app) { _app = app; app.Changed += QueueRedraw; FocusMode = FocusModeEnum.Click; }

    /// <summary>Check runs: hex radius in pixels (--mapzoom), centred on cluster (q, r).</summary>
    public void ZoomTo(float gs, int q, int r) { _gs = gs; _gx = -q * 1.5f * gs; _gy = r * 0.8660254f * gs; QueueRedraw(); }

    public void CentreGalaxy()
    {
        _gs = 60; _gx = 0; _gy = 0;
        if (_app.P.Clusters.Count == 0 && _app.Base is { Clusters.Count: > 0 } b)
        {
            // empty patch project: the whole galaxy it adds to
            _gs = 18;
            double bq = b.Clusters.Average(c => c.Q), br = b.Clusters.Average(c => c.R);
            _gx = (float)(-bq * 1.5 * _gs); _gy = (float)(br * 0.8660254 * _gs);
            return;
        }
        if (_app.P.Clusters.Count == 0) return;
        double q = _app.P.Clusters.Average(c => c.Q), r = _app.P.Clusters.Average(c => c.R);
        _gx = (float)(-q * 1.5 * _gs); _gy = (float)(r * 0.8660254 * _gs);
    }

    // ---------------------------------------------------------------------------------------------- geometry
    Vector2 HexPx(int q, int r) => new(Size.X / 2 + _gx + q * 1.5f * _gs, Size.Y / 2 + _gy - r * 0.8660254f * _gs);

    (int q, int r) PxHex(Vector2 p)
    {
        float fx = (p.X - Size.X / 2 - _gx) / (1.5f * _gs), fy = -(p.Y - Size.Y / 2 - _gy) / (0.8660254f * _gs);
        (int, int) best = (0, 0); float bd = float.MaxValue;
        for (int q = (int)MathF.Floor(fx) - 1; q <= (int)MathF.Floor(fx) + 2; q++)
        {
            int par = ((q % 2) + 2) % 2;                       // rows of a column have the column's parity
            int r0 = (int)MathF.Round((fy - par) / 2) * 2 + par;
            foreach (var r in new[] { r0 - 2, r0, r0 + 2 })
            {
                float d = HexPx(q, r).DistanceTo(p);
                if (d < bd) { bd = d; best = (q, r); }
            }
        }
        return best;
    }

    // Sectors of a cluster on the map: neighbouring flat-top hexes (like a small hex grid) that stay INSIDE the cluster
    // hex. Same order as GalaxyFacts.Slots: 2 = upper left, lower right; 3 = upper left, lower left, right.
    // Offsets in units of the sector hex radius (math y = north); the radius is solved once so every corner fits.
    static readonly (Vector2[] off, float r)[] Layouts = BuildLayouts();
    static (Vector2[] off, float r)[] BuildLayouts()
    {
        const float d = 1.7320508f * 1.08f;                       // centre distance of neighbouring hexes, plus a small gap
        float a = 0.2886751f * d;                                  // 3 sectors: left pair at -a, right one at +2a (centroid 0)
        var units = new[]
        {
            Array.Empty<Vector2>(),
            new[] { Vector2.Zero },
            new[] { new Vector2(-0.4330127f * d, 0.25f * d), new Vector2(0.4330127f * d, -0.25f * d) },   // along 150° / 330°
            new[] { new Vector2(-a, d / 2), new Vector2(-a, -d / 2), new Vector2(2 * a, 0) },
        };
        const float edge = 0.95f;                                  // cluster hex radius 1, keep 5 % clear of its border
        bool Fits(Vector2[] off, float r) => off.All(o => Enumerable.Range(0, 6).All(k =>
        {
            float x = MathF.Abs(o.X * r + r * MathF.Cos(MathF.PI / 3 * k)), y = MathF.Abs(o.Y * r + r * MathF.Sin(MathF.PI / 3 * k));
            return y <= 0.8660254f * edge + 1e-4f && 1.7320508f * x + y <= 1.7320508f * edge + 1e-4f;   // inside a flat-top hex
        }));
        return units.Select((off, n) =>
        {
            if (n <= 1) return (off, 0.93f);
            float lo = 0, hi = 1;
            for (int i = 0; i < 30; i++) { float m = (lo + hi) / 2; if (Fits(off, m)) lo = m; else hi = m; }
            return (off, lo);
        }).ToArray();
    }

    (Vector2 c, float r) SlotPx(Vector2 hexCentre, int n, int i)
    {
        var (off, r) = Layouts[Math.Clamp(n, 1, Layouts.Length - 1)];
        var o = off[Math.Min(i, off.Length - 1)];
        return (hexCentre + new Vector2(o.X * r * _gs, -o.Y * r * _gs), r * _gs);
    }

    (Vector2 c, float r) SectorPx(Sector s)
    {
        var cl = _app.P.ClusterOf(s.Id)!;
        return SlotPx(HexPx(cl.Q, cl.R), cl.Sectors.Count, cl.Sectors.IndexOf(s));
    }

    /// <summary>Screen place of a sector of the patched galaxy (same slots as own clusters).</summary>
    (Vector2 c, float r) BaseSectorPx(BaseGalaxy.ClusterInfo c, int i) => SlotPx(HexPx(c.Q, c.R), c.Sectors.Count, i);

    (BaseGalaxy.ClusterInfo c, BaseGalaxy.SectorInfo s)? BaseSectorAt(Vector2 p)
    {
        if (_app.Base == null) return null;
        foreach (var c in _app.Base.Clusters)
            for (int i = 0; i < c.Sectors.Count; i++)
            {
                var (sc, sr) = BaseSectorPx(c, i);
                if (sc.DistanceTo(p) < sr * 0.87f) return (c, c.Sectors[i]);
            }
        return null;
    }

    /// <summary>Screen point of a gate at (x, z) inside a base sector.</summary>
    Vector2? BaseGatePx(string sectorMacro, double x, double z)
    {
        if (_app.Base == null) return null;
        foreach (var c in _app.Base.Clusters)
            for (int i = 0; i < c.Sectors.Count; i++)
                if (c.Sectors[i].Macro == sectorMacro)
                {
                    var (sc, sr) = BaseSectorPx(c, i);
                    float len = (float)Math.Max(1, Math.Sqrt(x * x + z * z)), k = Math.Min(1f, len / (float)GalaxyFacts.SectorR) * sr * 0.78f;
                    return sc + new Vector2((float)x / len * k, -(float)z / len * k);
                }
        return null;
    }

    Sector? SectorAt(Vector2 p)
    {
        Sector? hit = null;
        foreach (var s in _app.P.AllSectors()) { var (c, r) = SectorPx(s); if (c.DistanceTo(p) < r * 0.87f) hit = s; }
        return hit;
    }

    Vector2 GatePx(Sector s, Gate g)
    {
        var (c, r) = SectorPx(s);
        float len = (float)Math.Max(1, Math.Sqrt(g.X * g.X + g.Z * g.Z)), k = Math.Min(1f, len / (float)GalaxyFacts.SectorR) * r * 0.78f;
        return c + new Vector2((float)g.X / len * k, -(float)g.Z / len * k);
    }

    static Vector2[] Hex(Vector2 c, float r) =>
        Enumerable.Range(0, 7).Select(i => c + new Vector2(r * MathF.Cos(MathF.PI / 3 * i), r * MathF.Sin(MathF.PI / 3 * i))).ToArray();

    /// <summary>Colour of a sector = its owner at game start (AI section), else the faction with the most stations there.</summary>
    static Color? SectorColour(Sector s)
    {
        // the owner set in the sector's AI section; else the faction with the most stations
        var best = s.Ai?.Owner ?? s.Stations.GroupBy(x => x.Faction).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
        return best != null && GalaxyFacts.Factions.TryGetValue(best, out var f) ? new Color(f.Colour) : null;
    }

    // ---------------------------------------------------------------------------------------------- drawing
    public override void _Draw()
    {
        var P = _app.P;
        var a = PxHex(Vector2.Zero); var b = PxHex(Size);
        var grid = new Color("1e2630");
        for (int q = a.q - 1; q <= b.q + 1; q++)
            for (int r = b.r - 2; r <= a.r + 2; r++)
                if ((((q + r) % 2) + 2) % 2 == 0) DrawPolyline(Hex(HexPx(q, r), _gs), grid, 1);

        // PATCH project: the galaxy it adds to, dimmed and locked (vanilla clusters cannot be changed here)
        if (_app.Base is { } bg)
            foreach (var c in bg.Clusters)
            {
                var p = HexPx(c.Q, c.R);
                if (p.X < -_gs * 2 || p.Y < -_gs * 2 || p.X > Size.X + _gs * 2 || p.Y > Size.Y + _gs * 2) continue;
                DrawColoredPolygon(Hex(p, _gs * 0.98f)[..6], new Color("151b22"));
                DrawPolyline(Hex(p, _gs * 0.98f), new Color("2a333e"), 1f);
                for (int i = 0; i < c.Sectors.Count; i++)
                {
                    var s = c.Sectors[i];
                    var (sp, sr) = BaseSectorPx(c, i);
                    DrawPolyline(Hex(sp, sr), new Color("323d4a"), 1f);
                    if (_gs > 34) Text(sp + new Vector2(0, c.Sectors.Count > 1 ? 4 : -sr * 0.2f), s.Name, sr > 40 ? 12 : 10, new Color("6b7a8c"), sr * 1.6f);
                    // unused vanilla gates (linked to nothing): can be reused by a patch
                    if (_gs > 34)
                        foreach (var g in s.Gates.Where(g => g.LinkedTo == null && P.BaseGates.All(bgt => bgt.Existing != g.Path)))
                            if (BaseGatePx(s.Macro, g.X, g.Z) is { } gp) DrawArc(gp, 3.5f, 0, MathF.Tau, 12, new Color("c9a24a"), 1.5f, true);
                }
            }

        if (_hover is { } h && P.ClusterAt(h.q, h.r) == null && _app.BaseClusterAt(h.q, h.r) == null && _app.Tool == "select" && _drag == null)
        {
            var p = HexPx(h.q, h.r);
            DrawColoredPolygon(Hex(p, _gs)[..6], new Color("18202a"));
            Text(p + new Vector2(0, 4), "double-click: new cluster", 11, new Color("4b5868"));
        }

        foreach (var c in P.Clusters)
        {
            var p = HexPx(c.Q, c.R);
            DrawColoredPolygon(Hex(p, _gs * 0.98f)[..6], new Color("1a222c"));
            DrawPolyline(Hex(p, _gs * 0.98f), new Color("3a4758"), 1.5f);
            bool sel = _app.Sel is { Kind: "sector" } x && P.ClusterOf(x.Id) == c;   // a click selects the whole cluster
            foreach (var s in c.Sectors)
            {
                var (sc, sr) = SectorPx(s); var col = SectorColour(s);
                DrawColoredPolygon(Hex(sc, sr)[..6], col is { } cc ? new Color(cc, 0.2f) : new Color("232d39"));
                DrawPolyline(Hex(sc, sr), sel ? new Color("cfe3d4") : col ?? new Color("56657a"), sel ? 1.8f : 1.2f);
            }
            // selected cluster: the green outline of the drag preview (easier to see)
            if (sel) DrawPolyline(Hex(p, _gs), new Color("7bd88f"), 3);
        }

        var done = new HashSet<int>();
        foreach (var s in P.AllSectors())
            foreach (var g in s.Gates)
            {
                if (g.Link == null || done.Contains(g.Id)) continue;
                var s2 = P.SectorOfGate(g.Link.Value); var g2 = P.GateById(g.Link); if (s2 == null || g2 == null) continue;
                done.Add(g2.Id);
                Vector2 p1 = GatePx(s, g), p2 = GatePx(s2, g2);
                DrawLine(p1, p2, g.Type == "accelerator" ? new Color("ffb347") : new Color("9fb4cc"), 2, true);
                DrawCircle(p1, 3.5f, new Color("e8f1ff")); DrawCircle(p2, 3.5f, new Color("e8f1ff"));
            }
        // PATCH: links into the patched galaxy
        foreach (var s in P.AllSectors())
            foreach (var g in s.Gates)
                if (P.BaseGateById(g.Link) is { } lk && BaseGatePx(lk.SectorMacro, lk.X, lk.Z) is { } bp)
                {
                    var p1 = GatePx(s, g);
                    DrawLine(p1, bp, new Color("9fb4cc"), 2, true);
                    DrawCircle(p1, 3.5f, new Color("e8f1ff")); DrawCircle(bp, 3.5f, lk.Existing != null ? new Color("c9a24a") : new Color("e8f1ff"));
                }
        foreach (var s in P.AllSectors())
            foreach (var g in s.Gates.Where(g => g.Link == null))
                DrawArc(GatePx(s, g), 3.5f, 0, MathF.Tau, 16, new Color("ffb347"), 1.5f, true);

        foreach (var s in P.AllSectors())
        {
            var (sc, sr) = SectorPx(s);
            if (_gs > 34) Text(sc + new Vector2(0, -sr * 0.45f), s.Name, sr > 40 ? 13 : 11, new Color("e6edf5"), sr * 1.6f);
            if (P.StartSector == s.Id && P.BaseGalaxy == null) Text(sc + new Vector2(0, sr * 0.6f), "▶ start", 12, new Color("7bd88f"));
            // resources: one coloured dot per ware (colours as the rocks / gas in the 3D view), bigger = more areas
            var wares = s.Res.GroupBy(r => r.Ware).Select(g => (ware: g.Key, n: g.Sum(r => r.Amount))).OrderBy(w => Array.IndexOf(GalaxyFacts.ResWare, w.ware)).ToList();
            if (_gs > 34 && wares.Count > 0)
            {
                float dot = Math.Clamp(sr * 0.07f, 3f, 6f), step = dot * 2.6f;
                var left = sc + new Vector2(-(wares.Count - 1) * step / 2, sr * 0.08f);
                for (int i = 0; i < wares.Count; i++)
                {
                    var col = DecorVisuals.WareColour.TryGetValue(wares[i].ware, out var wc) ? wc : new Color("9a9a9a");
                    DrawCircle(left + new Vector2(i * step, 0), dot * (wares[i].n >= 3 ? 1.25f : 1f), col);
                }
            }
        }

        if (_app.Tool == "connect" && P.SectorById(_app.ConnectFrom) is { } from)
            DrawDashedLine(SectorPx(from).c, _mouse, new Color("4fa3ff"), 2, 6);
        if (_drag is { Kind: "moveCluster", Moved: true } && _hover is { } hv)
        {
            var other = P.ClusterAt(hv.q, hv.r); bool free = (other == null || other == _drag.Cluster) && _app.BaseClusterAt(hv.q, hv.r) == null;
            DrawPolyline(Hex(HexPx(hv.q, hv.r), _gs), free ? new Color("7bd88f") : new Color("ff6b6b"), 3);
        }
    }

    /// <summary>PATCH: our sector dropped on a sector of the patched galaxy: new gate there, or reuse one of its unused gates.</summary>
    void ConnectToBaseMenu(Sector from, BaseGalaxy.ClusterInfo bc, BaseGalaxy.SectorInfo bs, Vector2 at)
    {
        var menu = new PopupMenu();
        var actions = new List<Action>();
        void Add(string text, Action a) { menu.AddItem(text, actions.Count); actions.Add(a); }
        int fromId = from.Id;
        Add($"New gate in {bs.Name}", () => _app.Change(() => _app.P.ConnectToBase(_app.P.SectorById(fromId)!, bc, bs, null)));
        var unused = bs.Gates.Where(g => g.LinkedTo == null && _app.P.BaseGates.All(bgt => bgt.Existing != g.Path)).ToList();
        foreach (var g in unused)
        {
            var gate = g;
            Add($"Use the unused vanilla gate in {bs.Name} ({g.Connection.Replace("connection_", "")}, {Math.Round(Math.Sqrt(g.X * g.X + g.Z * g.Z) / 1000)} km from the centre)",
                () => _app.Change(() => _app.P.ConnectToBase(_app.P.SectorById(fromId)!, bc, bs, gate)));
        }
        menu.IdPressed += id => actions[(int)id]();
        menu.PopupHide += () => { _app.WindowClosed(menu); menu.QueueFree(); };
        AddChild(menu);
        menu.Position = (Vector2I)(GetScreenPosition() + at);
        menu.Popup();
        _app.OpenWindow(menu, menu.Hide);
    }

    /// <summary>Right-click on the map: menu of the sector under the mouse (own: connect, open, delete; patched galaxy: look inside).</summary>
    /// <summary>
    /// Connect two of our sectors. If either has unconnected gates, a menu lets the user pick per side: one of those
    /// gates or a new one (radio items, the menu stays open until "Connect").
    /// </summary>
    void ConnectOwn(Sector a, Sector b, Vector2 at)
    {
        int aid = a.Id, bid = b.Id;
        var freeA = a.Gates.Where(g => g.Link == null).ToList();
        var freeB = b.Gates.Where(g => g.Link == null).ToList();
        void Do(int? ga, int? gb) => _app.Change(() =>
        {
            var P = _app.P;
            P.ConnectSectors(P.SectorById(aid)!, P.SectorById(bid)!, ga != null ? P.GateById(ga) : null, gb != null ? P.GateById(gb) : null);
        });
        if (freeA.Count == 0 && freeB.Count == 0) { Do(null, null); return; }

        var menu = new PopupMenu { HideOnCheckableItemSelection = false };
        int? pickA = freeA.FirstOrDefault()?.Id, pickB = freeB.FirstOrDefault()?.Id;   // an unconnected gate first: that is why one asks
        var groups = new Dictionary<int, (int group, int? gate)>();      // menu id -> side, gate (null = new)
        int nextId = 0;
        void Side(int group, Sector s, List<Gate> free, int? picked)
        {
            menu.AddSeparator(group == 0 ? $"Gate in {s.Name} (from)" : $"Gate in {s.Name} (to)");
            foreach (var g in free)
            {
                menu.AddRadioCheckItem("Unconnected gate " + Where(g), nextId); menu.SetItemChecked(menu.ItemCount - 1, picked == g.Id);
                groups[nextId++] = (group, g.Id);
            }
            menu.AddRadioCheckItem("New gate (placed towards the other sector)", nextId); menu.SetItemChecked(menu.ItemCount - 1, picked == null);
            groups[nextId++] = (group, null);
        }
        if (freeA.Count > 0) Side(0, a, freeA, pickA);
        if (freeB.Count > 0) Side(1, b, freeB, pickB);
        menu.AddSeparator();
        int connectId = nextId++, cancelId = nextId++;
        menu.AddItem($"Connect {a.Name} ↔ {b.Name}", connectId);
        menu.AddItem("Cancel", cancelId);
        menu.IdPressed += id =>
        {
            if (id == connectId) { menu.Hide(); Do(pickA, pickB); return; }
            if (id == cancelId) { menu.Hide(); return; }
            var (group, gate) = groups[(int)id];
            if (group == 0) pickA = gate; else pickB = gate;
            // radio: tick only the chosen one of its side
            foreach (var (mid, v) in groups) if (v.group == group) menu.SetItemChecked(menu.GetItemIndex(mid), mid == id);
        };
        menu.PopupHide += () => { _app.WindowClosed(menu); menu.QueueFree(); };
        AddChild(menu);
        menu.Position = (Vector2I)(GetScreenPosition() + at);
        menu.Popup();
        _app.OpenWindow(menu, menu.Hide);
    }

    /// <summary>"north-east, 150 km out": where a gate sits in its sector.</summary>
    static string Where(Gate g)
    {
        string[] dirs = { "north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west" };
        double ang = (Math.Atan2(g.X, g.Z) * 180 / Math.PI + 360) % 360;   // 0 = north (+z), 90 = east (+x)
        return $"{dirs[(int)Math.Round(ang / 45) % 8]}, {Math.Round(Math.Sqrt(g.X * g.X + g.Z * g.Z) / 1000)} km out";
    }

    /// <summary>Check runs: the gate choice between two sectors (--connectcheck a,b).</summary>
    public void ConnectCheck(int a, int b) { if (_app.P.SectorById(a) is { } sa && _app.P.SectorById(b) is { } sb) ConnectOwn(sa, sb, SectorPx(sb).c); }

    /// <summary>Check runs: the right-click menu of a sector (--rightclick id).</summary>
    public void MenuOf(int sid) { if (_app.P.SectorById(sid) is { } s) ContextMenu(SectorPx(s).c); }

    void ContextMenu(Vector2 at)
    {
        var P = _app.P;
        var menu = new PopupMenu();
        var actions = new List<Action>();
        void Add(PopupMenu m, string text, Action a, bool disabled = false) { m.AddItem(text, actions.Count); m.SetItemDisabled(m.ItemCount - 1, disabled); actions.Add(a); }
        if (SectorAt(at) is { } s)
        {
            int sid = s.Id; var c = P.ClusterOf(sid)!;
            _app.Sel = new Selection("sector", sid); _app.Refresh();
            // "Connect to": pick on the map (also sectors of the patched galaxy), or one of our sectors that has no
            // gate to this one yet (with its unconnected gates counted: those can be reused)
            var sub = new PopupMenu { Name = "connect" };
            Add(sub, "Pick the sector on the map ...", () => _app.StartConnect(sid));
            var linked = s.Gates.Select(g => P.SectorOfGate(g.Link ?? -1)?.Id).ToHashSet();
            var others = P.AllSectors().Where(o => o.Id != sid && !linked.Contains(o.Id)).OrderBy(o => o.Name).ToList();
            if (others.Count > 0) sub.AddSeparator("Your sectors not linked with " + s.Name);
            foreach (var o in others)
            {
                int oid = o.Id, free = o.Gates.Count(g => g.Link == null);
                Add(sub, o.Name + (free > 0 ? $"   ·   {free} unconnected gate{(free == 1 ? "" : "s")}" : ""), () => ConnectOwn(_app.P.SectorById(sid)!, _app.P.SectorById(oid)!, at));
            }
            sub.IdPressed += id => actions[(int)id]();
            menu.AddChild(sub); menu.AddSubmenuNodeItem("Connect to", sub);
            menu.AddSeparator();
            Add(menu, "Open sector", () => _app.OpenSector(sid));
            Add(menu, "Player starts here", () => _app.Change(() => _app.P.StartSector = sid), P.StartSector == sid || P.BaseGalaxy != null);
            Add(menu, $"Add sector to cluster ({c.Sectors.Count}/3)", () => _app.Change(() => { var ns = _app.P.AddSector(_app.P.Clusters.First(x => x.Id == c.Id)); _app.Sel = new Selection("sector", ns.Id); }), c.Sectors.Count >= 3);
            menu.AddSeparator();
            Add(menu, "Delete sector", () => { _app.Sel = new Selection("sector", sid); _app.DeleteSelection(); });
        }
        else if (BaseSectorAt(at) is { } bt)
        {
            Add(menu, $"Look inside {bt.s.Name} (read-only)", () => _app.OpenBaseSector(bt.c, bt.s));
            if (_app.Sel is { Kind: "sector" } sel && P.SectorById(sel.Id) is { } mine)
                Add(menu, $"Connect {mine.Name} to {bt.s.Name} ...", () => ConnectToBaseMenu(mine, bt.c, bt.s, at));
        }
        // whole map (also on empty space): both ask first, Ctrl+Z brings it back
        if (menu.ItemCount > 0) menu.AddSeparator();
        Add(menu, "Clear all sectors ...", _app.ClearAllSectors, !P.AllSectors().Any());
        Add(menu, "Clear map ...", _app.ClearMap, P.Clusters.Count == 0);
        menu.IdPressed += id => actions[(int)id]();
        menu.PopupHide += () => { _app.WindowClosed(menu); menu.QueueFree(); };
        AddChild(menu);
        menu.Position = (Vector2I)(GetScreenPosition() + at);
        menu.Popup();
        _app.OpenWindow(menu, menu.Hide);
    }

    void Text(Vector2 centre, string t, int size, Color col, float maxWidth = -1)
    {
        if (maxWidth > 0 && F.GetStringSize(t, HorizontalAlignment.Left, -1, size).X > maxWidth)
        {
            while (t.Length > 1 && F.GetStringSize(t + "…", HorizontalAlignment.Left, -1, size).X > maxWidth) t = t[..^1];
            t += "…";
        }
        float w = F.GetStringSize(t, HorizontalAlignment.Left, -1, size).X;
        DrawString(F, centre + new Vector2(-w / 2, 0), t, HorizontalAlignment.Left, -1, size, col);
    }

    // ---------------------------------------------------------------------------------------------- mouse
    /// <summary>
    /// WASD moves the map (W = north / up), Q / E zoom out / in around the view centre, Shift = 4 x faster; as the
    /// sector view's flight keys. Not while typing in a text field and not with Ctrl (Ctrl+S, Ctrl+Z are shortcuts).
    /// </summary>
    public override void _Process(double delta)
    {
        if (!IsVisibleInTree() || Input.IsKeyPressed(Key.Ctrl) || GetTree().Root.GuiGetFocusOwner() is LineEdit or TextEdit) return;
        var move = Vector2.Zero;
        if (Input.IsKeyPressed(Key.W)) move.Y += 1;
        if (Input.IsKeyPressed(Key.S)) move.Y -= 1;
        if (Input.IsKeyPressed(Key.A)) move.X += 1;
        if (Input.IsKeyPressed(Key.D)) move.X -= 1;
        int zoom = (Input.IsKeyPressed(Key.E) ? 1 : 0) - (Input.IsKeyPressed(Key.Q) ? 1 : 0);
        if (move == Vector2.Zero && zoom == 0) return;
        float fast = Input.IsKeyPressed(Key.Shift) ? 4f : 1f;
        if (move != Vector2.Zero)
        {
            var step = move.Normalized() * Size.X * 0.6f * fast * (float)delta;   // the map moves under the view: W shows what lies north
            _gx += step.X; _gy += step.Y;
        }
        if (zoom != 0)
        {
            float ns = Math.Clamp(_gs * MathF.Pow(1.8f * (fast > 1 ? 2 : 1), zoom * (float)delta), 14, 220), k = ns / _gs;
            _gx *= k; _gy *= k; _gs = ns;                               // around the view centre (pan is relative to it)
        }
        QueueRedraw();
    }

    public override void _GuiInput(InputEvent e)
    {
        var P = _app.P;
        if (e is InputEventMouseButton mb)
        {
            if (mb.Pressed && mb.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
            {
                float f = mb.ButtonIndex == MouseButton.WheelUp ? 1.15f : 1 / 1.15f;
                float ns = Math.Clamp(_gs * f, 14, 220), k = ns / _gs;
                var m = mb.Position - Size / 2;
                _gx = m.X - (m.X - _gx) * k; _gy = m.Y - (m.Y - _gy) * k; _gs = ns;
                QueueRedraw(); AcceptEvent(); return;
            }
            if (mb.Pressed && mb.ButtonIndex is MouseButton.Right or MouseButton.Middle) { _drag = new Drag { Kind = "pan", Start = mb.Position, At = mb.Position, Right = mb.ButtonIndex == MouseButton.Right }; return; }
            if (mb.ButtonIndex != MouseButton.Left && !mb.Pressed)
            {
                // a right click without dragging = the sector's menu (right drag still pans)
                if (_drag is { Kind: "pan", Right: true, Moved: false } && mb.ButtonIndex == MouseButton.Right) ContextMenu(mb.Position);
                if (_drag?.Kind == "pan") _drag = null;
                return;
            }
            if (mb.ButtonIndex != MouseButton.Left) return;

            if (mb.Pressed && mb.DoubleClick)
            {
                _drag = null;
                var s = SectorAt(mb.Position);
                if (s != null) { _app.OpenSector(s.Id); return; }
                if (BaseSectorAt(mb.Position) is { } bt) { _app.OpenBaseSector(bt.c, bt.s); return; }   // a sector of the patched galaxy: read-only
                var (q, r) = PxHex(mb.Position);
                if (_app.BaseClusterAt(q, r) is { } taken) _app.SetStatus($"This hex belongs to {taken.Name} ({(taken.Source == "base" ? "vanilla" : taken.Source)}): choose a free hex.");
                else if (P.ClusterAt(q, r) == null) _app.Change(() => { var ns = _app.P.AddCluster(q, r); _app.Sel = new Selection("sector", ns.Id); });
                return;
            }
            if (mb.Pressed && _app.Tool == "connect")
            {
                // "Connect to ...": this click picks the target; anywhere else cancels
                var from = P.SectorById(_app.ConnectFrom);
                var target = SectorAt(mb.Position);
                _app.SetTool("select");
                if (from == null) return;
                int fromId = from.Id;
                if (target != null && target != from) ConnectOwn(from, target, mb.Position);
                else if (target == null && BaseSectorAt(mb.Position) is { } bt) ConnectToBaseMenu(from, bt.c, bt.s, mb.Position);
                else _app.SetStatus("Connecting cancelled.");
                return;
            }
            if (mb.Pressed)
            {
                var s = SectorAt(mb.Position);
                if (s == null && P.ClusterAt(PxHex(mb.Position).q, PxHex(mb.Position).r) is { Sectors.Count: > 0 } hitCluster)
                    s = hitCluster.Sectors[0];                         // empty part of a cluster hex: the cluster
                if (s != null)
                {
                    _app.Sel = new Selection("sector", s.Id);
                    _drag = new Drag { Kind = "moveCluster", Cluster = P.ClusterOf(s.Id), Start = mb.Position, At = mb.Position };
                    _app.Refresh(); return;
                }
                _app.Sel = null; _drag = new Drag { Kind = "pan", Start = mb.Position, At = mb.Position }; _app.Refresh();
                return;
            }
            // left button released
            var d = _drag; _drag = null;
            if (d is { Kind: "moveCluster", Moved: true })
            {
                var (q, r) = PxHex(mb.Position); int cid = d.Cluster!.Id;
                if (P.ClusterAt(q, r) == null && _app.BaseClusterAt(q, r) == null) _app.Change(() => { var c = _app.P.Clusters.First(x => x.Id == cid); c.Q = q; c.R = r; });
            }
            QueueRedraw();
        }
        else if (e is InputEventMouseMotion mm)
        {
            _hover = PxHex(mm.Position); _mouse = mm.Position;
            if (_drag != null)
            {
                if (_drag.Kind == "pan") { _gx += mm.Relative.X; _gy += mm.Relative.Y; if (mm.Position.DistanceTo(_drag.Start) > 6) _drag.Moved = true; }
                else if (_drag.Kind == "moveCluster" && mm.Position.DistanceTo(_drag.Start) > 6) _drag.Moved = true;
                _drag.At = mm.Position;
            }
            QueueRedraw();
        }
    }
}
