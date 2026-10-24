using System.Net;
using System.Text.Json.Nodes;
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
    public required Func<string, (Server? Server, string? Error)> Decode { get; init; } // ссылка -> сервер от движка или текст ошибки
    public required Func<IReadOnlyList<string>, JsonObject, ListsResult> PrepareLists { get; init; } // (id списков, готовый outbound proxy)
    // после запуска sing-box: (порт Clash API, секрет, отмена) -> null или текст ошибки для пользователя
    public required Func<int, string, CancellationToken, string?> AfterStart { get; init; }
    // подписка: url -> кэш с диска; (url, прежний кэш) -> новый кэш (уже сохранённый) или текст ошибки
    public required Func<string, SubCache?> LoadSub { get; init; }
    public required Func<string, SubCache?, (SubCache? Cache, string? Error)> FetchSub { get; init; }
    public required Func<int, string, IClashApi> Api { get; init; }       // (порт, секрет) -> клиент Clash API
    public int PingMs { get; init; } = 3000;                              // таймаут одного пинга
    public TimeSpan PingCap { get; init; } = TimeSpan.FromSeconds(20);    // сколько всего пинговать при автовыборе
    public TimeSpan WatchEvery { get; init; } = TimeSpan.FromSeconds(30); // как часто проверять текущий сервер подписки
}

// Проверка после запуска sing-box не прошла: текст для пользователя как есть, без «Не удалось запустить: ».
public sealed class CheckFailedException(string message) : Exception(message);

// Что нужно для запуска в VLESS: заранее разрешённые IP серверов, их outbound'ы и копии списков (id -> файл .srs).
// Одиночная ссылка: один outbound с тегом proxy, Selected null. Подписка: теги s0…sN, Selected — выбранный, Keys — тег -> ключ сервера.
public sealed record VlessPrep(IReadOnlyList<IPAddress> Ips, IReadOnlyList<JsonObject> Outbounds, string? Selected,
    IReadOnlyDictionary<string, string> ListFiles, IReadOnlyDictionary<string, string> Keys)
{
    // outbound для скачивания списков: одиночный как есть, у подписки — копия выбранного с тегом proxy
    public JsonObject FetchProxy
    {
        get
        {
            if (Selected == null) return Outbounds[0];
            var o = (JsonObject)Outbounds.First(x => (string?)x["tag"] == Selected).DeepClone();
            o["tag"] = "proxy";
            return o;
        }
    }
}

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
// Подписка: сервер, Clash API и пинги (currentServer, api, pings) тоже под gate; автовыбор идёт в StartSingBox под ops
// (отмена — cts, как у AfterStart), SelectServer и PingConnected ходят в Clash API вне блокировок и сверяют epoch.
// Проверка текущего сервера (ServerWatch) живёт вместе со счётчиком скорости, только в подписке; её события идут вне gate.
// Все пинги Clash API (PingConnected, проверка и автопереключение watch) идут через pingGate: по одному.
// Настройки копируются в потоке вызывающего (ConnectAsync/AppsChanged): живой Settings из фона не читается.
// Обработчики событий не должны блокироваться на UI-потоке (Post, а не Invoke).
public sealed class Session : IDisposable
{
    const string RestartMsg = "Перезапуск sing-box: список приложений изменён";
    const string CrashMsg = "sing-box завершился, прокси не работает";
    const string ReopenMsg = "Перезапустите выбранные приложения: уже открытые соединения остаются прямыми.";
    const string NoAppsMsg = "Отключено: нет включённых приложений";
    const string VlessNoRulesMsg = "Отключено: нет включённых приложений, сайтов и списков";
    const string HotMsg = "Правила обновлены без перезапуска";
    const string WarnPrefix = "ВНИМАНИЕ: ";
    const string NoUdpWarn = "UDP через этот прокси не работает: игры и голосовая связь могут не заработать.";
    const string BypassNoUdpWarn = "UDP через порт «Мимо Podkop» не работает: игры и голосовая связь в этих программах могут не заработать.";
    const string ProxyDownWarn = "Прокси недоступен ({0}): программы «Через прокси» идут обычным путём.";
    const string BypassDownWarn = "Порт «Мимо Podkop» недоступен ({0}): программы из этого списка идут обычным путём.";
    const string BypassNoPortWarn = "Порт «Мимо Podkop» не указан: программы из этого списка идут обычным путём.";

    readonly Settings settings;
    readonly ISingBox sb;
    readonly string configPath;
    readonly string rulesDir;
    readonly Func<string, int, ProbeResult> probe;
    readonly TimeSpan restartDelay;
    readonly VlessDeps? vless; // null — режим VLESS недоступен

    readonly object gate = new();
    readonly object ops = new();
    readonly object speedGate = new(); // Speed(0,0) при уходе из Connected всегда последним

    ConnState state;
    string? error;
    bool? udpOk;
    VlessPrep? prep;                          // с чем работает sing-box в VLESS: сервер и копии списков (заменяется целиком)
    Exits live;                               // выходы, с которыми запущен текущий sing-box
    IReadOnlyList<string> warnings = [];      // предупреждения последней проверки (список не меняется, только заменяется)
    DateTime? since;
    Settings? latest;   // последний снимок настроек
    Settings? applied;  // снимок, с которым запущен текущий sing-box
    TrafficMeter? meter;
    int attempt;        // меняется при Connect/Disconnect: устаревшая проверка выходов ничего не запускает
    int ticket;         // номер последнего AppsChanged: перезапуск или горячее применение делает только он
    int epoch;          // меняется при каждом входе в Connected
    CancellationTokenSource cts = new(); // отмена проверки после запуска; Disconnect отменяет и заменяет новым
    bool disposed;
    string? currentServer;                    // подписка: ключ сервера, на котором работает sing-box
    IClashApi? api;                           // подписка: клиент Clash API запущенного sing-box
    ServerWatch? watch;                       // подписка: проверка текущего сервера
    readonly SemaphoreSlim pingGate = new(1, 1);
    IReadOnlyDictionary<string, int?> pings = new Dictionary<string, int?>(); // ключ -> последний пинг; заменяется целиком

    public event Action? Changed;
    public event Action<string>? Log;
    public event Action<Speed>? Speed;
    public event Action<string>? ServerChanged; // ключ текущего сервера подписки (вне gate)
    public event Action? PingsChanged;          // вне gate
    public event Action<string, string?, int?>? ServerDown;   // (ключ текущего, ключ лучшего или null, его пинг); вне gate
    public event Action<string, string, int>? ServerSwitched; // (было, стало, пинг): сервер переключил watch; вне gate
    public string? CurrentServer { get { lock (gate) return currentServer; } }
    public IReadOnlyDictionary<string, int?> Pings { get { lock (gate) return pings; } }

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
        rulesDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath))!, "rules");
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
        bool none = Needs(snap) == (false, false), entered = false, schedule = false, hot = false;
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
                    // те же выходы: правила лежат в файлах, sing-box перечитает их сам
                    if (state == ConnState.Connected && SameExits(snap, applied!)) hot = true;
                    else
                    {
                        schedule = true;
                        if (state == ConnState.Connected) { state = ConnState.Restarting; entered = true; }
                    }
                }
            }
        }
        if (none) { Disconnect(snap.Mode == ProxyMode.Vless ? VlessNoRulesMsg : NoAppsMsg); return; } // правило без приложений матчило бы весь трафик
        if (entered) Raise(true);
        if (hot) HotApply(t); // в потоке вызывающего: App вызывает по одному, скачивание списка здесь допустимо
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
            var v = CheckExits(snap, null, true); // вне блокировок: Disconnect и AppsChanged в это время не ждут

            string? fail = null, srvKey = null;
            bool restart = false, hot = false;
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
                    Started? st = null;
                    TrafficMeter? m = null;
                    try { st = StartSingBox(used, v, ct, true); m = st.Meter; }
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
                            prep = Adopt(st!, v.Vless);
                            srvKey = currentServer;
                            // список изменили, пока запускались: те же правила — ничего; те же выходы — файлы; иначе перезапуск
                            bool changed = !ReferenceEquals(used, latest) && !SameRules(used, latest!);
                            hot = changed && SameExits(latest!, used);
                            restart = changed && !hot;
                            if (restart) { state = ConnState.Restarting; t = ++ticket; }
                            else { state = ConnState.Connected; epoch++; if (hot) t = ++ticket; }
                        }
                    }
                    if (!keep || fail != null) { m?.Dispose(); (st?.Api as IDisposable)?.Dispose(); sb.Stop(); }
                    if (!keep) return;
                }
            }
            if (fail != null) { Raise(false, fail); return; }
            Raise(false, WithWarnings(ReopenMsg, v));
            if (srvKey != null) ServerChanged?.Invoke(srvKey);
            if (restart) ScheduleRestart(t); // перезапуск проверит выходы заново
            else
            {
                OnExited(); // sing-box мог упасть сразу после запуска, пока мы были в Checking
                if (hot) HotApply(t);
            }
            return;
        }
    }

    void ScheduleRestart(int t) =>
        Task.Delay(restartDelay).ContinueWith(_ => Restart(t), TaskScheduler.Default);

    void Restart(int t)
    {
        if (KeepRunning(t)) return;
        Settings snap;
        string? cur;
        lock (gate)
        {
            if (t != ticket || state != ConnState.Restarting) return; // есть изменение новее или уже отключены
            snap = latest!;
            cur = currentServer; // подписка: остаёмся на сервере, на котором работали (после ручного выбора он не равен Settings.Server)
        }
        // Вне блокировок: старый sing-box пока работает, AppsChanged и Disconnect в это время не ждут.
        var v = CheckExits(snap, cur, false);

        string? fail = v.Error, srvKey = null;
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
                watch?.Dispose(); watch = null; // sing-box сейчас остановят: проверять нечего
                ct = cts.Token; // вместе с ticket: Disconnect после этой точки отменит именно её
            }
            old?.Dispose();
            sb.Stop(); // Exited отсюда приходит в Restarting или Disconnected и не считается ошибкой
            if (fail == null)
            {
                Started? st = null;
                TrafficMeter? m = null;
                try { st = StartSingBox(snap, v, ct, false); m = st.Meter; }
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
                        prep = Adopt(st!, v.Vless);
                        live = v.Live;
                        warnings = v.Warnings;
                        udpOk = v.UdpOk;
                        if (currentServer != cur) srvKey = currentServer; // выбранный сервер пропал из подписки
                        if (t == ticket) { state = ConnState.Connected; epoch++; connected = true; } // иначе впереди ещё перезапуск
                    }
                }
                if (!keep || fail != null) { m?.Dispose(); (st?.Api as IDisposable)?.Dispose(); sb.Stop(); }
                if (!keep) return;
            }
        }
        if (fail != null) Raise(false, RestartMsg, fail);
        else Raise(false, WithWarnings(RestartMsg, v));
        if (srvKey != null) ServerChanged?.Invoke(srvKey);
        if (connected) OnExited();
    }
    // Выходы те же, что у запущенного sing-box (выключили и включили обратно, либо изменились только правила):
    // не перезапускаем, соединения не рвутся, правила дописывает HotApply. Под ops: перезапуск, который ещё идёт,
    // сначала закончится и обновит applied.
    bool KeepRunning(int t)
    {
        lock (ops)
        lock (gate)
        {
            if (t != ticket || state != ConnState.Restarting || !SameExits(latest!, applied!)) return false;
            state = ConnState.Connected;
            epoch++;
        }
        Raise(false);
        OnExited(); // sing-box мог упасть, пока мы были в Restarting
        HotApply(t);
        return true;
    }

    // Горячее применение: правила пишутся в файлы, sing-box перечитывает их сам. Вызывать без блокировок.
    // t — номер AppsChanged (или Connect, или ненужного перезапуска): отключение или изменение новее отменяет эту запись.
    // Правки, не менявшие ticket (вернули к applied), сходятся здесь же: после записи смотрим на latest заново.
    // Файлы пишутся под ops, но не под gate; applied обновляется только после повторной проверки ticket/state.
    void HotApply(int t)
    {
        Settings? first = null, last = null; // applied до первой удачной записи и после последней
        var logs = new List<string>();
        string? err = null;
        while (true)
        {
            Settings snap, prev;
            VlessPrep? p;
            lock (gate)
            {
                if (t != ticket || state != ConnState.Connected) break;
                snap = latest!;
                prev = applied!;
                p = prep;
            }
            if (SameRules(snap, prev)) break; // сошлись
            // Копии новых списков качаем вне блокировок; уже имеющиеся (и у выключенных) используем снова.
            // Не загрузившийся при подключении или раньше заново не качаем: только включённые с прошлого применения.
            VlessPrep? newPrep = null;
            if (snap.Mode == ProxyMode.Vless && p != null && vless != null)
            {
                var missing = snap.Lists.Where(id => !prev.Lists.Contains(id) && !p.ListFiles.ContainsKey(id)).ToList();
                if (missing.Count > 0)
                {
                    ListsResult r;
                    // ponytail: два PrepareLists могут редко идти параллельно (Disconnect+Connect во время скачивания, HotApply из Connect и из очереди):
                    // два временных sing-box разом идут на VLESS-сервер. Потолок принят; если мешает — один полёт вокруг PrepareLists.
                    try { r = vless.PrepareLists(missing, p.FetchProxy); }
                    catch { r = new ListsResult(new Dictionary<string, string>(), missing, []); } // ни один не скачался
                    foreach (var id in r.Stale) Log?.Invoke($"Список {ListName(id)} не обновился, работаю со старой копией");
                    foreach (var id in r.Failed) logs.Add($"{WarnPrefix}Список {ListName(id)} не загрузился");
                    var files = new Dictionary<string, string>(p.ListFiles);
                    foreach (var kv in r.Files) files[kv.Key] = kv.Value;
                    newPrep = p with { ListFiles = files };
                }
            }
            bool ok = false;
            lock (ops)
            {
                lock (gate) ok = t == ticket && state == ConnState.Connected;
                if (!ok) break; // отключили или пришло изменение новее: ничего не пишем
                try { RuleFiles.Write(rulesDir, snap, (newPrep ?? p)?.ListFiles ?? new Dictionary<string, string>()); }
                catch (Exception e) { err = e.Message; } // applied прежний: следующее изменение попробует снова
                lock (gate)
                {
                    ok = t == ticket && state == ConnState.Connected;
                    if (ok)
                    {
                        if (newPrep != null) prep = prep == null ? newPrep : newPrep with { Selected = prep.Selected }; // выбор сервера мог смениться за время скачивания
                        if (err == null) { applied = snap; first ??= prev; last = snap; }
                    }
                }
            }
            if (!ok || err != null) break;
        }
        if (first == null && err == null) return;
        var logOut = new List<string?>();
        if (first != null)
        {
            var old = first.ProcessNames();
            logOut.Add(HotMsg);
            if (last!.ProcessNames().Any(n => !old.Contains(n, StringComparer.OrdinalIgnoreCase))) logOut.Add(ReopenMsg);
        }
        logOut.AddRange(logs);
        if (err != null) logOut.Add("Не удалось обновить правила: " + err);
        Raise(false, [.. logOut]);
    }

    // Те же имена .exe в обоих списках программ и тот же набор списков сайтов (без учёта регистра и порядка) —
    // те же правила sing-box. Выходы при этом заново не проверяются.
    static bool SameRules(Settings a, Settings b) =>
        Same(a.ProcessNames(), b.ProcessNames()) && Same(a.BypassNames(), b.BypassNames())
        && Same(a.Lists, b.Lists) && Same(a.Sites, b.Sites);

    // Те же выходы: режим и какие из них нужны. Конфиг sing-box тот же, правила различаются только файлами.
    static bool SameExits(Settings a, Settings b) => a.Mode == b.Mode && Needs(a) == Needs(b);

    static bool Same(List<string> a, List<string> b) => a.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(b);

    // Какие списки непусты: в SOCKS5 — включённые программы «Через прокси» и «Мимо Podkop»;
    // в VLESS выход один, и ему нужны программы или списки сайтов.
    static (bool Proxy, bool Bypass) Needs(Settings s) => s.Mode == ProxyMode.Vless
        ? (s.ProcessNames().Count > 0 || s.Lists.Count > 0 || s.Sites.Count > 0, false)
        : (s.ProcessNames().Count > 0, s.BypassNames().Count > 0);

    // Итог проверки снимка b годится для снимка a: непусты те же списки программ, а в VLESS ещё тот же набор
    // списков сайтов (их копии готовились для b) и программы или сайты есть в обоих или ни в одном (без них нужен хоть один файл).
    static bool SameChecks(Settings a, Settings b) =>
        SameExits(a, b) && (a.Mode != ProxyMode.Vless
            || Same(a.Lists, b.Lists) && (a.ProcessNames().Count + a.Sites.Count > 0) == (b.ProcessNames().Count + b.Sites.Count > 0));

    // Проверяет только нужные выходы снимка: прокси (Ip, Port) — при программах «Через прокси», «Мимо Podkop»
    // (Ip, DirectPort) — при её программах и указанном порте. Оба сразу, до ~3 с каждый: вызывать вне блокировок.
    // current — сервер подписки, на котором уже работаем (перезапуск); connecting — подключение (подписку можно скачать)
    Verdict CheckExits(Settings snap, string? current, bool connecting)
    {
        if (snap.Mode == ProxyMode.Vless) return CheckVless(snap, current, connecting);
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
    Verdict CheckVless(Settings snap, string? current, bool connecting)
    {
        if (vless == null) return ErrorVerdict("Режим VLESS недоступен");
        if (Settings.IsSubscription(snap.VlessLink)) return CheckSubscription(snap, current, connecting);
        Server? srv; string? linkError;
        try { (srv, linkError) = vless.Decode(snap.VlessLink); }
        catch (Exception e) { (srv, linkError) = (null, e.Message); }
        if (srv == null) return ErrorVerdict(linkError ?? "Ссылка не подходит");
        foreach (var w in srv.Warnings) Log?.Invoke("Ссылка: " + w);
        if (!IPAddress.TryParse(srv.Host, out var ip))
        {
            try { ip = vless.Resolve(srv.Host); }
            catch { ip = null; }
        }
        if (ip == null) return ErrorVerdict("Сервер не найден: " + srv.Host);
        if (!srv.Udp) // у UDP-протоколов (Hysteria2, TUIC) TCP-проверка бессмысленна
        {
            string? down;
            try { down = vless.Reach(ip, srv.Port); }
            catch (Exception e) { down = e.Message; }
            if (down != null) return ErrorVerdict("Сервер не отвечает: " + down);
        }
        JsonObject proxy;
        try { proxy = ConfigBuilder.ProxyOutbound(srv, ip); }
        catch (Exception e) { return ErrorVerdict("Ссылка не подходит: " + e.Message); }

        return WithLists(snap, new VlessPrep([ip], [proxy], null, new Dictionary<string, string>(), new Dictionary<string, string>()));
    }

    // Копии списков сайтов (через выбранный сервер) и итог проверки.
    Verdict WithLists(Settings snap, VlessPrep basis)
    {
        var lists = new ListsResult(new Dictionary<string, string>(), [], []);
        if (snap.Lists.Count > 0)
        {
            try { lists = vless!.PrepareLists(snap.Lists, basis.FetchProxy); }
            catch { lists = lists with { Failed = snap.Lists.ToList() }; } // не смогли даже начать: ни один не скачался
        }
        foreach (var id in lists.Stale) Log?.Invoke($"Список {ListName(id)} не обновился, работаю со старой копией");
        return EvaluateVless(snap, basis, lists);
    }

    // Подписка до запуска: кэш (при подключении устаревший или отсутствующий — скачиваем), IP серверов, выбранный сервер.
    // Скачивание до ~15 с и DNS: вызывать вне блокировок. При перезапуске подписку не обновляем.
    Verdict CheckSubscription(Settings snap, string? current, bool connecting)
    {
        var url = snap.VlessLink.Trim();
        SubCache? cache;
        try { cache = vless!.LoadSub(url); }
        catch { cache = null; }
        if (connecting && (cache == null || SubStore.Stale(cache, DateTime.UtcNow)))
        {
            SubCache? fresh; string? err;
            try { (fresh, err) = vless!.FetchSub(url, cache); }
            catch (Exception e) { (fresh, err) = (null, e.Message); }
            if (fresh != null) cache = fresh;
            else if (cache != null) Log?.Invoke("Подписка не обновилась, работаю по копии: " + (err ?? "нет ответа"));
            else return ErrorVerdict(err ?? "Подписка не скачалась");
        }
        if (cache == null) return ErrorVerdict("Нет копии подписки");
        var sup = cache.Servers.Where(s => s.Server != null).ToList();
        if (sup.Count == 0) return ErrorVerdict("В подписке нет серверов, которые ViviTune умеет");

        // DNS, не сервер: можно параллельно
        var ips = new IPAddress?[sup.Count];
        Task.WaitAll(sup.Select((s, i) => Task.Run(() =>
        {
            var host = s.Server!.Host;
            if (IPAddress.TryParse(host, out var lit)) { ips[i] = lit; return; }
            try { ips[i] = vless!.Resolve(host); }
            catch { ips[i] = null; }
        })).ToArray());
        var items = new List<(SubServer S, IPAddress Ip, JsonObject Ob)>();
        for (int i = 0; i < sup.Count; i++)
        {
            if (ips[i] is not { } ip) continue; // не разрешился — не входит в конфиг
            try
            {
                var o = ConfigBuilder.ProxyOutbound(sup[i].Server!, ip);
                o["tag"] = "s" + items.Count;
                items.Add((sup[i], ip, o));
            }
            catch { } // сервер, который не собрать в outbound, пропускаем
        }
        if (items.Count == 0) return ErrorVerdict("Ни один сервер подписки не найден");

        int sel = items.FindIndex(x => x.S.Key == current);
        if (sel < 0) sel = items.FindIndex(x => x.S.Key == snap.Server);
        if (sel < 0) sel = 0;
        var chosen = items[sel];
        if (!snap.AutoServer && !chosen.S.Server!.Udp) // с галкой выберем по пингу; у UDP-протоколов TCP-проверка бессмысленна
        {
            string? down;
            try { down = vless!.Reach(chosen.Ip, chosen.S.Server.Port); }
            catch (Exception e) { down = e.Message; }
            if (down != null) return ErrorVerdict("Сервер не отвечает: " + down);
        }
        var keys = new Dictionary<string, string>();
        foreach (var x in items) keys[(string)x.Ob["tag"]!] = x.S.Key;
        var basis = new VlessPrep(items.Select(x => x.Ip).Distinct().ToList(), items.Select(x => x.Ob).ToList(), "s" + sel, new Dictionary<string, string>(), keys);
        return WithLists(snap, basis);
    }

    // Сервер VLESS ответил. Не загрузившийся список — предупреждение; нет ни программ, ни одного файла списка —
    // ошибка: правил для sing-box нет. Предупреждения — в порядке lists.Failed.
    public static Verdict EvaluateVless(Settings s, VlessPrep basis, ListsResult lists)
    {
        if (s.ProcessNames().Count == 0 && s.Sites.Count == 0 && lists.Files.Count == 0) return ErrorVerdict("Списки не загрузились");
        var w = lists.Failed.Select(id => $"Список {ListName(id)} не загрузился").ToList();
        return new Verdict(new Exits(true, false), w, null, null, basis with { ListFiles = lists.Files });
    }

    static Verdict ErrorVerdict(string error) => new(default, [], error, null);

    static string ListName(string id) => Catalogs.ListById(id)?.Name ?? id;

    // Строка лога, за ней предупреждения с префиксом.
    static string?[] WithWarnings(string msg, Verdict v) => [msg, .. v.Warnings.Select(w => WarnPrefix + w)];

    // Вызывать под ops. В VLESS после запуска — проверка сервера и DNS через Clash API (ct отменяет её при Disconnect);
    // не прошла — CheckFailedException, sing-box останавливает вызывающий. Счётчик скорости — только после неё.
    // Api — клиент Clash API (подписка); Picked — тег, выбранный автовыбором (null — остался прежний);
    // Pings — его пинги по тегам (null — автовыбора не было).
    sealed record Started(TrafficMeter Meter, IClashApi? Api, string? Picked, IReadOnlyDictionary<string, int?>? Pings);

    // В подписке с галкой при подключении (не перезапуске): до AfterStart пингуем серверы по очереди и берём самый быстрый.
    // Disconnect отменяет пинги через ct; если никто не ответил или отменили — остаётся выбранный.
    Started StartSingBox(Settings snap, Verdict v, CancellationToken ct, bool connecting)
    {
        int port = FreePort();
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        RuleFiles.Write(rulesDir, snap, v.Vless?.ListFiles ?? new Dictionary<string, string>());
        File.WriteAllText(configPath, v.Vless is { } p
            ? ConfigBuilder.BuildVless(snap, port, secret, p, rulesDir)
            : ConfigBuilder.Build(snap, port, secret, rulesDir, v.Live));
        sb.Start(configPath);
        IClashApi? clash = null;
        try
        {
            string? picked = null;
            IReadOnlyDictionary<string, int?>? pings = null;
            if (v.Vless is { Selected: not null } sub)
            {
                clash = vless!.Api(port, secret);
                if (connecting && snap.AutoServer)
                {
                    pings = Pinger.Ping(clash, sub.Outbounds.Select(o => (string)o["tag"]!).ToList(), vless.PingMs, vless.PingCap, null, ct);
                    var best = ct.IsCancellationRequested ? null : Pinger.Fastest(pings);
                    if (best == sub.Selected) picked = best;
                    else if (best != null && clash.Select("proxy", best)) picked = best;
                }
            }
            if (v.Vless != null && vless!.AfterStart(port, secret, ct) is { } err)
                throw new CheckFailedException(err);
            var m = new TrafficMeter(port, secret);
            m.Sample += s => OnSample(m, s);
            m.Start();
            return new Started(m, clash, picked, pings);
        }
        catch
        {
            (clash as IDisposable)?.Dispose();
            throw;
        }
    }

    // Под gate: что запомнить о запущенном sing-box; возвращает prep с актуальным выбранным сервером.
    VlessPrep? Adopt(Started st, VlessPrep? p)
    {
        watch?.Dispose(); watch = null;
        (api as IDisposable)?.Dispose();
        api = st.Api;
        if (p?.Selected == null) { currentServer = null; return p; }
        var tag = st.Picked ?? p.Selected;
        currentServer = p.Keys.GetValueOrDefault(tag);
        if (st.Pings != null)
            pings = st.Pings.Where(kv => p.Keys.ContainsKey(kv.Key)).ToDictionary(kv => p.Keys[kv.Key], kv => kv.Value);
        StartWatch(st.Api!, p);
        return p with { Selected = tag };
    }

    // Под gate; стартует вместе со счётчиком скорости (Adopt зовут в тот же момент, когда его запоминают).
    void StartWatch(IClashApi a, VlessPrep p)
    {
        var tags = p.Outbounds.Select(o => (string)o["tag"]!).ToList();
        ServerWatch? w = null;
        w = new ServerWatch(a, tags,
            () => { lock (gate) return prep?.Selected ?? ""; },
            () => { lock (gate) return applied?.AutoServer ?? false; },
            vless!.WatchEvery, vless.PingMs, vless.PingCap, pingGate,
            () => { lock (gate) return p.Keys.Where(kv => pings.ContainsKey(kv.Value)).ToDictionary(kv => kv.Key, kv => pings[kv.Value]); });
        w.Down += (cur, best, ms) =>
        {
            lock (gate) if (watch != w || state != ConnState.Connected) return;
            ServerDown?.Invoke(p.Keys.GetValueOrDefault(cur) ?? cur, best == null ? null : p.Keys.GetValueOrDefault(best), ms);
        };
        w.Switched += (from, to, ms) =>
        {
            string? fromKey, toKey = p.Keys.GetValueOrDefault(to);
            lock (gate)
            {
                if (watch != w || state != ConnState.Connected || toKey == null || prep == null) return;
                fromKey = currentServer;
                prep = prep with { Selected = to };
                currentServer = toKey;
            }
            ServerChanged?.Invoke(toKey);
            ServerSwitched?.Invoke(fromKey ?? p.Keys.GetValueOrDefault(from) ?? from, toKey, ms);
        };
        watch = w;
        w.Start();
    }

    // Переключение сервера подписки без перезапуска (Clash API). Вызывать не в UI-потоке.
    public bool SelectServer(string key)
    {
        IClashApi a; string tag; int e;
        lock (gate)
        {
            if (state != ConnState.Connected || api == null || prep is not { Selected: not null } p) return false;
            var kv = p.Keys.FirstOrDefault(x => x.Value == key);
            if (kv.Key == null) return false;
            a = api; tag = kv.Key; e = epoch;
        }
        bool ok;
        try { ok = a.Select("proxy", tag); }
        catch { ok = false; }
        if (!ok) return false;
        lock (gate)
        {
            if (state != ConnState.Connected || epoch != e) return false; // пока переключали, отключили или перезапустили
            prep = prep! with { Selected = tag };
            currentServer = key;
            watch?.Reset();
        }
        ServerChanged?.Invoke(key);
        return true;
    }

    // Пинг всех серверов подписки по очереди через запущенный sing-box; Pings обновляются по одному.
    public bool PingConnected(CancellationToken ct)
    {
        IClashApi a; VlessPrep p; int e;
        lock (gate)
        {
            if (state != ConnState.Connected || api == null || prep is not { Selected: not null } p0) return false;
            a = api; p = p0; e = epoch;
        }
        try { pingGate.Wait(ct); } // watch может пинговать: по одному
        catch (OperationCanceledException) { return true; }
        try
        {
            Pinger.Ping(a, p.Outbounds.Select(o => (string)o["tag"]!).ToList(), vless!.PingMs, null, (tag, ms) =>
            {
                if (ct.IsCancellationRequested) return; // отменённый пинг не ответ
                lock (gate)
                {
                    if (state != ConnState.Connected || epoch != e) return;
                    pings = new Dictionary<string, int?>(pings) { [p.Keys[tag]] = ms };
                }
                PingsChanged?.Invoke();
            }, ct);
        }
        finally { pingGate.Release(); }
        return true;
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
        prep = null;
        watch?.Dispose(); watch = null;
        (api as IDisposable)?.Dispose();
        api = null;
        currentServer = null;
        pings = new Dictionary<string, int?>();
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

    internal static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try { return ((IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }
}
