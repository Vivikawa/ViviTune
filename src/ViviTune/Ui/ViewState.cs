using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace ViviTune.Ui;

// Состояние окна для страниц. Меняется только в UI-потоке.
// Действия пользователя страницы передают через Request*: App подписан на события, Session страницы не видят.
public sealed class ViewState : INotifyPropertyChanged
{
    public const int HistoryMax = 60, LogMax = 500;

    ConnState state;
    string? errorText, warningText, settingsError;
    string detailText = "", ip = "";
    double upBps, downBps;
    long upTotal, downTotal;
    int port;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? ToggleRequested;
    public event Action<string>? AddAppRequested;
    public event Action<string>? RemoveAppRequested;
    public event Action<string, bool>? SetAppEnabledRequested;
    public event Action<string, int>? SetProxyRequested;

    public ConnState State
    {
        get => state;
        set
        {
            if (!Set(ref state, value)) return;
            if (value == ConnState.Disconnected) // без подключения — нули: старый график не держим
            {
                UpHistory.Clear();
                DownHistory.Clear();
            }
            if (value == ConnState.Checking) // новое подключение — итог с нуля; перезапуски (Restarting) его не сбрасывают
            {
                upTotal = downTotal = 0;
                Notify(nameof(UpTotalText), nameof(DownTotalText));
            }
            Notify(nameof(StatusText), nameof(SettingsEditable), nameof(IsBusy), nameof(IsConnected));
        }
    }

    // Проверка или перезапуск: кольцо крутится, кнопка неактивна.
    public bool IsBusy => state is ConnState.Checking or ConnState.Restarting;
    public bool IsConnected => state == ConnState.Connected;

    public string StatusText => state switch
    {
        ConnState.Checking => "Проверяю прокси…",
        ConnState.Restarting => "Перезапуск…",
        ConnState.Connected => "Подключено",
        _ => "Отключено"
    };

    public bool SettingsEditable => state == ConnState.Disconnected;

    public string? ErrorText { get => errorText; set => Set(ref errorText, value); }
    public string? WarningText { get => warningText; set => Set(ref warningText, value); }
    public string DetailText { get => detailText; set => Set(ref detailText, value); }

    public string Ip { get => ip; set { if (Set(ref ip, value)) Notify(nameof(ProxyLabel)); } }
    public int Port { get => port; set { if (Set(ref port, value)) Notify(nameof(ProxyLabel)); } }
    public string ProxyLabel => $"SOCKS5 · {ip}:{port}";
    public string? SettingsError { get => settingsError; set => Set(ref settingsError, value); }

    public double UpBps { get => upBps; private set { if (Set(ref upBps, value)) Notify(nameof(UpText)); } }
    public double DownBps { get => downBps; private set { if (Set(ref downBps, value)) Notify(nameof(DownText)); } }
    public string UpText => Format.Speed(upBps);
    public string DownText => Format.Speed(downBps);
    public string UpTotalText => "всего " + Format.Bytes(upTotal);
    public string DownTotalText => "всего " + Format.Bytes(downTotal);
    public ObservableCollection<double> UpHistory { get; } = new();
    public ObservableCollection<double> DownHistory { get; } = new();

    public ObservableCollection<AppRow> Apps { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();

    // Источники для страницы Приложения; зовутся НЕ в UI-потоке. App даёт ProcessCatalog, Shots — демо.
    // ListRunning(уже добавленные пути) — запущенные программы (путь, имя, без окна ли) без них; IconPng(путь) — PNG или null.
    public Func<IReadOnlyCollection<string>, IEnumerable<(string Path, string Name, bool Background)>> ListRunning { get; init; } = _ => [];
    public Func<string, byte[]?> IconPng { get; init; } = _ => null;

    readonly ConcurrentDictionary<string, Bitmap?> icons = new(StringComparer.OrdinalIgnoreCase);

    // Иконка по пути, один раз на путь (повторное открытие панели и строки берут из кэша).
    // Из любого потока, но извлечение медленное: только не в UI-потоке. null — плашка с буквой.
    public Bitmap? LoadIcon(string path) => icons.GetOrAdd(path, p =>
    {
        try
        {
            var png = IconPng(p);
            if (png == null) return null;
            using var ms = new MemoryStream(png);
            return new Bitmap(ms);
        }
        catch (Exception) { return null; } // битый PNG: плашка с буквой
    });

    public void PushSpeed(Speed s)
    {
        UpBps = s.UpBps;
        DownBps = s.DownBps;
        Push(UpHistory, s.UpBps, HistoryMax);
        Push(DownHistory, s.DownBps, HistoryMax);
        if (s.UpBytes == 0 && s.DownBytes == 0) return;
        upTotal += s.UpBytes;
        downTotal += s.DownBytes;
        Notify(nameof(UpTotalText), nameof(DownTotalText));
    }

    public void AddLog(string line) => Push(LogLines, line, LogMax);

    // Строки Apps по списку из Settings (порядок тот же): лишние убрать, недостающие вставить, Enabled обновить.
    // Новые строки — с именем файла и плашкой; их App возвращает, чтобы догрузить описание и иконку.
    public List<AppRow> SyncApps(List<AppEntry> apps)
    {
        for (int i = Apps.Count - 1; i >= 0; i--)
        {
            var path = Apps[i].Path;
            if (!apps.Exists(a => AppListOps.Same(a.Path, path))) Apps.RemoveAt(i);
        }
        var added = new List<AppRow>();
        for (int i = 0; i < apps.Count; i++)
        {
            if (i < Apps.Count && AppListOps.Same(Apps[i].Path, apps[i].Path))
            {
                Apps[i].Enabled = apps[i].Enabled;
                continue;
            }
            var row = new AppRow
            {
                Path = apps[i].Path,
                FileName = System.IO.Path.GetFileName(apps[i].Path),
                Name = System.IO.Path.GetFileNameWithoutExtension(apps[i].Path),
                Enabled = apps[i].Enabled
            };
            Apps.Insert(i, row);
            added.Add(row);
        }
        while (Apps.Count > apps.Count) Apps.RemoveAt(Apps.Count - 1);
        return added;
    }

    // Строка под статусом при подключении: «00:42:17 · TCP + UDP · 2 прилож.»; UDP неизвестен — без протоколов.
    public static string Detail(TimeSpan up, bool? udp, int apps)
    {
        var time = Format.Uptime(up < TimeSpan.Zero ? TimeSpan.Zero : up);
        var u = udp switch { true => " · TCP + UDP", false => " · только TCP", null => "" };
        return $"{time}{u} · {apps} прилож.";
    }

    public void RequestToggle() => ToggleRequested?.Invoke();
    public void RequestAddApp(string path) => AddAppRequested?.Invoke(path);
    public void RequestRemoveApp(string path) => RemoveAppRequested?.Invoke(path);
    public void RequestSetAppEnabled(string path, bool enabled) => SetAppEnabledRequested?.Invoke(path, enabled);
    // Ip/Port здесь — то, что сейчас в Settings (App держит их вместе). Без изменений события нет:
    // иначе заполнение полей страницы при старте (TextChanged приходит позже, асинхронно) перезаписывало бы settings.json.
    public void RequestSetProxy(string ip, int port)
    {
        if (ip == Ip && port == Port) return;
        SetProxyRequested?.Invoke(ip, port);
    }

    static void Push<T>(ObservableCollection<T> list, T value, int max)
    {
        list.Add(value);
        while (list.Count > max) list.RemoveAt(0);
    }

    bool Set<T>(ref T field, T value, [CallerMemberName] string name = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(name);
        return true;
    }

    void Notify(params string[] names)
    {
        foreach (var n in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}

public sealed class AppRow : INotifyPropertyChanged
{
    string name = "";
    Bitmap? icon;
    bool enabled = true;

    public event PropertyChangedEventHandler? PropertyChanged;

    // Цвета плашки с буквой, если у программы нет иконки
    static readonly IBrush[] BadgeColors = new[] { "#5865f2", "#ea4335", "#1db954", "#2b88d8", "#f59e0b", "#8b5cf6", "#ec4899", "#14b8a6" }
        .Select(c => (IBrush)new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse(c))).ToArray();

    public string Path { get; init; } = "";
    public string FileName { get; init; } = "";
    public string Name
    {
        get => name;
        set
        {
            if (!Set(ref name, value)) return;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Letter)));
        }
    }
    public Bitmap? Icon { get => icon; set => Set(ref icon, value); }
    public bool Enabled { get => enabled; set => Set(ref enabled, value); }

    public string Letter => name.Length > 0 ? char.ToUpperInvariant(name[0]).ToString() : "?";

    // Цвет по пути (не по имени): не меняется, когда имя файла сменится описанием программы
    public IBrush Badge
    {
        get
        {
            uint h = 0;
            foreach (var c in Path) h = h * 31 + char.ToLowerInvariant(c);
            h ^= h >> 15; // перемешать: иначе остаток от деления зависит почти только от последних букв
            h *= 0x2c1b3c6d;
            h ^= h >> 12;
            return BadgeColors[h % BadgeColors.Length];
        }
    }

    bool Set<T>(ref T field, T value, [CallerMemberName] string n = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        return true;
    }
}
