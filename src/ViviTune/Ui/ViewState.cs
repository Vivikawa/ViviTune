using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace ViviTune.Ui;

// Уведомления об изменении свойств для ViewState и строк списков.
public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(name);
        return true;
    }

    protected void Notify(params string[] names)
    {
        foreach (var n in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}

// Состояние окна для страниц. Меняется только в UI-потоке.
// Действия пользователя страницы передают через Request*: App подписан на события, Session страницы не видят.
public sealed class ViewState : Observable
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
    ProxyMode mode;
    string vlessHost = "", vlessLink = "", vlessSummary = "";
    bool vlessLinkError;

    public event Action? ToggleRequested;
    public event Action<string, bool>? AddAppRequested; // путь, в «Мимо Podkop» ли
    public event Action<string>? RemoveAppRequested;
    public event Action<string, bool>? SetAppEnabledRequested;
    public event Action<string, int, int?>? SetProxyRequested;
    public event Action<ProxyMode>? SetModeRequested;
    public event Action<string>? SetVlessRequested; // ссылка
    public event Action<string, bool>? SetListEnabledRequested; // id списка
    public event Action<string>? AddSiteRequested, RemoveSiteRequested; // нормализованная запись
    public event Action<bool>? SetAutostartRequested;
    public event Action? FetchServersRequested, PingRequested;
    public event Action<string>? SelectServerRequested; // ключ сервера
    public event Action<bool>? SetAutoServerRequested;

    // Есть ли задача автозапуска (ставит App). Уведомляет и без изменения: переключатель, который пользователь
    // щёлкнул, а задача не создалась или не удалилась, возвращается к настоящему значению.
    public bool Autostart { get => autostart; set { autostart = value; Notify(nameof(Autostart)); } }
    bool autostart;
    // Без изменений события нет: переключатель повторяет значение, которое ему поставил App
    public void RequestSetAutostart(bool on)
    {
        if (on != autostart) SetAutostartRequested?.Invoke(on);
    }

    public ViewState()
    {
        ListGroups = Catalogs.Groups
            .Select(g => new ListGroup(g, Catalogs.Lists.Where(l => l.Group == g).Select(l => new ListRow(l)).ToList()))
            .ToList();
    }

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
            Notify(nameof(StatusText), nameof(SettingsEditable), nameof(IsBusy), nameof(IsConnected), nameof(CanFetch), nameof(CanFetchNow), nameof(CanPing));
        }
    }

    // Проверка или перезапуск: кольцо крутится, кнопка неактивна.
    public bool IsBusy => state is ConnState.Checking or ConnState.Restarting;
    public bool IsConnected => state == ConnState.Connected;

    public string StatusText => state switch
    {
        ConnState.Checking => IsVless ? "Проверяю сервер…" : "Проверяю прокси…",
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
    public string ProxyLabel => IsVless
        ? (IsSubscription ? SelectedName is { } n ? "VLESS · " + n : "VLESS"
            : vlessHost.Length > 0 ? "VLESS · " + vlessHost : "VLESS")
        : $"SOCKS5 · {ip}:{port}";
    public string? SettingsError { get => settingsError; set => Set(ref settingsError, value); }

    public double UpBps { get => upBps; private set { if (Set(ref upBps, value)) Notify(nameof(UpText)); } }
    public double DownBps { get => downBps; private set { if (Set(ref downBps, value)) Notify(nameof(DownText)); } }
    public string UpLabel => IsVless ? "↑ Отдача через VPN" : "↑ Отдача через прокси";
    public string DownLabel => IsVless ? "↓ Загрузка через VPN" : "↓ Загрузка через прокси";
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
        set { if (Set(ref directTab, value)) Notify(nameof(ShownApps), nameof(EmptyText), nameof(DirectHintVisible), nameof(ExitDownHint), nameof(SitesTab)); }
    }

    public ProxyMode Mode
    {
        get => mode;
        set
        {
            if (!Set(ref mode, value)) return;
            Notify(nameof(IsVless), nameof(ProxyLabel), nameof(ProxyTabText), nameof(DirectTabText), nameof(EmptyText), nameof(DirectHintVisible),
                nameof(ExitDownHint), nameof(StatusText), nameof(UpLabel), nameof(DownLabel), nameof(SitesTab));
        }
    }
    public bool IsVless => mode == ProxyMode.Vless;
    // VLESS: вкладка «Сайты» показывает списки ITDog вместо программ
    public bool SitesTab => IsVless && directTab;

    public string VlessHost { get => vlessHost; private set { if (Set(ref vlessHost, value)) Notify(nameof(ProxyLabel)); } }
    public string VlessLink { get => vlessLink; private set => Set(ref vlessLink, value); }
    public string VlessSummary { get => vlessSummary; private set => Set(ref vlessSummary, value); }
    public bool VlessLinkError { get => vlessLinkError; private set => Set(ref vlessLinkError, value); }
    public IReadOnlyList<ListGroup> ListGroups { get; }

    // Ссылка из Settings: пока движок не разобрал — «Проверяю ссылку…» (пустая — без сводки)
    public void SetVless(string link)
    {
        VlessLink = link;
        VlessHost = "";
        VlessLinkError = false;
        bool sub = Settings.IsSubscription(link);
        VlessSummary = sub || string.IsNullOrWhiteSpace(link) ? "" : "Проверяю ссылку…";
        if (sub != IsSubscription)
        {
            isSubscription = sub;
            Notify(nameof(IsSubscription), nameof(CanFetch), nameof(CanFetchNow), nameof(ProxyLabel));
        }
    }

    // ---- подписка: список серверов в Настройках ----
    bool isSubscription, fetching, pinging, autoServer;
    string? subError;
    string subInfoText = "";
    public bool IsSubscription => isSubscription;
    public bool CanFetch => isSubscription && SettingsEditable;
    public bool Fetching { get => fetching; set { if (Set(ref fetching, value)) Notify(nameof(CanPing), nameof(CanFetchNow)); } }
    public bool CanFetchNow => CanFetch && !fetching && !pinging;
    // идёт пинг: «↻» недоступна (два пинга не накладываются)
    public bool Pinging { get => pinging; set { if (Set(ref pinging, value)) Notify(nameof(CanPing), nameof(CanFetchNow)); } }
    public bool CanPing => !pinging && !fetching && (SettingsEditable || IsConnected) && Servers.Any(r => r.Supported);
    public string SubInfoText { get => subInfoText; private set => Set(ref subInfoText, value); }
    public string? SubError { get => subError; private set => Set(ref subError, value); }
    public ObservableCollection<ServerRow> Servers { get; } = new();
    // Уведомляет и без изменения (как Autostart): галка, которую App не принял, возвращается
    public bool AutoServer { get => autoServer; set { autoServer = value; Notify(nameof(AutoServer)); } }
    string? SelectedName => Servers.FirstOrDefault(r => r.Selected)?.Name;

    public void SetServers(IReadOnlyList<SubServer> servers, string? selected)
    {
        Servers.Clear();
        foreach (var s in servers)
            Servers.Add(new ServerRow { Key = s.Key, Name = s.Name, Supported = s.Server != null, Reason = s.Server == null ? s.Error ?? "" : "", Selected = s.Server != null && s.Key == selected });
        Notify(nameof(ProxyLabel), nameof(CanPing));
    }
    public void SetSelected(string key)
    {
        foreach (var r in Servers) r.Selected = r.Supported && r.Key == key;
        Notify(nameof(ProxyLabel));
    }
    public void SetPing(string key, int? ms) => Servers.FirstOrDefault(r => r.Key == key)?.SetPing(ms);
    public void SetPinging(string key) => Servers.FirstOrDefault(r => r.Key == key)?.SetPinging();
    // Пинг кончился (в том числе прерван): строки, оставшиеся на «…», возвращаются к прежнему значению
    public void ResetPinging() { foreach (var r in Servers) r.ResetPinging(); }
    public void SetSubError(string? e) => SubError = string.IsNullOrEmpty(e) ? null : e;

    // «StressKVN · 2,1 из 10,0 ГБ · до 27.10.2026 · обновлено 5 мин назад»
    public void SetSubInfo(SubInfo? info, DateTime? fetchedUtc, DateTime nowUtc)
    {
        var parts = new List<string>();
        if (info != null)
        {
            if (!string.IsNullOrEmpty(info.Title)) parts.Add(info.Title);
            static string Gb(long b) => (b / 1073741824.0).ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',');
            parts.Add(info.Total > 0 ? $"{Gb(info.Used)} из {Gb(info.Total)} ГБ" : Gb(info.Used) + " ГБ");
            if (info.Expire is { } e) parts.Add($"до {e.Day:00}.{e.Month:00}.{e.Year}");
        }
        if (fetchedUtc is { } f)
        {
            var d = nowUtc - f;
            parts.Add(d < TimeSpan.FromMinutes(1) ? "обновлено только что"
                : d < TimeSpan.FromHours(1) ? $"обновлено {(int)d.TotalMinutes} мин назад"
                : $"обновлено {(int)d.TotalHours} ч назад");
        }
        SubInfoText = string.Join(" · ", parts);
    }

    public void RequestFetchServers() => FetchServersRequested?.Invoke();
    public void RequestPing() => PingRequested?.Invoke();
    // неподдерживаемый сервер не выбирается
    public void RequestSelectServer(string key)
    {
        if (Servers.FirstOrDefault(r => r.Key == key) is { Supported: true }) SelectServerRequested?.Invoke(key);
    }
    // Без изменений события нет: заполнение галки при старте не должно переписывать settings.json
    public void RequestSetAutoServer(bool on)
    {
        if (on != autoServer) SetAutoServerRequested?.Invoke(on);
    }

    // Итог разбора: сервер и сводка под полем либо текст ошибки; устаревший разбор (ссылку уже сменили) игнорируется
    public void SetVlessDecoded(string link, Server? server, string? error)
    {
        if (link != VlessLink) return;
        if (server != null) { VlessHost = server.Host; VlessSummary = "Сервер: " + server.Summary; VlessLinkError = false; }
        else { VlessHost = ""; VlessSummary = error ?? "Ссылка не разбирается"; VlessLinkError = true; }
    }

    public ObservableCollection<SiteRow> Sites { get; } = new();
    public string? SiteError { get => siteError; private set => Set(ref siteError, value); }
    string? siteError;

    // Строки «Своих сайтов» по Settings.Sites
    public void SyncSites(IReadOnlyList<string> sites)
    {
        Sites.Clear();
        foreach (var s in sites) Sites.Add(new SiteRow(s, SiteEntry.Display(s)));
        Notify(nameof(DirectTabText));
    }

    // true — запись принята (страница очищает ввод); пустой ввод — не ошибка
    public bool RequestAddSite(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var entry = SiteEntry.Parse(text, out var error);
        if (entry == null) { SiteError = error; return false; }
        SiteError = null;
        AddSiteRequested?.Invoke(entry);
        return true;
    }
    public void RequestRemoveSite(string value) => RemoveSiteRequested?.Invoke(value);

    // Строки списков по включённым id из Settings: Enabled, и Covered у тех, что уже входят в Russia inside
    public void SyncLists(IReadOnlyList<string> enabled)
    {
        bool inside = enabled.Contains("russia_inside");
        foreach (var r in ListGroups.SelectMany(g => g.Rows))
        {
            r.Enabled = enabled.Contains(r.Id);
            r.Covered = r.InRussiaInside && inside;
        }
        Notify(nameof(DirectTabText));
    }
    public ObservableCollection<AppRow> ShownApps => directTab ? DirectApps : Apps;
    public string ProxyTabText => IsVless ? $"Приложения · {Apps.Count(a => a.Enabled)}" : $"Через прокси · {Apps.Count(a => a.Enabled)}";
    public string DirectTabText => IsVless
        ? $"Сайты · {ListGroups.Sum(g => g.Rows.Count(r => r.Enabled)) + Sites.Count}"
        : $"Мимо Podkop · {DirectApps.Count(a => a.Enabled)}";
    public string EmptyText => IsVless ? "Добавьте приложение, трафик которого пойдёт через VPN"
        : directTab
        ? "Добавьте приложение, трафик которого пойдёт мимо Podkop"
        : "Добавьте приложение, трафик которого пойдёт через прокси";
    public int? DirectPort { get => directPort; set { if (Set(ref directPort, value)) Notify(nameof(DirectHintVisible)); } }
    // Вкладка «Мимо Podkop» без порта: подсказать, где его задать
    public bool DirectHintVisible => !IsVless && directTab && directPort == null;
    // Выход списка не отвечает при подключении: программы идут обычным путём (ставит App по Session.Live)
    public bool ProxyDown { get => proxyDown; set { if (Set(ref proxyDown, value)) Notify(nameof(ExitDownHint)); } }
    public bool BypassDown { get => bypassDown; set { if (Set(ref bypassDown, value)) Notify(nameof(ExitDownHint)); } }
    public string? ExitDownHint =>
        IsVless ? null
        : directTab && bypassDown ? "Порт «Мимо Podkop» не отвечает: программы из этой вкладки сейчас идут обычным путём."
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

    // Строка под статусом в VLESS: «00:12:03 · 2 приложения · 3 списка · 1 сайт»; нулевые части не пишутся.
    public static string DetailVless(TimeSpan up, int apps, int lists, int sites)
    {
        var s = Format.Uptime(up < TimeSpan.Zero ? TimeSpan.Zero : up);
        if (apps > 0) s += " · " + Format.Plural(apps, "приложение", "приложения", "приложений");
        if (lists > 0) s += " · " + Format.Plural(lists, "список", "списка", "списков");
        if (sites > 0) s += " · " + Format.Plural(sites, "сайт", "сайта", "сайтов");
        return s;
    }

    public void RequestToggle() => ToggleRequested?.Invoke();
    public void RequestAddApp(string path) => AddAppRequested?.Invoke(path, directTab && !IsVless); // в VLESS «Сайты» — не список программ: всё в Apps
    public void RequestRemoveApp(string path) => RemoveAppRequested?.Invoke(path);
    public void RequestSetAppEnabled(string path, bool enabled) => SetAppEnabledRequested?.Invoke(path, enabled);
    // Ip/Port/DirectPort здесь — то, что сейчас в Settings (App держит их вместе). Без изменений события нет:
    // иначе заполнение полей страницы при старте (TextChanged приходит позже, асинхронно) перезаписывало бы settings.json.
    public void RequestSetProxy(string ip, int port, int? directPort)
    {
        if (ip == Ip && port == Port && directPort == DirectPort) return;
        SetProxyRequested?.Invoke(ip, port, directPort);
    }

    public void RequestSetMode(ProxyMode m) => SetModeRequested?.Invoke(m);
    // Без изменений события нет (как у RequestSetProxy): заполнение полей при старте не должно переписывать settings.json
    public void RequestSetVless(string link)
    {
        if (link == VlessLink) return;
        SetVlessRequested?.Invoke(link);
    }
    public void RequestSetListEnabled(string id, bool enabled)
    {
        var row = ListGroups.SelectMany(g => g.Rows).FirstOrDefault(r => r.Id == id);
        if (row == null || row.Covered) return; // уже входит в Russia inside: не переключается
        SetListEnabledRequested?.Invoke(id, enabled);
    }

    static void Push<T>(ObservableCollection<T> list, T value, int max)
    {
        list.Add(value);
        while (list.Count > max) list.RemoveAt(0);
    }
}

public sealed class ServerRow : Observable
{
    bool selected;
    string pingText = "", pingClass = "";
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public bool Supported { get; init; } = true;
    public string Reason { get; init; } = "";
    public bool Selected { get => selected; set => Set(ref selected, value); }
    public string PingText { get => pingText; private set => Set(ref pingText, value); }
    // "ok" < 100 мс, "warn" остальное, "bad" — нет ответа; "" — не мерили, "…" — меряют
    public string PingClass
    {
        get => pingClass;
        private set { if (Set(ref pingClass, value)) Notify(nameof(IsOk), nameof(IsWarn), nameof(IsBad)); }
    }
    public bool IsOk => pingClass == "ok";
    public bool IsWarn => pingClass == "warn";
    public bool IsBad => pingClass == "bad";
    public bool HasReason => Reason.Length > 0;
    public void SetPing(int? ms)
    {
        PingText = ms == null ? "нет ответа" : ms + " мс";
        PingClass = ms == null ? "bad" : ms < 100 ? "ok" : "warn";
    }
    string prevText = "", prevClass = "";
    public void SetPinging()
    {
        if (pingClass != "…") { prevText = pingText; prevClass = pingClass; }
        PingText = "…"; PingClass = "…";
    }
    public void ResetPinging()
    {
        if (pingClass != "…") return;
        PingText = prevText; PingClass = prevClass;
    }
}

public sealed class AppRow : Observable
{
    string name = "";
    Bitmap? icon;
    bool enabled = true;

    // Цвета плашки с буквой, если у программы нет иконки
    static readonly IBrush[] BadgeColors = new[] { "#5865f2", "#ea4335", "#1db954", "#2b88d8", "#f59e0b", "#8b5cf6", "#ec4899", "#14b8a6" }
        .Select(c => (IBrush)new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse(c))).ToArray();

    public string Path { get; init; } = "";
    public string FileName { get; init; } = "";
    public string Name
    {
        get => name;
        set { if (Set(ref name, value)) Notify(nameof(Letter)); }
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
}

public sealed record SiteRow(string Value, string Display);

public sealed record ListGroup(string Title, IReadOnlyList<ListRow> Rows);

public sealed class ListRow : Observable
{
    readonly ItdogList list;
    bool enabled, covered;

    public ListRow(ItdogList list) => this.list = list;

    public string Id => list.Id;
    public string Name => list.Name;
    public bool InRussiaInside => list.InRussiaInside;
    public string Note => covered ? "уже входит в Russia inside" : list.Note;
    public bool Enabled
    {
        get => enabled;
        set { if (Set(ref enabled, value)) Notify(nameof(Shown)); }
    }
    public bool Covered
    {
        get => covered;
        set { if (Set(ref covered, value)) Notify(nameof(Shown), nameof(Note)); }
    }
    public bool Shown => enabled || covered;
}
