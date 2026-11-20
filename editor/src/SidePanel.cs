using System;
using System.Linq;
using Godot;
using X4Data.Galaxy;

namespace GalaxyEditor3D;

/// <summary>
/// Right-hand panel: properties of the selection (cluster + sector, gate), project settings and the checks.
/// Rebuilt after every change (deferred, so a control is never freed inside its own signal).
/// </summary>
public partial class SidePanel : VBoxContainer
{
    readonly App _app;
    bool _projectMode, _queued;

    public SidePanel(App app) { _app = app; AddThemeConstantOverride("separation", 4); }

    /// <summary>Redraw with the current state (project settings stay open until closed).</summary>
    public void Rebuild() => Queue();

    /// <summary>Project settings replace the panel's content until Close, Esc, or a new selection.</summary>
    public void ShowProjectSettings()
    {
        _projectMode = true;
        _app.OpenWindow(this, () => { _projectMode = false; Queue(); });
        Queue();
    }
    void Queue() { if (_queued) return; _queued = true; CallDeferred(MethodName.Build); }

    void Build()
    {
        _queued = false;
        foreach (var c in GetChildren()) { RemoveChild(c); c.QueueFree(); }
        var P = _app.P;
        if (_projectMode && _app.Sel != null) { _projectMode = false; _app.WindowClosed(this); }   // something was selected: back to it
        if (_projectMode) { ProjectSettings(); return; }

        if (_app.OpenSectorId == null && _app.Sel is { Kind: "sector" } sel && P.SectorById(sel.Id) is { } s)
        {
            var c = P.ClusterOf(s.Id)!;
            Head("Cluster");
            Text("Name", c.Name, v => c.Name = v);
            Area("Description", c.Desc, v => c.Desc = v);
            ClusterLook(c);
            Hint($"Backdrop = planets, sun and nebula of that vanilla cluster. Position on the map: column {c.Q}, row {c.R}.");
            var add = Button($"Add sector to cluster ({c.Sectors.Count}/3)", () => _app.Change(() => { var ns = _app.P.AddSector(c); _app.Sel = new Selection("sector", ns.Id); }));
            add.Disabled = c.Sectors.Count >= 3;
            SectorFields(s);
            var row = new HFlowContainer(); AddChild(row);
            Button("Open sector", () => _app.OpenSector(s.Id), row);
            Button("Player starts here", () => _app.Change(() => _app.P.StartSector = s.Id), row);
            Button("Delete sector", _app.DeleteSelection, row);
        }
        else if (_app.OpenSectorId != null && P.SectorById(_app.OpenSectorId) is { } os)
        {
            if (_app.Sel is { Kind: "gate" } gs && P.GateById(gs.Id) is { } g) GateFields(os, g);
            else if (_app.Sel is { Kind: "decor" } ds && P.DecorById(ds.Id) is { } dec) DecorFields(dec);
            else
            {
                DecorPalette(os);
                Head("Gates");
                foreach (var gate in os.Gates)
                {
                    var target = gate.Link != null ? P.SectorOfGate(gate.Link.Value)?.Name : null;
                    Button((gate.Type == "accelerator" ? "Accelerator" : "Gate") + " → " + (target ?? "not connected"), () => { _app.Sel = new Selection("gate", gate.Id); _app.Refresh(); });
                }
                Button("Add gate", () => _app.Change(() =>
                {
                    // free spot on a 150 km ring, 30 degree steps
                    double ang = 0; for (int i = 0; i < 12 && os.Gates.Any(x => Math.Sqrt(Math.Pow(x.X - 150_000 * Math.Sin(ang), 2) + Math.Pow(x.Z - 150_000 * Math.Cos(ang), 2)) < 40_000); i++) ang += Math.PI / 6;
                    var ng = _app.P.AddGate(os, 150_000 * Math.Sin(ang), 150_000 * Math.Cos(ang));
                    _app.Sel = new Selection("gate", ng.Id);
                }));
                Hint("Click a marker in 3D (or a list entry) to edit it. Drag a marker to move it, Shift + drag: up / down. Right-click: copy, cut, paste, connect.");
                Head("Cluster look and sound");
                ClusterLook(P.ClusterOf(os.Id)!);
                Hint("Backdrop and music belong to the cluster: all its sectors share them.");
                SectorFields(os);
            }
        }
        else
        {
            Head("Galaxy");
            Hint($"{P.Clusters.Count} clusters, {P.AllSectors().Count()} sectors, {P.AllSectors().Sum(s => s.Gates.Count) / 2} gate connections.");
            Hint("Select a sector to edit it.");
        }

        Head("Checks");
        var issues = P.Validate();
        if (issues.Count == 0) AddChild(new Label { Text = "No problems found.", Modulate = new Color("7bd88f") });
        foreach (var t in issues.Take(12)) AddChild(new Label { Text = "⚠ " + t, Modulate = new Color("ffb347"), AutowrapMode = TextServer.AutowrapMode.WordSmart });
        if (issues.Count > 12) Hint($"… {issues.Count - 12} more");
    }

    // ---------------------------------------------------------------------------------------------- decoration
    string _decorCategory = "Asteroid field";
    string? _decorRef;

    /// <summary>Palette: group, item (named as the game names it), Add at the view centre; then the placed ones.</summary>
    void DecorPalette(Sector s)
    {
        Head("Decoration");
        var cat = _app.Decor;
        if (cat == null) { Hint("The palette needs the game files (\"Folders...\")."); return; }
        var groups = cat.Categories.ToArray();
        if (!groups.Contains(_decorCategory)) _decorCategory = groups.FirstOrDefault() ?? "";
        ChoiceLocal("Group", groups.Select(x => (x, $"{x} ({cat.Items.Count(i => i.Category == x)})")).ToArray(), _decorCategory, v => { _decorCategory = v; _decorRef = null; Queue(); });
        var items = cat.Items.Where(i => i.Category == _decorCategory).ToList();
        if (_decorRef == null || items.All(i => i.Ref != _decorRef)) _decorRef = items.FirstOrDefault()?.Ref;
        ChoiceLocal("Item", items.Select(i => (i.Ref, i.Egosoft ? i.Label : "⚠ " + i.Label)).ToArray(), _decorRef ?? "", v => _decorRef = v);
        var chosen = items.FirstOrDefault(i => i.Ref == _decorRef);
        Button("Add at view centre", () =>
        {
            if (chosen == null) return;
            void Place() => _app.Change(() =>
            {
                var c = _app.SectorViewCentre();
                var d = _app.P.AddDecor(s, chosen.Kind, chosen.Ref, c.X, c.Y, c.Z);
                _app.Sel = new Selection("decor", d.Id);
            });
            if (!chosen.Egosoft) _app.Confirm(ForeignWarning(chosen.Source), Place); else Place();
        }).Disabled = chosen == null;
        foreach (var d in s.Decor)
        {
            var label = cat.Get(d.Kind, d.Ref)?.Label ?? d.Ref;
            Button(label, () => { _app.Sel = new Selection("decor", d.Id); _app.Refresh(); });
        }
    }

    void DecorFields(Decoration d)
    {
        var item = _app.Decor?.Get(d.Kind, d.Ref);
        Head(d.Kind == "region" ? "Field" : "Object");
        Hint(item?.Label ?? d.Ref);
        Hint(d.Kind == "region" ? $"Region definition: {d.Ref}" : $"Macro: {d.Ref}");
        if (item is { Egosoft: false }) AddChild(new Label { Text = $"⚠ From the mod folder \"{item.Source}\": your galaxy needs that mod.", Modulate = new Color("ffb347"), AutowrapMode = TextServer.AutowrapMode.WordSmart });
        if (item?.Category == "Anomaly")
        {
            var target = d.Link != null ? _app.P.SectorOfDecor(d.Link.Value)?.Name : null;
            Hint("Leads to: " + (target ?? "not connected") + ". Right-click the marker: Connect to / Disconnect.");
            Hint("The export links the pair by a script at game start (vanilla does the same); to be tested in the game.");
        }
        Number("East x (km)", d.X / 1000, -1000, 1000, 1, v => d.X = Math.Round(v * 1000));
        Number("Height y (km)", d.Y / 1000, -500, 500, 1, v => d.Y = Math.Round(v * 1000));
        Number("North z (km)", d.Z / 1000, -1000, 1000, 1, v => d.Z = Math.Round(v * 1000));
        Number("Turn (°)", d.Yaw, -180, 180, 5, v => d.Yaw = GalaxyOps.NormYaw(v));
        Number("Tilt (°)", d.Pitch, -90, 90, 5, v => d.Pitch = Math.Clamp(Math.Round(v), -90, 90));
        Number("Roll (°)", d.Roll, -180, 180, 5, v => d.Roll = GalaxyOps.NormYaw(v));
        Hint("Drag the marker in 3D to move it (Shift: up / down, Alt: off the 1 km grid). R / Shift+R turn it.");
        var row = new HFlowContainer(); AddChild(row);
        Button("Delete", _app.DeleteSelection, row);
        Button("◀ Sector", () => { _app.Sel = null; _app.Refresh(); }, row);
    }

    /// <summary>A dropdown that only changes panel state (no project change, no undo step).</summary>
    void ChoiceLocal(string label, (string value, string text)[] options, string current, Action<string> set)
    {
        var o = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill, FitToLongestItem = false, ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        for (int i = 0; i < options.Length; i++) { o.AddItem(options[i].text, i); if (options[i].value == current) o.Selected = i; }
        o.ItemSelected += i => set(options[i].value);
        Row(label).AddChild(o);
    }

    /// <summary>Backdrop and music of a cluster: dropdowns from the installed game, with Play / Stop.</summary>
    void ClusterLook(Cluster c)
    {
        var game = _app.Game;
        (string, string)[] backdrops;
        if (_app.Backdrops.Count > 0)
        {
            string ExtName(string folder) => game?.Vfs.Extensions.All.FirstOrDefault(e => e.FolderName.Equals(folder, StringComparison.OrdinalIgnoreCase))?.Name ?? folder;
            backdrops = _app.Backdrops.Select(b =>
            {
                string label = b.Number is { } n ? $"Backdrop {n:00}" : b.Component;
                if (b.Source != "base") label += b.Egosoft ? $" ({ExtName(b.Source)})" : $"  ⚠ from mod \"{ExtName(b.Source)}\"";
                return (b.Component, label);
            }).ToArray();
        }
        else backdrops = Enumerable.Range(1, GalaxyFacts.Backdrops).Select(i => ($"Cluster_{i:00}", $"Backdrop {i:00}")).ToArray();
        Choice("Backdrop", backdrops, c.BackdropComponent(), v =>
        {
            c.BackdropRef = v;
            var m = System.Text.RegularExpressions.Regex.Match(v, @"^Cluster_(\d+)$");
            if (m.Success) c.Backdrop = int.Parse(m.Groups[1].Value);      // the HTML editor exports by number
        }, v => _app.Backdrops.FirstOrDefault(b => b.Component == v) is { Egosoft: false } b ? ForeignWarning(b.Source) : null);

        var lib = _app.Music.Library;
        var music = lib != null
            ? new[] { ("", "(none)") }.Concat(lib.All.Select(m => (m.Id, m.Description))).ToArray()
            : GalaxyFacts.Music.Select(m => (m, m == "" ? "(none)" : m)).ToArray();
        Choice("Music", music, c.Music, v => c.Music = v);
        var row = new HBoxContainer(); AddChild(row);
        row.AddChild(new Control { CustomMinimumSize = new Vector2(110, 0) });
        bool playing = _app.Music.Current != null;
        var play = Button(playing ? "■ Stop music" : "▶ Play music", () => { if (_app.Music.Current != null) _app.Music.Stop(); else if (c.Music != "") _app.Music.Play(c.Music); }, row);
        play.Disabled = lib == null || (!playing && c.Music == "");
        if (playing) row.AddChild(new Label { Text = "♪ " + (lib?.Get(_app.Music.Current!)?.Description ?? _app.Music.Current), ClipText = true, SizeFlagsHorizontal = SizeFlags.ExpandFill, Modulate = new Color(1, 1, 1, 0.6f) });
        if (lib == null) Hint("Backdrops and music come from the game files: set the game folder under \"Folders...\".");
    }

    /// <summary>Warning before anything from another author's mod is used (using another author's work without permission may count as stealing).</summary>
    string ForeignWarning(string folder)
    {
        var ext = _app.Game?.Vfs.Extensions.All.FirstOrDefault(e => e.FolderName.Equals(folder, StringComparison.OrdinalIgnoreCase));
        return $"This comes from the mod \"{ext?.Name ?? folder}\"{(ext != null ? $" ({ext.Id})" : "")}, not from Egosoft.\n\n"
             + "Your galaxy will then NEED that mod: players without it see nothing there.\n"
             + "Copying its files into your own mod without the author's permission is not allowed and can count as stealing.\n\n"
             + "Use it anyway?";
    }

    void SectorFields(Sector s)
    {
        Head("Sector");
        Text("Name", s.Name, v => s.Name = v);
        Area("Description", s.Desc, v => s.Desc = v);
        Number("Sunlight", s.Sunlight, 0, 5, 0.05, v => s.Sunlight = v);
        Number("Economy", s.Economy, 0, 1, 0.05, v => s.Economy = v);
        Number("Security", s.Security, 0, 1, 0.05, v => s.Security = v);
        var place = _app.P.Place(s);
        Number("Backdrop east (1000 km)", place.X / 1e6, -500, 500, 1, v => s.PlaceX = Math.Round(v * 1e6));
        Number("Backdrop up (1000 km)", place.Y / 1e6, -500, 500, 1, v => s.PlaceY = Math.Round(v * 1e6));
        Number("Backdrop north (1000 km)", place.Z / 1e6, -500, 500, 1, v => s.PlaceZ = Math.Round(v * 1e6));
        if (s.PlaceX != null || s.PlaceY != null || s.PlaceZ != null)
            Button("Reset position in backdrop", () => _app.Change(() => { s.PlaceX = s.PlaceY = s.PlaceZ = null; }));
        Hint("Position in backdrop: where the sector sits among the backdrop's planets and moons (planets are 100,000 to 400,000 km away). Egosoft does this too.");
        Hint("Sunlight 1.0 = 100 % solar output. Economy and security 0 to 1, as in the vanilla sectors (mostly 0.25 to 0.75).");
        Head("Resources");
        for (int i = 0; i < s.Res.Count; i++)
        {
            var r = s.Res[i]; var row = new HBoxContainer(); AddChild(row);
            var amount = new SpinBox { MinValue = 1, MaxValue = 50, Value = r.Amount, CustomMinimumSize = new Vector2(64, 0) };
            amount.ValueChanged += v => _app.Change(() => r.Amount = (int)v); row.AddChild(amount);
            Opt(row, GalaxyFacts.ResSize, r.Size, v => r.Size = v); Opt(row, GalaxyFacts.ResWare, r.Ware, v => r.Ware = v);
            Opt(row, GalaxyFacts.ResYield, r.Yield, v => r.Yield = v); Opt(row, GalaxyFacts.ResSpeed, r.Speed, v => r.Speed = v);
            var del = new Button { Text = "×" }; del.Pressed += () => _app.Change(() => s.Res.Remove(r)); row.AddChild(del);
        }
        Button("Add resource areas", () => _app.Change(() => s.Res.Add(new ResourceArea())));
        Hint("Columns: how many areas, size, ware, yield, refill speed. Since 9.00 the game places these areas itself.");
    }

    void GateFields(Sector s, Gate g)
    {
        var P = _app.P;
        Head("Gate");
        var options = new System.Collections.Generic.List<(string, string)> { ("", "(not connected)") };
        foreach (var s2 in P.AllSectors().Where(x => x.Id != s.Id))
            foreach (var g2 in s2.Gates.Where(x => x.Link == null || x.Link == g.Id)) options.Add(("g" + g2.Id, $"{s2.Name}: existing gate"));
        foreach (var s2 in P.AllSectors().Where(x => x.Id != s.Id)) options.Add(("s" + s2.Id, $"{s2.Name}: new gate"));
        Choice("Leads to", options.ToArray(), g.Link != null ? "g" + g.Link : "", v =>
        {
            if (v == "") P.Unlink(g);
            else if (v[0] == 'g') P.Link(g, P.GateById(int.Parse(v[1..]))!);
            else { var s2 = P.SectorById(int.Parse(v[1..]))!; var g2 = P.AddGate(s2, -g.X, -g.Z); g2.Type = g.Type; P.Link(g, g2); }
        });
        Choice("Type", new[] { ("gate", "Jump gate"), ("accelerator", "Accelerator") }, g.Type, v => g.Type = v);
        Number("East x (km)", g.X / 1000, -1000, 1000, 1, v => g.X = Math.Round(v * 1000));
        Number("Height y (km)", g.Y / 1000, -200, 200, 1, v => g.Y = Math.Round(v * 1000));
        Number("North z (km)", g.Z / 1000, -1000, 1000, 1, v => g.Z = Math.Round(v * 1000));
        Number("Facing (°)", g.Yaw, -180, 180, 5, v => g.Yaw = GalaxyOps.NormYaw(v));
        Hint("0° = north, 90° = east. Keys R / Shift+R turn the gate. Height: above / below the ecliptic.");
        var row = new HFlowContainer(); AddChild(row);
        Button("Face sector centre", () => _app.Change(() => g.Yaw = GalaxyOps.FaceCentre(g.X, g.Z)), row);
        if (g.Link != null) Button("Go to other side", () => { var g2 = P.GateById(g.Link)!; _app.OpenSector(P.SectorOfGate(g2.Id)!.Id); _app.Sel = new Selection("gate", g2.Id); _app.Refresh(); }, row);
        Button("Delete gate", _app.DeleteSelection, row);
        Button("◀ Sector", () => { _app.Sel = null; _app.Refresh(); }, row);
    }

    void ProjectSettings()
    {
        var P = _app.P;
        static string Clean(string v) => new string(v.Trim().ToLowerInvariant().Select(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_' ? ch : '_').ToArray());
        Head("Project settings");
        Text("Mod name", P.ModName, v => P.ModName = v);
        Text("Mod folder / id", P.ModId, v => P.ModId = Clean(v));
        Text("Prefix", P.Prefix, v => P.Prefix = Clean(v).Replace("_", ""));
        Number("Text page", P.TextPage, 1, 9_999_999, 1, v => P.TextPage = (int)v);
        Text("Author", P.Author, v => P.Author = v);
        var full = new CheckBox { Text = "Full conversion", ButtonPressed = P.ReplaceVanillaStations };
        full.Toggled += v => _app.Change(() => P.ReplaceVanillaStations = v); AddChild(full);
        Hint("Prefix starts every name the mod creates, so it cannot clash with other mods. Text page holds the sector names; no other mod may use it.");
        Hint("Full conversion removes all vanilla stations from the game's station list, for every game start. Leave it off while testing.");
        Button("Close", () => _app.CloseWindow(this));
        Hint("Esc closes this panel too.");
    }

    // ---------------------------------------------------------------------------------------------- field helpers
    void Head(string t) { AddChild(new HSeparator()); AddChild(new Label { Text = t.ToUpperInvariant(), Modulate = new Color(1, 1, 1, 0.55f) }); }
    void Hint(string t) => AddChild(new Label { Text = t, AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = new Color(1, 1, 1, 0.55f), CustomMinimumSize = new Vector2(320, 0), SizeFlagsHorizontal = SizeFlags.ExpandFill });

    HBoxContainer Row(string label)
    {
        var row = new HBoxContainer(); AddChild(row);
        row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(110, 0), Modulate = new Color(1, 1, 1, 0.7f) });
        return row;
    }

    void Text(string label, string value, Action<string> set)
    {
        var e = new LineEdit { Text = value, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        void Commit() { if (e.Text != value) { var t = e.Text; _app.Change(() => set(t)); } }
        e.TextSubmitted += _ => Commit(); e.FocusExited += Commit;
        Row(label).AddChild(e);
    }

    void Area(string label, string value, Action<string> set)
    {
        var e = new TextEdit { Text = value, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 52), WrapMode = TextEdit.LineWrappingMode.Boundary };
        e.FocusExited += () => { if (e.Text != value) { var t = e.Text; _app.Change(() => set(t)); } };
        Row(label).AddChild(e);
    }

    void Number(string label, double value, double min, double max, double step, Action<double> set)
    {
        var e = new SpinBox { MinValue = min, MaxValue = max, Step = step, Value = value, SizeFlagsHorizontal = SizeFlags.ExpandFill, AllowGreater = true, AllowLesser = true };
        e.ValueChanged += v => _app.Change(() => set(v));
        Row(label).AddChild(e);
    }

    void Choice(string label, (string value, string text)[] options, string current, Action<string> set, Func<string, string?>? warnFirst = null)
    {
        var o = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill, FitToLongestItem = false, ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        for (int i = 0; i < options.Length; i++) { o.AddItem(options[i].text, i); if (options[i].value == current) o.Selected = i; }
        o.ItemSelected += i =>
        {
            var v = options[i].value;
            if (warnFirst?.Invoke(v) is { } warning) _app.Confirm(warning, () => _app.Change(() => set(v)), () => Rebuild());   // No: dropdown shows the old choice again
            else _app.Change(() => set(v));
        };
        Row(label).AddChild(o);
    }

    void Opt(HBoxContainer row, string[] list, string current, Action<string> set)
    {
        var o = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill, FitToLongestItem = false, ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        for (int i = 0; i < list.Length; i++) { o.AddItem(list[i], i); if (list[i] == current) o.Selected = i; }
        o.ItemSelected += i => { var v = list[i]; _app.Change(() => set(v)); };
        row.AddChild(o);
    }

    Button Button(string text, Action press, Container? parent = null)
    {
        // long names are cut with "…" (tooltip shows all), so the panel keeps its width and the 3D view its room
        // only full-width list buttons cut their text; buttons side by side in a row keep their natural width
        var b = new Button { Text = text, FocusMode = FocusModeEnum.None, TooltipText = text };
        if (parent == null) { b.ClipText = true; b.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis; }   // (an overrun setting also shrinks the minimum width)
        b.Pressed += press; (parent ?? this).AddChild(b); return b;
    }
}
