using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using X4Data.Galaxy;
using X4Data.Space;

namespace GalaxyEditor3D;

/// <summary>
/// Preview pictures of the decoration palette (128 x 128 PNG), rendered by the editor itself in a hidden 3D view:
///   objects (wrecks, landmarks, asteroids ...): their model from a three-quarter view;
///   fields (regions): straight from above, so the picture shows the shape and how the rocks / gas are spread.
/// Looked up in two folders: "previews" next to the program (pictures a release ships) and the user cache
/// (user://previews, what this PC rendered). Vanilla and DLC items are rendered by themselves in the background;
/// items of other authors' mods only on request (MakeModPreviews), because they change with every mod list.
/// One item per few frames, so the editor stays usable meanwhile.
/// </summary>
public partial class Previews : Node
{
    /// <summary>Bumped when the look of the pictures changes: older pictures are then rendered again.</summary>
    const int Look = 4;
    const int Size = 128;

    readonly App _app;
    readonly Dictionary<string, Texture2D?> _loaded = new();
    readonly List<DecorCatalog.Item> _queue = new();
    readonly HashSet<string> _queued = new();
    SubViewport _vp = null!;
    Camera3D _cam = null!;
    DecorVisuals _visuals = null!;
    Node3D? _subject; DecorCatalog.Item? _current; int _wait; int _done;

    /// <summary>A picture became available (key = Key(item)).</summary>
    public event Action<string>? Ready;

    public Previews(App app) => _app = app;

    public static string Key(DecorCatalog.Item i) => $"{i.Kind}__{i.Ref}".ToLowerInvariant();

    static string Clean(string key) => new string(key.Select(c => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-' ? c : '_').ToArray());

    /// <summary>Folder of pictures shipped with the program (next to the exe; in the Godot editor: the project folder).</summary>
    static string ShippedFolder => Path.Combine(OS.HasFeature("editor") ? ProjectSettings.GlobalizePath("res://") : Path.GetDirectoryName(OS.GetExecutablePath())!, "previews", $"v{Look}");
    static string CacheFolder => Path.Combine(ProjectSettings.GlobalizePath("user://previews"), $"v{Look}");

    public int Pending => _queue.Count + (_current != null ? 1 : 0);
    public int DoneThisSession => _done;

    public override void _Ready()
    {
        _vp = new SubViewport
        {
            Size = new Vector2I(Size, Size), OwnWorld3D = true, TransparentBg = true, Msaa3D = Viewport.Msaa.Msaa4X,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
        };
        AddChild(_vp);
        _vp.AddChild(new WorldEnvironment { Environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.ClearColor,
            AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.6f, 0.65f, 0.75f), AmbientLightEnergy = 0.6f,
            ReflectedLightSource = Godot.Environment.ReflectionSource.Sky,
            Sky = new Sky { SkyMaterial = new ProceduralSkyMaterial
            {
                SkyTopColor = new Color(0.16f, 0.2f, 0.3f), SkyHorizonColor = new Color(0.3f, 0.32f, 0.38f),
                GroundBottomColor = new Color(0.05f, 0.05f, 0.07f), GroundHorizonColor = new Color(0.22f, 0.23f, 0.27f),
            } },
            TonemapMode = Godot.Environment.ToneMapper.Aces, TonemapWhite = 6f,
        } });
        _vp.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-40, 35, 0), LightEnergy = 2.4f });
        _cam = new Camera3D { Fov = 35 };
        _vp.AddChild(_cam);
        _visuals = new DecorVisuals(_app) { Preview = true };
    }

    /// <summary>The picture of an item, or null (then it is queued if it may be rendered automatically).</summary>
    public Texture2D? Get(DecorCatalog.Item item, bool queueIfMissing = true)
    {
        var key = Key(item);
        if (_loaded.TryGetValue(key, out var t) && t != null) return t;
        foreach (var dir in new[] { ShippedFolder, CacheFolder })
        {
            var f = Path.Combine(dir, Clean(key) + ".png");
            if (!File.Exists(f)) continue;
            var img = Image.LoadFromFile(f);
            if (img == null || img.IsEmpty()) continue;
            return _loaded[key] = ImageTexture.CreateFromImage(img);
        }
        if (queueIfMissing && item.Egosoft) Request(item);
        return null;
    }

    /// <summary>Puts an item into the queue (first = rendered next).</summary>
    public void Request(DecorCatalog.Item item, bool first = false)
    {
        var key = Key(item);
        if (_current != null && Key(_current) == key) return;
        if (_queued.Add(key)) { if (first) _queue.Insert(0, item); else _queue.Add(item); }
        else if (first) { _queue.RemoveAll(i => Key(i) == key); _queue.Insert(0, item); }
    }

    /// <summary>Items of other authors' mods that have no picture yet.</summary>
    public List<DecorCatalog.Item> ModItemsWithout() =>
        _app.Decor?.Items.Where(i => !i.Egosoft && Get(i, false) == null).ToList() ?? new();

    public void MakeModPreviews() { foreach (var i in ModItemsWithout()) Request(i); }

    public override void _Process(double delta)
    {
        if (_app.Conv == null || _app.Decor == null) return;            // models need the game files
        if (_current != null)
        {
            if (--_wait > 0) return;
            Capture();
            return;
        }
        if (_queue.Count == 0) return;
        var item = _queue[0]; _queue.RemoveAt(0); _queued.Remove(Key(item));
        if (Get(item, false) != null) return;                          // made meanwhile (or shipped)
        Stage(item);
    }

    /// <summary>Builds the item in the hidden view and points the camera at it; the picture is taken a few frames later.</summary>
    void Stage(DecorCatalog.Item item)
    {
        _current = item;
        try
        {
            var (body, _, _, _) = _visuals.Build(new Decoration { Id = 1, Kind = item.Kind, Ref = item.Ref });
            _subject = body; _vp.AddChild(body);
            var box = Bounds(body);
            var centre = box.GetCenter();
            float radius = Math.Max(1f, box.Size.Length() / 2);
            float dist = radius / Mathf.Sin(Mathf.DegToRad(_cam.Fov) / 2) * 1.02f;
            // fields from straight above (shape and spread of the rocks); models from the front-right, a little above
            var dir = item.Kind == "region" ? new Vector3(0, 1, 0.0001f).Normalized() : new Vector3(0.75f, 0.55f, 1f).Normalized();
            _cam.Near = Math.Max(0.05f, dist - radius * 1.5f); _cam.Far = dist + radius * 2;
            _cam.LookAtFromPosition(centre + dir * dist, centre, item.Kind == "region" ? Vector3.Forward : Vector3.Up);
        }
        catch (Exception e) { DebugLog.Add($"preview of {item.Ref} failed: {e.Message}"); Clear(); return; }
        _vp.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        _wait = 3;                                                     // let Godot upload meshes / textures and draw
    }

    void Capture()
    {
        var item = _current!;
        try
        {
            var img = _vp.GetTexture().GetImage();
            Directory.CreateDirectory(CacheFolder);
            var key = Key(item);
            img.SavePng(Path.Combine(CacheFolder, Clean(key) + ".png"));
            _loaded[key] = ImageTexture.CreateFromImage(img);
            _done++;
            Ready?.Invoke(key);
        }
        catch (Exception e) { DebugLog.Add($"preview of {item.Ref} not saved: {e.Message}"); }
        Clear();
        if (_done % 25 == 0) _visuals.Free();                          // model templates: keep memory low on long runs
    }

    void Clear()
    {
        _subject?.QueueFree(); _subject = null; _current = null;
        _vp.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
    }

    /// <summary>World bounds of everything visible under a node.</summary>
    static Aabb Bounds(Node3D root)
    {
        Aabb? box = null;
        void Walk(Node n)
        {
            if (n is VisualInstance3D v && n is not Light3D)
            {
                var b = v.GlobalTransform * v.GetAabb();
                if (b.Size.LengthSquared() > 0) box = box is { } x ? x.Merge(b) : b;
            }
            foreach (var c in n.GetChildren()) Walk(c);
        }
        Walk(root);
        return box ?? new Aabb(new Vector3(-500, -500, -500), new Vector3(1000, 1000, 1000));
    }
}
