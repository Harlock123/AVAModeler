using System.Buffers.Binary;
using System.Text;
using AVAModeler.Core;

namespace AVAModeler.Formats;

/// <summary>
/// Reads big-endian IFF data from a byte array, never past the end of the chunk it was given. Every read is
/// bounds-checked and a short read throws a <see cref="FormatException"/> naming the chunk.
/// </summary>
internal sealed class IffReader
{
    private readonly byte[] _data;
    private readonly int _end;
    private int _pos;

    /// <summary>What is being read (such as "POLS" or "SURF \"Hull\""), for error messages.</summary>
    public string Context { get; }

    public IffReader(byte[] data, int start, int length, string context)
    {
        _data = data;
        _pos = start;
        _end = start + length;
        Context = context;
    }

    public bool AtEnd => _pos >= _end;
    public int Remaining => _end - _pos;

    public FormatException Error(string message) => new($"{Context}: {message}");

    private int Take(int n)
    {
        if (n < 0 || n > Remaining) throw Error($"needs {n} more bytes but only {Remaining} remain");
        int at = _pos;
        _pos += n;
        return at;
    }

    public byte U1() => _data[Take(1)];
    public ushort U2() => BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(Take(2)));
    public short I2() => BinaryPrimitives.ReadInt16BigEndian(_data.AsSpan(Take(2)));
    public uint U4() => BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(Take(4)));
    public float F4() => BinaryPrimitives.ReadSingleBigEndian(_data.AsSpan(Take(4)));
    public string Id4() => Encoding.Latin1.GetString(_data, Take(4), 4);
    public Vec3 Vec12() => new(F4(), F4(), F4());
    public byte[] Bytes(int n) => _data.AsSpan(Take(n), n).ToArray();
    public void Skip(int n) => Take(n);

    /// <summary>An LWO2 variable-length index: two bytes, or four when the first byte is 0xFF.</summary>
    public int VX()
    {
        if (Remaining >= 1 && _data[_pos] == 0xFF) return (int)(U4() & 0x00FFFFFF);
        return U2();
    }

    /// <summary>A null-terminated string, padded to an even length.</summary>
    public string S0()
    {
        int zero = Array.IndexOf(_data, (byte)0, _pos, Remaining);
        if (zero < 0) throw Error("string has no terminating null");
        int start = _pos;
        int length = zero - start;
        Take(length + 1);
        if ((length + 1) % 2 == 1 && Remaining > 0) Take(1);
        return Encoding.Latin1.GetString(_data, start, length);
    }

    /// <summary>
    /// Reads the next chunk header (a four-byte ID and a 4-byte length, or a 2-byte length for sub-chunks) and
    /// returns a reader over its body, skipping the pad byte after an odd-length body.
    /// </summary>
    public IffReader Chunk(bool shortLength, out string id)
    {
        id = Id4();
        uint length = shortLength ? U2() : U4();
        if (length > Remaining) throw Error($"chunk {id} says it is {length} bytes but only {Remaining} remain");
        var body = new IffReader(_data, Take((int)length), (int)length, Context == "FORM" ? id : $"{Context} {id}");
        if (length % 2 == 1 && Remaining > 0) Take(1);
        return body;
    }

    /// <summary>Opens a FORM: checks the header and length and returns the form type and a reader over its chunks.</summary>
    public static IffReader Form(byte[] data, out string type)
    {
        var file = new IffReader(data, 0, data.Length, "");
        if (data.Length < 12 || file.Id4() != "FORM") throw new FormatException("Not an IFF file: it doesn't start with FORM.");
        uint length = file.U4();
        if (length < 4 || length > file.Remaining)
            throw new FormatException($"FORM says it is {length} bytes but the file holds {file.Remaining} after the header.");
        type = file.Id4();
        return new IffReader(data, 12, (int)length - 4, "FORM");
    }

    /// <summary>A reader over what's left, under another name for error messages.</summary>
    public IffReader Named(string context) => new(_data, _pos, Remaining, context);

    /// <summary>The remaining bytes as a copy, leaving the reader at the end.</summary>
    public byte[] Rest() => Bytes(Remaining);
}

/// <summary>Writes big-endian IFF data, filling in chunk lengths and pad bytes.</summary>
internal sealed class IffWriter
{
    private readonly MemoryStream _s = new();
    private readonly byte[] _buf = new byte[4];

    public void U1(byte v) => _s.WriteByte(v);
    public void U2(ushort v) { BinaryPrimitives.WriteUInt16BigEndian(_buf, v); _s.Write(_buf, 0, 2); }
    public void I2(short v) { BinaryPrimitives.WriteInt16BigEndian(_buf, v); _s.Write(_buf, 0, 2); }
    public void U4(uint v) { BinaryPrimitives.WriteUInt32BigEndian(_buf, v); _s.Write(_buf, 0, 4); }
    public void F4(float v) { BinaryPrimitives.WriteSingleBigEndian(_buf, v); _s.Write(_buf, 0, 4); }
    public void Bytes(byte[] b) => _s.Write(b);

    /// <summary>An LWO2 variable-length index: two bytes below 0xFF00, otherwise four with 0xFF first.</summary>
    public void VX(int v)
    {
        if (v < 0 || v > 0x00FFFFFF) throw new InvalidOperationException($"Index {v} is out of range for a variable-length index.");
        if (v < 0xFF00) U2((ushort)v);
        else U4(0xFF000000u | (uint)v);
    }

    public void Id4(string id)
    {
        if (id.Length != 4) throw new ArgumentException($"Chunk ID \"{id}\" isn't four characters.");
        _s.Write(Encoding.Latin1.GetBytes(id));
    }

    /// <summary>A null-terminated string, padded to an even length.</summary>
    public void S0(string s)
    {
        var b = Encoding.Latin1.GetBytes(s);
        _s.Write(b);
        _s.WriteByte(0);
        if ((b.Length + 1) % 2 == 1) _s.WriteByte(0);
    }

    /// <summary>Writes a chunk: its ID, its length (4 bytes, or 2 for a sub-chunk), the body, then a pad byte if odd.</summary>
    public void Chunk(string id, Action body, bool shortLength = false)
    {
        Id4(id);
        long lengthAt = _s.Position;
        if (shortLength) U2(0); else U4(0);
        long start = _s.Position;
        body();
        long length = _s.Position - start;
        if (shortLength && length > ushort.MaxValue) throw new InvalidOperationException($"Sub-chunk {id} is too long ({length} bytes) for a 2-byte length.");
        if (length > uint.MaxValue) throw new InvalidOperationException($"Chunk {id} is too long for a 4-byte length.");
        if (length % 2 == 1) _s.WriteByte(0);
        long end = _s.Position;
        _s.Position = lengthAt;
        if (shortLength) U2((ushort)length); else U4((uint)length);
        _s.Position = end;
    }

    public void Chunk(RawChunk c, bool shortLength = false) => Chunk(c.Id, () => Bytes(c.Data), shortLength);

    public byte[] ToArray() => _s.ToArray();

    /// <summary>A small body built on its own, such as a sub-chunk's data.</summary>
    public static byte[] Build(Action<IffWriter> body)
    {
        var w = new IffWriter();
        body(w);
        return w.ToArray();
    }
}

/// <summary>Bits shared by the loaders.</summary>
internal static class Loading
{
    /// <summary>Puts the first layer with anything in it in the foreground and turns the rest off.</summary>
    public static void ShowFirstLayer(Document doc)
    {
        int first = doc.Layers.FindIndex(l => !l.Mesh.IsEmpty);
        if (first < 0) first = 0;
        for (int i = 0; i < doc.Layers.Count; i++) doc.Layers[i].State = i == first ? LayerState.Foreground : LayerState.Off;
    }

    /// <summary>The layer at an index, adding empty layers up to it if needed.</summary>
    public static Layer LayerAt(Document doc, int index)
    {
        while (doc.Layers.Count <= index) doc.Layers.Add(new Layer());
        return doc.Layers[index];
    }

    /// <summary>A polygon followed by its detail polygons (and theirs).</summary>
    public static IEnumerable<Polygon> Flatten(Polygon p) =>
        p.Details == null ? [p] : p.Details.SelectMany(Flatten).Prepend(p);

    public static byte[] ReadAll(Stream s)
    {
        if (s is MemoryStream ms && ms.Position == 0) return ms.ToArray();
        using var copy = new MemoryStream();
        s.CopyTo(copy);
        return copy.ToArray();
    }
}
