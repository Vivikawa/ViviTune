namespace ViviTune;

// Проверка текущего сервера подписки: каждые interval — Delay("proxy"). Три неудачи подряд: с галкой «самый быстрый» —
// пингуем остальные и переключаемся (Switched), иначе или если никто не ответил — Down. После Down повторно не сообщаем,
// пока не будет успеха, Reset() или переключения. Работает в своём потоке пула; события приходят оттуда же.
// Все обращения к Clash API идут через pingGate (его делит Session с PingConnected): пинги не пересекаются.
// Dispose не ждёт: отмена уходит в пул (колбэки отмены не должны выполняться под чужим lock), события после него не приходят.
public sealed class ServerWatch : IDisposable
{
    const int Fails = 3;
    const int ProxyMs = 5000;

    readonly IClashApi api;
    readonly IReadOnlyList<string> tags;
    readonly Func<string> current;
    readonly Func<bool> auto;
    readonly TimeSpan interval;
    readonly int pingMs;
    readonly TimeSpan cap;
    readonly SemaphoreSlim pingGate;
    readonly Func<IReadOnlyDictionary<string, int?>>? lastPings; // последние пинги по тегам (без галки Down предлагает лучший)
    // переключение (вместе с обновлением состояния Session); зовётся под sync, только если Reset не было с начала проверки
    readonly Func<string, bool> select;
    readonly Action<string, int?>? onPing; // результат каждого пинга при автопереключении
    readonly CancellationTokenSource cts = new();
    readonly object sync = new();
    int gen; // растёт в Reset: проверка, начатая до него, ничего не переключает и не сообщает
    int fails;
    bool reported;
    bool notified; // «никто не ответил» уже показано: окно не повторяем, а автопереключение пробует на следующих сериях
    volatile bool disposed;

    public event Action<string, string?, int?>? Down;
    public event Action<string, string, int>? Switched;

    public ServerWatch(IClashApi api, IReadOnlyList<string> tags, Func<string> current, Func<bool> auto, TimeSpan interval, int pingMs, TimeSpan cap,
        SemaphoreSlim? pingGate = null, Func<IReadOnlyDictionary<string, int?>>? lastPings = null,
        Func<string, bool>? select = null, Action<string, int?>? onPing = null)
    {
        this.select = select ?? (t => api.Select("proxy", t));
        this.onPing = onPing;
        this.api = api; this.tags = tags; this.current = current; this.auto = auto;
        this.interval = interval; this.pingMs = pingMs; this.cap = cap;
        this.pingGate = pingGate ?? new SemaphoreSlim(1, 1);
        this.lastPings = lastPings;
    }

    public void Reset()
    {
        lock (sync) { gen++; fails = 0; reported = false; notified = false; } // ждёт переключение, которое уже идёт (оно под sync)
    }

    public void Start() => _ = Loop(cts.Token);

    async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            try { await Task.Run(() => Tick(ct), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch { } // обработчик события не должен ронять проверку
        }
    }

    void Tick(CancellationToken ct)
    {
        int? d; int g;
        lock (sync) g = gen;
        pingGate.Wait(ct);
        try { d = api.Delay("proxy", ProxyMs, ct); }
        catch { d = null; }
        finally { pingGate.Release(); }
        if (disposed) return;
        lock (sync)
        {
            if (g != gen) return;
            if (d != null) { fails = 0; reported = false; notified = false; return; }
            if (++fails < Fails || reported) return;
        }
        var cur = current();
        if (auto())
        {
            IReadOnlyDictionary<string, int?> pings;
            pingGate.Wait(ct);
            try { pings = Pinger.Ping(api, tags.Where(t => t != cur).ToList(), pingMs, cap, onPing, ct); }
            finally { pingGate.Release(); }
            if (disposed) return;
            var best = Pinger.Fastest(pings, cur);
            bool ok;
            lock (sync)
            {
                if (g != gen) return; // пока пинговали, сервер выбрали вручную
                try { ok = best != null && select(best); }
                catch { ok = false; }
                if (ok) { fails = 0; reported = false; notified = false; }
            }
            if (disposed) return;
            if (ok)
            {
                Switched?.Invoke(cur, best!, pings[best!]!.Value);
                return;
            }
            bool first;
            lock (sync)
            {
                if (g != gen) return;
                fails = 0; // новая серия неудач - новая попытка автопереключения
                first = !notified;
                notified = true;
            }
            if (first) Down?.Invoke(cur, null, null);
        }
        else
        {
            var last = lastPings?.Invoke();
            var best = last == null ? null : Pinger.Fastest(last, cur);
            Report(cur, best, best == null ? null : last![best], g);
        }
    }

    void Report(string cur, string? best, int? ms, int g)
    {
        lock (sync)
        {
            if (g != gen) return;
            reported = true;
        }
        Down?.Invoke(cur, best, ms);
    }

    public void Dispose()
    {
        disposed = true;
        ThreadPool.QueueUserWorkItem(_ => cts.Cancel());
    }
}
