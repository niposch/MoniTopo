using System.Security.Cryptography;
using System.Text;

namespace MoniTopo.Windows.Display;

internal sealed record ParsedEdid(
    string? ManufacturerId,
    int? ProductCode,
    string? Serial,
    string? ModelName,
    int? PhysicalWidthMillimeters,
    int? PhysicalHeightMillimeters,
    string? ModeSignature);

internal static class EdidParser
{
    private const int MinimumEdidLength = 128;

    internal static ParsedEdid Parse(byte[]? edid)
    {
        if (edid is null || edid.Length < MinimumEdidLength ||
            !edid.AsSpan(0, 8).SequenceEqual((ReadOnlySpan<byte>)[0, 255, 255, 255, 255, 255, 255, 0]) ||
            edid.Take(MinimumEdidLength).Sum(value => value) % 256 != 0)
        {
            return new ParsedEdid(null, null, null, null, null, null, null);
        }

        var manufacturer = DecodeManufacturer(edid[8], edid[9]);
        var productCode = edid[10] | (edid[11] << 8);
        var numericSerial = BitConverter.ToUInt32(edid, 12);
        var descriptorSerial = NormalizeSerial(ReadDescriptor(edid, 0xFF));
        var serial = descriptorSerial ?? (numericSerial is 0 or uint.MaxValue ? null : numericSerial.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var model = ReadDescriptor(edid, 0xFC);
        int? width = edid[21] == 0 ? null : edid[21] * 10;
        int? height = edid[22] == 0 ? null : edid[22] * 10;
        var signature = Convert.ToHexString(SHA256.HashData(edid))[..16];
        return new ParsedEdid(manufacturer, productCode, serial, model, width, height, signature);
    }

    internal static string? DecodeManufacturer(byte high, byte low)
    {
        var value = (high << 8) | low;
        Span<char> characters = stackalloc char[3];
        characters[0] = DecodeLetter((value >> 10) & 0x1F);
        characters[1] = DecodeLetter((value >> 5) & 0x1F);
        characters[2] = DecodeLetter(value & 0x1F);
        return characters.Contains('?') ? null : new string(characters);
    }

    private static char DecodeLetter(int value) => value is >= 1 and <= 26 ? (char)('A' + value - 1) : '?';

    private static string? NormalizeSerial(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.All(character => character is '0' or ' ') ||
        value.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("n/a", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("none", StringComparison.OrdinalIgnoreCase)
            ? null : value;

    private static string? ReadDescriptor(byte[] edid, byte descriptorType)
    {
        for (var offset = 54; offset <= 108; offset += 18)
        {
            if (edid[offset] != 0 || edid[offset + 1] != 0 || edid[offset + 3] != descriptorType)
            {
                continue;
            }

            var value = Encoding.ASCII.GetString(edid, offset + 5, 13).Trim('\0', '\r', '\n', ' ');
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }
}
