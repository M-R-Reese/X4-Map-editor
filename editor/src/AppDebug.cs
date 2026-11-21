using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Godot;
using X4Data;
using X4Data.Galaxy;

namespace GalaxyEditor3D;

/// <summary>
/// Messages of this session (status line texts, errors), newest last, for the debug report. Also printed, so they
/// reach Godot's own log file (user://logs/godot.log).
/// </summary>
public static class DebugLog
{
    static readonly List<string> Lines = new();
    const int Max = 2000;

    public static void Add(string text)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {text}";
        lock (Lines) { Lines.Add(line); if (Lines.Count > Max) Lines.RemoveRange(0, Lines.Count - Max); }
        GD.Print(line);
    }

    public static string All() { lock (Lines) return string.Join("\n", Lines); }

    /// <summary>Errors nobody caught (outside Godot's own callbacks: those Godot writes into its log itself).</summary>
    public static void CatchUnhandled()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Add("UNHANDLED: " + e.ExceptionObject);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) => { Add("UNOBSERVED TASK: " + e.Exception); e.SetObserved(); };
    }
}

/// <summary>
/// Debug window: switches for the look (shader layers, normal map direction, frame statistics) and the DEBUG REPORT:
/// one zip with what is needed to find a fault (system, settings, extensions, notes, logs, screenshot, optionally the
/// project). Paths are made anonymous (user name, profile number). No game files go into it.
/// </summary>
public partial class App
{
    /// <summary>
    /// Where players send debug reports (shown in the debug window and the README). The editor sends nothing itself:
    /// a mail button (mailto) was dropped 2026-11-20: it needs a mail program set up (failed on Windows with new
    /// Outlook, often none on Linux), and a mail server password must never be inside a public program.
    /// </summary>
    public const string DebugMail = "debug@astrainferna.com";
    public const string IssuesUrl = "https://github.com/M-R-Reese/X4-Map-editor/issues";

    /// <summary>Screenshot of the window as it was when the debug window opened (the window itself is not on it).</summary>
    Image? _debugShot;
    string? _lastReport;

    void ShowDebug()
    {
        _debugShot = GetViewport().GetTexture().GetImage();
        var layer = new CanvasLayer { Layer = 10 }; AddChild(layer);
        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.5f) }; dim.SetAnchorsPreset(LayoutPreset.FullRect); layer.AddChild(dim);
        var centre = new CenterContainer(); centre.SetAnchorsPreset(LayoutPreset.FullRect); layer.AddChild(centre);
        var panel = new PanelContainer { CustomMinimumSize = new Vector2(560, 0) }; centre.AddChild(panel);
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color("1b2028"), BorderColor = new Color("3a4350"), BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1, CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6 });
        var box = new VBoxContainer(); box.AddThemeConstantOverride("separation", 8);
        var margin = new MarginContainer(); foreach (var side in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 16);
        margin.AddChild(box); panel.AddChild(margin);
        Label Hint(string t) { var l = new Label { Text = t, AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = new Color(1, 1, 1, 0.6f), CustomMinimumSize = new Vector2(520, 0) }; box.AddChild(l); return l; }
        void Check(string text, bool on, Action<bool> set) { var c = new CheckBox { Text = text, ButtonPressed = on }; c.Toggled += v => set(v); box.AddChild(c); }

        box.AddChild(new Label { Text = "DEBUG" });
        Hint("Look checks: switch single layers of the X4 materials off to see what each does.");
        Check("Glow maps (lights)", X4Shaders.Glow, v => { X4Shaders.Glow = v; X4Shaders.ApplyAll(); });
        Check("Metal", X4Shaders.Metal, v => { X4Shaders.Metal = v; X4Shaders.ApplyAll(); });
        Check("Vertex colours", X4Shaders.VertexColours, v => { X4Shaders.VertexColours = v; X4Shaders.ApplyAll(); });
        Check("Normal maps: green flipped (key N)", X4Shaders.FlipNormalGreen, v => { if (v != X4Shaders.FlipNormalGreen) X4Shaders.ToggleNormalGreen(); });
        Check("Frame rate and draw calls in the 3D view", _sector.ShowStats, v => _sector.ShowStats = v);

        box.AddChild(new HSeparator());
        box.AddChild(new Label { Text = "DEBUG REPORT" });
        Hint("Makes one zip file on your desktop to send with a bug report: editor version, system and "
            + "graphics card, folder settings, game extensions, the editor's notes and logs, and a screenshot. Your user name and "
            + "profile number are replaced in all paths. No game files are put in. Have a look inside before you send it.");
        bool withProject = true;
        Check("Include the open project (names and positions of your galaxy)", true, v => withProject = v);
        var row = new HBoxContainer(); box.AddChild(row);
        var result = Hint("");
        Btn(row, "Create debug report", () =>
        {
            try
            {
                var zip = CreateDebugReport(withProject);
                result.Text = "Saved: " + zip;
                SetStatus("Debug report saved: " + zip);
                OS.ShellShowInFileManager(zip);
                _lastReport = zip;
            }
            catch (Exception e) { result.Text = "The report could not be made: " + e.Message; DebugLog.Add("debug report failed: " + e); }
        });
        Btn(row, "Open log folder", () => OS.ShellOpen(ProjectSettings.GlobalizePath("user://logs")));

        // where to send it: plain text you can select, plus a copy button (works the same on Windows and Linux)
        box.AddChild(new HSeparator());
        box.AddChild(new Label { Text = "SEND IT TO" });
        Hint("Attach the zip and write in a few words what you did and what went wrong.");
        void Contact(string label, string text, bool web)
        {
            var line = new HBoxContainer(); box.AddChild(line);
            line.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(110, 0), Modulate = new Color(1, 1, 1, 0.7f) });
            line.AddChild(new LineEdit { Text = text, Editable = false, SelectingEnabled = true, SizeFlagsHorizontal = SizeFlags.ExpandFill });
            Btn(line, "Copy", () => { DisplayServer.ClipboardSet(text); result.Text = $"Copied: {text}"; });
            if (web) Btn(line, "Open", () => OS.ShellOpen(text));
        }
        Contact("E-mail", DebugMail, false);
        Contact("GitHub Issues", IssuesUrl, true);
        box.AddChild(new HSeparator());
        var bottom = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End }; box.AddChild(bottom);
        Btn(bottom, "Close", () => CloseWindow(layer));
        OpenWindow(layer, layer.QueueFree);
    }

    /// <summary>Writes the report zip to the desktop (or the home folder) and returns its path.</summary>
    string CreateDebugReport(bool withProject)
    {
        var s = Settings.Load(SettingsFile);
        var folder = System.Environment.GetFolderPath(System.Environment.SpecialFolder.DesktopDirectory);
        if (!Directory.Exists(folder)) folder = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        var zipPath = Path.Combine(folder, $"x4mapeditor_debug_{DateTime.Now:yyyyMMdd_HHmmss}.zip");
        var anon = Anonymiser(s);

        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        void Text(string name, string text)
        {
            var e = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
            w.Write(anon(text));
        }

        // report.txt: what runs where
        var r = new StringBuilder();
        r.AppendLine($"{AppName} {Version}   report {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        r.AppendLine($"OS: {OS.GetName()} {OS.GetVersion()}   ({System.Runtime.InteropServices.RuntimeInformation.OSDescription})");
        r.AppendLine($".NET: {System.Environment.Version}   Godot: {Engine.GetVersionInfo()["string"]}");
        r.AppendLine($"Graphics: {RenderingServer.GetVideoAdapterVendor()} {RenderingServer.GetVideoAdapterName()}   API {RenderingServer.GetVideoAdapterApiVersion()}   "
            + $"renderer {ProjectSettings.GetSetting("rendering/renderer/rendering_method")}");
        r.AppendLine($"CPU: {OS.GetProcessorName()} ({OS.GetProcessorCount()} threads)   RAM: {OS.GetMemoryInfo()["physical"].AsInt64() / 1_048_576} MB   "
            + $"screen {DisplayServer.ScreenGetSize()}   window {GetWindow().Size}   video memory used {RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.VideoMemUsed) / 1_048_576} MB");
        r.AppendLine();
        r.AppendLine("Folders:");
        r.AppendLine($"  game     {s.GameFolder}  (exists: {Directory.Exists(s.GameFolder)})");
        r.AppendLine($"  profile  {s.ProfileFolder}  (exists: {Directory.Exists(s.ProfileFolder)})");
        r.AppendLine($"  export   {s.ExportFolder}  (exists: {Directory.Exists(s.ExportFolder)})");
        foreach (var f in s.ExtraModFolders) r.AppendLine($"  extra mods {f}");
        foreach (var f in s.ExtractedFolders) r.AppendLine($"  extracted  {f}");
        foreach (var p in s.Problems()) r.AppendLine($"  PROBLEM: {p}");
        r.AppendLine();
        if (Game == null) r.AppendLine("Game files: NOT READ");
        else
        {
            r.AppendLine($"Game files: {Game.Vfs.Count:N0}   materials {Game.Materials.Count}   models built: {(Conv != null ? "yes" : "no")}   decor catalogue: {(Decor != null ? "yes" : "no")}");
            r.AppendLine("Extensions (load order):");
            foreach (var e in Game.Vfs.Extensions.All)
                r.AppendLine($"  {(e.Enabled ? "on " : "off")} {e.Id} v{e.Version}  folder {e.FolderName}  '{e.Name}'{(e.EnabledBy != "" ? "  (" + e.EnabledBy + ")" : "")}");
        }
        r.AppendLine();
        r.AppendLine($"Project: {EditP.ModName} ({EditP.ModId}), {EditP.Clusters.Count} clusters, {EditP.AllSectors().Count()} sectors, file {(_file != null ? Path.GetFileName(_file) : "not saved")}");
        r.AppendLine($"Open sector: {OpenSectorId?.ToString() ?? "none"}   selected: {(Sel != null ? Sel.Kind + " " + Sel.Id : "none")}");
        r.AppendLine($"Look: glow {X4Shaders.Glow}, metal {X4Shaders.Metal}, vertex colours {X4Shaders.VertexColours}, normal green flipped {X4Shaders.FlipNormalGreen}");
        Text("report.txt", r.ToString());

        // notes: everything the data library and the model builder could not read or understand
        var n = new StringBuilder();
        void Notes(string title, IEnumerable<string>? lines)
        {
            var list = lines?.ToList() ?? new();
            n.AppendLine($"== {title} ({list.Count})");
            foreach (var l in list.Distinct().Take(3000)) n.AppendLine(l);
            n.AppendLine();
        }
        Notes("extensions", Game?.Vfs.Extensions.Notes);
        Notes("game data", Game?.Notes);
        Notes("models / materials / textures", Conv?.Notes);
        Text("notes.txt", n.ToString());
        Text("session_log.txt", DebugLog.All());

        // Godot's own log files (the newest few): engine errors, C# exceptions in callbacks
        var logs = ProjectSettings.GlobalizePath("user://logs");
        if (Directory.Exists(logs))
            foreach (var f in new DirectoryInfo(logs).GetFiles("*.log").OrderByDescending(f => f.LastWriteTimeUtc).Take(3))
            {
                string text;
                using (var fs = new FileStream(f.FullName, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite)) using (var sr = new StreamReader(fs)) text = sr.ReadToEnd();
                Text("logs/" + f.Name, text);
            }

        if (_debugShot != null)
        {
            // JPG: a tenth of the PNG size, keeps the report small enough to mail
            var e = zip.CreateEntry("screenshot.jpg", CompressionLevel.NoCompression);
            using var w = e.Open(); var jpg = _debugShot.SaveJpgToBuffer(0.85f); w.Write(jpg, 0, jpg.Length);
        }
        if (withProject) Text("project.galaxy.json", EditP.ToJson());
        DebugLog.Add("debug report written");
        return zipPath;
    }

    /// <summary>Replaces the user's name, home folder and X4 profile number in a text (all spellings of the slashes).</summary>
    static Func<string, string> Anonymiser(Settings s)
    {
        var pairs = new List<(string from, string to)>();
        var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        if (home.Length > 3) pairs.Add((home, "%USERPROFILE%"));
        var profileId = Path.GetFileName(s.ProfileFolder.TrimEnd('\\', '/'));
        if (profileId.Length >= 4 && profileId.All(char.IsDigit)) pairs.Add((profileId, "<profile>"));
        var user = System.Environment.UserName;
        if (user.Length >= 3) pairs.Add((user, "<user>"));
        var rules = pairs.SelectMany(p => new[] { p, (p.from.Replace('\\', '/'), p.to) }).Distinct()
            .Select(p => (re: new Regex(Regex.Escape(p.Item1), RegexOptions.IgnoreCase), p.Item2)).ToList();
        return text => rules.Aggregate(text, (t, rule) => rule.re.Replace(t, rule.Item2));
    }
}
