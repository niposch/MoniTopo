using System.Runtime.InteropServices;

namespace MoniTopo.Windows.Display;

internal sealed record RollbackDisplayProperty(
    NativeAdapterId SourceAdapterId,
    uint SourceId,
    NativeAdapterId TargetAdapterId,
    uint TargetId,
    int WindowsScalePercent,
    bool HdrEnabled);

internal sealed record RollbackDisplayState(
    NativeDisplaySnapshot CoreConfiguration,
    IReadOnlyList<RollbackDisplayProperty> Properties);

internal static class DisplayRollbackSnapshotSerializer
{
    private const uint Magic = 0x4D545242;
    private const int Version = 1;
    private const int MaximumPathCount = 256;
    private const int MaximumModeCount = 1024;
    private const int MaximumPropertyCount = 256;

    internal static void Write(string path, RollbackDisplayState state)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new ArgumentException("A rollback path requires a directory.", nameof(path));
        Directory.CreateDirectory(directory);
        var temporaryPath = path + ".tmp";
        using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Magic);
            writer.Write(Version);
            WriteStructArray(writer, state.CoreConfiguration.Paths);
            WriteStructArray(writer, state.CoreConfiguration.Modes);
            writer.Write(state.Properties.Count);
            foreach (var property in state.Properties)
            {
                WriteAdapter(writer, property.SourceAdapterId);
                writer.Write(property.SourceId);
                WriteAdapter(writer, property.TargetAdapterId);
                writer.Write(property.TargetId);
                writer.Write(property.WindowsScalePercent);
                writer.Write(property.HdrEnabled);
            }
        }

        File.Move(temporaryPath, path, overwrite: true);
    }

    internal static RollbackDisplayState Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream);
        if (reader.ReadUInt32() != Magic || reader.ReadInt32() != Version)
        {
            throw new InvalidDataException("The display rollback snapshot version is not supported.");
        }

        var paths = ReadStructArray<NativePathInfo>(reader, MaximumPathCount);
        var modes = ReadStructArray<NativeModeInfo>(reader, MaximumModeCount);
        var propertyCount = ReadBoundedCount(reader, MaximumPropertyCount);
        var properties = new RollbackDisplayProperty[propertyCount];
        for (var index = 0; index < propertyCount; index++)
        {
            properties[index] = new RollbackDisplayProperty(
                ReadAdapter(reader),
                reader.ReadUInt32(),
                ReadAdapter(reader),
                reader.ReadUInt32(),
                reader.ReadInt32(),
                reader.ReadBoolean());
        }

        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException("The display rollback snapshot contains trailing data.");
        }

        return new RollbackDisplayState(new NativeDisplaySnapshot(paths, modes), properties);
    }

    private static void WriteStructArray<T>(BinaryWriter writer, T[] values)
        where T : unmanaged
    {
        writer.Write(Marshal.SizeOf<T>());
        writer.Write(values.Length);
        writer.Write(MemoryMarshal.AsBytes(values.AsSpan()));
    }

    private static T[] ReadStructArray<T>(BinaryReader reader, int maximumCount)
        where T : unmanaged
    {
        var elementSize = reader.ReadInt32();
        if (elementSize != Marshal.SizeOf<T>())
        {
            throw new InvalidDataException("The display rollback snapshot uses an incompatible native structure size.");
        }

        var count = ReadBoundedCount(reader, maximumCount);
        var byteCount = checked(elementSize * count);
        var bytes = reader.ReadBytes(byteCount);
        if (bytes.Length != byteCount)
        {
            throw new EndOfStreamException("The display rollback snapshot ended unexpectedly.");
        }

        var result = new T[count];
        MemoryMarshal.Cast<byte, T>(bytes).CopyTo(result);
        return result;
    }

    private static int ReadBoundedCount(BinaryReader reader, int maximum)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > maximum)
        {
            throw new InvalidDataException("The display rollback snapshot contains an invalid item count.");
        }

        return count;
    }

    private static void WriteAdapter(BinaryWriter writer, NativeAdapterId adapterId)
    {
        writer.Write(adapterId.LowPart);
        writer.Write(adapterId.HighPart);
    }

    private static NativeAdapterId ReadAdapter(BinaryReader reader) => new(reader.ReadUInt32(), reader.ReadInt32());
}
