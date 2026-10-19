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

// Итог проверки выходов: какие живы, предупреждения (без префикса), ошибка (ни одного живого выхода с программами),
// UDP прокси (null, если прокси не жив).
public sealed record Verdict(Exits Live, IReadOnlyList<string> Warnings, string? Error, bool? UdpOk);

// Подключение: проверка выходов (прокси и «Мимо Podkop»), запуск/перезапуск sing-box, ошибки, скорость.
// Состояние меняется только под gate; события вызываются вне gate.
// Start/Stop sing-box идут по одному под ops (ops берётся раньше gate; под gate sing-box не вызывается,
// поэтому Exited, пришедший из любого потока, не может зависнуть на gate).
// Проверка выходов (до ~3 с на выход) идёт вне всех блокировок; после неё под ops + gate заново смотрим
// attempt/ticket и состояние: за это время могли отключить или изменить список.
// Настройки копируются в потоке вызывающего (ConnectAsync/AppsChanged): живой Settings из фона не читается.
// Обработчики событий не должны блокироваться на UI-потоке (Post, а не Invoke).
public sealed class Session : IDisposable
{
    const string RestartMsg = "Перезапуск sing-box: список приложений изменён";
    const string CrashMsg = "sing-box завершился, прокси не работает";
    const string ReopenMsg = "Перезапустите выбранные приложения: уже открытые соединения остаются прямыми.";
    const string NoAppsMsg = "Отключено: нет включённых приложений";
    const string WarnPrefix = "ВНИМАНИЕ: ";
    const string NoUdpWarn = "UDP через этот прокси не работает: игры и голосовая связь могут не заработать.";
    const string BypassNoUdpWarn = "UDP через порт «Мимо Podkop» не работает: игры и голосовая связь в этих программах могут не заработать.";
    const string ProxyDownWarn = "Прокси недоступен ({0}): программы «Через прокси» идут обычным путём.";
    const string BypassDownWarn = "Порт «Мимо Podkop» недоступен ({0}): программы из этого списка идут обычным путём.";
    const string BypassNoPortWarn = "Порт «Мимо Podkop» не указан: программы из этого списка идут обычным путём.";

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
    Exits live;                               // выходы, с которыми запущен текущий sing-box
    IReadOnlyList<string> warnings = [];      // предупреждения последней проверки (список не меняется, только заменяется)
    DateTime? since;
    Settings? latest;   // последний снимок настроек
    Settings? applied;  // снимок, с которым запущен текущий sing-box
    TrafficMeter? meter;
    int attempt;        // меняется при Connect/Disconnect: устаревшая проверка выходов ничего не запускает
    int ticket;         // номер последнего AppsChanged: перезапуск делает только он
    int epoch;          // меняется при каждом входе в Connected
    bool disposed;

    public event Action? Changed;
    public event Action<string>? Log;
    public event Action<Speed>? Speed;

    public ConnState State { get { lock (gate) return state; } }
    public string? Error { get { lock (gate) return error; } }
    public bool? UdpOk { get { lock (gate) return udpOk; } }
    public IReadOnlyList<string> Warnings { get { lock (gate) return warnings; } }
    public Exits Live { get { lock (gate) return live; } }
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
        var snap = settings.Clone();
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
        var snap = settings.Clone();
        bool none = Needs(snap) == (false, false), entered = false, schedule = false;
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
        Settings snap;
        lock (gate) snap = latest!;
        while (true)
        {
            var v = CheckExits(snap); // вне блокировок: Disconnect и AppsChanged в это время не ждут

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
                    // Список изменили во время проверки. Адрес и порты при подключении не меняются, поэтому итог
                    // годится и для нового снимка, если непусты те же списки; иначе проверяем выходы заново.
                    if (Needs(used) != Needs(snap)) { snap = used; continue; }
                    fail = v.Error;
                    if (fail != null) ToDisconnected(fail);
                }
                if (fail == null)
                {
                    TrafficMeter? m = null;
                    try { m = StartSingBox(used, v.Live); }
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
                            live = v.Live;
                            warnings = v.Warnings;
                            udpOk = v.UdpOk;
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
            Raise(false, WithWarnings(ReopenMsg, v));
            if (restart) ScheduleRestart(t); // перезапуск проверит выходы заново
            else OnExited(); // sing-box мог упасть сразу после запуска, пока мы были в Checking
            return;
        }
    }

    void ScheduleRestart(int t) =>
        Task.Delay(restartDelay).ContinueWith(_ => Restart(t), TaskScheduler.Default);

    void Restart(int t)
    {
        if (KeepRunning(t)) return;
        Settings snap;
        lock (gate)
        {
            if (t != ticket || state != ConnState.Restarting) return; // есть изменение новее или уже отключены
            snap = latest!;
        }
        // Вне блокировок: старый sing-box пока работает, AppsChanged и Disconnect в это время не ждут.
        var v = CheckExits(snap);

        string? fail = v.Error;
        bool connected = false;
        lock (ops)
        {
            TrafficMeter? old;
            lock (gate)
            {
                // пока проверяли, пришло изменение новее (его перезапуск проверит заново) или отключили
                if (t != ticket || state != ConnState.Restarting) return;
                if (fail != null) old = ToDisconnected(fail); // ни одного живого выхода: останавливаем, как Disconnect
                else { old = meter; meter = null; }
            }
            old?.Dispose();
            sb.Stop(); // Exited отсюда приходит в Restarting или Disconnected и не считается ошибкой
            if (fail == null)
            {
                TrafficMeter? m = null;
                try { m = StartSingBox(snap, v.Live); }
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
                        live = v.Live;
                        warnings = v.Warnings;
                        udpOk = v.UdpOk;
                        if (t == ticket) { state = ConnState.Connected; epoch++; connected = true; } // иначе впереди ещё перезапуск
                    }
                }
                if (!keep || fail != null) { m?.Dispose(); sb.Stop(); }
                if (!keep) return;
            }
        }
        if (fail != null) Raise(false, RestartMsg, fail);
        else Raise(false, WithWarnings(RestartMsg, v));
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

    // Те же имена .exe в обоих списках (без учёта регистра и порядка) — те же правила sing-box.
    // Выходы при этом заново не проверяются.
    static bool SameNames(Settings a, Settings b) =>
        Same(a.ProcessNames(), b.ProcessNames()) && Same(a.BypassNames(), b.BypassNames());

    static bool Same(List<string> a, List<string> b) => a.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(b);

    // Какие списки непусты (включённые программы): «Через прокси» и «Мимо Podkop».
    static (bool Proxy, bool Bypass) Needs(Settings s) => (s.ProcessNames().Count > 0, s.BypassNames().Count > 0);

    // Проверяет только нужные выходы снимка: прокси (Ip, Port) — при программах «Через прокси», «Мимо Podkop»
    // (Ip, DirectPort) — при её программах и указанном порте. Оба сразу, до ~3 с каждый: вызывать вне блокировок.
    Verdict CheckExits(Settings snap)
    {
        var (needProxy, needBypass) = Needs(snap);
        var bypass = needBypass && snap.DirectPort is int dp ? Task.Run(() => SafeProbe(snap.Ip, dp)) : null;
        var proxy = needProxy ? SafeProbe(snap.Ip, snap.Port) : null;
        return Evaluate(snap, proxy, bypass?.Result);
    }

    ProbeResult SafeProbe(string ip, int port)
    {
        try { return probe(ip, port); }
        catch (Exception e) { return new ProbeResult { Error = e.Message }; }
    }

    // proxy/bypass — null, если этот выход не проверялся. Выход жив, только если его список непуст и он ответил.
    public static Verdict Evaluate(Settings s, ProbeResult? proxy, ProbeResult? bypass)
    {
        var (needProxy, needBypass) = Needs(s);
        bool proxyLive = needProxy && proxy?.Reachable == true;
        bool bypassLive = needBypass && s.DirectPort != null && bypass?.Reachable == true;
        if (!proxyLive && !bypassLive)
        {
            var err = needProxy ? "Прокси недоступен: " + proxy?.Error
                : s.DirectPort == null ? "Порт «Мимо Podkop» не указан"
                : "Порт «Мимо Podkop» недоступен: " + bypass?.Error;
            return new Verdict(default, [], err, null);
        }
        var w = new List<string>();
        if (needProxy && !proxyLive) w.Add(string.Format(ProxyDownWarn, proxy?.Error));
        if (needBypass && s.DirectPort == null) w.Add(BypassNoPortWarn);
        if (needBypass && s.DirectPort != null && !bypassLive) w.Add(string.Format(BypassDownWarn, bypass?.Error));
        if (proxyLive && !proxy!.UdpSupported) w.Add(NoUdpWarn);
        if (bypassLive && !bypass!.UdpSupported) w.Add(BypassNoUdpWarn);
        return new Verdict(new Exits(proxyLive, bypassLive), w, null, proxyLive ? proxy!.UdpSupported : null);
    }

    // Строка лога, за ней предупреждения с префиксом.
    static string?[] WithWarnings(string msg, Verdict v) => [msg, .. v.Warnings.Select(w => WarnPrefix + w)];

    TrafficMeter StartSingBox(Settings snap, Exits exits)
    {
        int port = FreePort();
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        File.WriteAllText(configPath, ConfigBuilder.Build(snap, port, secret, exits));
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
        live = default;
        warnings = [];
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

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try { return ((IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }
}
