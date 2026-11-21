using System;
using System.Collections.Generic;
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
        _box = this;
        var P = _app.P;
        if (_projectMode && _app.Sel != null) { _projectMode = false; _app.WindowClosed(this); }   // something was selected: back to it
        if (_projectMode) { ProjectSettings(); return; }
        if (_app.ReadOnly) { ReadOnlyFields(); return; }

        if (_app.OpenSectorId == null && _app.Sel is { Kind: "sector" } sel && P.SectorById(sel.Id) is { } s)
        {
            var c = P.ClusterOf(s.Id)!;
            Head($"Cluster: {c.Name}", $"Position on the map: column {c.Q}, row {c.R}. Backdrop and music belong to the cluster: all its sectors share them.");
            NameField(c.Name, c.NameRef, (n, r) => { c.Name = n; c.NameRef = r; });
            Area("Description", c.Desc, v => c.Desc = v);
            ClusterLook(c);
            var add = Button($"Add sector to cluster ({c.Sectors.Count}/3)", () => _app.Change(() => { var ns = _app.P.AddSector(c); _app.Sel = new Selection("sector", ns.Id); }));
            add.Disabled = c.Sectors.Count >= 3;
            // a click selects the whole cluster: every sector gets its own sections, with its own buttons
            foreach (var sec in c.Sectors)
            {
                int sid = sec.Id;
                SectorFields(sec, c.Sectors.Count > 1);
                var row = new HFlowContainer(); _box.AddChild(row);
                Button("Open sector", () => _app.OpenSector(sid), row);
                if (P.BaseGalaxy == null) Button("Player starts here", () => _app.Change(() => _app.P.StartSector = sid), row).Disabled = P.StartSector == sid;
                Button("Delete sector", () => { _app.Sel = new Selection("sector", sid); _app.DeleteSelection(); }, row);
            }
            EndSection();
        }
        else if (_app.OpenSectorId != null && P.SectorById(_app.OpenSectorId) is { } os)
        {
            if (_app.Sel is { Kind: "gate" } gs && P.GateById(gs.Id) is { } g) GateFields(os, g);
            else if (_app.Sel is { Kind: "decor" } ds && P.DecorById(ds.Id) is { } dec) DecorFields(dec);
            else
            {
                DecorPalette(os);
                Head("Gates", "Click a marker in 3D (or a list entry) to edit it. Drag a marker to move it, Shift + drag: up / down. Right-click: copy, cut, paste, connect.");
                foreach (var gate in os.Gates)
                {
                    var target = P.LinkTarget(gate);
                    Button((gate.Type == "accelerator" ? "Accelerator" : "Gate") + " → " + (target ?? "not connected"), () => { _app.Sel = new Selection("gate", gate.Id); _app.Refresh(); });
                }
                Button("Add gate", () => _app.Change(() =>
                {
                    // free spot on a 150 km ring, 30 degree steps
                    double ang = 0; for (int i = 0; i < 12 && os.Gates.Any(x => Math.Sqrt(Math.Pow(x.X - 150_000 * Math.Sin(ang), 2) + Math.Pow(x.Z - 150_000 * Math.Cos(ang), 2)) < 40_000); i++) ang += Math.PI / 6;
                    var ng = _app.P.AddGate(os, 150_000 * Math.Sin(ang), 150_000 * Math.Cos(ang));
                    _app.Sel = new Selection("gate", ng.Id);
                }));
                Head("Cluster look and sound", "Backdrop and music belong to the cluster: all its sectors share them.");
                ClusterLook(P.ClusterOf(os.Id)!);
                SectorFields(os);
            }
        }
        else
        {
            Head("Galaxy", "Select a sector on the map to edit it." + (P.BaseGalaxy != null ? " The patched galaxy's clusters are shown dimmed: place yours in free hexes, double-click one of theirs to look inside." : ""));
            Hint($"{P.Clusters.Count} clusters, {P.AllSectors().Count()} sectors, {P.AllSectors().Sum(s => s.Gates.Count) / 2} gate connections.");
            if (P.BaseGalaxy != null) Hint($"Patch for {(P.BaseFolder == "xu_ep2_universe" ? "the X4 galaxy (vanilla + DLCs)" : P.BaseFolder)} ({_app.Base?.Clusters.Count.ToString() ?? "..."} clusters).");
        }

        var issues = _app.Validate();
        Head(issues.Count == 0 ? "Checks  ✓" : $"Checks  ({issues.Count} ⚠)", "What would go wrong in the game: unconnected gates, names, text page.");
        if (issues.Count == 0) _box.AddChild(new Label { Text = "No problems found.", Modulate = new Color("7bd88f") });
        foreach (var t in issues.Take(12)) _box.AddChild(new Label { Text = "⚠ " + t, Modulate = new Color("ffb347"), AutowrapMode = TextServer.AutowrapMode.WordSmart });
        if (issues.Count > 12) Hint($"… {issues.Count - 12} more");
    }

    // ---------------------------------------------------------------------------------------------- decoration
    /// <summary>The decorations placed in this sector (new ones come from the palette on the left of the 3D view).</summary>
    void DecorPalette(Sector s)
    {
        Head($"Decoration ({s.Decor.Count})", "New decorations come from the palette on the left of the 3D view: a group, then a picture, then click into the sector.");
        var cat = _app.Decor;
        if (cat == null) { Hint("The palette needs the game files (\"Folders...\")."); return; }
        foreach (var d in s.Decor)
        {
            var label = cat.Get(d.Kind, d.Ref)?.Label ?? d.Ref;
            int cut = label.IndexOf(" · shape of", StringComparison.Ordinal);
            if (cut > 0) label = label[..cut];
            Button(label, () => { _app.Sel = new Selection("decor", d.Id); _app.Refresh(); });
        }
    }

    void DecorFields(Decoration d)
    {
        var item = _app.Decor?.Get(d.Kind, d.Ref);
        Head(d.Kind == "region" ? "Field" : "Object", "Drag the marker in 3D to move it (Shift: up / down, Alt: off the 1 km grid). The arrows move it along one axis, the rings turn it (5° steps, Alt: free). R / Shift+R turn it.");
        Hint(item?.Label ?? d.Ref, d.Kind == "region" ? $"Region definition: {d.Ref}" : $"Macro: {d.Ref}");
        if (item is { Egosoft: false }) _box.AddChild(new Label { Text = $"⚠ From the mod folder \"{item.Source}\": your galaxy needs that mod.", Modulate = new Color("ffb347"), AutowrapMode = TextServer.AutowrapMode.WordSmart });
        if (item?.Category == "Anomaly")
        {
            var target = d.Link != null ? _app.P.SectorOfDecor(d.Link.Value)?.Name : null;
            Hint("Leads to: " + (target ?? "not connected"), "Right-click the marker: Connect to / Disconnect. The export links the pair by a script at game start (vanilla does the same).");
        }
        Number("East x (km)", d.X / 1000, -1000, 1000, 1, v => d.X = Math.Round(v * 1000));
        Number("Height y (km)", d.Y / 1000, -500, 500, 1, v => d.Y = Math.Round(v * 1000));
        Number("North z (km)", d.Z / 1000, -1000, 1000, 1, v => d.Z = Math.Round(v * 1000));
        Number("Turn (°)", d.Yaw, -180, 180, 5, v => d.Yaw = GalaxyOps.NormYaw(v));
        Number("Tilt (°)", d.Pitch, -90, 90, 5, v => d.Pitch = Math.Clamp(Math.Round(v), -90, 90));
        Number("Roll (°)", d.Roll, -180, 180, 5, v => d.Roll = GalaxyOps.NormYaw(v));
        EndSection();
        var row = new HFlowContainer(); _box.AddChild(row);
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
        }, v => _app.Backdrops.FirstOrDefault(b => b.Component == v) is { Egosoft: false } b ? _app.ForeignWarning(b.Source) : null, arrows: true,
           tip: "Planets, sun and nebula of that vanilla cluster, seen from every sector of this cluster.",
           dice: v => _app.Backdrops.FirstOrDefault(b => b.Component == v) is not { Egosoft: false });

        var lib = _app.Music.Library;
        var music = lib != null
            ? new[] { ("", "(none)") }.Concat(lib.All.Select(m => (m.Id, m.Description))).ToArray()
            : GalaxyFacts.Music.Select(m => (m, m == "" ? "(none)" : m)).ToArray();
        Choice("Music", music, c.Music, v => c.Music = v, arrows: true, tip: "The music that plays in this cluster.", dice: v => v != "");
        var row = new HBoxContainer(); _box.AddChild(row);
        row.AddChild(new Control { CustomMinimumSize = new Vector2(110, 0) });
        bool playing = _app.Music.Current != null;
        var play = Button(playing ? "■ Stop music" : "▶ Play music", () => { if (_app.Music.Current != null) _app.Music.Stop(); else if (c.Music != "") _app.Music.Play(c.Music); }, row);
        play.Disabled = lib == null || (!playing && c.Music == "");
        if (playing) row.AddChild(new Label { Text = "♪ " + (lib?.Get(_app.Music.Current!)?.Description ?? _app.Music.Current), ClipText = true, SizeFlagsHorizontal = SizeFlags.ExpandFill, Modulate = new Color(1, 1, 1, 0.6f) });
        if (lib == null) Hint("Backdrops and music come from the game files: set the game folder under \"Folders...\".");
    }

    /// <summary>The sector's fields; several = more sectors in the cluster: the headings name the sector (own fold state each).</summary>
    void SectorFields(Sector s, bool several = false)
    {
        Head(several ? $"Sector: {s.Name}" : "Sector", null, several ? $"Sector {s.Id}" : null);
        NameField(s.Name, s.NameRef, (n, r) => { s.Name = n; s.NameRef = r; });
        Area("Description", s.Desc, v => s.Desc = v);
        Number("Sunlight", s.Sunlight, 0, 5, 0.05, v => s.Sunlight = v, "1.0 = 100 % solar output.");
        Number("Economy", s.Economy, 0, 1, 0.05, v => s.Economy = v, "0 to 1, as in the vanilla sectors (mostly 0.25 to 0.75).");
        Number("Security", s.Security, 0, 1, 0.05, v => s.Security = v, "0 to 1, as in the vanilla sectors (mostly 0.25 to 0.75).");
        var place = _app.P.Place(s);
        const string placeTip = "Position in backdrop: where the sector sits among the backdrop's planets and moons (planets are 100,000 to 400,000 km away). Egosoft does this too.";
        Number("Backdrop east (1000 km)", place.X / 1e6, -500, 500, 1, v => s.PlaceX = Math.Round(v * 1e6), placeTip);
        Number("Backdrop up (1000 km)", place.Y / 1e6, -500, 500, 1, v => s.PlaceY = Math.Round(v * 1e6), placeTip);
        Number("Backdrop north (1000 km)", place.Z / 1e6, -500, 500, 1, v => s.PlaceZ = Math.Round(v * 1e6), placeTip);
        if (s.PlaceX != null || s.PlaceY != null || s.PlaceZ != null)
            Button("Reset position in backdrop", () => _app.Change(() => { s.PlaceX = s.PlaceY = s.PlaceZ = null; }));
        // resources: a sub-heading inside the sector's section (not a section of its own)
        _box.AddChild(new Label { Text = "Resources  ⓘ", Modulate = new Color(1, 1, 1, 0.6f), MouseFilter = MouseFilterEnum.Stop,
                                  TooltipText = "Columns: how many areas, size, ware, yield, refill speed. Since 9.00 the game places these areas itself." });
        for (int i = 0; i < s.Res.Count; i++)
        {
            var r = s.Res[i]; var row = new HBoxContainer(); _box.AddChild(row);
            var amount = new SpinBox { MinValue = 1, MaxValue = 50, Value = r.Amount, CustomMinimumSize = new Vector2(64, 0) };
            amount.ValueChanged += v => _app.Change(() => r.Amount = (int)v); row.AddChild(amount);
            Opt(row, GalaxyFacts.ResSize, r.Size, v => r.Size = v); Opt(row, GalaxyFacts.ResWare, r.Ware, v => r.Ware = v);
            Opt(row, GalaxyFacts.ResYield, r.Yield, v => r.Yield = v); Opt(row, GalaxyFacts.ResSpeed, r.Speed, v => r.Speed = v);
            var del = new Button { Text = "×" }; del.Pressed += () => _app.Change(() => s.Res.Remove(r)); row.AddChild(del);
        }
        var resRow = new HFlowContainer(); _box.AddChild(resRow);
        Button("Add resource areas", () => _app.Change(() => s.Res.Add(new ResourceArea())), resRow);
        // random: replaces the list; the wares of the sector's fields are added again (a field without them gives nothing)
        var rnd = Button("Random resources", () => { int nPlaced = 0; _app.Change(() =>
        {
            s.RandomResources(Rng);
            foreach (var d in s.Decor.Where(d => d.Kind == "region"))
                if (_app.Decor?.Regions.Get(d.Ref) is { } def) s.AddResourcesFor(_app.FieldWares(d.Ref), def.R);
            // and a field for every ware that has none yet (Egosoft fields only: no mod needed)
            if (_app.Decor is { } cat)
            {
                var fields = cat.Regions.All.Where(f => !f.Hidden && (f.Source == "base" || f.Source.StartsWith("ego_", StringComparison.OrdinalIgnoreCase)))
                    .Select(f => (f.Name, (IReadOnlyList<string>)f.Wares.ToList(), (double)f.R)).ToList();
                var placed = _app.P.AddFieldsForResources(s, fields, Rng);
                nPlaced = placed.Count;
            }
        });
        if (nPlaced > 0) _app.SetStatus($"Random resources: {nPlaced} field{(nPlaced == 1 ? "" : "s")} placed for them (see Decoration in the 3D view of the sector).");
        }, resRow);
        rnd.Icon = App.SvgIcon(App.DiceSvg);
        rnd.TooltipText = "Replaces the resource areas by random ones: ore most often, nividium and scrap rarely; some sectors come out rich, some poor. The wares of the sector's fields are always kept, and every new ware gets a field holding it (placed away from the gates). Without the game files no fields can be placed.";
        AiFields(s);
    }

    // ---------------------------------------------------------------------------------------------- sector AI
    /// <summary>Known sector tags explained (others: "used by N vanilla sectors").</summary>
    static readonly System.Collections.Generic.Dictionary<string, string> TagTips = new()
    {
        ["anarchy"] = "Anarchy: in a sector WITHOUT owner the Scale Plate Pact builds its drug factories here (vanilla: the Scale Plate sectors). Pirates feel at home.",
        ["allowrandomanomaly"] = "Random anomalies may appear here (most vanilla sectors have it).",
        ["noaggressivespawn"] = "No aggressive ships are spawned here (vanilla: story sectors).",
    };
    static readonly System.Collections.Generic.Dictionary<string, string> PirateKinds = new()
    {
        ["piratebase"] = "pirate base", ["piratedock"] = "pirate dock (equipment, repairs)", ["piratedepot"] = "pirate depot", ["anarchyport"] = "anarchy port",
    };

    /// <summary>
    /// "AI and factions" part of a sector: owner at game start, a pirate station, whether factions may take it, which
    /// factions ignore it, sector tags. All lists come from the installed game (X4Data.Galaxy.FactionCatalog).
    /// </summary>
    void AiFields(Sector s)
    {
        _box.AddChild(new Label { Text = "AI and factions  ⓘ", Modulate = new Color(1, 1, 1, 0.6f), MouseFilter = MouseFilterEnum.Stop,
                                  TooltipText = "Who owns the sector when a game starts, pirates, and what the factions' AI may do here. X4 has no owner field: a faction owns a sector by a claiming station there (the export places one), then the game's own rules bring its factories, traders, miners and patrols." });
        var fc = _app.Factions;
        if (fc == null) { Hint("Needs the game files (\"Folders...\"): the factions are read from them."); return; }
        var ai = s.Ai ?? new SectorAi();
        // every change goes through here: the sector gets an Ai block when needed, loses it when all is default again
        void Set(Action<SectorAi> f) => _app.Change(() => { var a = s.Ai ??= new SectorAi(); f(a); if (a.IsDefault) s.Ai = null; });
        string Src(string source) => source == "base" ? "" : $"  ({_app.Game?.Vfs.Extensions.All.FirstOrDefault(e => e.FolderName.Equals(source, StringComparison.OrdinalIgnoreCase))?.Name ?? source})";

        // ---- owner
        var owners = new[] { ("", "(nobody)") }.Concat(fc.Owners.Select(f => (f.Id, f.Name + (f.Pirate ? "  ☠" : "") + Src(f.Source)))).ToArray();
        if (ai.Owner != null && owners.All(o => o.Item1 != ai.Owner)) owners = owners.Append((ai.Owner, ai.Owner + "  ⚠ not in the game")).ToArray();
        Choice("Owner at start", owners, ai.Owner ?? "", v => Set(a => a.Owner = v == "" ? null : v),
            tip: "The export places one claiming station (defence platform) of this faction: the sector is theirs from the game start. ☠ = pirate faction.");
        if (ai.Owner != null && fc.Get(ai.Owner) is { } of)
            Hint($"{fc.Products.GetValueOrDefault(of.Id)} factory types and {fc.Jobs.GetValueOrDefault(of.Id)} ship jobs of the game may come to sectors {of.Name} own.",
                 "Counted in the game's god.xml (factories placed in a faction's sectors) and jobs.xml (traders, miners, patrols, pirates ...). The game decides by demand which ones really come, and when.");

        // ---- pirate station
        var pir = new[] { ("", "(none)") }.Concat(fc.PirateStations.Select(t =>
            ($"{t.Id}|{t.Faction}", $"{fc.Get(t.Faction)?.Name ?? t.Faction}: {PirateKinds.GetValueOrDefault(t.Tag, t.Tag)}{Src(t.Source)}"))).ToArray();
        var cur = ai.PirateStation != null ? $"{ai.PirateStation}|{ai.PirateFaction}" : "";
        Choice("Pirate station", pir, cur, v => Set(a =>
        {
            var parts = v.Split('|');
            a.PirateStation = v == "" ? null : parts[0]; a.PirateFaction = v == "" ? null : parts[1];
        }), tip: "A pirate base of a pirate faction, placed at game start. It claims nothing: the sector keeps its owner, or stays free (a pirate haven). The pirates' ships come with it.");
        if (ai.PirateStation != null)
            Hint(ai.Owner == null ? "No owner: a free pirate haven." : $"In {fc.Get(ai.Owner)?.Name ?? ai.Owner} space: the owner's police and patrols will fight it.");

        // ---- may factions take it?
        var logic = new CheckBox { Text = "Factions may claim / invade it", ButtonPressed = ai.FactionLogic,
            TooltipText = "Off: while the sector has no owner, no faction AI tries to take it (the game's factionlogic flag; vanilla: Heretic's End, Silent Witness ...). An owner set above keeps it anyway." };
        logic.Toggled += v => Set(a => a.FactionLogic = v);
        _box.AddChild(logic);

        // ---- factions that ignore it: a dropdown with ticks (many factions)
        var names = ai.Excluded.Select(id => fc.Get(id)?.Name ?? id).ToList();
        var mb = new MenuButton { Text = "Ignored by: " + (names.Count == 0 ? "no faction" : string.Join(", ", names)) + "  ▾", Flat = false, ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, SizeFlagsHorizontal = SizeFlags.ExpandFill,
            TooltipText = "These factions' AI leaves the sector alone: no expansion, no invasion, no building (the game's per-faction excluded list, sent at game start and after each load). Not a law for every game system: their ships may still fly through." };
        var pm = mb.GetPopup(); pm.HideOnCheckableItemSelection = false;
        var list = fc.Excludable.ToList();
        for (int i = 0; i < list.Count; i++) { pm.AddCheckItem(list[i].Name + (list[i].Pirate ? "  ☠" : "") + Src(list[i].Source), i); pm.SetItemChecked(i, ai.Excluded.Contains(list[i].Id)); }
        pm.IdPressed += id =>
        {
            var f = list[(int)id];
            Set(a => { if (!a.Excluded.Remove(f.Id)) a.Excluded.Add(f.Id); });
        };
        Row("Faction AI").AddChild(mb);

        // ---- sector tags
        var tags = new HFlowContainer();
        foreach (var (tag, count) in fc.MapTags)
        {
            var cb = new CheckBox { Text = tag, ButtonPressed = ai.Tags.Contains(tag),
                TooltipText = TagTips.GetValueOrDefault(tag, $"Sector tag the game uses in {count} vanilla sector(s) (DLC or story: scripts look for it).") };
            cb.Toggled += v => Set(a => { a.Tags.Remove(tag); if (v) a.Tags.Add(tag); });
            tags.AddChild(cb);
        }
        _box.AddChild(new Label { Text = "Sector tags", Modulate = new Color(1, 1, 1, 0.7f) });
        _box.AddChild(tags);
        if (ai.Tags.Contains("anarchy") && fc.TagProducts.TryGetValue("anarchy", out var drugs))
            Hint(ai.Owner == null ? $"Anarchy without owner: {string.Join(", ", drugs.Distinct())} factories of the game may come here." : "Anarchy factories only come while the sector has NO owner.");
    }

    void GateFields(Sector s, Gate g)
    {
        var P = _app.P;
        Head("Gate", "Drag the marker in 3D to move it, the ring turns it. Keys R / Shift+R turn the gate.");
        var options = new System.Collections.Generic.List<(string, string)> { ("", "(not connected)") };
        foreach (var s2 in P.AllSectors().Where(x => x.Id != s.Id))
            foreach (var g2 in s2.Gates.Where(x => x.Link == null || x.Link == g.Id)) options.Add(("g" + g2.Id, $"{s2.Name}: existing gate"));
        foreach (var s2 in P.AllSectors().Where(x => x.Id != s.Id)) options.Add(("s" + s2.Id, $"{s2.Name}: new gate"));
        var baseSide = P.BaseGateById(g.Link);
        if (baseSide != null) options.Add(("b" + baseSide.Id, $"{baseSide.SectorName}: {(baseSide.Existing != null ? "reused vanilla gate" : "new gate there")}"));
        Choice("Leads to", options.ToArray(), g.Link != null ? (baseSide != null ? "b" : "g") + g.Link : "", v =>
        {
            if (v == "") P.Unlink(g);
            else if (v[0] == 'b') { }                                  // stays as it is
            else if (v[0] == 'g') P.Link(g, P.GateById(int.Parse(v[1..]))!);
            else { var s2 = P.SectorById(int.Parse(v[1..]))!; var g2 = P.AddGate(s2, -g.X, -g.Z); g2.Type = g.Type; P.Link(g, g2); }
        });
        Choice("Type", new[] { ("gate", "Jump gate"), ("accelerator", "Accelerator") }, g.Type, v => g.Type = v);
        Number("East x (km)", g.X / 1000, -1000, 1000, 1, v => g.X = Math.Round(v * 1000));
        Number("Height y (km)", g.Y / 1000, -200, 200, 1, v => g.Y = Math.Round(v * 1000), HeightTip);
        Number("North z (km)", g.Z / 1000, -1000, 1000, 1, v => g.Z = Math.Round(v * 1000));
        Number("Facing (°)", g.Yaw, -180, 180, 5, v => g.Yaw = GalaxyOps.NormYaw(v), FacingTip);
        var row = new HFlowContainer(); _box.AddChild(row);
        Button("Face sector centre", () => _app.Change(() => g.Yaw = GalaxyOps.FaceCentre(g.X, g.Z)), row);
        if (g.Link != null && baseSide == null) Button("Go to other side", () => { var g2 = P.GateById(g.Link)!; _app.OpenSector(P.SectorOfGate(g2.Id)!.Id); _app.Sel = new Selection("gate", g2.Id); _app.Refresh(); }, row);
        EndSection();
        row = new HFlowContainer(); _box.AddChild(row);
        Button("Delete gate", _app.DeleteSelection, row);
        Button("◀ Sector", () => { _app.Sel = null; _app.Refresh(); }, row);
        if (baseSide != null)
        {
            Head($"Other side: {baseSide.SectorName}", baseSide.Existing != null
                ? "An unused gate of the galaxy you patch: the patch only links it to this gate. Its place stays as the game has it."
                : "A new gate the patch adds to this sector of the galaxy you patch. Keep it 40 km or more away from the gates already there.");
            if (baseSide.Existing != null) Hint("Reused gate, place as in the game.");
            else
            {
                Button($"Open {baseSide.SectorName} in 3D to move it", () => _app.OpenBaseGate(baseSide));
                Number("East x (km)", baseSide.X / 1000, -1000, 1000, 1, v => baseSide.X = Math.Round(v * 1000));
                Number("Height y (km)", baseSide.Y / 1000, -200, 200, 1, v => baseSide.Y = Math.Round(v * 1000), HeightTip);
                Number("North z (km)", baseSide.Z / 1000, -1000, 1000, 1, v => baseSide.Z = Math.Round(v * 1000));
                Number("Facing (°)", baseSide.Yaw, -180, 180, 5, v => baseSide.Yaw = GalaxyOps.NormYaw(v), FacingTip);
                Button("Face its sector centre", () => _app.Change(() => baseSide.Yaw = GalaxyOps.FaceCentre(baseSide.X, baseSide.Z)));
            }
        }
    }

    void ProjectSettings()
    {
        var P = _app.P;
        static string Clean(string v) => new string(v.Trim().ToLowerInvariant().Select(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_' ? ch : '_').ToArray());
        Head("Mod", "Name, folder and prefix of the exported mod.");
        Text("Mod name", P.ModName, v => P.ModName = v);
        Text("Mod folder / id", P.ModId, v => P.ModId = Clean(v), "Folder name and id of the exported mod (lower case, letters, digits, _).");
        Text("Prefix", P.Prefix, v => P.Prefix = Clean(v).Replace("_", ""), "Starts every name the mod creates, so it cannot clash with other mods. Don't change it once saves use the mod.");
        Text("Author", P.Author, v => P.Author = v);
        var full = new CheckBox { Text = "Full conversion", ButtonPressed = P.ReplaceVanillaStations,
                                  TooltipText = "Removes all vanilla stations from the game's station list, for every game start. Leave it off while testing." };
        full.Toggled += v => _app.Change(() => P.ReplaceVanillaStations = v); _box.AddChild(full);
        Head("Texts", "The text page holds the sector names; each ticked language gets its own text file.");
        Number("Text page", P.TextPage, 1, 9_999_999, 1, v => P.TextPage = (int)v, "Holds the sector names; no other mod may use it. \"Check page\" tells whether it is free.");
        var pageRow = new HFlowContainer(); _box.AddChild(pageRow);
        var check = Button("Check page", () =>
        {
            var who = _app.PageOwners();
            _app.SetStatus(_app.Game == null ? "Game files not read: page not checked."
                : who.Count == 0 ? $"Text page {P.TextPage} is free." : $"Text page {P.TextPage} is used by: {string.Join(", ", who)}");
        }, pageRow);
        check.TooltipText = "Is the page number free in the installed game and mods?";
        _box.AddChild(new Label { Text = "Text files (languages) ⓘ", Modulate = new Color(1, 1, 1, 0.7f), MouseFilter = MouseFilterEnum.Stop,
                             TooltipText = "Each ticked language gets its own t/0001-lNNN.xml with the same (English) names. Nothing ticked = all 16 game languages." });
        var langs = new HFlowContainer(); _box.AddChild(langs);
        foreach (var (lid, lname) in X4Data.Text.TextPages.Languages)
        {
            var cb = new CheckBox { Text = lname, ButtonPressed = P.TextLanguages.Count == 0 || P.TextLanguages.Contains(lid) };
            cb.Toggled += v => _app.Change(() =>
            {
                if (P.TextLanguages.Count == 0) P.TextLanguages.AddRange(X4Data.Text.TextPages.Languages.Select(l => l.Id));   // "all" -> explicit list
                P.TextLanguages.Remove(lid); if (v) P.TextLanguages.Add(lid);
            });
            langs.AddChild(cb);
        }
        EndSection();
        Button("Close", () => _app.CloseWindow(this)).TooltipText = "Esc closes this panel too.";
    }

    // ---------------------------------------------------------------------------------------------- field helpers
    // Explanations are tooltips (hover), not text in the panel: the panel shows values, data and warnings only.
    const string HeightTip = "Above (+) or below (-) the ecliptic, the sector's main plane.";
    const string FacingTip = "0° = north, 90° = east. Keys R / Shift+R turn the gate.";

    // ---- foldable sections: Head starts one (folded by default), the fields after it go inside until the next Head / EndSection.
    // What the user unfolded stays unfolded while the editor runs (the panel is rebuilt after every change).
    static readonly System.Collections.Generic.HashSet<string> OpenSections = new();
    Container _box = null!;                          // where the field helpers add their controls

    /// <summary>Section heading = fold button (▸ folded, ▾ open); with a tip it shows ⓘ and explains the section on hover.</summary>
    void Head(string t, string? tip = null, string? key = null)
    {
        _box = this;
        AddChild(new HSeparator());
        key ??= t.Split('(', ':')[0].Replace("✓", "").Trim();            // "Decoration (3)" and "Other side: X" keep their state
        bool open = OpenSections.Contains(key);
        string Caption(bool o) => (o ? "▾  " : "▸  ") + t.ToUpperInvariant() + (tip != null ? "  ⓘ" : "");
        var h = new Button { Text = Caption(open), Flat = true, Alignment = HorizontalAlignment.Left, FocusMode = FocusModeEnum.None,
                             TooltipText = tip ?? "", Modulate = new Color(1, 1, 1, 0.75f) };
        var content = new VBoxContainer { Visible = open };
        content.AddThemeConstantOverride("separation", 4);
        h.Pressed += () =>
        {
            bool o = !content.Visible; content.Visible = o; h.Text = Caption(o);
            if (o) OpenSections.Add(key); else OpenSections.Remove(key);
        };
        AddChild(h); AddChild(content);
        _box = content;
    }
    public static void Unfold(string[] keys) { foreach (var k in keys) OpenSections.Add(k.Trim()); }
    /// <summary>What follows is outside any section (action buttons: always visible).</summary>
    void EndSection() => _box = this;
    /// <summary>A grey line of data (counts, places, lists); tip = more on hover.</summary>
    void Hint(string t, string? tip = null) => _box.AddChild(new Label { Text = t, AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = new Color(1, 1, 1, 0.55f), CustomMinimumSize = new Vector2(320, 0), SizeFlagsHorizontal = SizeFlags.ExpandFill,
                                                                    TooltipText = tip ?? "", MouseFilter = tip != null ? MouseFilterEnum.Stop : MouseFilterEnum.Ignore });

    /// <summary>Label + room for the field; the tip shows on the label (the field gets it from its caller).</summary>
    HBoxContainer Row(string label, string? tip = null)
    {
        var row = new HBoxContainer(); _box.AddChild(row);
        row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(110, 0), Modulate = new Color(1, 1, 1, 0.7f),
                                 TooltipText = tip ?? "", MouseFilter = tip != null ? MouseFilterEnum.Stop : MouseFilterEnum.Ignore });
        return row;
    }

    /// <summary>
    /// Name with the "Voice" button: choose one of the game's spoken names (VoicePicker). Typing a name by hand makes it
    /// silent again (the text is then written as typed).
    /// </summary>
    void NameField(string value, string? nameRef, Action<string, string?> set)
    {
        var tip = nameRef != null ? $"Spoken name (text {nameRef}): the game says it and shows it in each player's language. Typing another name makes it silent." : null;
        var e = new LineEdit { Text = value, SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = tip ?? "" };
        void Commit() { if (e.Text != value) { var t = e.Text; _app.Change(() => set(t, null)); } }
        e.TextSubmitted += _ => Commit(); e.FocusExited += Commit;
        var row = Row("Name", tip);
        row.AddChild(e);
        var b = new Button { Text = nameRef != null ? "Voice ✓" : "Voice ▾", FocusMode = FocusModeEnum.None,
                             TooltipText = (tip != null ? tip + "\n\n" : "") + "Choose a spoken name: names the game has voice lines for (English and German), free ones first." };
        b.Pressed += () => _app.ShowVoicePicker(nameRef, (n, r) => _app.Change(() => set(n, r)));
        row.AddChild(b);
    }

    /// <summary>A sector of the patched galaxy (read-only view): what it has, nothing to edit.</summary>
    void ReadOnlyFields()
    {
        var s = _app.P.SectorById(_app.OpenSectorId);
        if (s == null) return;
        Head("Read-only sector", "This sector belongs to the galaxy your patch adds to: you can look, not change (only your patch's new gates move). Ctrl+C on a field copies it, to paste it (Ctrl+V) into one of your own sectors.");
        _box.AddChild(new Label { Text = s.Name, Modulate = new Color("e8f1ff") });
        Hint($"Sunlight {s.Sunlight:0.##}   Economy {s.Economy:0.##}   Security {s.Security:0.##}");
        if (_app.Sel is { Kind: "decor" } ds && _app.P.DecorById(ds.Id) is { } d)
        {
            Head("Selected field");
            _box.AddChild(new Label { Text = _app.Decor?.Get(d.Kind, d.Ref)?.Label ?? d.Ref, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(320, 0) });
            Hint($"{d.Ref}   at {d.X / 1000:0} / {d.Y / 1000:0} / {d.Z / 1000:0} km");
        }
        if (_app.Sel is { Kind: "gate" } gs && _app.P.ViewBaseGates.ContainsKey(gs.Id) && _app.P.GateById(gs.Id) is { } my)
        {
            Head("Your patch's new gate", "The patch adds it to this sector: drag it, use the arrows / ring, or type the place. Keep it 40 km or more away from the gates already there.");
            Hint($"Leads to {_app.P.SectorOfGate(my.Link ?? -1)?.Name}");
            Number("East x (km)", my.X / 1000, -1000, 1000, 1, v => my.X = Math.Round(v * 1000));
            Number("Height y (km)", my.Y / 1000, -200, 200, 1, v => my.Y = Math.Round(v * 1000), HeightTip);
            Number("North z (km)", my.Z / 1000, -1000, 1000, 1, v => my.Z = Math.Round(v * 1000));
            Number("Facing (°)", my.Yaw, -180, 180, 5, v => my.Yaw = GalaxyOps.NormYaw(v), FacingTip);
            Button("Face the sector centre", () => _app.Change(() => my.Yaw = GalaxyOps.FaceCentre(my.X, my.Z)));
            var near = s.Gates.Where(o => o.Id != my.Id).Select(o => (o, d: Math.Sqrt((o.X - my.X) * (o.X - my.X) + (o.Y - my.Y) * (o.Y - my.Y) + (o.Z - my.Z) * (o.Z - my.Z)))).OrderBy(t => t.d).FirstOrDefault();
            if (near.o != null && near.d < 40_000)
                _box.AddChild(new Label { Text = $"⚠ Only {near.d / 1000:0} km from the gate to {_app.P.SectorOfGate(near.o.Link ?? -1)?.Name ?? "?"}: keep 40 km or more.", Modulate = new Color("ffb347"), AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(320, 0) });
        }
        Head($"Gates ({s.Gates.Count})");
        foreach (var g in s.Gates)
            Hint($"{(g.Type == "accelerator" ? "Accelerator" : "Gate")} at {g.X / 1000:0} / {g.Z / 1000:0} km → {(g.Link != null ? _app.P.SectorOfGate(g.Link.Value)?.Name : "nothing (unused)")}");
        Head($"Fields ({s.Decor.Count})");
        foreach (var grp in s.Decor.GroupBy(x => x.Ref).OrderBy(x => x.Key))
            Hint($"{grp.Count()} × {_app.Decor?.Get("region", grp.Key)?.Label ?? grp.Key}");
        Head("Resources");
        if (s.Res.Count == 0) Hint("none");
        foreach (var r in s.Res) Hint($"{r.Amount} × {r.Size} {r.Ware}, yield {r.Yield}, refill {r.Speed}");
        EndSection();
        Button("◀ Back to the galaxy", _app.BackToGalaxy);
    }

    void Text(string label, string value, Action<string> set, string? tip = null)
    {
        var e = new LineEdit { Text = value, SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = tip ?? "" };
        void Commit() { if (e.Text != value) { var t = e.Text; _app.Change(() => set(t)); } }
        e.TextSubmitted += _ => Commit(); e.FocusExited += Commit;
        Row(label, tip).AddChild(e);
    }

    void Area(string label, string value, Action<string> set)
    {
        var e = new TextEdit { Text = value, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 52), WrapMode = TextEdit.LineWrappingMode.Boundary };
        e.FocusExited += () => { if (e.Text != value) { var t = e.Text; _app.Change(() => set(t)); } };
        Row(label).AddChild(e);
    }

    void Number(string label, double value, double min, double max, double step, Action<double> set, string? tip = null)
    {
        var e = new SpinBox { MinValue = min, MaxValue = max, Step = step, Value = value, SizeFlagsHorizontal = SizeFlags.ExpandFill, AllowGreater = true, AllowLesser = true, TooltipText = tip ?? "" };
        e.ValueChanged += v => _app.Change(() => set(v));
        Row(label, tip).AddChild(e);
    }

    /// <summary>Dropdown; arrows = also ◀ ▶ buttons that step to the previous / next entry (wrapping around).</summary>
    void Choice(string label, (string value, string text)[] options, string current, Action<string> set, Func<string, string?>? warnFirst = null, bool arrows = false, string? tip = null, Func<string, bool>? dice = null)
    {
        var o = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill, FitToLongestItem = false, ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, TooltipText = tip ?? "" };
        for (int i = 0; i < options.Length; i++) { o.AddItem(options[i].text, i); if (options[i].value == current) o.Selected = i; }
        void Pick(long i)
        {
            var v = options[i].value;
            if (warnFirst?.Invoke(v) is { } warning) _app.Confirm(warning, () => _app.Change(() => set(v)), () => Rebuild());   // No: dropdown shows the old choice again
            else _app.Change(() => set(v));
        }
        o.ItemSelected += Pick;
        var row = Row(label, tip);
        if (arrows && options.Length > 1)
        {
            void Step(int d) => Pick(((o.Selected < 0 ? 0 : o.Selected) + d + options.Length) % options.Length);
            var prev = new Button { Text = "◀", FocusMode = FocusModeEnum.None, TooltipText = "Previous" }; prev.Pressed += () => Step(-1);
            var next = new Button { Text = "▶", FocusMode = FocusModeEnum.None, TooltipText = "Next" }; next.Pressed += () => Step(1);
            row.AddChild(prev); row.AddChild(o); row.AddChild(next);
        }
        else row.AddChild(o);
        // dice: a random entry other than the current one, among those dice() allows
        if (dice != null)
        {
            var pool = Enumerable.Range(0, options.Length).Where(i => options[i].value != current && dice(options[i].value)).ToArray();
            var b = new Button { Icon = App.SvgIcon(App.DiceSvg), FocusMode = FocusModeEnum.None, TooltipText = "Random " + label.ToLowerInvariant(), Disabled = pool.Length == 0 };
            b.Pressed += () => Pick(pool[Rng.Next(pool.Length)]);
            row.AddChild(b);
        }
    }

    void Opt(HBoxContainer row, string[] list, string current, Action<string> set)
    {
        var o = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill, FitToLongestItem = false, ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        for (int i = 0; i < list.Length; i++) { o.AddItem(list[i], i); if (list[i] == current) o.Selected = i; }
        o.ItemSelected += i => { var v = list[i]; _app.Change(() => set(v)); };
        row.AddChild(o);
    }

    static readonly Random Rng = new();

    Button Button(string text, Action press, Container? parent = null)
    {
        // long names are cut with "…" (tooltip shows all), so the panel keeps its width and the 3D view its room
        // only full-width list buttons cut their text; buttons side by side in a row keep their natural width
        var b = new Button { Text = text, FocusMode = FocusModeEnum.None, TooltipText = text };
        if (parent == null) { b.ClipText = true; b.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis; }   // (an overrun setting also shrinks the minimum width)
        b.Pressed += press; (parent ?? _box).AddChild(b); return b;
    }
}
