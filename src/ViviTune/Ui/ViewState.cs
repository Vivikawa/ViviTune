using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
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
        set { if (Set(ref state, value)) Notify(nameof(StatusText), nameof(SettingsEditable)); }
    }

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

    public double UpBps { get => upBps; private set => Set(ref upBps, value); }
    public double DownBps { get => downBps; private set => Set(ref downBps, value); }
    public ObservableCollection<double> UpHistory { get; } = new();
    public ObservableCollection<double> DownHistory { get; } = new();

    public ObservableCollection<AppRow> Apps { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();

    public void PushSpeed(Speed s)
    {
        UpBps = s.UpBps;
        DownBps = s.DownBps;
        Push(UpHistory, s.UpBps, HistoryMax);
        Push(DownHistory, s.DownBps, HistoryMax);
    }

    public void AddLog(string line) => Push(LogLines, line, LogMax);

    public void RequestToggle() => ToggleRequested?.Invoke();
    public void RequestAddApp(string path) => AddAppRequested?.Invoke(path);
    public void RequestRemoveApp(string path) => RemoveAppRequested?.Invoke(path);
    public void RequestSetAppEnabled(string path, bool enabled) => SetAppEnabledRequested?.Invoke(path, enabled);
    public void RequestSetProxy(string ip, int port) => SetProxyRequested?.Invoke(ip, port);

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

    public string Path { get; init; } = "";
    public string FileName { get; init; } = "";
    public string Name { get => name; set => Set(ref name, value); }
    public Bitmap? Icon { get => icon; set => Set(ref icon, value); }
    public bool Enabled { get => enabled; set => Set(ref enabled, value); }

    void Set<T>(ref T field, T value, [CallerMemberName] string n = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
