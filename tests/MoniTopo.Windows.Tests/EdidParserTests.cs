using System.Text;
using MoniTopo.Windows.Display;

namespace MoniTopo.Windows.Tests;

public sealed class EdidParserTests
{
    [Fact]
    public void ParsesSyntheticIdentityWithoutPersistingRawEdid()
    {
        var edid = new byte[128];
        var manufacturer = (20 << 10) | (19 << 5) | 20; // TST
        edid[8] = (byte)(manufacturer >> 8);
        edid[9] = (byte)manufacturer;
        edid[10] = 0x34;
        edid[11] = 0x12;
        edid[21] = 60;
        edid[22] = 34;
        WriteDescriptor(edid, 54, 0xFF, "SERIAL-FAKE");
        WriteDescriptor(edid, 72, 0xFC, "Synthetic 27");

        var parsed = EdidParser.Parse(edid);

        Assert.Equal("TST", parsed.ManufacturerId);
        Assert.Equal(0x1234, parsed.ProductCode);
        Assert.Equal("SERIAL-FAKE", parsed.Serial);
        Assert.Equal("Synthetic 27", parsed.ModelName);
        Assert.Equal(600, parsed.PhysicalWidthMillimeters);
        Assert.Equal(340, parsed.PhysicalHeightMillimeters);
        Assert.Equal(16, parsed.ModeSignature?.Length);
    }

    [Fact]
    public void MissingOrShortEdidReturnsNoSignals()
    {
        var parsed = EdidParser.Parse(new byte[16]);

        Assert.Null(parsed.ManufacturerId);
        Assert.Null(parsed.Serial);
        Assert.Null(parsed.ModeSignature);
    }

    private static void WriteDescriptor(byte[] edid, int offset, byte type, string value)
    {
        edid[offset + 3] = type;
        var bytes = Encoding.ASCII.GetBytes(value.PadRight(13, ' '));
        Array.Copy(bytes, 0, edid, offset + 5, 13);
    }
}
