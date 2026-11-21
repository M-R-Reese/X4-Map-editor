using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using X4Data.Galaxy;
using X4Data.Text;

namespace GalaxyEditor3D;

/// <summary>
/// "Voice" button next to a cluster / sector name: the game's spoken location names (text page 20005), free ones
/// (no system / sector of the game, a DLC or an installed mod uses them) first, with a Roman numeral if wanted
/// (page 20402, spoken "one", "two" ...). Listen plays the voice line from the game archives.
/// </summary>
public partial class VoicePicker : Window
{
    readonly App _app;
    readonly string? _current;
    readonly Action<string, string?> _use;
    VoicedNames? _names;
    TextDb? _de;
    LineEdit _search = null!;
    CheckBox _showUsed = null!;
    OptionButton _numeral = null!;
    ItemList _list = null!;
    Label _info = null!;
    AudioStreamPlayer _player = null!;
    readonly Queue<string> _toPlay = new();
    List<VoicedNames.Entry> _shown = new();

    static VoicedNames? _cache;       // read once per run (the texts of two languages + the archive lists)
    static TextDb? _cacheDe;

    public VoicePicker(App app, string? currentRef, Action<string, string?> use) { _app = app; _current = currentRef; _use = use; }

    public override void _Ready()
    {
        Title = "Spoken names";
        Size = new Vector2I(680, 600);
        Transient = true;
        var bg = new PanelContainer(); bg.SetAnchorsPreset(Control.LayoutPreset.FullRect); AddChild(bg);
        var margin = new MarginContainer(); bg.AddChild(margin);
        foreach (var side in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 12);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        margin.AddChild(box);
        // the explanation is the heading's tooltip (hover)
        box.AddChild(new Label
        {
            Text = "SPOKEN NAMES  ⓘ", Modulate = new Color(1, 1, 1, 0.6f), MouseFilter = Control.MouseFilterEnum.Stop,
            TooltipText = "Names the game has voice lines for (English and German). The game says them and shows them in each player's language.\n"
                        + "\"Free\" = no system or sector of the game, its DLCs or your installed mods uses it.",
        });
        var row = new HBoxContainer(); box.AddChild(row);
        _search = new LineEdit { PlaceholderText = "Search (English or German)", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClearButtonEnabled = true };
        _search.TextChanged += _ => Fill();
        row.AddChild(_search);
        _showUsed = new CheckBox { Text = "Show used ones", TooltipText = "Also list names a system or sector of the game, its DLCs or your mods already uses (greyed)." };
        _showUsed.Toggled += _ => Fill();
        row.AddChild(_showUsed);
        _numeral = new OptionButton { TooltipText = "Roman numeral after the name, spoken as a number (\"Black Hole Sun IV\" = \"... four\")" };
        _numeral.AddItem("no number", 0);
        _numeral.Selected = 0;
        row.AddChild(_numeral);
        _list = new ItemList { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SelectMode = ItemList.SelectModeEnum.Single };
        _list.ItemActivated += i => Use((int)i);
        _list.ItemSelected += i => ShowInfo((int)i);
        box.AddChild(_list);
        _info = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(600, 40), Modulate = new Color(1, 1, 1, 0.75f) };
        box.AddChild(_info);
        var buttons = new HBoxContainer(); box.AddChild(buttons);
        Btn(buttons, "▶ Listen (English)", () => Listen(44));
        Btn(buttons, "▶ German", () => Listen(49));
        Btn(buttons, "Use this name", () => { if (_list.GetSelectedItems() is { Length: > 0 } s) Use(s[0]); });
        if (_current != null) Btn(buttons, "Make silent", () => { Close(); _use(Display(_current), null); });
        Btn(buttons, "Close", Close);
        _player = new AudioStreamPlayer { VolumeDb = -2 };
        _player.Finished += PlayNext;
        AddChild(_player);
        CloseRequested += Close;
        _app.OpenWindow(this, Close);
        Load();
    }

    bool _closed;
    void Close()
    {
        if (_closed) return;
        _closed = true;
        _player.Stop();
        _app.WindowClosed(this);
        QueueFree();
    }

    static void Btn(Container parent, string text, Action press) { var b = new Button { Text = text, FocusMode = Control.FocusModeEnum.None }; b.Pressed += press; parent.AddChild(b); }

    async void Load()
    {
        if (_app.Game == null || _app.Texts == null) { _info.Text = "The game files are not read yet: try again in a moment."; return; }
        if (_cache == null)
        {
            _info.Text = "Reading the voice lines …";
            var g = _app.Game; var en = _app.Texts;
            (_cache, _cacheDe) = await Task.Run(() => { var de = new TextDb(g, 49); return (VoicedNames.Build(g, en, de), de); });
            if (!IsInstanceValid(this)) return;
        }
        _names = _cache; _de = _cacheDe;
        foreach (var (id, text) in _names.Numerals) _numeral.AddItem(text, id);
        int cur = VoicedNames.Numeral(_current);
        for (int i = 0; i < _numeral.ItemCount; i++) if (_numeral.GetItemId(i) == cur) _numeral.Selected = i;
        Fill();
        var pick = VoicedNames.PoolId(_current);
        for (int i = 0; i < _shown.Count; i++) if (_shown[i].Id == pick) { _list.Select(i); _list.EnsureCurrentIsVisible(); ShowInfo(i); }
        if (pick == null) _info.Text = $"{_names.Names.Count(n => n.Free)} free of {_names.Names.Count} spoken names. Double-click a name to use it.";
    }

    /// <summary>Pool ids the open project already uses (other clusters / sectors).</summary>
    Dictionary<int, string> InProject()
    {
        var d = new Dictionary<int, string>();
        var p = _app.EditP;
        foreach (var c in p.Clusters)
        {
            if (VoicedNames.PoolId(c.NameRef) is int ci) d.TryAdd(ci, c.Name + " (your cluster)");
            foreach (var s in c.Sectors) if (VoicedNames.PoolId(s.NameRef) is int si) d.TryAdd(si, s.Name + " (your sector)");
        }
        return d;
    }

    void Fill()
    {
        if (_names == null) return;
        _list.Clear();
        var q = _search.Text.Trim();
        var mine = InProject();
        _shown = _names.Names
            .Where(n => _showUsed.ButtonPressed || n.Free)
            .Where(n => q == "" || n.English.Contains(q, StringComparison.OrdinalIgnoreCase) || (n.German?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderBy(n => n.Free ? 0 : 1).ThenBy(n => mine.ContainsKey(n.Id) ? 1 : 0).ThenBy(n => n.English, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var n in _shown)
        {
            var text = n.English + (n.German != null && n.German != n.English ? $"   ·   {n.German}" : "") + $"   ·   {n.Group}";
            if (mine.TryGetValue(n.Id, out var who)) text += $"   ·   in your project: {who}";
            if (!n.Free) text += $"   ·   used: {string.Join(", ", n.UsedBy.Take(2))}";
            int i = _list.AddItem(text);
            if (!n.Free || mine.ContainsKey(n.Id)) _list.SetItemCustomFgColor(i, new Color(1, 1, 1, 0.45f));
            _list.SetItemTooltip(i, $"{{{VoicedNames.PoolPage},{n.Id}}}  voice: {(n.VoiceEn ? "English" : "")}{(n.VoiceEn && n.VoiceDe ? " + " : "")}{(n.VoiceDe ? "German" : "")}");
        }
    }

    void ShowInfo(int i)
    {
        if (i < 0 || i >= _shown.Count) return;
        var n = _shown[i];
        _info.Text = $"{n.English}  (German: {n.German ?? "?"}), text {{{VoicedNames.PoolPage},{n.Id}}}. "
                   + (n.Free ? "Free: nothing in the game uses it." : $"Used by {string.Join(", ", n.UsedBy)}: two places with the same name confuse players.");
    }

    int NumeralId => _numeral.GetItemId(_numeral.Selected);

    void Use(int i)
    {
        if (i < 0 || i >= _shown.Count) return;
        var n = _shown[i];
        void Go() { Close(); _use(n.English + (NumeralId > 0 ? " " + _names!.Numerals.First(x => x.Id == NumeralId).Text : ""), n.Reference(NumeralId)); }
        if (!n.Free) _app.Confirm($"\"{n.English}\" is already used by {string.Join(", ", n.UsedBy)}.\n\nUse it anyway?", Go);
        else Go();
    }

    /// <summary>Editor text of a reference (for "Make silent": the name stays, only the voice goes).</summary>
    string Display(string reference) => _app.Texts?.Lookup(reference) ?? (_app.Texts != null ? TextDb.Clean(System.Text.RegularExpressions.Regex.Replace(reference, @"\{(\d+),(\d+)\}", m => _app.Texts.Get(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) ?? m.Value)) : reference);

    void Listen(int language)
    {
        if (_list.GetSelectedItems() is not { Length: > 0 } s || _app.Game == null) return;
        var n = _shown[s[0]];
        _toPlay.Clear();
        _toPlay.Enqueue($"voice-L{language:000}/{VoicedNames.PoolPage}/normal/{n.Id}.ogg");
        if (NumeralId > 0) _toPlay.Enqueue($"voice-L{language:000}/{VoicedNames.NumeralPage}/normal/{NumeralId}.ogg");
        _player.Stop();
        PlayNext();
    }

    void PlayNext()
    {
        if (_toPlay.Count == 0 || _app.Game == null) return;
        var path = _toPlay.Dequeue();
        if (!_app.Game.Vfs.Exists(path)) { _info.Text = "No voice line in this language: " + path; return; }
        var stream = AudioStreamOggVorbis.LoadFromBuffer(_app.Game.Vfs.Read(path));
        if (stream == null) { _info.Text = "Not playable: " + path; return; }
        _player.Stream = stream;
        _player.Play();
    }
}
