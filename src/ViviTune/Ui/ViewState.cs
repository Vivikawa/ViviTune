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
    int? directPort;
    bool directTab, proxyDown, bypassDown;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? ToggleRequested;
    public event Action<string, bool>? AddAppRequested; // путь, в «Мимо Podkop» ли
    public event Action<string>? RemoveAppRequested;
    public event Action<string, bool>? SetAppEnabledRequested;
    public event Action<string, int, int?>? SetProxyRequested;

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
    public ObservableCollection<AppRow> DirectApps { get; } = new();

    // Открыта вкладка «Мимо Podkop» (иначе «Через прокси»)
    public bool DirectTab
    {
        get => directTab;
        set { if (Set(ref directTab, value)) Notify(nameof(ShownApps), nameof(EmptyText), nameof(DirectHintVisible), nameof(ExitDownHint)); }
    }
    public ObservableCollection<AppRow> ShownApps => directTab ? DirectApps : Apps;
    public string ProxyTabText => $"Через прокси · {Apps.Count(a => a.Enabled)}";
    public string DirectTabText => $"Мимо Podkop · {DirectApps.Count(a => a.Enabled)}";
    public string EmptyText => directTab
        ? "Добавьте приложение, трафик которого пойдёт мимо Podkop"
        : "Добавьте приложение, трафик которого пойдёт через прокси";
    public int? DirectPort { get => directPort; set { if (Set(ref directPort, value)) Notify(nameof(DirectHintVisible)); } }
    // Вкладка «Мимо Podkop» без порта: подсказать, где его задать
    public bool DirectHintVisible => directTab && directPort == null;
    // Выход списка не отвечает при подключении: программы идут обычным путём (ставит App по Session.Live)
    public bool ProxyDown { get => proxyDown; set { if (Set(ref proxyDown, value)) Notify(nameof(ExitDownHint)); } }
    public bool BypassDown { get => bypassDown; set { if (Set(ref bypassDown, value)) Notify(nameof(ExitDownHint)); } }
    public string? ExitDownHint =>
        directTab && bypassDown ? "Порт «Мимо Podkop» не отвечает: программы из этой вкладки сейчас идут обычным путём."
        : !directTab && proxyDown ? "Прокси не отвечает: программы из этой вкладки сейчас идут обычным путём."
        : null;
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

    // Строки Apps и DirectApps по спискам из Settings (порядок тот же): лишние убрать, недостающие вставить, Enabled обновить.
    // Новые строки обоих списков — с именем файла и плашкой; их App возвращает, чтобы догрузить описание и иконку.
    public List<AppRow> SyncApps(List<AppEntry> apps, List<AppEntry> directApps)
    {
        var added = SyncRows(Apps, apps);
        added.AddRange(SyncRows(DirectApps, directApps));
        Notify(nameof(ProxyTabText), nameof(DirectTabText));
        return added;
    }

    static List<AppRow> SyncRows(ObservableCollection<AppRow> rows, List<AppEntry> apps)
    {
        for (int i = rows.Count - 1; i >= 0; i--)
        {
            var path = rows[i].Path;
            if (!apps.Exists(a => AppListOps.Same(a.Path, path))) rows.RemoveAt(i);
        }
        var added = new List<AppRow>();
        for (int i = 0; i < apps.Count; i++)
        {
            if (i < rows.Count && AppListOps.Same(rows[i].Path, apps[i].Path))
            {
                rows[i].Enabled = apps[i].Enabled;
                continue;
            }
            var row = new AppRow
            {
                Path = apps[i].Path,
                FileName = System.IO.Path.GetFileName(apps[i].Path),
                Name = System.IO.Path.GetFileNameWithoutExtension(apps[i].Path),
                Enabled = apps[i].Enabled
            };
            rows.Insert(i, row);
            added.Add(row);
        }
        while (rows.Count > apps.Count) rows.RemoveAt(rows.Count - 1);
        return added;
    }

    // Строка под статусом при подключении: «00:42:17 · TCP + UDP · 2 через прокси · 1 мимо Podkop»;
    // UDP неизвестен — без протоколов, нулевая часть не пишется.
    public static string Detail(TimeSpan up, bool? udp, int proxyApps, int bypassApps)
    {
        var time = Format.Uptime(up < TimeSpan.Zero ? TimeSpan.Zero : up);
        var u = udp switch { true => " · TCP + UDP", false => " · только TCP", null => "" };
        var p = proxyApps > 0 ? $" · {proxyApps} через прокси" : "";
        var d = bypassApps > 0 ? $" · {bypassApps} мимо Podkop" : "";
        return time + u + p + d;
    }

    public void RequestToggle() => ToggleRequested?.Invoke();
    public void RequestAddApp(string path) => AddAppRequested?.Invoke(path, directTab);
    public void RequestRemoveApp(string path) => RemoveAppRequested?.Invoke(path);
    public void RequestSetAppEnabled(string path, bool enabled) => SetAppEnabledRequested?.Invoke(path, enabled);
    // Ip/Port/DirectPort здесь — то, что сейчас в Settings (App держит их вместе). Без изменений события нет:
    // иначе заполнение полей страницы при старте (TextChanged приходит позже, асинхронно) перезаписывало бы settings.json.
    public void RequestSetProxy(string ip, int port, int? directPort)
    {
        if (ip == Ip && port == Port && directPort == DirectPort) return;
        SetProxyRequested?.Invoke(ip, port, directPort);
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
