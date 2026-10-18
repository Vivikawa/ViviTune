using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;

namespace ViviTune.Windows;

public sealed record RunningApp(string Path, string Name);

public static class ProcessCatalog
{
    // excludePaths — уже добавленные: исключаются по имени файла (та же программа в другой папке — тоже она).
    public static List<RunningApp> Running(IEnumerable<string> excludePaths)
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";
        var self = Environment.ProcessPath ?? "";
        var skip = new HashSet<string>(excludePaths.Select(System.IO.Path.GetFileName).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<RunningApp>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                string? path;
                try { path = p.MainModule?.FileName; } catch { continue; }
                if (string.IsNullOrEmpty(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (path.StartsWith(win, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(System.IO.Path.GetFileName(path), "sing-box.exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(path, self, StringComparison.OrdinalIgnoreCase) || skip.Contains(System.IO.Path.GetFileName(path)) || !seen.Add(path)) continue;
                result.Add(new RunningApp(path, DisplayName(path)));
            }
            catch { }
            finally { p.Dispose(); }
        }
        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return result;
    }

    public static string DisplayName(string exePath)
    {
        try
        {
            var d = FileVersionInfo.GetVersionInfo(exePath).FileDescription;
            if (!string.IsNullOrWhiteSpace(d)) return d.Trim();
        }
        catch { }
        return System.IO.Path.GetFileNameWithoutExtension(exePath);
    }

    public static byte[]? IconPng(string exePath)
    {
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(exePath);
            if (icon == null) return null;
            using var bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawIcon(icon, new Rectangle(0, 0, 32, 32));
            }
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        catch { return null; }
    }
}
