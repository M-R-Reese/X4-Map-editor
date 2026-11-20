using System.IO.Compression;
using System.Numerics;
using System.Text;

namespace X4Data.Model;

/// <summary>
/// Reader of X4 model files (.xmf, magic "XUMF"). Layout worked out from the game's files (2026-11-20, own code):
///   header 64 bytes: magic, version (byte 4), big endian (byte 5), header size (byte 6), number of buffers (8),
///     buffer descriptor size (9), number of materials (10), material size (11), vertex count (uint @14),
///     index count (uint @18), primitive type (uint @22, 4 = triangle list)
///   buffer descriptors (188 bytes each): type (30 = index buffer, else vertex), usage index, data offset,
///     compressed flag (1 = zlib), -, format, compressed size, item count, item size, sections, 4 x pad,
///     number of vertex elements, 16 x element (uint D3DDECLTYPE, byte D3DDECLUSAGE, byte usage index, 2 pad)
///   materials (136 bytes each): first index, index count, name (128 chars, zero padded)
///   then the buffer data, each at header + descriptors + materials + data offset
/// Check: header + descriptors + materials + all compressed sizes = file size.
/// </summary>
public sealed class Xmf
{
    public Vector3[] Positions = Array.Empty<Vector3>();
    public Vector3[]? Normals;
    public Vector2[]? Uvs;
    /// <summary>Vertex colour 0 as RGBA bytes (backdrop skies are painted this way), or null.</summary>
    public byte[]? Colors;
    public int[] Indices = Array.Empty<int>();
    public List<(int First, int Count, string Name)> Materials = new();

    // D3DDECLTYPE sizes in bytes (index = type number)
    static readonly int[] TypeSize = { 4, 8, 12, 16, 4, 4, 4, 8, 4, 4, 8, 4, 8, 4, 4, 4, 8 };
    const int UsagePosition = 0, UsageNormal = 3, UsageTexcoord = 5, UsageColor = 10;

    public static Xmf Read(byte[] b)
    {
        if (b.Length == 0) return new Xmf();               // the game has empty placeholder models (look-at targets, fx)
        if (b.Length < 64 || Encoding.ASCII.GetString(b, 0, 4) != "XUMF") throw new InvalidDataException("not an .xmf file");
        if (b[5] != 0) throw new InvalidDataException("big endian .xmf not supported");
        int headerSize = b[6], nBuf = b[8], descSize = b[9], nMat = b[10], matSize = b[11];
        uint primitive = BitConverter.ToUInt32(b, 22);
        if (primitive != 4) throw new InvalidDataException($"primitive type {primitive} not supported (only triangle list)");

        int dataStart = headerSize + nBuf * descSize + nMat * matSize;
        var x = new Xmf();
        for (int m = 0; m < nMat; m++)
        {
            int o = headerSize + nBuf * descSize + m * matSize;
            var name = Encoding.ASCII.GetString(b, o + 8, matSize - 8);
            int z = name.IndexOf('\0'); if (z >= 0) name = name[..z];
            x.Materials.Add(((int)BitConverter.ToUInt32(b, o), (int)BitConverter.ToUInt32(b, o + 4), name));
        }

        for (int i = 0; i < nBuf; i++)
        {
            int d = headerSize + i * descSize;
            uint type = U(b, d), offset = U(b, d + 8), compressed = U(b, d + 12);
            uint csize = U(b, d + 24), count = U(b, d + 28), itemSize = U(b, d + 32), sections = U(b, d + 36);
            if (sections != 1) throw new InvalidDataException($"buffer {i}: {sections} sections not supported");
            var raw = new ReadOnlySpan<byte>(b, dataStart + (int)offset, (int)csize);
            byte[] data = compressed == 1 ? Inflate(raw, (int)(count * itemSize)) : raw.ToArray();
            if (data.Length != count * itemSize) throw new InvalidDataException($"buffer {i}: {data.Length} bytes, expected {count * itemSize}");

            if (type == 30) x.Indices = ReadIndices(data, (int)count, (int)itemSize);
            else ReadVertices(x, b, d, data, (int)count, (int)itemSize);
        }
        return x;
    }

    static uint U(byte[] b, int o) => BitConverter.ToUInt32(b, o);

    static byte[] Inflate(ReadOnlySpan<byte> src, int expected)
    {
        using var input = new MemoryStream(src.ToArray());
        using var z = new ZLibStream(input, CompressionMode.Decompress);
        var outBuf = new byte[expected];
        int done = 0, n;
        while (done < expected && (n = z.Read(outBuf, done, expected - done)) > 0) done += n;
        return done == expected ? outBuf : outBuf[..done];
    }

    static int[] ReadIndices(byte[] d, int count, int size)
    {
        var r = new int[count];
        for (int i = 0; i < count; i++) r[i] = size == 2 ? BitConverter.ToUInt16(d, i * 2) : (int)BitConverter.ToUInt32(d, i * 4);
        return r;
    }

    static void ReadVertices(Xmf x, byte[] b, int desc, byte[] d, int count, int stride)
    {
        int nEl = (int)U(b, desc + 56), off = 0;
        if (nEl == 0) return;                              // buffer without a vertex layout (seen in system meshes): nothing to show
        for (int e = 0; e < nEl && e < 16; e++)
        {
            int eo = desc + 60 + e * 8;
            int type = (int)U(b, eo), usage = b[eo + 4], usageIndex = b[eo + 5];
            if (type < 0 || type >= TypeSize.Length) throw new InvalidDataException($"vertex element type {type} unknown");
            int at = off;
            if (usage == UsagePosition && usageIndex == 0 && type == 2)
            {
                x.Positions = new Vector3[count];
                for (int v = 0; v < count; v++) x.Positions[v] = F3(d, v * stride + at);
            }
            else if (usage == UsageNormal && usageIndex == 0)
            {
                x.Normals = new Vector3[count];
                for (int v = 0; v < count; v++) x.Normals[v] = type == 2 ? F3(d, v * stride + at) : Packed(d, v * stride + at, type);
            }
            else if (usage == UsageTexcoord && usageIndex == 0)
            {
                x.Uvs = new Vector2[count];
                for (int v = 0; v < count; v++)
                {
                    int p = v * stride + at;
                    x.Uvs[v] = type == 1 ? new Vector2(BitConverter.ToSingle(d, p), BitConverter.ToSingle(d, p + 4))
                             : type == 15 ? new Vector2((float)BitConverter.ToHalf(d, p), (float)BitConverter.ToHalf(d, p + 2))
                             : Vector2.Zero;
                }
            }
            else if (usage == UsageColor && usageIndex == 0 && type == 4)
            {
                // D3DCOLOR is stored B, G, R, A in memory -> RGBA
                x.Colors = new byte[count * 4];
                for (int v = 0; v < count; v++)
                {
                    int p = v * stride + at;
                    x.Colors[v * 4] = d[p + 2]; x.Colors[v * 4 + 1] = d[p + 1]; x.Colors[v * 4 + 2] = d[p]; x.Colors[v * 4 + 3] = d[p + 3];
                }
            }
            off += TypeSize[type];
        }
        if (off != stride) throw new InvalidDataException($"vertex elements add up to {off} bytes, stride is {stride}");
    }

    static Vector3 F3(byte[] d, int p) => new(BitConverter.ToSingle(d, p), BitConverter.ToSingle(d, p + 4), BitConverter.ToSingle(d, p + 8));

    /// <summary>
    /// Packed normal. D3DCOLOR (4) is stored B,G,R,A in memory -> x = byte 2, y = byte 1, z = byte 0.
    /// Mapping checked against face normals by the test (NormalOrder).
    /// </summary>
    public static int[] NormalOrder = { 2, 1, 0 };
    static Vector3 Packed(byte[] d, int p, int type)
    {
        if (type == 4 || type == 5 || type == 8)
            return Vector3.Normalize(new Vector3(d[p + NormalOrder[0]], d[p + NormalOrder[1]], d[p + NormalOrder[2]]) / 127.5f - Vector3.One);
        if (type == 16)   // FLOAT16_4
            return new Vector3((float)BitConverter.ToHalf(d, p), (float)BitConverter.ToHalf(d, p + 2), (float)BitConverter.ToHalf(d, p + 4));
        return Vector3.UnitY;
    }
}
