using System;
using System.Collections.Generic;
using Godot;

namespace GalaxyEditor3D;

/// <summary>
/// Move / turn handles on the selected gate or decoration, like X4's station designer: one arrow per axis
/// (red = east x, green = up y, blue = north z) and one ring per turn (green = yaw around "up"; for decorations also
/// red = pitch, blue = roll, both turning with the object as in a gimbal). Always drawn on top, same size on screen.
/// The gizmo only shows and computes: SectorView asks Pick() on a click, then Drag() while the mouse moves, and writes
/// the result into the project on release (one undo step).
/// Rotation as DecorVisuals.Rotation: basis = Up(-yaw) * Right(-pitch) * Back(+roll). Changing one angle turns the
/// object around that angle's gimbal axis: yaw = world up, pitch = Up(-yaw) * right, roll = Up(-yaw) * Right(-pitch) * back.
/// </summary>
public partial class Gizmo : Node3D
{
    public enum Handle { None, MoveX, MoveY, MoveZ, Yaw, Pitch, Roll }

    const float RingRadius = 0.78f, PickPixels = 12f;
    static readonly Color Red = new(1f, 0.28f, 0.25f), Green = new(0.35f, 0.95f, 0.35f), Blue = new(0.3f, 0.55f, 1f), Hot = new(1f, 0.9f, 0.35f);

    readonly Dictionary<Handle, (Node3D node, StandardMaterial3D mat, Color colour)> _parts = new();
    Camera3D _cam = null!;
    Node3D? _target;
    /// <summary>Show the pitch / roll rings (decorations); gates only turn around "up".</summary>
    bool _allRings;
    Handle _hover = Handle.None;

    // the object's angles in degrees (X4 meaning); updated while dragging
    public double Yaw, Pitch, Roll;

    // drag state
    Handle _drag = Handle.None;
    Vector3 _startPos, _axis, _ringFrom;
    float _startT, _theta;
    double _startAngle;
    Vector2 _lastScreen;

    public Handle Dragging => _drag;

    public void Init(Camera3D cam)
    {
        _cam = cam;
        Arrow(Handle.MoveX, Vector3.Right, Red);
        Arrow(Handle.MoveY, Vector3.Up, Green);
        Arrow(Handle.MoveZ, Vector3.Forward, Blue);           // Godot -z = X4 +z (north)
        Ring(Handle.Yaw, Green); Ring(Handle.Pitch, Red); Ring(Handle.Roll, Blue);
        Visible = false;
    }

    /// <summary>Attach to an item's holder (null = hide). Angles are the item's current yaw / pitch / roll.</summary>
    public void Attach(Node3D? holder, bool allRings, double yaw, double pitch, double roll)
    {
        _target = holder; _allRings = allRings; Yaw = yaw; Pitch = pitch; Roll = roll; _drag = Handle.None;
        Visible = holder != null;
        _parts[Handle.Pitch].node.Visible = _parts[Handle.Roll].node.Visible = allRings;
        Place();
    }

    public override void _Process(double delta) { if (Visible) Place(); }

    /// <summary>Follow the target; scale so the gizmo is always about the same size on screen; turn the gimbal rings.</summary>
    void Place()
    {
        if (_target == null || !IsInstanceValid(_target) || _cam == null) return;
        Position = _target.Position;
        float dist = Math.Max(1f, _cam.GlobalPosition.DistanceTo(Position));
        float size = dist * Mathf.Tan(Mathf.DegToRad(_cam.Fov) / 2f) * 0.22f;
        Scale = Vector3.One * size;
        var yawB = new Basis(Vector3.Up, -Mathf.DegToRad((float)Yaw));
        var pitchB = new Basis(Vector3.Right, -Mathf.DegToRad((float)Pitch));
        // TorusMesh lies flat (axis = y): turn it so its axis is the angle's gimbal axis
        _parts[Handle.Yaw].node.Basis = Basis.Identity;
        _parts[Handle.Pitch].node.Basis = yawB * new Basis(Vector3.Back, -Mathf.Pi / 2);      // axis y -> +x
        _parts[Handle.Roll].node.Basis = yawB * pitchB * new Basis(Vector3.Right, Mathf.Pi / 2); // axis y -> +z (back)
    }

    /// <summary>World axis of a handle (unit length).</summary>
    Vector3 AxisOf(Handle h) => h switch
    {
        Handle.MoveX => Vector3.Right,
        Handle.MoveY => Vector3.Up,
        Handle.MoveZ => Vector3.Forward,
        _ => _parts[h].node.Basis.Y.Normalized(),             // ring: its torus axis
    };

    // ---------------------------------------------------------------------------------------------- picking
    /// <summary>The handle under the mouse (within a few pixels), or None.</summary>
    public Handle Pick(Vector2 screen)
    {
        if (!Visible || _target == null) return Handle.None;
        Handle best = Handle.None; float bestD = PickPixels;
        void Try(Handle h, float d) { if (d < bestD) { bestD = d; best = h; } }
        float s = Scale.X;
        foreach (var h in new[] { Handle.MoveX, Handle.MoveY, Handle.MoveZ })
        {
            var a = Position + AxisOf(h) * s * 0.12f; var b = Position + AxisOf(h) * s * 1.05f;
            if (_cam.IsPositionBehind(a) || _cam.IsPositionBehind(b)) continue;
            Try(h, SegmentDistance(screen, _cam.UnprojectPosition(a), _cam.UnprojectPosition(b)));
        }
        foreach (var h in new[] { Handle.Yaw, Handle.Pitch, Handle.Roll })
        {
            if (!_parts[h].node.Visible) continue;
            var basis = _parts[h].node.Basis;
            Vector2? prev = null;
            for (int i = 0; i <= 64; i++)
            {
                float t = i / 64f * Mathf.Tau;
                var p = Position + (basis.X.Normalized() * Mathf.Cos(t) + basis.Z.Normalized() * Mathf.Sin(t)) * RingRadius * s;
                if (_cam.IsPositionBehind(p)) { prev = null; continue; }
                var sp = _cam.UnprojectPosition(p);
                if (prev is { } q) Try(h, SegmentDistance(screen, q, sp));
                prev = sp;
            }
        }
        return best;
    }

    /// <summary>Highlights the handle under the mouse (not while dragging: then the dragged one stays lit).</summary>
    public void Hover(Vector2 screen)
    {
        var h = _drag != Handle.None ? _drag : Pick(screen);
        if (h == _hover) return;
        _hover = h;
        foreach (var (k, part) in _parts) part.mat.AlbedoColor = new Color(k == h ? Hot : part.colour, k == h ? 1f : 0.85f);
    }

    static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a; float len = ab.LengthSquared();
        float t = len < 1e-6f ? 0 : Math.Clamp((p - a).Dot(ab) / len, 0, 1);
        return p.DistanceTo(a + ab * t);
    }

    // ---------------------------------------------------------------------------------------------- dragging
    public void BeginDrag(Handle h, Vector2 screen)
    {
        _drag = h; _startPos = Position; _axis = AxisOf(h); _lastScreen = screen;
        if (h is Handle.MoveX or Handle.MoveY or Handle.MoveZ) _startT = AxisParam(screen) ?? 0;
        else
        {
            _startAngle = h == Handle.Yaw ? Yaw : h == Handle.Pitch ? Pitch : Roll;
            _ringFrom = RingPoint(screen) ?? Vector3.Zero; _theta = 0;
        }
        Hover(screen);
    }

    /// <summary>
    /// Mouse moved while dragging. Returns the new position (Godot space) for move handles; for rings the angles
    /// (Yaw / Pitch / Roll) change. snap: 1 km grid for moves, 5° steps for turns.
    /// </summary>
    public Vector3 Drag(Vector2 screen, bool snap)
    {
        var rel = screen - _lastScreen; _lastScreen = screen;
        if (_drag is Handle.MoveX or Handle.MoveY or Handle.MoveZ)
        {
            if (AxisParam(screen) is not float t) return Position;
            var p = _startPos + _axis * (t - _startT);
            if (snap)
            {
                // only the dragged component snaps; the others stay as they were
                if (_drag == Handle.MoveX) p.X = Mathf.Round(p.X / 1000) * 1000;
                if (_drag == Handle.MoveY) p.Y = Mathf.Round(p.Y / 1000) * 1000;
                if (_drag == Handle.MoveZ) p.Z = Mathf.Round(p.Z / 1000) * 1000;
            }
            Position = p;
            return p;
        }
        if (_drag is Handle.Yaw or Handle.Pitch or Handle.Roll)
        {
            // turn around the ring's axis (right hand, Godot space), summed up step by step
            if (RingPoint(screen) is { } now && _ringFrom != Vector3.Zero) { _theta += _ringFrom.SignedAngleTo(now, _axis); _ringFrom = now; }
            else
            {
                _theta += rel.X * 0.01f;                               // ring seen edge-on: sideways mouse movement turns it
                _ringFrom = RingPoint(screen) ?? Vector3.Zero;
            }
            float theta = _theta;
            // X4 angle from the turn: yaw and pitch run against the Godot rotation, roll with it (see class comment)
            double deg = Mathf.RadToDeg(theta) * (_drag == Handle.Roll ? 1 : -1);
            double a = _startAngle + deg;
            if (snap) a = Math.Round(a / 5) * 5;
            a = Normalize(a);
            if (_drag == Handle.Yaw) Yaw = a; else if (_drag == Handle.Pitch) Pitch = a; else Roll = a;
            if (_drag != Handle.Roll) Place();                        // the outer gimbal turns the inner rings
        }
        return Position;
    }

    public void EndDrag() { _drag = Handle.None; }

    static double Normalize(double a) { a %= 360; if (a > 180) a -= 360; if (a <= -180) a += 360; return Math.Round(a, 1); }

    /// <summary>Where along the drag axis (metres from the start position) the mouse ray passes closest; null if the axis points at the camera.</summary>
    float? AxisParam(Vector2 screen)
    {
        var o = _cam.ProjectRayOrigin(screen); var d = _cam.ProjectRayNormal(screen);
        var w0 = _startPos - o;
        float b = _axis.Dot(d), dd = _axis.Dot(w0), e = d.Dot(w0);
        float denom = 1 - b * b;
        if (denom < 1e-3f) return null;
        return (b * e - dd) / denom;
    }

    /// <summary>Mouse ray hit on the ring's plane, as a vector from the centre; null when the plane is seen edge-on.</summary>
    Vector3? RingPoint(Vector2 screen)
    {
        var o = _cam.ProjectRayOrigin(screen); var d = _cam.ProjectRayNormal(screen);
        float den = d.Dot(_axis);
        if (Math.Abs(den) < 0.08f) return null;
        float t = (Position - o).Dot(_axis) / den;
        if (t <= 0) return null;
        var v = o + d * t - Position;
        return v.LengthSquared() < 1e-6f ? null : v;
    }

    // ---------------------------------------------------------------------------------------------- meshes
    StandardMaterial3D Mat(Color c) => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, NoDepthTest = true, RenderPriority = 100,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha, AlbedoColor = new Color(c, 0.85f),
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    MeshInstance3D Inst(Mesh m, Material mat) => new() { Mesh = m, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };

    /// <summary>Shaft + cone along dir (unit size: tip at 1.05).</summary>
    void Arrow(Handle h, Vector3 dir, Color c)
    {
        var mat = Mat(c);
        var node = new Node3D { Name = h.ToString() };
        // CylinderMesh / CylinderMesh-cone stand along +y: turn +y onto dir
        var turn = dir == Vector3.Up ? Basis.Identity : new Basis(new Quaternion(Vector3.Up, dir));
        var shaft = Inst(new CylinderMesh { TopRadius = 0.016f, BottomRadius = 0.016f, Height = 0.75f, RadialSegments = 10, Rings = 1 }, mat);
        shaft.Transform = new Transform3D(turn, dir * (0.12f + 0.75f / 2));
        var cone = Inst(new CylinderMesh { TopRadius = 0f, BottomRadius = 0.06f, Height = 0.2f, RadialSegments = 16, Rings = 1 }, mat);
        cone.Transform = new Transform3D(turn, dir * (0.87f + 0.1f));
        node.AddChild(shaft); node.AddChild(cone);
        AddChild(node);
        _parts[h] = (node, mat, c);
    }

    void Ring(Handle h, Color c)
    {
        var mat = Mat(c);
        var node = new Node3D { Name = h.ToString() };
        node.AddChild(Inst(new TorusMesh { InnerRadius = RingRadius - 0.012f, OuterRadius = RingRadius + 0.012f, Rings = 96, RingSegments = 6 }, mat));
        AddChild(node);
        _parts[h] = (node, mat, c);
    }
}
