using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;

namespace ViviTune.Windows;

// Background — ни у одного процесса программы нет окна (службы, помощники, консольные утилиты, свёрнутые в трей).
public sealed record RunningApp(string Path, string Name, bool Background);

public static class ProcessCatalog
{
    // excludePaths — уже добавленные: исключаются по имени файла (та же программа в другой папке — тоже она).
    public static List<RunningApp> Running(IEnumerable<string> excludePaths)
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";
        var self = Environment.ProcessPath ?? "";
        var skip = new HashSet<string>(excludePaths.Select(System.IO.Path.GetFileName).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        var procs = new List<(string, bool)>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                string? path;
                try { path = p.MainModule?.FileName; } catch { continue; }
                if (string.IsNullOrEmpty(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (path.StartsWith(win, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(System.IO.Path.GetFileName(path), "sing-box.exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(path, self, StringComparison.OrdinalIgnoreCase) || skip.Contains(System.IO.Path.GetFileName(path))) continue;
                procs.Add((path, p.MainWindowHandle != IntPtr.Zero)); // MainWindowHandle — видимое окно верхнего уровня
            }
            catch { }
            finally { p.Dispose(); }
        }
        var result = Group(procs).Select(g => new RunningApp(g.Path, DisplayName(g.Path), g.Background)).ToList();
        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return result;
    }

    // Одна строка на путь (без учёта регистра); фоновая — если окна нет ни у одного её процесса (у Chrome окно только у одного из десятка).
    public static IEnumerable<(string Path, bool Background)> Group(IEnumerable<(string Path, bool HasWindow)> procs) =>
        procs.GroupBy(p => p.Path, StringComparer.OrdinalIgnoreCase).Select(g => (g.Key, !g.Any(p => p.HasWindow)));

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
            using var bmp = icon.ToBitmap(); // системный размер иконки, с прозрачностью; масштабирует Avalonia
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        catch { return null; }
    }
}
