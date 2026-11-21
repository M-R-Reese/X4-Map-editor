using System.Text.RegularExpressions;

namespace X4Data.Text;

/// <summary>
/// Spoken location names a sector or cluster can use. Facts (9.00, checked 2026-11-21):
/// - page 20005 "Location Name Pool" holds ~400 system / sector names, ~300 with audio in English and German
///   (voice-L044/20005/normal/&lt;id&gt;.ogg, the same under voice-L049);
/// - vanilla names point at it: sector {20004,10011} = "{20003,10001} {20402,1}", system {20003,10001} = "{20005,6002}"
///   (Grand Exchange) and page 20402 "Roman Numerals" (ids 1 .. 25, all voiced) speaks "I" as "one";
/// - so a name written as "{20005,id}" (or "{20005,id} {20402,n}") is spoken, and shows in each player's language.
/// "Used" = some text of the game, a DLC or an installed mod refers to that pool id (outside page 20005 itself).
/// </summary>
public sealed class VoicedNames
{
    public const int PoolPage = 20005, NumeralPage = 20402;

    public sealed record Entry(int Id, string English, string? German, bool VoiceEn, bool VoiceDe, string Group, List<string> UsedBy)
    {
        public bool Free => UsedBy.Count == 0;
        /// <summary>The text reference to write as the name, with an optional numeral 1 .. 25.</summary>
        public string Reference(int numeral = 0) => numeral > 0 ? $"{{{PoolPage},{Id}}} {{{NumeralPage},{numeral}}}" : $"{{{PoolPage},{Id}}}";
    }

    public List<Entry> Names { get; } = new();
    /// <summary>Roman numerals with audio: 1 .. 25 and their text ("I", "II" ...).</summary>
    public List<(int Id, string Text)> Numerals { get; } = new();

    static readonly Regex PoolRef = new(@"\{\s*20005\s*,\s*(\d+)\s*\}");

    public static VoicedNames Build(GameData g, TextDb en, TextDb? de = null)
    {
        var v = new VoicedNames();
        // who refers to each pool id: the referring text, resolved ("Grand Exchange" for {20003,10001})
        var users = new Dictionary<int, List<string>>();
        foreach (var ((page, id), raw) in en.Raw)
        {
            if (page == PoolPage) continue;
            foreach (Match m in PoolRef.Matches(raw))
            {
                var pid = int.Parse(m.Groups[1].Value);
                if (!users.TryGetValue(pid, out var l)) users[pid] = l = new List<string>();
                var who = $"{{{page},{id}}}";
                if (!l.Contains(who)) l.Add(who);
            }
        }
        string group = "";
        foreach (var ((page, id), raw) in en.Raw.Where(t => t.Key.page == PoolPage).OrderBy(t => t.Key.id))
        {
            var text = en.Get(page, id) ?? "";
            if (id % 1000 == 0) { group = TextDb.Clean(raw.Replace("(", "").Replace(")", "")); continue; }   // "(Argon Names)" heads a group
            bool ven = g.Vfs.Exists($"voice-L044/{PoolPage}/normal/{id}.ogg"), vde = g.Vfs.Exists($"voice-L049/{PoolPage}/normal/{id}.ogg");
            if (!ven && !vde) continue;
            if (!text.Any(char.IsLetter)) continue;                          // placeholders like "-"
            var used = users.TryGetValue(id, out var u) ? u.Select(r => Describe(en, r)).Distinct().ToList() : new List<string>();
            v.Names.Add(new Entry(id, text, de?.Get(page, id), ven, vde, group, used));
        }
        for (int i = 1; i <= 25; i++)
            if (g.Vfs.Exists($"voice-L044/{NumeralPage}/normal/{i}.ogg") && en.Get(NumeralPage, i) is { } t) v.Numerals.Add((i, t));
        return v;
    }

    /// <summary>"Grand Exchange (system)" for a referring text; the page tells what it names.</summary>
    static string Describe(TextDb en, string reference)
    {
        var m = Regex.Match(reference, @"\{(\d+),(\d+)\}");
        int page = int.Parse(m.Groups[1].Value), id = int.Parse(m.Groups[2].Value);
        var kind = page switch { 20003 => "system", 20004 => "sector", _ => $"text {page}" };
        var text = en.Get(page, id) ?? reference;
        if (text.Length > 40) text = text[..40] + "…";
        return $"{text} ({kind})";
    }

    /// <summary>Pool id of a name reference written by Reference(), or null.</summary>
    public static int? PoolId(string? reference)
    {
        if (reference == null) return null;
        var m = PoolRef.Match(reference);
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    /// <summary>Numeral of a name reference ("{20005,1004} {20402,2}" -> 2), 0 if none.</summary>
    public static int Numeral(string? reference)
    {
        if (reference == null) return 0;
        var m = Regex.Match(reference, @"\{\s*20402\s*,\s*(\d+)\s*\}");
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }
}
