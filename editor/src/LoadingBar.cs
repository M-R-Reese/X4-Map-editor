using Godot;

namespace GalaxyEditor3D;

/// <summary>
/// The loading window at start: what is being done (step), how far (bar), and the item in work (detail). Lies above
/// everything (start menu too) and takes the clicks while it is open; the editor is usable when it closes.
/// Start-up may take its time (2026-11-22): game files, lists, then the warm-up that builds the project's models,
/// textures and shaders so the editor runs smooth afterwards.
/// </summary>
public partial class LoadingBar : CanvasLayer
{
    readonly Label _step, _detail;
    readonly ProgressBar _bar;

    public LoadingBar()
    {
        Layer = 20;
        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.55f), MouseFilter = Control.MouseFilterEnum.Stop };
        dim.SetAnchorsPreset(Control.LayoutPreset.FullRect); AddChild(dim);
        var centre = new CenterContainer(); centre.SetAnchorsPreset(Control.LayoutPreset.FullRect); AddChild(centre);
        var panel = new PanelContainer { CustomMinimumSize = new Vector2(480, 0) }; centre.AddChild(panel);
        var margin = new MarginContainer(); panel.AddChild(margin);
        foreach (var side in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 18);
        var col = new VBoxContainer(); col.AddThemeConstantOverride("separation", 10); margin.AddChild(col);
        col.AddChild(new Label { Text = "Loading", ThemeTypeVariation = "HeaderMedium" });
        _step = new Label(); col.AddChild(_step);
        _bar = new ProgressBar { MinValue = 0, MaxValue = 100, ShowPercentage = true, CustomMinimumSize = new Vector2(0, 22) }; col.AddChild(_bar);
        _detail = new Label { Modulate = new Color(1, 1, 1, 0.6f), ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        col.AddChild(_detail);
    }

    /// <summary>A new step; at = how far the whole loading is (0..1).</summary>
    public void Step(string text, double at) { _step.Text = text; _detail.Text = ""; At(at); }

    /// <summary>Progress inside the step (0..1 of the whole) and the item in work.</summary>
    public void Progress(double at, string detail = "") { At(at); _detail.Text = detail; }

    void At(double at) => _bar.Value = System.Math.Clamp(at, 0, 1) * 100;
}
