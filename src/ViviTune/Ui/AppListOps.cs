namespace ViviTune.Ui;

// Список приложений из Settings: пути сравниваются без учёта регистра. Без Avalonia (тестируется отдельно).
// App меняет копию (Copy) и подменяет ею Settings.Apps целиком: Session читает список в своих потоках.
public static class AppListOps
{
    public static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // Та же программа: одно имя .exe (правила sing-box идут по имени), папка может быть другой — после обновления.
    public static bool SameFile(string a, string b) =>
        string.Equals(Path.GetFileName(a), Path.GetFileName(b), StringComparison.OrdinalIgnoreCase);

    public static List<AppEntry> Copy(List<AppEntry> apps) =>
        apps.Select(a => new AppEntry { Path = a.Path, Enabled = a.Enabled }).ToList();

    // false — путь пустой или уже есть; тот же файл в другой папке — путь заменяется на месте (Enabled прежний);
    // новое добавляется в конец и сразу включено.
    public static bool Add(List<AppEntry> apps, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || apps.Exists(a => Same(a.Path, path))) return false;
        var old = apps.Find(a => SameFile(a.Path, path));
        if (old != null) old.Path = path;
        else apps.Add(new AppEntry { Path = path, Enabled = true });
        return true;
    }

    public static bool Remove(List<AppEntry> apps, string path) => apps.RemoveAll(a => Same(a.Path, path)) > 0;

    // false — такого пути нет или значение то же.
    public static bool SetEnabled(List<AppEntry> apps, string path, bool enabled)
    {
        bool changed = false;
        foreach (var a in apps)
            if (Same(a.Path, path) && a.Enabled != enabled)
            {
                a.Enabled = enabled;
                changed = true;
            }
        return changed;
    }
}
