namespace AVAModeler.Formats.Tests;

public class MalformedTests
{
    [Theory]
    [InlineData("not an iff file", "FORM")]
    [InlineData("FORM\0\0\0\x00ffLWOB", "FORM says")]
    [InlineData("FORM\0\0\0\x04ILBM", "ILBM")]
    public void RejectsWhatIsNotAnObject(string text, string message)
    {
        var data = System.Text.Encoding.Latin1.GetBytes(text);
        Assert.Contains(message, Assert.Throws<FormatException>(() => Lwob.Read(data)).Message);
    }

    public static TheoryData<string, byte[]> BadChunks => new()
    {
        { "PNTS", Bytes.Form("LWOB", f => f.Chunk("PNTS", b => b.F4(1).F4(2))) },
        { "POLS", Bytes.Form("LWOB", f => f.Chunk("PNTS", b => b.Vec(0, 0, 0)).Chunk("POLS", b => b.U2(2).U2(0).U2(1).I2(1))) },
        { "POLS", Bytes.Form("LWOB", f => f.Chunk("PNTS", b => b.Vec(0, 0, 0)).Chunk("POLS", b => b.U2(3).U2(0))) },
        { "POLS", Bytes.Form("LWOB", f => f.Chunk("PNTS", b => b.Vec(0, 0, 0)).Chunk("POLS", b => b.U2(1).U2(0).I2(-1).I2(-2))) },
        { "SRFS", Bytes.Form("LWOB", f => f.Chunk("SRFS", b => b.U1(65, 66))) },
        { "SURF \"A\" COLR", Bytes.Form("LWOB", f => f.Chunk("SRFS", b => b.S0("A")).Chunk("SURF", b => b.S0("A").Sub("COLR", s => s.U1(1)))) },
        { "SURF \"A\": chunk DIFF", Bytes.Form("LWOB", f => f.Chunk("SRFS", b => b.S0("A")).Chunk("SURF", b => b.S0("A").Id("DIFF").U2(40).U2(0))) },
        { "FORM: chunk POLS says", Bytes.Form("LWOB", f => f.Id("POLS").U4(1000).U2(0)) },
    };

    [Theory]
    [MemberData(nameof(BadChunks))]
    public void NamesTheBadChunk(string chunk, byte[] data) =>
        Assert.Contains(chunk, Assert.Throws<FormatException>(() => Lwob.Read(data)).Message);

    /// <summary>Every truncation and many single-byte corruptions of real files either read or throw FormatException.</summary>
    [Fact]
    public void DamagedFilesFailCleanly()
    {
        var random = new Random(1234);
        foreach (var (original, read) in new (byte[], Func<byte[], object>)[]
                 {
                     (LwobTests.Legacy(), d => Lwob.Read(d)),
                     (LwobTests.Layered(), d => Lwob.Read(d)),
                     (Lwo2Tests.TwoLayers(), d => Lwo2.Read(d)),
                 })
        {
            for (int length = 0; length < original.Length; length++)
                Survives(() => read(original[..length]));
            for (int i = 0; i < 500; i++)
            {
                var damaged = (byte[])original.Clone();
                damaged[random.Next(damaged.Length)] = (byte)random.Next(256);
                Survives(() => read(damaged));
            }
        }
    }

    private static void Survives(Func<object> read)
    {
        try { read(); }
        catch (FormatException) { }
    }
}
