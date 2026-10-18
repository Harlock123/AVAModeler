using System.Buffers.Binary;
using System.Text;

namespace AVAModeler.Formats.Tests;

/// <summary>Builds big-endian IFF bytes by hand, independently of the code under test.</summary>
internal sealed class Bytes
{
    private readonly List<byte> _b = [];

    public Bytes U1(params byte[] v) { _b.AddRange(v); return this; }
    public Bytes U2(int v) { _b.Add((byte)(v >> 8)); _b.Add((byte)v); return this; }
    public Bytes I2(int v) => U2((ushort)(short)v);
    public Bytes U4(uint v) { U2((int)(v >> 16)); return U2((int)(v & 0xFFFF)); }
    public Bytes Id(string id) { _b.AddRange(Encoding.ASCII.GetBytes(id)); return this; }

    public Bytes F4(float v)
    {
        var buf = new byte[4];
        BinaryPrimitives.WriteSingleBigEndian(buf, v);
        _b.AddRange(buf);
        return this;
    }

    public Bytes Vec(float x, float y, float z) => F4(x).F4(y).F4(z);

    public Bytes S0(string s)
    {
        _b.AddRange(Encoding.ASCII.GetBytes(s));
        _b.Add(0);
        if ((s.Length + 1) % 2 == 1) _b.Add(0);
        return this;
    }

    /// <summary>A chunk with a 4-byte length.</summary>
    public Bytes Chunk(string id, Action<Bytes> body) => Wrap(id, body, false);

    /// <summary>A sub-chunk with a 2-byte length.</summary>
    public Bytes Sub(string id, Action<Bytes> body) => Wrap(id, body, true);

    private Bytes Wrap(string id, Action<Bytes> body, bool shortLength)
    {
        var inner = new Bytes();
        body(inner);
        Id(id);
        if (shortLength) U2(inner._b.Count); else U4((uint)inner._b.Count);
        _b.AddRange(inner._b);
        if (inner._b.Count % 2 == 1) _b.Add(0);
        return this;
    }

    public byte[] ToArray() => [.. _b];

    public static byte[] Form(string type, Action<Bytes> body) => new Bytes().Chunk("FORM", b => body(b.Id(type))).ToArray();
}

/// <summary>A uniquely named folder under the temp path, deleted on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "avamodeler-formats-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        if (Directory.Exists(Path)) Directory.Delete(Path, true);
    }
}
