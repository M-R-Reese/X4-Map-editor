using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using X4Data.Space;

namespace GalaxyEditor3D;

/// <summary>
/// Decoration palette on the left of the sector view, inspired by X4's station designer: a narrow strip with one
/// button per group (picture of its first item + short name); a click opens the group beside it as a grid of tiles
/// (preview picture + name, search box). Click a tile, then click into the sector to place it there (Shift: place
/// more; Esc or right click: stop). Double-click a tile: place it at the view centre at once.
/// The strip folds away to a small tab ("◀").
/// </summary>
public partial class DecorPalette : HBoxContainer
{
    readonly App _app;
    VBoxContainer _strip = null!;
    PanelContainer _flyout = null!;
    Label _title = null!, _footer = null!;
    LineEdit _search = null!;
    HFlowContainer _grid = null!;
    Button _fold = null!, _modButton = null!;
    string? _open;                                         // open group, null = fly-out closed
    bool _folded;
    readonly Dictionary<string, (Button button, TextureRect pic)> _tiles = new();
    readonly Dictionary<string, Button> _groupButtons = new();
    double _footerTimer;

    /// <summary>Short names for the strip (the catalogue's group names are longer).</summary>
    static readonly Dictionary<string, string> Short = new()
    {
        ["Asteroid field"] = "Asteroid fields", ["Gas cloud"] = "Gas clouds", ["Nebula"] = "Nebulae", ["Wreck field"] = "Wreck fields",
        ["Wreck"] = "Wrecks", ["Landmark"] = "Landmarks", ["Data vault"] = "Data vaults", ["Nav beacon"] = "Beacons",
        ["Single asteroid"] = "Asteroids", ["Anomaly"] = "Anomalies", ["Other"] = "Other fields", ["Station"] = "Stations",
    };
    /// <summary>Order of the groups in the strip: fields first, then objects.</summary>
    static readonly string[] Order = { "Station", "Asteroid field", "Gas cloud", "Nebula", "Wreck field", "Other", "Single asteroid", "Wreck", "Landmark", "Data vault", "Nav beacon", "Anomaly" };

    public DecorPalette(App app) { _app = app; }

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 0);
        MouseFilter = MouseFilterEnum.Ignore;                       // only the strip and the fly-out take the mouse

        var stripPanel = new PanelContainer { MouseFilter = MouseFilterEnum.Stop };
        stripPanel.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0.07f, 0.09f, 0.12f, 0.92f) });
        AddChild(stripPanel);
        var stripScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, CustomMinimumSize = new Vector2(84, 0) };
        stripPanel.AddChild(stripScroll);
        _strip = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _strip.AddThemeConstantOverride("separation", 2);
        stripScroll.AddChild(_strip);

        _flyout = new PanelContainer { CustomMinimumSize = new Vector2(392, 0), Visible = false, MouseFilter = MouseFilterEnum.Stop };
        _flyout.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0.09f, 0.11f, 0.15f, 0.95f), BorderColor = new Color("3a4350"), BorderWidthRight = 1 });
        AddChild(_flyout);
        var fbox = new VBoxContainer(); fbox.AddThemeConstantOverride("separation", 6);
        var fm = new MarginContainer(); foreach (var side in new[] { "left", "right", "top", "bottom" }) fm.AddThemeConstantOverride("margin_" + side, 8);
        fm.AddChild(fbox); _flyout.AddChild(fm);
        var head = new HBoxContainer(); fbox.AddChild(head);
        _title = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill }; head.AddChild(_title);
        var close = new Button { Text = "✕", Flat = true, TooltipText = "Close the group" }; close.Pressed += () => Open(null); head.AddChild(close);
        _search = new LineEdit { PlaceholderText = "Search (name, ware, size, id)...", ClearButtonEnabled = true };
        _search.TextChanged += _ => Fill();
        fbox.AddChild(_search);
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        fbox.AddChild(scroll);
        _grid = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _grid.AddThemeConstantOverride("h_separation", 6); _grid.AddThemeConstantOverride("v_separation", 6);
        scroll.AddChild(_grid);
        _footer = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = new Color(1, 1, 1, 0.55f) };
        fbox.AddChild(_footer);
        _modButton = new Button { Text = "Make pictures of mod items", Visible = false, TooltipText = "Renders the pictures of items from other authors' mods (vanilla and DLC items get theirs by themselves)." };
        _modButton.Pressed += () => { _app.Previews?.MakeModPreviews(); UpdateFooter(); };
        fbox.AddChild(_modButton);

        if (_app.Previews != null) _app.Previews.Ready += OnPreview;
        Rebuild();
    }

    /// <summary>Strip buttons from the catalogue (call again when the game files are read).</summary>
    public void Rebuild()
    {
        foreach (var c in _strip.GetChildren()) { _strip.RemoveChild(c); c.QueueFree(); }
        _groupButtons.Clear();
        _fold = new Button { Text = _folded ? "▶" : "◀", Flat = true, TooltipText = "Fold the decoration palette away / open it" };
        _fold.Pressed += () => { _folded = !_folded; Rebuild(); };
        _strip.AddChild(_fold);
        var cat = _app.Decor;
        if (_folded) { _flyout.Visible = false; return; }
        if (cat == null)
        {
            _strip.AddChild(new Label { Text = "Reading\ngame\nfiles...", HorizontalAlignment = HorizontalAlignment.Center, Modulate = new Color(1, 1, 1, 0.5f) });
            return;
        }
        var groups = cat.Categories.OrderBy(g => Array.IndexOf(Order, g) is var i && i >= 0 ? i : 99).ThenBy(g => g).ToList();
        foreach (var g in groups)
        {
            var first = cat.Items.FirstOrDefault(i => i.Category == g && i.Egosoft) ?? cat.Items.First(i => i.Category == g);
            var b = new Button
            {
                Text = Short.TryGetValue(g, out var s) ? s : g, ToggleMode = true, ButtonPressed = _open == g,
                Icon = _app.Previews?.Get(first), ExpandIcon = true, IconAlignment = HorizontalAlignment.Center,
                VerticalIconAlignment = VerticalAlignment.Top, CustomMinimumSize = new Vector2(80, 78), ClipText = true,
                TooltipText = $"{g} ({cat.Items.Count(i => i.Category == g)})",
            };
            b.AddThemeFontSizeOverride("font_size", 11);
            b.Pressed += () => Open(_open == g ? null : g);
            _strip.AddChild(b);
            _groupButtons[g] = b;
        }
        if (_open != null && !groups.Contains(_open)) _open = null;
        _flyout.Visible = _open != null;
        if (_open != null) Fill();
    }

    /// <summary>Opens a group's tiles (screenshot checks: --palette "Wreck").</summary>
    public void OpenGroup(string group) { if (_folded) { _folded = false; Rebuild(); } Open(group); }

    void Open(string? group)
    {
        _open = group;
        foreach (var (g, b) in _groupButtons) b.SetPressedNoSignal(g == group);
        _flyout.Visible = group != null;
        _search.Text = "";
        if (group != null) Fill();
    }

    /// <summary>Tiles of the open group that match the search; their pictures are rendered first.</summary>
    void Fill()
    {
        foreach (var c in _grid.GetChildren()) { _grid.RemoveChild(c); c.QueueFree(); }
        _tiles.Clear();
        var cat = _app.Decor;
        if (cat == null || _open == null) return;
        var words = _search.Text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var items = cat.Items.Where(i => i.Category == _open)
                             .Where(i => words.All(w => i.Label.ToLowerInvariant().Contains(w) || i.Ref.ToLowerInvariant().Contains(w))).ToList();
        _title.Text = $"{_open}  ({items.Count})";
        foreach (var item in items) _grid.AddChild(Tile(item));
        // what the user is looking at first: these tiles' pictures go to the front of the queue (back to front keeps the order)
        for (int i = items.Count - 1; i >= 0; i--)
            if (items[i].Egosoft && _app.Previews?.Get(items[i], false) == null) _app.Previews?.Request(items[i], first: true);
        UpdateFooter();
    }

    Control Tile(DecorCatalog.Item item)
    {
        var b = new Button { CustomMinimumSize = new Vector2(110, 138), ToggleMode = true, ButtonPressed = _app.Armed == item, FocusMode = FocusModeEnum.None,
                             TooltipText = $"{item.Label}\n{item.Ref}{(item.Egosoft ? "" : $"\n⚠ from the mod folder \"{item.Source}\"")}\n\nClick, then click into the sector to place it. Double-click: place at the view centre." };
        var box = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        box.SetAnchorsPreset(LayoutPreset.FullRect); box.OffsetLeft = 4; box.OffsetRight = -4; box.OffsetTop = 4; box.OffsetBottom = -4;
        var pic = new TextureRect
        {
            Texture = _app.Previews?.Get(item), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            CustomMinimumSize = new Vector2(102, 94), MouseFilter = MouseFilterEnum.Ignore,
        };
        box.AddChild(pic);
        var name = new Label
        {
            Text = (item.Egosoft ? "" : "⚠ ") + ShortLabel(item), AutowrapMode = TextServer.AutowrapMode.WordSmart, MaxLinesVisible = 2,
            HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(102, 0),
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
        };
        name.AddThemeFontSizeOverride("font_size", 11);
        if (!item.Egosoft) name.Modulate = new Color("ffb347");
        box.AddChild(name);
        b.AddChild(box);
        b.GuiInput += e =>
        {
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true, DoubleClick: true }) { _app.Disarm(); _app.PlaceDecor(item, null); }
        };
        b.Pressed += () => { if (_app.Armed == item) _app.Disarm(); else _app.Arm(item); };
        _tiles[Previews.Key(item)] = (b, pic);
        return b;
    }

    /// <summary>The name without the group word and the "shape of ..." part (the tooltip has all).</summary>
    static string ShortLabel(DecorCatalog.Item item)
    {
        var s = item.Label;
        int cut = s.IndexOf(" · shape of", StringComparison.Ordinal);
        if (cut > 0) s = s[..cut];
        int colon = s.IndexOf(": ", StringComparison.Ordinal);
        if (item.Kind == "region" && colon > 0) s = s[(colon + 2)..];
        return s;
    }

    /// <summary>Which tile is "in the hand" changed: light only that one.</summary>
    public void ShowArmed()
    {
        foreach (var (key, t) in _tiles) t.button.SetPressedNoSignal(_app.Armed != null && Previews.Key(_app.Armed) == key);
    }

    void OnPreview(string key)
    {
        if (_tiles.TryGetValue(key, out var t)) t.pic.Texture = _app.Previews!.Get(_app.Decor!.Items.First(i => Previews.Key(i) == key), false);
        // a group button still without picture: take its first item's
        foreach (var (g, b) in _groupButtons)
            if (b.Icon == null && _app.Decor!.Items.FirstOrDefault(i => i.Category == g && i.Egosoft) is { } first && Previews.Key(first) == key)
                b.Icon = _app.Previews!.Get(first, false);
    }

    public override void _Process(double delta)
    {
        if (!_flyout.Visible || (_footerTimer -= delta) > 0) return;
        _footerTimer = 0.5;
        UpdateFooter();
    }

    void UpdateFooter()
    {
        var pv = _app.Previews;
        if (pv == null) { _footer.Text = ""; _modButton.Visible = false; return; }
        int mods = _open != null ? (_app.Decor?.Items.Count(i => i.Category == _open && !i.Egosoft) ?? 0) : 0;
        var without = mods > 0 ? pv.ModItemsWithout().Count : 0;
        _footer.Text = (pv.Pending > 0 ? $"Making pictures: {pv.Pending} to go (made this session: {pv.DoneThisSession}). " : "")
                     + (without > 0 ? $"{without} items from other authors' mods have no picture yet." : "");
        _modButton.Visible = without > 0;
    }
}
