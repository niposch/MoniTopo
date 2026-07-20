using System.Globalization;

namespace MoniTopo.Core.Versioning;

public readonly record struct ReleaseVersion : IComparable<ReleaseVersion>
{
    public ReleaseVersion(DateOnly date, int revision)
    {
        if (date.Year < 2000)
        {
            throw new ArgumentOutOfRangeException(nameof(date), "Release years must be 2000 or later.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(revision);

        Date = date;
        Revision = revision;
    }

    public DateOnly Date { get; }

    public int Revision { get; }

    public string PackageVersion => string.Create(
        CultureInfo.InvariantCulture,
        $"{Date.Year}.{Date.Month}{Date.Day:00}.{Revision}");

    public string DisplayVersion => Revision == 0
        ? Date.ToString("dd.MM.yy", CultureInfo.InvariantCulture)
        : string.Create(CultureInfo.InvariantCulture, $"{Date:dd.MM.yy}.{Revision}");

    public int CompareTo(ReleaseVersion other)
    {
        var dateComparison = Date.CompareTo(other.Date);
        return dateComparison != 0 ? dateComparison : Revision.CompareTo(other.Revision);
    }

    public static bool operator <(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) < 0;

    public static bool operator <=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) > 0;

    public static bool operator >=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) >= 0;

    public static ReleaseVersion Parse(string value)
    {
        if (!TryParse(value, out var version))
        {
            throw new FormatException($"'{value}' is not a MoniTopo package version.");
        }

        return version;
    }

    public static bool TryParse(string? value, out ReleaseVersion version)
    {
        version = default;
        var parts = value?.Split('.', StringSplitOptions.None);
        if (parts is not { Length: 3 } ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var year) ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var revision) ||
            parts[1].Length is < 3 or > 4 ||
            parts.Any(part => part.Length == 0 || part.Any(character => !char.IsAsciiDigit(character))))
        {
            return false;
        }

        var monthLength = parts[1].Length - 2;
        if (!int.TryParse(parts[1][..monthLength], NumberStyles.None, CultureInfo.InvariantCulture, out var month) ||
            !int.TryParse(parts[1][monthLength..], NumberStyles.None, CultureInfo.InvariantCulture, out var day) ||
            revision < 0)
        {
            return false;
        }

        try
        {
            version = new ReleaseVersion(new DateOnly(year, month, day), revision);
            return version.PackageVersion == value;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}
