using System;
using System.Linq;
using Godot;
using X4Data.Galaxy;

namespace GalaxyEditor3D;

/// <summary>
/// 2D top view of the open sector in the corner of the 3D view: rings every 50 km, gates,
/// decorations (fields as their real outline seen from above), the camera with its view direction.
/// Left click / drag on it: the camera flies there. North is up, east right.
/// </summary>
public partial class Minimap : Control
{
    readonly App _app;
    readonly SectorView _view;
    const float SizePx = 250;
    bool _dragging;

    public Minimap(App app, SectorView view)
    {
        _app = app; _view = view;
        CustomMinimumSize = new Vector2(SizePx, SizePx);
        Size = new Vector2(SizePx, SizePx);
        MouseFilter = MouseFilterEnum.Stop;                       // clicks here do not reach the 3D view
    }

    public override void _Process(double delta)
    {
        if (!IsVisibleInTree()) return;
        Position = new Vector2(GetParentAreaSize().X - SizePx - 10, 10);   // top right corner
        QueueRedraw();
    }

    float Scale => (SizePx / 2 - 8) / (float)_view.Reach;
    Vector2 ToPx(double x, double z) => new(SizePx / 2 + (float)x * Scale, SizePx / 2 - (float)z * Scale);
    (double x, double z) FromPx(Vector2 p) => ((p.X - SizePx / 2) / Scale, -(p.Y - SizePx / 2) / Scale);

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), new Color(0.04f, 0.06f, 0.09f, 0.82f));
        DrawRect(new Rect2(Vector2.Zero, Size), new Color("3a4758"), false, 1);
        var c = new Vector2(SizePx / 2, SizePx / 2);
        for (int km = 50; km * 1000 <= _view.Reach; km += 50)
            DrawArc(c, km * 1000 * Scale, 0, MathF.Tau, 48, km % 100 == 0 ? new Color(0.35f, 0.45f, 0.6f, 0.6f) : new Color(0.25f, 0.32f, 0.42f, 0.4f), 1);
        DrawLine(new Vector2(c.X, 4), new Vector2(c.X, SizePx - 4), new Color(0.3f, 0.4f, 0.55f, 0.5f));
        DrawLine(new Vector2(4, c.Y), new Vector2(SizePx - 4, c.Y), new Color(0.3f, 0.4f, 0.55f, 0.5f));
        DrawString(ThemeDB.FallbackFont, new Vector2(c.X + 3, 14), "N", HorizontalAlignment.Left, -1, 11, new Color(0.6f, 0.75f, 1f));

        var s = _app.P.SectorById(_app.OpenSectorId);
        if (s == null) return;
        // fields: their outline from above
        foreach (var d in s.Decor.Where(d => d.Kind == "region"))
        {
            var def = _app.Decor?.Regions.Get(d.Ref);
            if (def == null) continue;
            var col = DecorVisuals.CategoryColour.TryGetValue(def.Category, out var cc) ? cc : Colors.Gray;
            var rot = DecorVisuals.Rotation(d);
            if (def.Boundary == "splinetube" && def.Spline.Count >= 2)
            {
                var path = X4Data.Space.Regions.SamplePath(def, Math.Max(2000, def.R)).Select(p =>
                {
                    var g = rot * new Vector3(p.X, p.Y, -p.Z);                         // turned like in 3D
                    return ToPx(d.X + g.X, d.Z - g.Z);
                }).ToArray();
                if (path.Length > 1) DrawPolyline(path, new Color(col, 0.7f), Math.Max(1, def.R * Scale * 2), true);
            }
            else DrawCircle(ToPx(d.X, d.Z), Math.Max(2, def.R * Scale), new Color(col, 0.35f));
        }
        // markers (positions follow a drag in progress)
        foreach (var (kind, id, x, z, colour) in _view.MapItems())
        {
            var p = ToPx(x, z);
            bool sel = _app.Sel != null && _app.Sel.Kind == kind && _app.Sel.Id == id;
            if (kind == "gate") DrawArc(p, 4, 0, MathF.Tau, 12, sel ? new Color("ffe066") : colour, 2);
            else DrawRect(new Rect2(p - new Vector2(3, 3), new Vector2(6, 6)), sel ? new Color("ffe066") : colour);
        }
        // camera: target cross and view direction
        var cam = _view.Camera;
        var t = ToPx(cam.Target.X, -cam.Target.Z);
        var fwd = -cam.GlobalBasis.Z; var dir = new Vector2(fwd.X, fwd.Z);
        if (dir.Length() > 0.01f) DrawLine(t, t + dir.Normalized() * 22, new Color("7bd88f"), 2);
        DrawLine(t - new Vector2(5, 0), t + new Vector2(5, 0), new Color("7bd88f"), 2);
        DrawLine(t - new Vector2(0, 5), t + new Vector2(0, 5), new Color("7bd88f"), 2);
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } b)
        {
            _dragging = b.Pressed;
            if (b.Pressed) Jump(b.Position);
            AcceptEvent();
        }
        else if (e is InputEventMouseMotion m && _dragging) { Jump(m.Position); AcceptEvent(); }
    }

    void Jump(Vector2 p) { var (x, z) = FromPx(p); _view.LookAtX4(x, z); }
}
