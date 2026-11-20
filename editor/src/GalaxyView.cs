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

    sealed class Drag { public string Kind = ""; public Sector? From; public Cluster? Cluster; public Vector2 Start, At; public bool Moved; }

    public GalaxyView(App app) { _app = app; app.Changed += QueueRedraw; FocusMode = FocusModeEnum.Click; }

    public void CentreGalaxy()
    {
        _gs = 60; _gx = 0; _gy = 0;
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

    (Vector2 c, float r) SectorPx(Sector s)
    {
        var cl = _app.P.ClusterOf(s.Id)!; int n = cl.Sectors.Count; var slot = GalaxyFacts.Slots[n][cl.Sectors.IndexOf(s)]; var p = HexPx(cl.Q, cl.R);
        return n == 1 ? (p, _gs * 0.93f) : (p + new Vector2(slot[0] * _gs * 0.46f, -slot[1] * _gs * 0.44f), _gs * 0.42f);
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

    /// <summary>Colour of a sector = faction with the most stations there (stations come from the population editor later).</summary>
    static Color? SectorColour(Sector s)
    {
        var best = s.Stations.GroupBy(x => x.Faction).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
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

        if (_hover is { } h && P.ClusterAt(h.q, h.r) == null && _app.Tool == "select" && _drag == null)
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
            foreach (var s in c.Sectors)
            {
                var (sc, sr) = SectorPx(s); var col = SectorColour(s); bool sel = _app.Sel is { Kind: "sector" } x && x.Id == s.Id;
                DrawColoredPolygon(Hex(sc, sr)[..6], col is { } cc ? new Color(cc, 0.2f) : new Color("232d39"));
                DrawPolyline(Hex(sc, sr), sel ? Colors.White : col ?? new Color("56657a"), sel ? 2.5f : 1.2f);
            }
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
        foreach (var s in P.AllSectors())
            foreach (var g in s.Gates.Where(g => g.Link == null))
                DrawArc(GatePx(s, g), 3.5f, 0, MathF.Tau, 16, new Color("ffb347"), 1.5f, true);

        foreach (var s in P.AllSectors())
        {
            var (sc, sr) = SectorPx(s);
            if (_gs > 34) Text(sc + new Vector2(0, -sr * 0.45f), s.Name, sr > 40 ? 13 : 11, new Color("e6edf5"), sr * 1.6f);
            if (P.StartSector == s.Id) Text(sc + new Vector2(0, sr * 0.6f), "▶ start", 12, new Color("7bd88f"));
        }

        if (_drag is { Kind: "connect", From: { } from })
            DrawDashedLine(SectorPx(from).c, _drag.At, new Color("4fa3ff"), 2, 6);
        if (_drag is { Kind: "moveCluster", Moved: true } && _hover is { } hv)
        {
            var other = P.ClusterAt(hv.q, hv.r); bool free = other == null || other == _drag.Cluster;
            DrawPolyline(Hex(HexPx(hv.q, hv.r), _gs), free ? new Color("7bd88f") : new Color("ff6b6b"), 3);
        }
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
            if (mb.Pressed && mb.ButtonIndex is MouseButton.Right or MouseButton.Middle) { _drag = new Drag { Kind = "pan", Start = mb.Position, At = mb.Position }; return; }
            if (mb.ButtonIndex != MouseButton.Left && !mb.Pressed) { if (_drag?.Kind == "pan") _drag = null; return; }
            if (mb.ButtonIndex != MouseButton.Left) return;

            if (mb.Pressed && mb.DoubleClick)
            {
                _drag = null;
                var s = SectorAt(mb.Position);
                if (s != null) { _app.OpenSector(s.Id); return; }
                var (q, r) = PxHex(mb.Position);
                if (P.ClusterAt(q, r) == null) _app.Change(() => { var ns = _app.P.AddCluster(q, r); _app.Sel = new Selection("sector", ns.Id); });
                return;
            }
            if (mb.Pressed)
            {
                var s = SectorAt(mb.Position);
                if (s != null && _app.Tool == "connect") { _drag = new Drag { Kind = "connect", From = s, At = mb.Position }; return; }
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
            if (d?.Kind == "connect")
            {
                var s2 = SectorAt(mb.Position);
                if (s2 != null && s2 != d.From) _app.Change(() => _app.P.ConnectSectors(_app.P.SectorById(d.From!.Id)!, _app.P.SectorById(s2.Id)!));
            }
            else if (d is { Kind: "moveCluster", Moved: true })
            {
                var (q, r) = PxHex(mb.Position); int cid = d.Cluster!.Id;
                if (P.ClusterAt(q, r) == null) _app.Change(() => { var c = _app.P.Clusters.First(x => x.Id == cid); c.Q = q; c.R = r; });
            }
            QueueRedraw();
        }
        else if (e is InputEventMouseMotion mm)
        {
            _hover = PxHex(mm.Position);
            if (_drag != null)
            {
                if (_drag.Kind == "pan") { _gx += mm.Relative.X; _gy += mm.Relative.Y; }
                else if (_drag.Kind == "moveCluster" && mm.Position.DistanceTo(_drag.Start) > 6) _drag.Moved = true;
                _drag.At = mm.Position;
            }
            QueueRedraw();
        }
    }
}
