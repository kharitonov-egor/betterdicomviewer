using System.Text;
using BetterDicomViewer.Core.Geometry;

namespace BetterDicomViewer.Core.Viewing;

/// <summary>Patient-direction letters (L/R, A/P, S/I) for screen edges, from LPS direction vectors.</summary>
public static class OrientationLabels
{
    public static string ForDirection(Vec3 v, double threshold = 0.3)
    {
        var parts = new List<(double Mag, char Letter)>(3)
        {
            (Math.Abs(v.X), v.X >= 0 ? 'L' : 'R'),
            (Math.Abs(v.Y), v.Y >= 0 ? 'P' : 'A'),
            (Math.Abs(v.Z), v.Z >= 0 ? 'S' : 'I'),
        };
        var sb = new StringBuilder(3);
        foreach (var p in parts.OrderByDescending(p => p.Mag))
            if (p.Mag >= threshold || sb.Length == 0)
                sb.Append(p.Letter);
        return sb.ToString();
    }

    /// <summary>Edge labels (left, top, right, bottom) for a view whose screen axes point along <paramref name="right"/> and <paramref name="down"/> in LPS.</summary>
    public static (string Left, string Top, string Right, string Bottom) ForView(Vec3 right, Vec3 down) =>
        (ForDirection(-right), ForDirection(-down), ForDirection(right), ForDirection(down));

    /// <summary>
    /// Converts Patient Orientation (0020,0020) letters such as "A\F" into LPS vectors for the image row and column directions.
    /// Returns null when the value is missing or malformed.
    /// </summary>
    public static (Vec3 Row, Vec3 Column)? FromPatientOrientation(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Split('\\');
        if (parts.Length != 2) return null;
        var row = Parse(parts[0]);
        var col = Parse(parts[1]);
        if (row is null || col is null) return null;
        return (row.Value, col.Value);
    }

    private static Vec3? Parse(string letters)
    {
        var v = Vec3.Zero;
        foreach (var ch in letters.Trim().ToUpperInvariant())
        {
            v += ch switch
            {
                'L' => Vec3.UnitX,
                'R' => -Vec3.UnitX,
                'P' => Vec3.UnitY,
                'A' => -Vec3.UnitY,
                'H' or 'S' => Vec3.UnitZ,
                'F' or 'I' => -Vec3.UnitZ,
                _ => Vec3.Zero,
            };
        }
        return v.LengthSquared > 0 ? v.Normalized() : null;
    }
}
