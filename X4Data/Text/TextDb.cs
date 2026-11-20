using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using X4Data.Xml;

namespace X4Data.Text;

/// <summary>
/// The game's texts: t/0001-l&lt;language&gt;.xml (+ the same file of every extension, merged by page), looked up by
/// {page,id}. Like the game: references "{page,id}" inside a text are resolved, parentheses are comments and removed
/// unless escaped "\(" "\)". Language 44 = English, 49 = German.
/// </summary>
public sealed class TextDb
{
    readonly Dictionary<(int page, int id), string> _raw = new();
    public int Language { get; }
    public int Count => _raw.Count;
    static readonly Regex RefRe = new(@"\{(\d+)\s*,\s*(\d+)\}");

    public TextDb(GameData g, int language = 44)
    {
        Language = language;
        var doc = GameXml.Load(g.Vfs, $"t/0001-l{language:000}.xml", g.Notes);
        if (doc == null && language != 44) doc = GameXml.Load(g.Vfs, "t/0001-l044.xml", g.Notes);
        foreach (var page in doc?.Root?.Elements("page") ?? Enumerable.Empty<XElement>())
        {
            if (!int.TryParse((string?)page.Attribute("id"), out var pid)) continue;
            foreach (var t in page.Elements("t"))
                if (int.TryParse((string?)t.Attribute("id"), out var tid)) _raw[(pid, tid)] = t.Value;   // later (extensions) win
        }
    }

    /// <summary>Text of {page,id}, resolved and cleaned; null if the game has none.</summary>
    public string? Get(int page, int id) => _raw.TryGetValue((page, id), out var s) ? Clean(Resolve(s, 0)) : null;

    /// <summary>Resolves a reference written as "{page,id}" (as in macros: name="{20001,701}"); other strings come back as they are.</summary>
    public string? Lookup(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return null;
        var m = RefRe.Match(reference);
        if (!m.Success || m.Index != 0 || m.Length != reference.Trim().Length) return reference;
        return Get(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
    }

    string Resolve(string s, int depth) =>
        depth > 8 ? s : RefRe.Replace(s, m => _raw.TryGetValue((int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)), out var r) ? Resolve(r, depth + 1) : m.Value);

    /// <summary>Removes "(comment)" parts, keeps escaped "\(" "\)" as plain parentheses.</summary>
    public static string Clean(string s)
    {
        var sb = new StringBuilder(); int depth = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length && (s[i + 1] == '(' || s[i + 1] == ')')) { if (depth == 0) sb.Append(s[i + 1]); i++; continue; }
            if (c == '(') { depth++; continue; }
            if (c == ')' && depth > 0) { depth--; continue; }
            if (depth == 0) sb.Append(c);
        }
        return sb.ToString().Trim();
    }
}
