using System.Buffers.Binary;
using System.Text;

namespace AVAModeler.Slicing.Tests;

/// <summary>.x3g for Sailfish printers (the Flashforge Creator Pro), read back command by command.</summary>
public class X3gTests
{
    private static readonly SailfishMachine Fcp = SailfishMachine.CreatorPro;

    /// <summary>One x3g command: its number and its fields as they were read.</summary>
    private sealed record Cmd(int Code, long[] Ints, double Distance = 0, int Feed = 0, string Name = "");

    /// <summary>Reads an .x3g back into commands, knowing each one's length; anything unknown fails the test.</summary>
    private static List<Cmd> Read(byte[] x)
    {
        var list = new List<Cmd>();
        int i = 0;
        int U8() => x[i++];
        int U16() { int v = BinaryPrimitives.ReadUInt16LittleEndian(x.AsSpan(i)); i += 2; return v; }
        long I32() { long v = BinaryPrimitives.ReadInt32LittleEndian(x.AsSpan(i)); i += 4; return v; }
        long U32() { long v = BinaryPrimitives.ReadUInt32LittleEndian(x.AsSpan(i)); i += 4; return v; }
        float F32() { float v = BinaryPrimitives.ReadSingleLittleEndian(x.AsSpan(i)); i += 4; return v; }
        while (i < x.Length)
        {
            int c = U8();
            switch (c)
            {
                case 153:
                {
                    U32();
                    var sb = new StringBuilder();
                    for (int ch; (ch = U8()) != 0;) sb.Append((char)ch);
                    list.Add(new Cmd(c, [], Name: sb.ToString()));
                    break;
                }
                case 154: list.Add(new Cmd(c, [U8()])); break;
                case 155:
                    list.Add(new Cmd(c, [I32(), I32(), I32(), I32(), I32(), U32(), U8()], F32(), U16()));
                    break;
                case 139: list.Add(new Cmd(c, [I32(), I32(), I32(), I32(), I32(), U32()])); break;
                case 140: list.Add(new Cmd(c, [I32(), I32(), I32(), I32(), I32()])); break;
                case 131: case 132: list.Add(new Cmd(c, [U8(), U32(), U16()])); break;
                case 144: case 137: list.Add(new Cmd(c, [U8()])); break;
                case 135: case 141: list.Add(new Cmd(c, [U8(), U16(), U16()])); break;
                case 136:
                {
                    int tool = U8(), action = U8(), len = U8();
                    long value = len == 2 ? U16() : len == 1 ? U8() : throw new InvalidOperationException($"payload {len}");
                    list.Add(new Cmd(c, [tool, action, value]));
                    break;
                }
                default: throw new InvalidOperationException($"Unknown x3g command {c} at byte {i - 1}.");
            }
        }
        return list;
    }

    [Fact]
    public void A_small_program_comes_out_byte_for_byte()
    {
        // From (0, 0, 0): 10 mm along X at 600 mm/min, then a 1 mm extrusion on the spot.
        var bytes = X3g.Convert("G92 X0 Y0 Z0 E0\nG1 X10 F600\nG1 E1\nM104 S200 T0\n", Fcp, "cube");
        var expected = new List<byte> { 153, 0, 0, 0, 0, (byte)'c', (byte)'u', (byte)'b', (byte)'e', 0 };
        expected.Add(140);
        for (int k = 0; k < 5; k++) expected.AddRange(new byte[4]);
        // 155: X = round(10 × 94.117647) = 941 steps; the move takes 1 s, so 941 steps a second; 10 mm; 10 mm/s × 64.
        expected.Add(155);
        expected.AddRange(Le(941)); expected.AddRange(Le(0)); expected.AddRange(Le(0)); expected.AddRange(Le(0)); expected.AddRange(Le(0));
        expected.AddRange(Le(941));
        expected.Add(0x18);
        expected.AddRange(BitConverter.GetBytes(10f));
        expected.AddRange([0x80, 0x02]);
        // 155: 1 mm of filament is -96 steps (MakerBot extruders turn backward to push), at the 600 mm/min still set.
        expected.Add(155);
        expected.AddRange(Le(941)); expected.AddRange(Le(0)); expected.AddRange(Le(0)); expected.AddRange(Le(-96)); expected.AddRange(Le(0));
        // 1 mm at 600 mm/min is 0.1 s: 96 steps in it, 960 a second; 10 mm/s × 64.
        expected.AddRange(Le(960));
        expected.Add(0x18);
        expected.AddRange(BitConverter.GetBytes(1f));
        expected.AddRange([0x80, 0x02]);
        // 136: tool 0, action 3 (nozzle temperature), 2 bytes, 200 °C. Then the end of the build.
        expected.AddRange([136, 0, 3, 2, 200, 0, 154, 0]);
        Assert.Equal(expected.ToArray(), bytes);
    }

    private static byte[] Le(int v) => BitConverter.GetBytes(v);

    [Fact]
    public void A_cube_for_the_creator_pro_homes_heats_and_moves_where_the_gcode_says()
    {
        var fcp = Presets.Printers.Single(p => p.Name == "Flashforge Creator Pro");
        var job = PrintJob.Run(Shapes.Cube(0.02), new PrintSetup { Printer = fcp });
        var cmds = Read(X3g.Convert(job.GCode, Fcp, "cube"));
        Assert.Equal(153, cmds[0].Code);
        Assert.Equal("cube", cmds[0].Name);
        Assert.Equal(154, cmds[^1].Code);
        // Heated (and waited for, with no timeout) before homing and moving.
        int bedWait = cmds.FindIndex(c => c.Code == 141), nozzleWait = cmds.FindIndex(c => c.Code == 135);
        int home = cmds.FindIndex(c => c.Code == 132), firstMove = cmds.FindIndex(c => c.Code is 155 or 139);
        Assert.Contains(cmds, c => c.Code == 136 && c.Ints[1] == 31 && c.Ints[2] == 60);
        Assert.Contains(cmds, c => c.Code == 136 && c.Ints[1] == 3 && c.Ints[2] == 210);
        Assert.Equal(0xFFFF, cmds[bedWait].Ints[2]);
        Assert.Equal(0xFFFF, cmds[nozzleWait].Ints[2]);
        Assert.True(bedWait < home && nozzleWait < home && home < firstMove);
        Assert.Equal(1 | 2, cmds[home].Ints[0]);
        Assert.Contains(cmds, c => c.Code == 131 && c.Ints[0] == 4);
        Assert.Contains(cmds, c => c.Code == 144 && c.Ints[0] == 31);
        // The first move after recalling the home offsets is an absolute point, giving every axis.
        Assert.Equal(139, cmds.First(c => c.Code is 155 or 139 && cmds.IndexOf(c) > cmds.FindIndex(k => k.Code == 144)).Code);

        // Played back: the moves stay on the cube (round the bed's middle, in steps), within the build volume.
        var moves = cmds.Where(c => c.Code == 155).ToList();
        // At least a move for every printed line in the G-code (travel and retraction are moves too).
        Assert.True(moves.Count >= Playback.Run(job.GCode).Extrusions.Count, $"{moves.Count} moves");
        Assert.All(moves, m =>
        {
            // The prime line runs along the bed's very edge: a step's rounding either way.
            Assert.InRange(m.Ints[0] / Fcp.XSteps, -113.5 - 0.011, 113.5 + 0.011);
            Assert.InRange(m.Ints[1] / Fcp.YSteps, -74 - 0.011, 74 + 0.011);
            Assert.InRange(m.Ints[2] / Fcp.ZSteps, 0, 150);
            Assert.True(m.Ints[5] > 0, "a step rate");
            Assert.Equal(0x18, m.Ints[6]);
        });
        // The cube's 20 mm is 1882 steps across.
        var high = moves.Where(m => m.Ints[2] > 10 * Fcp.ZSteps && m.Ints[2] < 18 * Fcp.ZSteps).ToList();
        Assert.InRange(high.Max(m => m.Ints[0]) - high.Min(m => m.Ints[0]), 1882 - 90, 1882);
        // Every millimetre of filament in the G-code is in the .x3g, to the step: the rounding carries over.
        double filament = -cmds.Where(c => c.Code == 155).Sum(c => c.Ints[3]) / Fcp.ASteps;
        var run = Playback.Run(job.GCode);
        Assert.Equal(run.E, filament, 1);
        // Never faster than the axes go.
        Assert.All(moves, m => Assert.True(m.Feed / 64.0 <= 18000 / 60.0 + 1e-6));
    }

    [Fact]
    public void Commands_sailfish_lacks_are_refused_with_their_line()
    {
        var e = Assert.Throws<FormatException>(() => X3g.Convert("G21\nG28 ; home\n", Fcp));
        Assert.Contains("Line 2", e.Message);
        Assert.Contains("G28", e.Message);
        // A move straight after homing that doesn't give every axis can't be placed.
        var f = Assert.Throws<FormatException>(() => X3g.Convert("G162 X Y F2000\nG1 Z5 F900\n", Fcp));
        Assert.Contains("must give X, Y and Z", f.Message);
        // Another printer's G-code (Marlin's G28) isn't Sailfish's.
        var marlin = PrintJob.Run(Shapes.Cube(0.01), new PrintSetup()).GCode;
        Assert.Throws<FormatException>(() => X3g.Convert(marlin, Fcp));
    }

    [Fact]
    public void The_creator_pro_preset_is_tied_to_its_machine_and_saves_whole()
    {
        var fcp = Presets.Printers.Single(p => p.Name == "Flashforge Creator Pro");
        Assert.Equal("fcp", fcp.X3gMachine);
        Assert.Equal(94.117647, SailfishMachine.ById[fcp.X3gMachine!].XSteps);
        Assert.Throws<ArgumentException>(() => (fcp with { X3gMachine = "nope" }).Checked());
        string dir = Path.Combine(Path.GetTempPath(), "avamodeler-x3g-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var job = PrintJob.Run(Shapes.Cube(0.01), new PrintSetup { Printer = fcp });
            X3g.Save(job.GCode, Fcp, Path.Combine(dir, "cube.x3g"));
            Assert.Equal(["cube.x3g"], Directory.GetFiles(dir).Select(Path.GetFileName));
            Assert.Equal(153, File.ReadAllBytes(Path.Combine(dir, "cube.x3g"))[0]);
        }
        finally { Directory.Delete(dir, true); }
    }
}
