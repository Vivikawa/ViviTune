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

// Внешние действия режима VLESS: App даёт настоящие, тесты — подмены.
public sealed class VlessDeps
{
    public required Func<string, IPAddress?> Resolve { get; init; }        // имя -> IP; null — не нашлось
    public required Func<IPAddress, int, string?> Reach { get; init; }     // TCP до сервера: null — ответил, иначе причина
    public required Func<IReadOnlyList<string>, ListsResult> PrepareLists { get; init; }
    // после запуска sing-box: (порт Clash API, секрет, отмена) -> null или текст ошибки для пользователя
    public required Func<int, string, CancellationToken, string?> AfterStart { get; init; }
}

// Проверка после запуска sing-box не прошла: текст для пользователя как есть, без «Не удалось запустить: ».
public sealed class CheckFailedException(string message) : Exception(message);

// Что нужно для запуска в VLESS: заранее разрешённый IP сервера и копии списков (id -> файл .srs).
public sealed record VlessPrep(IPAddress Server, IReadOnlyDictionary<string, string> ListFiles);

// Итог проверки выходов: какие живы, предупреждения (без префикса), ошибка (ни одного живого выхода с программами),
// UDP прокси (null, если прокси не жив); Vless — только в режиме VLESS.
public sealed record Verdict(Exits Live, IReadOnlyList<string> Warnings, string? Error, bool? UdpOk, VlessPrep? Vless = null);

// Подключение: проверка выходов (прокси и «Мимо Podkop»; в VLESS — сервер и списки), запуск/перезапуск sing-box,
// ошибки, скорость.
// Состояние меняется только под gate; события вызываются вне gate.
// Start/Stop sing-box идут по одному под ops (ops берётся раньше gate; под gate sing-box не вызывается,
// поэтому Exited, пришедший из любого потока, не может зависнуть на gate).
// Проверка выходов (до ~3 с на выход) идёт вне всех блокировок; после неё под ops + gate заново смотрим
// attempt/ticket и состояние: за это время могли отключить или изменить список.
// В VLESS проверка после запуска (сервер и DNS через Clash API) идёт под ops, между Start и счётчиком скорости;
// её отмену (cts) берём под gate вместе с attempt/ticket, Disconnect отменяет её до того, как ждать ops.
// Настройки копируются в потоке вызывающего (ConnectAsync/AppsChanged): живой Settings из фона не читается.
// Обработчики событий не должны блокироваться на UI-потоке (Post, а не Invoke).
public sealed class Session : IDisposable
{
    const string RestartMsg = "Перезапуск sing-box: список приложений изменён";
    const string VlessRestartMsg = "Перезапуск sing-box: приложения или списки изменены";
    const string CrashMsg = "sing-box завершился, прокси не работает";
    const string ReopenMsg = "Перезапустите выбранные приложения: уже открытые соединения остаются прямыми.";
    const string NoAppsMsg = "Отключено: нет включённых приложений";
    const string VlessNoRulesMsg = "Отключено: нет включённых приложений и списков";
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
    readonly VlessDeps? vless; // null — режим VLESS недоступен

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
    CancellationTokenSource cts = new(); // отмена проверки после запуска; Disconnect отменяет и заменяет новым
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

    public Session(Settings settings, ISingBox singBox, string configPath, Func<string, int, ProbeResult> probe, TimeSpan restartDelay, VlessDeps? vless = null)
    {
        this.settings = settings;
        sb = singBox;
        this.configPath = configPath;
        this.probe = probe;
        this.restartDelay = restartDelay;
        this.vless = vless;
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
        CancellationTokenSource check;
        lock (gate)
        {
            attempt++;
            ticket++;
            m = ToDisconnected(null);
            check = cts;
            cts = new();
        }
        // ponytail: проверка идёт под ops, поэтому Disconnect ждёт её; отмена держит ожидание коротким.
        check.Cancel(); // вне gate: колбэки отмены — код проверки
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
                if (state == ConnState.Connected && SameRules(snap, applied!)) return;
                if (state != ConnState.Checking)
                {
                    t = ++ticket;
                    schedule = true;
                    if (state == ConnState.Connected) { state = ConnState.Restarting; entered = true; }
                }
            }
        }
        if (none) { Disconnect(snap.Mode == ProxyMode.Vless ? VlessNoRulesMsg : NoAppsMsg); return; } // правило без приложений матчило бы весь трафик
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
                CancellationToken ct;
                lock (gate)
                {
                    if (a != attempt || state != ConnState.Checking) return; // отключили во время проверки
                    used = latest!;
                    // Список изменили во время проверки. Адрес и порты при подключении не меняются, поэтому итог
                    // годится и для нового снимка, если от него зависит то же (SameChecks); иначе проверяем заново.
                    if (!SameChecks(used, snap)) { snap = used; continue; }
                    fail = v.Error;
                    if (fail != null) ToDisconnected(fail);
                    ct = cts.Token; // вместе с attempt: Disconnect после этой точки отменит именно её
                }
                if (fail == null)
                {
                    TrafficMeter? m = null;
                    try { m = StartSingBox(used, v, ct); }
                    catch (CheckFailedException e) { fail = e.Message; }
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
        var msg = snap.Mode == ProxyMode.Vless ? VlessRestartMsg : RestartMsg;

        string? fail = v.Error;
        bool connected = false;
        lock (ops)
        {
            TrafficMeter? old;
            CancellationToken ct;
            lock (gate)
            {
                // пока проверяли, пришло изменение новее (его перезапуск проверит заново) или отключили
                if (t != ticket || state != ConnState.Restarting) return;
                if (fail != null) old = ToDisconnected(fail); // ни одного живого выхода: останавливаем, как Disconnect
                else { old = meter; meter = null; }
                ct = cts.Token; // вместе с ticket: Disconnect после этой точки отменит именно её
            }
            old?.Dispose();
            sb.Stop(); // Exited отсюда приходит в Restarting или Disconnected и не считается ошибкой
            if (fail == null)
            {
                TrafficMeter? m = null;
                try { m = StartSingBox(snap, v, ct); }
                catch (CheckFailedException e) { fail = e.Message; }
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
        if (fail != null) Raise(false, msg, fail);
        else Raise(false, WithWarnings(msg, v));
        if (connected) OnExited();
    }

    // Набор вернули к тому, с которым sing-box уже запущен (выключили и включили обратно): не перезапускаем,
    // соединения не рвутся. Под ops: перезапуск, который ещё идёт, сначала закончится и обновит applied.
    bool KeepRunning(int t)
    {
        lock (ops)
            lock (gate)
            {
                if (t != ticket || state != ConnState.Restarting || !SameRules(latest!, applied!)) return false;
                state = ConnState.Connected;
                epoch++;
            }
        Raise(false);
        OnExited(); // sing-box мог упасть, пока мы были в Restarting
        return true;
    }

    // Те же имена .exe в обоих списках программ и тот же набор списков сайтов (без учёта регистра и порядка) —
    // те же правила sing-box. Выходы при этом заново не проверяются.
    static bool SameRules(Settings a, Settings b) =>
        Same(a.ProcessNames(), b.ProcessNames()) && Same(a.BypassNames(), b.BypassNames()) && Same(a.Lists, b.Lists);

    static bool Same(List<string> a, List<string> b) => a.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(b);

    // Какие списки непусты: в SOCKS5 — включённые программы «Через прокси» и «Мимо Podkop»;
    // в VLESS выход один, и ему нужны программы или списки сайтов.
    static (bool Proxy, bool Bypass) Needs(Settings s) => s.Mode == ProxyMode.Vless
        ? (s.ProcessNames().Count > 0 || s.Lists.Count > 0, false)
        : (s.ProcessNames().Count > 0, s.BypassNames().Count > 0);

    // Итог проверки снимка b годится для снимка a: непусты те же списки программ, а в VLESS ещё тот же набор
    // списков сайтов (их копии готовились для b) и программы есть в обоих или ни в одном (без них нужен хоть один файл).
    static bool SameChecks(Settings a, Settings b) =>
        a.Mode == b.Mode && Needs(a) == Needs(b) && (a.Mode != ProxyMode.Vless
            || Same(a.Lists, b.Lists) && (a.ProcessNames().Count > 0) == (b.ProcessNames().Count > 0));

    // Проверяет только нужные выходы снимка: прокси (Ip, Port) — при программах «Через прокси», «Мимо Podkop»
    // (Ip, DirectPort) — при её программах и указанном порте. Оба сразу, до ~3 с каждый: вызывать вне блокировок.
    Verdict CheckExits(Settings snap)
    {
        if (snap.Mode == ProxyMode.Vless) return CheckVless(snap);
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

    // VLESS до запуска: IP сервера (системным DNS, туннеля ещё нет), TCP до него, копии списков. TCP до ~3 с,
    // списки качаются с GitHub: вызывать вне блокировок. Исключения подмен наружу не выходят (иначе Connect
    // или Restart оборвался бы и состояние осталось бы в Checking/Restarting).
    Verdict CheckVless(Settings snap)
    {
        if (vless == null) return ErrorVerdict("Режим VLESS недоступен");
        if (VlessServer.Parse(snap.VlessLink, out var linkError) is not { } srv) return ErrorVerdict(linkError!); // Validate уже прошёл
        if (!IPAddress.TryParse(srv.Host, out var ip))
        {
            try { ip = vless.Resolve(srv.Host); }
            catch { ip = null; }
        }
        if (ip == null) return ErrorVerdict("VLESS-сервер не найден: " + srv.Host);
        string? down;
        try { down = vless.Reach(ip, srv.Port); }
        catch (Exception e) { down = e.Message; }
        if (down != null) return ErrorVerdict("VLESS-сервер не отвечает: " + down);

        var lists = new ListsResult(new Dictionary<string, string>(), [], []);
        if (snap.Lists.Count > 0)
        {
            try { lists = vless.PrepareLists(snap.Lists); }
            catch { lists = lists with { Failed = snap.Lists.ToList() }; } // не смогли даже начать: ни один не скачался
        }
        foreach (var id in lists.Stale) Log?.Invoke($"Список {ListName(id)} не обновился, работаю со старой копией");
        return EvaluateVless(snap, ip, lists);
    }

    // Сервер VLESS ответил. Не загрузившийся список — предупреждение; нет ни программ, ни одного файла списка —
    // ошибка: правил для sing-box нет. Предупреждения — в порядке lists.Failed.
    public static Verdict EvaluateVless(Settings s, IPAddress server, ListsResult lists)
    {
        if (s.ProcessNames().Count == 0 && lists.Files.Count == 0) return ErrorVerdict("Списки не загрузились");
        var w = lists.Failed.Select(id => $"Список {ListName(id)} не загрузился").ToList();
        return new Verdict(new Exits(true, false), w, null, null, new VlessPrep(server, lists.Files));
    }

    static Verdict ErrorVerdict(string error) => new(default, [], error, null);

    static string ListName(string id) => Catalogs.ListById(id)?.Name ?? id;

    // Строка лога, за ней предупреждения с префиксом.
    static string?[] WithWarnings(string msg, Verdict v) => [msg, .. v.Warnings.Select(w => WarnPrefix + w)];

    // Вызывать под ops. В VLESS после запуска — проверка сервера и DNS через Clash API (ct отменяет её при Disconnect);
    // не прошла — CheckFailedException, sing-box останавливает вызывающий. Счётчик скорости — только после неё.
    TrafficMeter StartSingBox(Settings snap, Verdict v, CancellationToken ct)
    {
        int port = FreePort();
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        File.WriteAllText(configPath, v.Vless is { } p
            ? ConfigBuilder.BuildVless(snap, port, secret, p.Server, p.ListFiles)
            : ConfigBuilder.Build(snap, port, secret, v.Live));
        sb.Start(configPath);
        if (v.Vless != null && vless!.AfterStart(port, secret, ct) is { } err)
            throw new CheckFailedException(err);
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
