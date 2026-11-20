using System.IO.Compression;
using X4Data.Vfs;

namespace X4Data.Textures;

/// <summary>
/// DDS texture as the game ships it (usually gzipped: "name.gz", else "name.dds"). Keeps the block-compressed data as
/// it is (the GPU decodes DXT / BC itself). Format names match Godot's Image.Format members.
/// </summary>
public sealed class Dds
{
    public int Width, Height, Mipmaps;
    /// <summary>Godot Image.Format name: Dxt1, Dxt3, Dxt5, RgtcR, RgtcRg, BptcRgba, Rgba8.</summary>
    public string Format = "";
    /// <summary>All mip levels back to back, largest first.</summary>
    public byte[] Data = Array.Empty<byte>();

    /// <summary>Finds and reads a texture by the path the material library gives (no extension).</summary>
    public static Dds? Load(VirtualFileSystem vfs, string path, bool small = false)
    {
        foreach (var p in small ? new[] { path + "-small.gz", path + ".gz", path + ".dds" } : new[] { path + ".gz", path + ".dds" })
            if (vfs.Exists(p))
            {
                var b = vfs.Read(p);
                if (p.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)) b = Gunzip(b);
                return Parse(b);
            }
        return null;
    }

    static byte[] Gunzip(byte[] b)
    {
        using var i = new GZipStream(new MemoryStream(b), CompressionMode.Decompress);
        using var o = new MemoryStream();
        i.CopyTo(o);
        return o.ToArray();
    }

    public static Dds Parse(byte[] b)
    {
        if (b.Length < 128 || b[0] != 'D' || b[1] != 'D' || b[2] != 'S') throw new InvalidDataException("not a DDS file");
        var t = new Dds
        {
            Height = BitConverter.ToInt32(b, 12),
            Width = BitConverter.ToInt32(b, 16),
            Mipmaps = Math.Max(1, BitConverter.ToInt32(b, 28)),
        };
        uint pfFlags = BitConverter.ToUInt32(b, 80);
        string four = System.Text.Encoding.ASCII.GetString(b, 84, 4);
        int start = 128;
        if ((pfFlags & 4) != 0)                       // DDPF_FOURCC
        {
            t.Format = four switch
            {
                "DXT1" => "Dxt1", "DXT2" or "DXT3" => "Dxt3", "DXT4" or "DXT5" => "Dxt5",
                "ATI1" or "BC4U" => "RgtcR", "ATI2" or "BC5U" => "RgtcRg",
                "DX10" => Dx10(BitConverter.ToUInt32(b, 128)),
                _ => throw new InvalidDataException($"DDS fourcc '{four}' not supported"),
            };
            if (four == "DX10") start = 148;
        }
        else
        {
            int bits = BitConverter.ToInt32(b, 88);
            if (bits != 32) throw new InvalidDataException($"uncompressed DDS with {bits} bits not supported");
            t.Format = "Rgba8";
        }
        t.Data = b[start..];
        bool bgra = t.Format == "Bgra8" || (t.Format == "Rgba8" && (pfFlags & 4) == 0 && BitConverter.ToUInt32(b, 92) == 0x00FF0000);
        if (t.Format == "Bgra8") t.Format = "Rgba8";
        if (bgra)   // red mask in byte 2 = BGRA -> swap to RGBA
            for (int i = 0; i + 3 < t.Data.Length; i += 4) (t.Data[i], t.Data[i + 2]) = (t.Data[i + 2], t.Data[i]);
        return t;
    }

    static string Dx10(uint dxgi) => dxgi switch
    {
        70 or 71 or 72 => "Dxt1", 73 or 74 or 75 => "Dxt3", 76 or 77 or 78 => "Dxt5",
        79 or 80 or 81 => "RgtcR", 82 or 83 or 84 => "RgtcRg", 97 or 98 or 99 => "BptcRgba",
        27 or 28 or 29 => "Rgba8",
        87 or 91 => "Bgra8",                              // B8G8R8A8: swapped to RGBA in Parse
        _ => throw new InvalidDataException($"DXGI format {dxgi} not supported"),
    };
}
