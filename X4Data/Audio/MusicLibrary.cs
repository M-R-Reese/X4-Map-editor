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
    public sealed record Music(string Id, string Description, List<Track> Tracks);

    public List<Music> All { get; } = new();

    public MusicLibrary(GameData g)
    {
        var doc = GameXml.Load(g.Vfs, "libraries/sound_library.xml", g.Notes);
        foreach (var s in doc?.Root?.Elements("sound") ?? Enumerable.Empty<XElement>())
        {
            var id = (string?)s.Attribute("id") ?? "";
            if (!id.StartsWith("music_", StringComparison.OrdinalIgnoreCase)) continue;
            var tracks = new List<Track>();
            foreach (var sel in s.Descendants("select"))
            {
                var start = (string?)sel.Attribute("start");
                if (start == null) continue;
                var file = Vfs.VirtualFileSystem.Norm(start);
                if (!file.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)) file += ".ogg";
                if (!g.Vfs.Exists(file)) continue;                       // keep only tracks this installation has
                int w = int.TryParse((string?)sel.Attribute("weight"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 1;
                if (w > 0) tracks.Add(new Track(file, w));
            }
            if (tracks.Count > 0) All.Add(new Music(id, (string?)s.Attribute("description") ?? id, tracks));
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
