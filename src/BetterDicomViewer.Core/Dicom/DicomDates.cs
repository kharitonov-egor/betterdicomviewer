using System.Globalization;

namespace BetterDicomViewer.Core.Dicom;

public static class DicomDates
{
    public static DateOnly? ParseDate(string? da)
    {
        if (string.IsNullOrWhiteSpace(da)) return null;
        var s = da.Trim().Replace(".", "", StringComparison.Ordinal);
        return s.Length >= 8 && DateOnly.TryParseExact(s[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    /// <summary>Parses TM values such as "HHMMSS.FFFFFF", "HHMM", or the legacy "HH:MM:SS".</summary>
    public static TimeSpan? ParseTime(string? tm)
    {
        if (string.IsNullOrWhiteSpace(tm)) return null;
        var s = tm.Trim().Replace(":", "", StringComparison.Ordinal);
        var fraction = 0.0;
        var dot = s.IndexOf('.');
        if (dot >= 0)
        {
            if (!double.TryParse("0" + s[dot..], NumberStyles.Float, CultureInfo.InvariantCulture, out fraction)) return null;
            s = s[..dot];
        }
        if (s.Length < 2 || s.Length % 2 != 0 || !s.All(char.IsAsciiDigit)) return null;
        var h = int.Parse(s[..2], CultureInfo.InvariantCulture);
        var m = s.Length >= 4 ? int.Parse(s.Substring(2, 2), CultureInfo.InvariantCulture) : 0;
        var sec = s.Length >= 6 ? int.Parse(s.Substring(4, 2), CultureInfo.InvariantCulture) : 0;
        if (h > 23 || m > 59 || sec > 60) return null;
        return new TimeSpan(h, m, sec) + TimeSpan.FromSeconds(fraction);
    }

    public static DateTime? Combine(string? da, string? tm)
    {
        var d = ParseDate(da);
        var t = ParseTime(tm);
        if (d is null || t is null) return null;
        return d.Value.ToDateTime(TimeOnly.MinValue) + t.Value;
    }

    /// <summary>Parses DT values "YYYYMMDDHHMMSS.FFFFFF&amp;ZZXX". Any UTC offset is ignored; all times in one study share a clock.</summary>
    public static DateTime? ParseDateTime(string? dt)
    {
        if (string.IsNullOrWhiteSpace(dt)) return null;
        var s = dt.Trim();
        var tz = s.IndexOfAny(['+', '-'], 8);
        if (tz > 0) s = s[..tz];
        if (s.Length < 8) return null;
        var date = ParseDate(s[..8]);
        if (date is null) return null;
        var time = s.Length > 8 ? ParseTime(s[8..]) : TimeSpan.Zero;
        return time is null ? null : date.Value.ToDateTime(TimeOnly.MinValue) + time.Value;
    }

    public static string FormatDate(string? da)
    {
        var d = ParseDate(da);
        return d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? (da ?? "");
    }
}
