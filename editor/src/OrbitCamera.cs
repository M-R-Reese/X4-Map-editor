using System;
using Godot;

namespace GalaxyEditor3D;

/// <summary>
/// Camera that circles a target point, like the game's map:
/// right mouse = turn, middle mouse (or Shift + right) = move sideways, wheel = zoom, F = frame again,
/// left mouse = LeftPressed / LeftDragged / LeftReleased events (select, drag objects), right click without turning = RightClicked (menu),
/// W / S forward / back, A / D left / right, Q / E down / up (along the ecliptic, not the view tilt), Shift = 4 x faster.
/// </summary>
public partial class OrbitCamera : Camera3D
{
    public Vector3 Target;
    public float Distance = 10000f;
    float _yaw = 0.6f, _pitch = -0.35f;
    Aabb _frameBox; float _frameFactor = 1f;
    bool _leftDown;
    /// <summary>Left mouse button: pressed / moved while held (position, movement, Shift) / released, in viewport pixels.</summary>
    public event Action<Vector2>? LeftPressed;
    public event Action<Vector2, Vector2, bool>? LeftDragged;
    public event Action<Vector2>? LeftReleased;
    /// <summary>Right button pressed and released without turning the view (context menu), viewport pixels.</summary>
    public event Action<Vector2>? RightClicked;
    Vector2? _rightDown;
    /// <summary>Asked every frame before WASD moves the camera (false while typing in a text field, or view hidden).</summary>
    public Func<bool>? KeysAllowed;

    /// <summary>WASD / QE flight: moves the target (and so the camera) at a speed that fits the zoom.</summary>
    public override void _Process(double delta)
    {
        if (KeysAllowed?.Invoke() != true || Input.IsKeyPressed(Key.Ctrl)) return;   // Ctrl+S / Ctrl+Z are shortcuts, not flying
        var move = Vector3.Zero;
        // forward = where the camera looks, flattened onto the ecliptic; right = camera's right, flattened
        var fwd = -GlobalBasis.Z; fwd.Y = 0; fwd = fwd.LengthSquared() > 1e-6f ? fwd.Normalized() : Vector3.Forward;
        var right = GlobalBasis.X; right.Y = 0; right = right.LengthSquared() > 1e-6f ? right.Normalized() : Vector3.Right;
        if (Input.IsKeyPressed(Key.W)) move += fwd;
        if (Input.IsKeyPressed(Key.S)) move -= fwd;
        if (Input.IsKeyPressed(Key.D)) move += right;
        if (Input.IsKeyPressed(Key.A)) move -= right;
        if (Input.IsKeyPressed(Key.E)) move += Vector3.Up;
        if (Input.IsKeyPressed(Key.Q)) move -= Vector3.Up;
        if (move == Vector3.Zero) return;
        float speed = Distance * 0.8f * (Input.IsKeyPressed(Key.Shift) ? 4f : 1f);   // whole view width in about a second
        Target += move.Normalized() * speed * (float)delta;
        UpdatePose();
    }

    public override void _Ready()
    {
        Near = 5f; Far = 3_000_000f; Fov = 60f;
        UpdatePose();
    }

    public void SetView(float pitch, float yaw) { _pitch = pitch; _yaw = yaw; UpdatePose(); }

    /// <summary>Points the camera at a box from a distance that shows all of it (factor &lt; 1 = closer).</summary>
    public void Frame(Aabb box, float factor = 1f)
    {
        _frameBox = box; _frameFactor = factor;
        Target = box.GetCenter();
        // half the diagonal must fit into the field of view (60 degrees: tan 30 = 0.577)
        Distance = Math.Max(box.Size.Length(), 10f) * 0.5f / 0.577f * 1.05f * factor;
        UpdatePose();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventMouseButton b)
        {
            if (b.ButtonIndex == MouseButton.Left)
            {
                _leftDown = b.Pressed;
                if (b.Pressed) LeftPressed?.Invoke(b.Position); else LeftReleased?.Invoke(b.Position);
                return;
            }
            if (b.ButtonIndex == MouseButton.Right)
            {
                if (b.Pressed) _rightDown = b.Position;
                else if (_rightDown is { } rd && rd.DistanceTo(b.Position) < 5) { _rightDown = null; RightClicked?.Invoke(b.Position); }
                return;
            }
            if (!b.Pressed) return;
            if (b.ButtonIndex == MouseButton.WheelUp) Distance *= 0.88f;
            else if (b.ButtonIndex == MouseButton.WheelDown) Distance /= 0.88f;
            else return;
            Distance = Math.Clamp(Distance, 20f, 2_000_000f);
            UpdatePose();
        }
        else if (e is InputEventMouseMotion m)
        {
            if (_leftDown && (m.ButtonMask & MouseButtonMask.Left) != 0) { LeftDragged?.Invoke(m.Position, m.Relative, m.ShiftPressed); return; }
            bool pan = (m.ButtonMask & MouseButtonMask.Middle) != 0 || ((m.ButtonMask & MouseButtonMask.Right) != 0 && m.ShiftPressed);
            if (pan)
            {
                float k = Distance * 0.0012f;
                Target += (-GlobalBasis.X * m.Relative.X + GlobalBasis.Y * m.Relative.Y) * k;
                UpdatePose();
            }
            else if ((m.ButtonMask & MouseButtonMask.Right) != 0)
            {
                _yaw -= m.Relative.X * 0.005f;
                _pitch = Math.Clamp(_pitch - m.Relative.Y * 0.005f, -1.55f, 1.55f);
                UpdatePose();
            }
        }
        else if (e is InputEventKey k && k.Pressed && k.Keycode == Key.F) Frame(_frameBox, _frameFactor);
    }

    /// <summary>Applies a changed Target / Distance.</summary>
    public void Refresh() => UpdatePose();

    void UpdatePose()
    {
        var dir = new Vector3(MathF.Sin(_yaw) * MathF.Cos(_pitch), -MathF.Sin(_pitch), MathF.Cos(_yaw) * MathF.Cos(_pitch));
        Position = Target + dir * Distance;
        if (IsInsideTree()) LookAt(Target, Vector3.Up);
        else LookAtFromPosition(Position, Target, Vector3.Up);
    }
}
