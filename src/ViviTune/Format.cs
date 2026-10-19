using System.Globalization;

namespace ViviTune;

public static class Format
{
    public static string Speed(double bps)
    {
        var ic = CultureInfo.InvariantCulture;
        if (bps < 1024) return Math.Round(bps).ToString("0", ic) + " Б/с";
        if (bps < 1024 * 1024) return (bps / 1024).ToString("0.0", ic) + " КБ/с";
        return (bps / (1024 * 1024)).ToString("0.0", ic) + " МБ/с";
    }

    public static string Bytes(long b)
    {
        var ic = CultureInfo.InvariantCulture;
        if (b < 1024) return b.ToString(ic) + " Б";
        if (b < 1024 * 1024) return (b / 1024.0).ToString("0.0", ic) + " КБ";
        if (b < 1024L * 1024 * 1024) return (b / (1024.0 * 1024)).ToString("0.0", ic) + " МБ";
        return (b / (1024.0 * 1024 * 1024)).ToString("0.00", ic) + " ГБ";
    }

    public static string Uptime(TimeSpan t) =>
        string.Create(CultureInfo.InvariantCulture, $"{(long)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}");
}
