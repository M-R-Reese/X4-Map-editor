using System;
using System.Linq;
using Godot;
using X4Data;

namespace GalaxyEditor3D;

/// <summary>
/// First-start window: the user picks the game folder (required) and optional folders. Fields start with guesses
/// (Steam library list, Documents\Egosoft\X4). "Start" checks them and saves user://settings.json.
/// </summary>
public partial class SetupPanel : PanelContainer
{
    public event Action<Settings>? Done;
    readonly Settings _s;
    LineEdit _game = null!, _profile = null!, _mods = null!, _extracted = null!, _export = null!;
    Label _problems = null!;

    public SetupPanel(Settings s) => _s = s;

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(860, 0);
        // solid window look (the default panel style is see-through)
        var style = new StyleBoxFlat { BgColor = new Color("1b2129"), BorderColor = new Color("3a4758"), CornerDetail = 4 };
        style.SetBorderWidthAll(1); style.SetCornerRadiusAll(6); style.SetContentMarginAll(20);
        AddThemeStyleboxOverride("panel", style);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 10);
        AddChild(box);
        box.AddChild(new Label { Text = $"{App.AppName}: folders  ⓘ", ThemeTypeVariation = "HeaderLarge", MouseFilter = MouseFilterEnum.Stop,
                                 TooltipText = "The editor reads the models straight from your installed game. Nothing is copied or shipped." });
        _game = Row(box, "Game folder (required)", _s.GameFolder, "folder with X4.exe and 01.cat");
        _profile = Row(box, "Profile folder (optional)", _s.ProfileFolder, "Documents\\Egosoft\\X4\\<number>: says which mods are on");
        _mods = Row(box, "Other mod folders (optional)", string.Join(";", _s.ExtraModFolders), "mods outside the game's extensions folder: a mod folder or a folder of mods, several with ;");
        _extracted = Row(box, "Extracted folders (optional)", string.Join(";", _s.ExtractedFolders), "unpacked game files, several with ;");
        _export = Row(box, "Export folder", _s.ExportFolder, "where exported galaxy mods go");
        _problems = new Label { Modulate = new Color(1, 0.5f, 0.4f), AutowrapMode = TextServer.AutowrapMode.WordSmart };
        box.AddChild(_problems);
        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        box.AddChild(buttons);
        var cancel = new Button { Text = "Cancel", CustomMinimumSize = new Vector2(120, 36), TooltipText = "Close without changes (Esc)" };
        cancel.Pressed += () => Cancelled?.Invoke();
        buttons.AddChild(cancel);
        var start = new Button { Text = "Start", CustomMinimumSize = new Vector2(160, 36) };
        start.Pressed += OnStart;
        buttons.AddChild(start);
    }

    /// <summary>Cancel button (or Esc via the app's window list): close without saving anything.</summary>
    public event Action? Cancelled;

    LineEdit Row(VBoxContainer parent, string label, string value, string hint)
    {
        parent.AddChild(new Label { Text = label, TooltipText = hint, MouseFilter = MouseFilterEnum.Stop });
        var row = new HBoxContainer();
        var edit = new LineEdit { Text = value, PlaceholderText = hint, SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = hint };
        var browse = new Button { Text = "Browse..." };
        browse.Pressed += () =>
        {
            var dlg = new FileDialog { FileMode = FileDialog.FileModeEnum.OpenDir, Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = true, Title = label };
            if (edit.Text != "") dlg.CurrentDir = edit.Text.Split(';')[0];
            dlg.DirSelected += d => edit.Text = d.Replace('/', '\\');
            AddChild(dlg);
            dlg.PopupCentered(new Vector2I(900, 600));
        };
        row.AddChild(edit); row.AddChild(browse);
        parent.AddChild(row);
        return edit;
    }

    void OnStart()
    {
        _s.GameFolder = _game.Text.Trim();
        _s.ProfileFolder = _profile.Text.Trim();
        _s.ExtraModFolders = _mods.Text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        _s.ExtractedFolders = _extracted.Text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        _s.ExportFolder = _export.Text.Trim();
        var p = _s.Problems();
        _problems.Text = string.Join("\n", p);
        if (p.Count == 0) Done?.Invoke(_s);
    }
}
