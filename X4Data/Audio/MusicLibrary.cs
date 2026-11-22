using System.Globalization;
using System.Xml.Linq;
using X4Data.Xml;

namespace X4Data.Audio;

/// <summary>
/// Music of the game: libraries/sound_library.xml (+ DLC patches), entries &lt;sound id="music_..."&gt; with weighted
/// samples (&lt;select weight start="music\cluster\p1_amb_04"/&gt;); the files are .ogg in the archives.
/// </summary>
public sealed class MusicLibrary
{
    public sealed record Track(string File, int Weight);
    /// <summary>Source = who DEFINES the id ("base", a DLC or mod folder): an export only names the id, so a mod that
    /// replaces the tracks of an Egosoft id leaves it Egosoft's.</summary>
    public sealed record Music(string Id, string Description, List<Track> Tracks, string Source = "base");

    public List<Music> All { get; } = new();

    public MusicLibrary(GameData g)
    {
        // who defines each id: the base file, else the first extension (load order) whose file names it
        const string lib = "libraries/sound_library.xml";
        var source = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Ids(string path, string src)
        {
            if (!g.Vfs.Exists(path)) return;
            try { foreach (var s in GameXml.Parse(g.Vfs.Read(path)).Descendants("sound")) if ((string?)s.Attribute("id") is { } i) source.TryAdd(i, src); }
            catch { }
        }
        Ids(lib, "base");
        foreach (var ext in g.Vfs.Extensions.LoadOrder) Ids($"extensions/{ext.FolderName}/{lib}", ext.FolderName);
        var doc = GameXml.Load(g.Vfs, lib, g.Notes);
        foreach (var s in doc?.Root?.Elements("sound") ?? Enumerable.Empty<XElement>())
        {
            var id = (string?)s.Attribute("id") ?? "";
            if (!id.StartsWith("music_", StringComparison.OrdinalIgnoreCase)) continue;
            var tracks = new List<Track>();
            // tracks: <sample selection="random"><select start=".."/>...</sample>, or one track as <sample start=".."/>
            foreach (var sel in s.Descendants().Where(e => e.Name.LocalName is "select" or "sample" && e.Attribute("start") != null))
            {
                var start = (string?)sel.Attribute("start");
                if (start == null) continue;
                var file = Vfs.VirtualFileSystem.Norm(start);
                if (!file.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)) file += ".ogg";
                if (!g.Vfs.Exists(file)) continue;                       // keep only tracks this installation has
                int w = int.TryParse((string?)sel.Attribute("weight"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 1;
                if (w > 0) tracks.Add(new Track(file, w));
            }
            if (tracks.Count > 0) All.Add(new Music(id, (string?)s.Attribute("description") ?? id, tracks, source.GetValueOrDefault(id, "base")));
        }
        All.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));
    }

    public Music? Get(string id) => All.FirstOrDefault(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>A track chosen by the game's weights (avoids repeating the last one when there is a choice).</summary>
    public static Track Pick(Music m, Random rnd, string? last = null)
    {
        var pool = m.Tracks.Count > 1 && last != null ? m.Tracks.Where(t => t.File != last).ToList() : m.Tracks;
        int roll = rnd.Next(pool.Sum(t => t.Weight));
        foreach (var t in pool) { roll -= t.Weight; if (roll < 0) return t; }
        return pool[^1];
    }
}
