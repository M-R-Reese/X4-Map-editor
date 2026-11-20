namespace X4Data.Vfs;

/// <summary>
/// One X4 archive: a .cat text file listing the files, and a .dat of the same name holding them back to back.
/// Each .cat line is "path size timestamp md5". Paths may contain spaces, so the line is split from the right.
/// A file's offset in the .dat is the sum of the sizes of all files listed before it.
/// </summary>
public sealed class Catalog
{
    public string CatPath { get; }
    public string DatPath { get; }
    public IReadOnlyList<CatalogEntry> Entries => _entries;
    readonly List<CatalogEntry> _entries = new();

    public Catalog(string catPath)
    {
        CatPath = catPath;
        DatPath = Path.ChangeExtension(catPath, ".dat");
        long offset = 0;
        foreach (var raw in File.ReadLines(catPath))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            // split off the last three fields: md5, timestamp, size
            int iMd5 = line.LastIndexOf(' ');
            int iTime = line.LastIndexOf(' ', iMd5 - 1);
            int iSize = line.LastIndexOf(' ', iTime - 1);
            if (iSize <= 0) throw new InvalidDataException($"{catPath}: bad line '{line}'");
            long size = long.Parse(line.AsSpan(iSize + 1, iTime - iSize - 1));
            long time = long.Parse(line.AsSpan(iTime + 1, iMd5 - iTime - 1));
            string md5 = line[(iMd5 + 1)..];
            _entries.Add(new CatalogEntry(this, line[..iSize], offset, size, time, md5));
            offset += size;
        }
    }

    /// <summary>Reads one file's bytes from the .dat (opened per read: thread safe, no handle kept).</summary>
    public byte[] Read(CatalogEntry e)
    {
        var buf = new byte[e.Size];
        using var h = File.OpenHandle(DatPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        int done = 0;
        while (done < buf.Length)
        {
            int n = RandomAccess.Read(h, buf.AsSpan(done), e.Offset + done);
            if (n <= 0) throw new EndOfStreamException($"{DatPath}: file '{e.Path}' cut off");
            done += n;
        }
        return buf;
    }
}

/// <summary>One file inside a catalog. Path as written in the .cat (relative, forward slashes).</summary>
public sealed record CatalogEntry(Catalog Catalog, string Path, long Offset, long Size, long Timestamp, string Md5);
