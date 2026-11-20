using System.Diagnostics;
using System.Numerics;
using X4Data;
using X4Data.Model;
using X4Data.Textures;

/// <summary>Milestone 2 checks: models, materials, textures of the jump gate, and the reader over many files.</summary>
static class ModelTests
{
    public static void Run(GameData g, Action<string, bool, string> check)
    {
        Console.WriteLine("\n--- Models (milestone 2)");
        check("index merged", g.MacroCount > 4000 && g.ComponentCount > 3000, $"{g.MacroCount} macros, {g.ComponentCount} components");
        check("material library", g.Materials.Count > 2000, $"{g.Materials.Count} materials");

        var notes = new List<string>();
        var parts = Assembly.Build(g, "props_gates_anc_gate_macro", 0, notes);
        foreach (var n in notes) Console.WriteLine("  note: " + n);
        check("gate assembled", parts.Count >= 3, string.Join(", ", parts.Select(p => $"{p.PartName} {p.Mesh.Positions.Length}v")));

        // size of the gate: about 4 km wide (lights in the component sit at x = +-1903 m)
        var all = parts.SelectMany(p => p.Mesh.Positions.Select(v => Vector3.Transform(v, p.Transform))).ToList();
        var min = all.Aggregate(new Vector3(float.MaxValue), Vector3.Min); var max = all.Aggregate(new Vector3(float.MinValue), Vector3.Max);
        check("gate size plausible", max.X - min.X is > 2000 and < 8000, $"box {min} .. {max}");

        // packed normals: which byte order matches the face normals best (+1 = all agree)
        var main = parts.First(p => p.PartName == "part_main").Mesh;
        var best = BestNormalOrder(main);
        check("normals match the faces", best.score > 0.8, $"order {string.Join(",", best.order)} score {best.score:F2}");
        Xmf.NormalOrder = best.order;

        check("index ranges inside the vertices", main.Indices.All(i => i < main.Positions.Length), "");
        check("material ranges cover the indices", main.Materials.Sum(m => m.Count) == main.Indices.Length, $"{main.Materials.Count} materials");

        // textures of every material of the gate
        var texNotes = new List<string>(); int texOk = 0;
        foreach (var name in parts.SelectMany(p => p.MaterialNames).Distinct())
        {
            var mat = g.Materials.Get(name);
            if (mat == null) { texNotes.Add($"material '{name}' missing"); continue; }
            var diff = mat.Map("diffuse_map");
            if (diff == null) { texNotes.Add($"{name}: no diffuse_map ({mat.Shader})"); continue; }
            try
            {
                var t = Dds.Load(g.Vfs, diff);
                if (t == null) texNotes.Add($"{name}: texture file '{diff}' missing");
                else { texOk++; Console.WriteLine($"  {name}: {t.Width}x{t.Height} {t.Format} {t.Mipmaps} mips"); }
            }
            catch (Exception e) { texNotes.Add($"{name}: {e.Message}"); }
        }
        foreach (var n in texNotes) Console.WriteLine("  note: " + n);
        check("gate textures read", texOk > 0, $"{texOk} ok, {texNotes.Count} notes");

        // backdrops, their sun, and the music
        var bds = X4Data.Space.Backdrops.List(g);
        check("backdrops listed", bds.Count >= 50 && bds.All(b => b.Egosoft) && bds.Any(b => b.Component == "Cluster_01"), $"{bds.Count} backdrops: {string.Join(", ", bds.GroupBy(b => b.Source).Select(x => x.Key + " " + x.Count()))}");
        var light = X4Data.Space.Backdrops.Light(g, "Cluster_01");
        check("backdrop sun found", light != null && light.SunIntensity > 0, light?.ToString() ?? "");
        var bparts = Assembly.Build(g, "Cluster_01", 0, notes);
        check("backdrop assembled", bparts.Count >= 5 && bparts.Any(p => p.Mesh.Colors != null), string.Join(", ", bparts.Select(p => p.PartName + (p.Mesh.Colors != null ? "(colours)" : ""))));
        var music = new X4Data.Audio.MusicLibrary(g);
        var argon = music.Get("music_soundtrack_argon");
        check("music library", music.All.Count > 10 && argon != null, $"{music.All.Count} music ids, argon: {argon?.Tracks.Count} tracks");
        var ogg = argon != null ? g.Vfs.Read(argon.Tracks[0].File) : Array.Empty<byte>();
        check("music file readable", ogg.Length > 1000 && System.Text.Encoding.ASCII.GetString(ogg, 0, 4) == "OggS", $"{argon?.Tracks[0].File} {ogg.Length:N0} bytes");

        // reader over many models of the game: how many fail and why
        var sw = Stopwatch.StartNew();
        var files = g.Vfs.Files.Where(f => f.Path.EndsWith("-lod0.xmf", StringComparison.OrdinalIgnoreCase)).ToList();
        var rnd = new Random(1234);
        var sample = Enumerable.Range(0, 1500).Select(_ => files[rnd.Next(files.Count)]).Distinct().ToList();
        var errors = new Dictionary<string, int>();
        foreach (var f in sample)
            try { Xmf.Read(f.Read()); }
            catch (Exception e)
            {
                var k = e.Message.Split(':')[^1].Trim();
                if (errors.GetValueOrDefault(k) < 2) Console.WriteLine($"  bad model: {f.Path} {f.Size} bytes: {k} / first bytes {Convert.ToHexString(f.Read().AsSpan(0, (int)Math.Min(16, f.Size)))}");
                errors[k] = errors.GetValueOrDefault(k) + 1;
            }
        foreach (var e in errors.OrderByDescending(e => e.Value)) Console.WriteLine($"  {e.Value,5} x {e.Key}");
        int failed = errors.Values.Sum();
        check("xmf reader on random game models", failed * 100 <= sample.Count, $"{sample.Count - failed} of {sample.Count} read in {sw.ElapsedMilliseconds} ms");

        // textures: many random ones
        var texFiles = g.Vfs.Files.Where(f => f.Path.StartsWith("assets/textures/", StringComparison.OrdinalIgnoreCase) && f.Path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)).ToList();
        var tsample = Enumerable.Range(0, 300).Select(_ => texFiles[rnd.Next(texFiles.Count)]).Distinct().ToList();
        var terr = new Dictionary<string, int>();
        foreach (var f in tsample)
            try { Dds.Load(g.Vfs, f.Path[..^3]); }
            catch (Exception e) { terr[e.Message] = terr.GetValueOrDefault(e.Message) + 1; }
        foreach (var e in terr.OrderByDescending(e => e.Value)) Console.WriteLine($"  {e.Value,5} x {e.Key}");
        check("dds reader on random game textures", terr.Values.Sum() * 50 <= tsample.Count, $"{tsample.Count - terr.Values.Sum()} of {tsample.Count}");
    }

    /// <summary>Tries the 6 byte orders for packed normals; score = mean dot product with the face normal.</summary>
    static (int[] order, double score) BestNormalOrder(Xmf m)
    {
        var orders = new[] { new[] { 0, 1, 2 }, new[] { 0, 2, 1 }, new[] { 1, 0, 2 }, new[] { 1, 2, 0 }, new[] { 2, 0, 1 }, new[] { 2, 1, 0 } };
        (int[] o, double s) best = (orders[5], -2);
        var keep = Xmf.NormalOrder;
        foreach (var o in orders)
        {
            Xmf.NormalOrder = o;
            // normals are decoded at read time, so re-decode via a fresh read is not possible here; decode by hand instead
            double sum = 0; int n = 0;
            for (int t = 0; t + 2 < m.Indices.Length && n < 20000; t += 3 * 7)
            {
                int a = m.Indices[t], b = m.Indices[t + 1], c = m.Indices[t + 2];
                var face = Vector3.Cross(m.Positions[b] - m.Positions[a], m.Positions[c] - m.Positions[a]);
                if (face.LengthSquared() < 1e-6f || m.Normals == null) continue;
                face = Vector3.Normalize(face);
                var vn = Reorder(m.Normals[a], keep, o);
                sum += Math.Abs(Vector3.Dot(face, vn)); n++;    // abs: winding direction is checked separately on screen
            }
            double s = n > 0 ? sum / n : -1;
            if (s > best.s) best = (o, s);
        }
        Xmf.NormalOrder = keep;
        return best;
    }

    /// <summary>Normal decoded with order 'from' -> the same bytes decoded with order 'to'.</summary>
    static Vector3 Reorder(Vector3 v, int[] from, int[] to)
    {
        var bytes = new float[3];
        bytes[from[0]] = v.X; bytes[from[1]] = v.Y; bytes[from[2]] = v.Z;
        return new Vector3(bytes[to[0]], bytes[to[1]], bytes[to[2]]);
    }
}
