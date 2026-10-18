using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace ViviTune;

public interface ISingBox
{
    event Action<string>? Line;
    event Action? Exited;
    bool Running { get; }
    void Start(string configPath);
    void Stop();
}

public enum ConnState { Disconnected, Checking, Connected, Restarting }

// Подключение: проверка прокси, запуск/перезапуск sing-box, ошибки, скорость.
// Состояние меняется только под gate; события вызываются вне gate.
// Start/Stop sing-box идут по одному под ops (ops берётся раньше gate; под gate sing-box не вызывается,
// поэтому Exited, пришедший из любого потока, не может зависнуть на gate).
// Настройки копируются в потоке вызывающего (ConnectAsync/AppsChanged): живой Settings из фона не читается.
// Обработчики событий не должны блокироваться на UI-потоке (Post, а не Invoke).
public sealed class Session : IDisposable
{
    const string RestartMsg = "Перезапуск sing-box: список приложений изменён";
    const string CrashMsg = "sing-box завершился, прокси не работает";
    const string ReopenMsg = "Перезапустите выбранные приложения: уже открытые соединения остаются прямыми.";
    const string NoUdpMsg = "ВНИМАНИЕ: UDP через этот прокси не работает: игры и голосовая связь могут не заработать.";
    const string NoAppsMsg = "Отключено: нет включённых приложений";

    readonly Settings settings;
    readonly ISingBox sb;
    readonly string configPath;
    readonly Func<string, int, ProbeResult> probe;
    readonly TimeSpan restartDelay;

    readonly object gate = new();
    readonly object ops = new();
    readonly object speedGate = new(); // Speed(0,0) при уходе из Connected всегда последним

    ConnState state;
    string? error;
    bool? udpOk;
    DateTime? since;
    Settings? latest;   // последний снимок настроек
    Settings? applied;  // снимок, с которым запущен текущий sing-box
    TrafficMeter? meter;
    int attempt;        // меняется при Connect/Disconnect: устаревшая проверка прокси ничего не запускает
    int ticket;         // номер последнего AppsChanged: перезапуск делает только он
    int epoch;          // меняется при каждом входе в Connected
    bool disposed;

    public event Action? Changed;
    public event Action<string>? Log;
    public event Action<Speed>? Speed;

    public ConnState State { get { lock (gate) return state; } }
    public string? Error { get { lock (gate) return error; } }
    public bool? UdpOk { get { lock (gate) return udpOk; } }
    public DateTime? ConnectedSince { get { lock (gate) return since; } }

    public Session(Settings settings, ISingBox singBox, string configPath, Func<string, int, ProbeResult> probe, TimeSpan restartDelay)
    {
        this.settings = settings;
        sb = singBox;
        this.configPath = configPath;
        this.probe = probe;
        this.restartDelay = restartDelay;
        sb.Line += OnLine;
        sb.Exited += OnExited;
    }

    public Task ConnectAsync()
    {
        var snap = Snapshot(settings);
        int a = 0;
        lock (gate)
        {
            if (disposed || state != ConnState.Disconnected) return Task.CompletedTask;
            error = snap.Validate();
            if (error == null)
            {
                state = ConnState.Checking;
                latest = snap;
                a = ++attempt;
            }
        }
        Raise(false);
        return a == 0 ? Task.CompletedTask : Task.Run(() => Connect(a));
    }

    public void Disconnect() => Disconnect(null);

    void Disconnect(string? log)
    {
        TrafficMeter? m;
        lock (gate)
        {
            attempt++;
            ticket++;
            m = ToDisconnected(null);
        }
        m?.Dispose();
        lock (ops) sb.Stop();
        Raise(true, log);
    }

    public void AppsChanged()
    {
        var snap = Snapshot(settings);
        bool none = snap.ProcessNames().Count == 0, entered = false, schedule = false;
        int t = 0;
        lock (gate)
        {
            if (disposed || state == ConnState.Disconnected) return;
            if (!none)
            {
                latest = snap; // при Checking его подхватит Connect
                // правила те же, что у запущенного sing-box (убран/переключён выключенный, дубль имени): соединения не рвём
                if (state == ConnState.Connected && SameNames(snap, applied!)) return;
                if (state != ConnState.Checking)
                {
                    t = ++ticket;
                    schedule = true;
                    if (state == ConnState.Connected) { state = ConnState.Restarting; entered = true; }
                }
            }
        }
        if (none) { Disconnect(NoAppsMsg); return; } // правило без приложений матчило бы весь трафик
        if (entered) Raise(true);
        if (schedule) ScheduleRestart(t);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
        }
        Disconnect();
        sb.Line -= OnLine;
        sb.Exited -= OnExited;
    }

    void Connect(int a)
    {
        string ip;
        int port;
        lock (gate) { ip = latest!.Ip; port = latest.Port; }
        ProbeResult r;
        try { r = probe(ip, port); }
        catch (Exception e) { r = new ProbeResult { Error = e.Message }; }

        string? fail = null;
        bool restart = false;
        int t = 0;
        lock (ops)
        {
            Settings used;
            lock (gate)
            {
                if (a != attempt || state != ConnState.Checking) return; // отключили во время проверки
                used = latest!;
                if (!r.Reachable)
                {
                    fail = "Прокси недоступен: " + r.Error;
                    ToDisconnected(fail);
                }
            }
            if (fail == null)
            {
                TrafficMeter? m = null;
                try { m = StartSingBox(used); }
                catch (Exception e) { fail = "Не удалось запустить: " + e.Message; }
                bool keep;
                lock (gate)
                {
                    keep = a == attempt && state == ConnState.Checking;
                    if (keep && fail != null) ToDisconnected(fail);
                    else if (keep)
                    {
                        meter = m;
                        applied = used;
                        udpOk = r.UdpSupported;
                        since = DateTime.Now;
                        restart = !ReferenceEquals(used, latest); // список изменили, пока запускались
                        if (restart) { state = ConnState.Restarting; t = ++ticket; }
                        else { state = ConnState.Connected; epoch++; }
                    }
                }
                if (!keep || fail != null) { m?.Dispose(); sb.Stop(); }
                if (!keep) return;
            }
        }
        if (fail != null) { Raise(false, fail); return; }
        Raise(false, ReopenMsg, r.UdpSupported ? null : NoUdpMsg);
        if (restart) ScheduleRestart(t);
        else OnExited(); // sing-box мог упасть сразу после запуска, пока мы были в Checking
    }

    void ScheduleRestart(int t) =>
        Task.Delay(restartDelay).ContinueWith(_ => Restart(t), TaskScheduler.Default);

    void Restart(int t)
    {
        if (KeepRunning(t)) return;
        string? fail = null;
        bool connected = false;
        lock (ops)
        {
            Settings snap;
            TrafficMeter? old;
            lock (gate)
            {
                if (t != ticket || state != ConnState.Restarting) return; // есть изменение новее или уже отключены
                snap = latest!;
                old = meter;
                meter = null;
            }
            old?.Dispose();
            sb.Stop(); // Exited отсюда приходит в Restarting и не считается ошибкой
            TrafficMeter? m = null;
            try { m = StartSingBox(snap); }
            catch (Exception e) { fail = "Не удалось запустить: " + e.Message; }
            bool keep;
            lock (gate)
            {
                // в Restarting вернуться, пока мы держим ops, нельзя: значит, Disconnect не вызывали
                keep = state == ConnState.Restarting;
                if (keep && fail != null) ToDisconnected(fail);
                else if (keep)
                {
                    meter = m;
                    applied = snap;
                    if (t == ticket) { state = ConnState.Connected; epoch++; connected = true; } // иначе впереди ещё перезапуск
                }
            }
            if (!keep || fail != null) { m?.Dispose(); sb.Stop(); }
            if (!keep) return;
        }
        Raise(false, RestartMsg, fail);
        if (connected) OnExited();
    }

    // Набор вернули к тому, с которым sing-box уже запущен (выключили и включили обратно): не перезапускаем,
    // соединения не рвутся. Под ops: перезапуск, который ещё идёт, сначала закончится и обновит applied.
    bool KeepRunning(int t)
    {
        lock (ops)
            lock (gate)
            {
                if (t != ticket || state != ConnState.Restarting || !SameNames(latest!, applied!)) return false;
                state = ConnState.Connected;
                epoch++;
            }
        Raise(false);
        OnExited(); // sing-box мог упасть, пока мы были в Restarting
        return true;
    }

    // Те же имена .exe (без учёта регистра и порядка) — тот же конфиг для правил sing-box.
    static bool SameNames(Settings a, Settings b) =>
        a.ProcessNames().ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(b.ProcessNames());

    TrafficMeter StartSingBox(Settings snap)
    {
        int port = FreePort();
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        File.WriteAllText(configPath, ConfigBuilder.Build(snap, port, secret));
        sb.Start(configPath);
        var m = new TrafficMeter(port, secret);
        m.Sample += s => OnSample(m, s);
        m.Start();
        return m;
    }

    // Также проверка «жив ли sing-box» сразу после входа в Connected.
    void OnExited()
    {
        int e;
        lock (gate)
        {
            if (state != ConnState.Connected) return; // Stop/перезапуск/отключение: выход ожидаемый
            e = epoch;
        }
        bool alive;
        try { alive = sb.Running; }
        catch { alive = false; }
        if (alive) return; // запоздавшее событие от прежнего процесса
        TrafficMeter? m;
        lock (gate)
        {
            if (state != ConnState.Connected || epoch != e) return;
            m = ToDisconnected(CrashMsg);
        }
        m?.Dispose();
        Raise(true, CrashMsg);
    }

    void OnLine(string line) => Log?.Invoke(line);

    void OnSample(TrafficMeter m, Speed s)
    {
        lock (speedGate)
        {
            lock (gate)
                if (meter != m || state != ConnState.Connected) return;
            Speed?.Invoke(s);
        }
    }

    // под gate; возвращает счётчик скорости, который надо остановить вне gate
    TrafficMeter? ToDisconnected(string? err)
    {
        var m = meter;
        meter = null;
        state = ConnState.Disconnected;
        error = err;
        udpOk = null;
        since = null;
        return m;
    }

    void Raise(bool zeroSpeed, params string?[] logs)
    {
        foreach (var l in logs)
            if (l != null) Log?.Invoke(l);
        Changed?.Invoke();
        if (zeroSpeed)
            lock (speedGate) Speed?.Invoke(new Speed(0, 0));
    }

    static Settings Snapshot(Settings s) => new()
    {
        Ip = s.Ip,
        Port = s.Port,
        Apps = s.Apps.Select(a => new AppEntry { Path = a.Path, Enabled = a.Enabled }).ToList()
    };

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try { return ((IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }
}
